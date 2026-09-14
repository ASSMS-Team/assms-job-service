using Confluent.Kafka;

using JobService.Messaging.Consumers;
using JobService.Messaging.Contracts;
using JobService.Repositories;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobService.Tests;

// The JobAssigned consume loop, run against a consumer the test controls rather
// than a broker. What is being pinned is the one decision the loop makes that a
// reader of the code cannot check by eye: which failures are committed past and
// which are read again.
public class JobAssignedConsumerTests
{
    private const string JobId = "9f1c7a24-8f4e-4c3a-9a52-2b6d0f5e1a77";
    private const string AssignmentId = "e2d7b415-9a63-4c08-b1f5-7d4e2a9c6301";
    private const string TechnicianId = "5c8a3f71-4b29-4e6d-8a03-9f1b7c2e4d58";

    // A well-formed JobAssigned, in the shape the Dispatch Service publishes -
    // six payload fields, camelCase, UTC timestamps.
    private static string ValidMessage(
        string jobId = JobId,
        string assignmentId = AssignmentId,
        string assignedAt = "2026-09-14T09:15:02.446Z") => $$"""
        {
          "eventId": "7b41e9c6-0d38-4a52-9f17-3c8b6e2d5a04",
          "eventType": "JobAssigned",
          "eventVersion": 1,
          "occurredAt": "{{assignedAt}}",
          "producer": "dispatch-service",
          "payload": {
            "assignmentId": "{{assignmentId}}",
            "jobId": "{{jobId}}",
            "jobReference": "JOB-7K2M9X",
            "technicianId": "{{TechnicianId}}",
            "technicianReference": "TECH-0001",
            "assignedAt": "{{assignedAt}}"
          }
        }
        """;

    // Runs the hosted service until the fake consumer runs out of messages, which
    // is what stands in for the host shutting down - the real loop only ends when
    // it is stopped.
    //
    // The wait is bounded so that a loop which stops making progress fails the
    // test instead of hanging the run.
    private static async Task RunAsync(
        CancellationTokenSource cts,
        FakeKafkaConsumer kafka,
        FakeJobRepository repository,
        Action<ConsumerConfig>? onConfig = null)
    {
        kafka.OnMessagesExhausted = cts.Cancel;

        // A real container rather than a fake scope factory: the loop opens one
        // scope per message, and that is the behaviour being relied on.
        await using var provider = new ServiceCollection()
            .AddScoped<IJobRepository>(_ => repository)
            .BuildServiceProvider();

        var consumer = new JobAssignedConsumer(
            "localhost:9092",
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JobAssignedConsumer>.Instance,
            config =>
            {
                onConfig?.Invoke(config);

                return kafka;
            });

        await consumer.StartAsync(cts.Token);

        await consumer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---- subscription ------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_SubscribesToJobAssignedWithTheContractsGroupAndManualCommits()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();
        ConsumerConfig? captured = null;

        // Act
        await RunAsync(cts, kafka, repository, config => captured = config);

        // Assert - the group name is the contract's, not a local string. Sharing
        // Reporting's group would make Kafka deliver each event to only one of
        // the two services.
        Assert.Equal("assms-job-job-assigned", JobAssignedPayload.ConsumerGroup);
        Assert.Equal(JobAssignedPayload.ConsumerGroup, captured!.GroupId);
        Assert.Equal("job-assigned", Assert.Single(kafka.SubscribedTopics));

        // Manual commit, and earliest. Auto-commit would move the offset on a
        // timer with no idea whether the write succeeded; Latest would silently
        // skip every assignment made before this service first ran.
        Assert.False(captured.EnableAutoCommit);
        Assert.Equal(AutoOffsetReset.Earliest, captured.AutoOffsetReset);
    }

    [Fact]
    public async Task ExecuteAsync_OnShutdown_ClosesTheConsumer()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - leaving the group deliberately reassigns the partitions at
        // once rather than after the session timeout expires.
        Assert.Equal(1, kafka.CloseCallCount);
    }

    // ---- the happy path ----------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WithAValidMessage_WritesEveryAssignmentFieldAndCommits()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();
        var message = kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - the six published fields the row records, mapped out of the
        // envelope's payload.
        var applied = Assert.Single(repository.AppliedAssignments);

        Assert.Equal(JobId, applied.JobId);
        Assert.Equal(AssignmentId, applied.AssignmentId);
        Assert.Equal(TechnicianId, applied.TechnicianId);
        Assert.Equal("TECH-0001", applied.TechnicianReference);

        // Dispatch publishes no status on this event - the event type is the
        // status change - so the consumer supplies it from the contract.
        Assert.Equal("ASSIGNED", applied.Status);
        Assert.Equal(JobAssignedPayload.AssignedStatus, applied.Status);

        Assert.Equal(new DateTime(2026, 9, 14, 9, 15, 2, 446, DateTimeKind.Utc), applied.AssignedAt);

        Assert.Equal(message.TopicPartitionOffset, Assert.Single(kafka.CommittedOffsets));
    }

    [Fact]
    public async Task ExecuteAsync_CommitsAfterTheWriteAndNotBefore()
    {
        // Arrange - the order matters: the write is allowed to go first precisely
        // because it is idempotent, and a commit that ran first would lose the
        // assignment entirely if the process then died.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();
        var committedWhenWritten = -1;

        repository.OnApplyAssignment = () => committedWhenWritten = kafka.CommittedOffsets.Count;

        kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - nothing was committed at the moment the write ran, and
        // something was committed by the end.
        Assert.Equal(0, committedWhenWritten);
        Assert.Single(kafka.CommittedOffsets);
    }

    [Fact]
    public async Task ExecuteAsync_ReadsAssignedAtAsUtc()
    {
        // Arrange - the stale-event guard compares this value against the stored
        // one, so a timestamp read as the server's local time would make the
        // comparison depend on the timezone of whatever machine the service runs
        // on.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();

        kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert
        var applied = Assert.Single(repository.AppliedAssignments);

        Assert.Equal(DateTimeKind.Utc, applied.AssignedAt.Kind);
    }

    // ---- duplicate safety --------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WithARedeliveredEvent_WritesItAgainAndCommitsRatherThanFailing()
    {
        // Arrange - the same event twice, which is what at-least-once delivery
        // guarantees will happen. The consumer does not deduplicate in memory; it
        // hands both to the repository, whose UPDATE declines the second because
        // the stored assigned_at is no longer older than the incoming one.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();

        kafka.Enqueue(ValidMessage());
        kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - both attempted, both committed, nothing thrown. The guard is
        // in the SQL, and the loop's job is to let it do its work rather than to
        // treat a duplicate as an error.
        Assert.Equal(2, repository.ApplyAssignmentAsyncCallCount);
        Assert.Equal(2, kafka.CommittedOffsets.Count);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheUpdateChangesNothing_CommitsAndDoesNotRetry()
    {
        // Arrange - false is what the repository returns when the UPDATE matched
        // no row: the event was already applied, it is older than what is stored,
        // or the job is not in this database. None of the three improves on a
        // second attempt.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository { ApplyAssignmentResult = false };
        var message = kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - committed past, and never seeked back. A no-op result is an
        // answer, not a failure.
        Assert.Equal(1, repository.ApplyAssignmentAsyncCallCount);
        Assert.Equal(message.TopicPartitionOffset, Assert.Single(kafka.CommittedOffsets));
        Assert.Empty(kafka.SeekedOffsets);
    }

    // ---- stale-event protection --------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WithAnOlderAssignment_StillPassesTheEventsOwnTimestampToTheGuard()
    {
        // Arrange - a newer assignment followed by an older one, which is what a
        // replay from the beginning produces once US-15 allows a job to be
        // reassigned. The consumer must not reorder or filter: it passes each
        // event's own assignedAt through, and the repository's WHERE clause is
        // what refuses the older one.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();

        kafka.Enqueue(ValidMessage(assignedAt: "2026-09-14T12:00:00.000Z"));
        kafka.Enqueue(ValidMessage(
            assignmentId: "11111111-2222-3333-4444-555555555555",
            assignedAt: "2026-09-14T09:00:00.000Z"));

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - both timestamps reached the guard, in arrival order and
        // unmodified. Nothing in the loop compares them, which is deliberate:
        // the comparison belongs in the UPDATE, where it is atomic against the
        // stored row.
        Assert.Equal(2, repository.AppliedAssignments.Count);
        Assert.Equal(
            new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc),
            repository.AppliedAssignments[0].AssignedAt);
        Assert.Equal(
            new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc),
            repository.AppliedAssignments[1].AssignedAt);
    }

    // ---- malformed events: commit past -------------------------------------

    [Fact]
    public async Task ExecuteAsync_WithAMalformedMessage_CommitsPastItWithoutWriting()
    {
        // Arrange - JSON that will never parse. Not committing it would stop this
        // loop dead on that one message and every assignment behind it.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();
        var message = kafka.Enqueue("{ not json");

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert
        Assert.Empty(repository.AppliedAssignments);
        Assert.Equal(message.TopicPartitionOffset, Assert.Single(kafka.CommittedOffsets));
        Assert.Empty(kafka.SeekedOffsets);
    }

    [Fact]
    public async Task ExecuteAsync_WithAMalformedMessage_KeepsProcessingTheOnesBehindIt()
    {
        // Arrange - the reason committing past a bad message matters at all.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();

        kafka.Enqueue("{ not json");
        kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert
        Assert.Single(repository.AppliedAssignments);
        Assert.Equal(2, kafka.CommittedOffsets.Count);
    }

    [Theory]
    // A bare null: valid JSON, not an event.
    [InlineData("null")]
    // An envelope with no payload at all.
    [InlineData("""{"eventId":"a","eventType":"JobAssigned","eventVersion":1,"producer":"dispatch-service"}""")]
    // Each of the four fields the UPDATE cannot be written without, blanked in
    // turn. An event missing any of them would write a null into a column the
    // report and the job detail both read.
    [InlineData("""{"payload":{"assignmentId":"a","jobId":"","technicianId":"t","technicianReference":"r","assignedAt":"2026-09-14T09:15:02.446Z"}}""")]
    [InlineData("""{"payload":{"assignmentId":"","jobId":"j","technicianId":"t","technicianReference":"r","assignedAt":"2026-09-14T09:15:02.446Z"}}""")]
    [InlineData("""{"payload":{"assignmentId":"a","jobId":"j","technicianId":"","technicianReference":"r","assignedAt":"2026-09-14T09:15:02.446Z"}}""")]
    [InlineData("""{"payload":{"assignmentId":"a","jobId":"j","technicianId":"t","technicianReference":"","assignedAt":"2026-09-14T09:15:02.446Z"}}""")]
    // A missing timestamp, which would defeat the guard that makes the write
    // idempotent - a default DateTime is older than every stored value.
    [InlineData("""{"payload":{"assignmentId":"a","jobId":"j","technicianId":"t","technicianReference":"r"}}""")]
    public async Task ExecuteAsync_WithAnUnusableEvent_CommitsPastItWithoutWriting(string body)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository();
        var message = kafka.Enqueue(body);

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - treated exactly as malformed: no retry will make a missing
        // field appear.
        Assert.Empty(repository.AppliedAssignments);
        Assert.Equal(message.TopicPartitionOffset, Assert.Single(kafka.CommittedOffsets));
        Assert.Empty(kafka.SeekedOffsets);
    }

    // ---- transient database failure: seek and retry ------------------------

    [Fact]
    public async Task ExecuteAsync_WhenTheWriteFails_SeeksBackToTheMessageAndDoesNotCommit()
    {
        // Arrange - a write that failed for a reason outside the message. The
        // same message will succeed once the database is back, so its offset must
        // not move.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository
        {
            ApplyAssignmentExceptionToThrow = new InvalidOperationException("The database was unreachable.")
        };

        // Cancelling at the moment of failure unwinds the loop instead of
        // sleeping out its five-second retry delay.
        repository.OnApplyAssignment = cts.Cancel;

        var message = kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - seeked, not committed. Seeking is what makes the next read
        // return this message again; without it the consumer's in-memory position
        // has already moved past it.
        Assert.Equal(message.TopicPartitionOffset, Assert.Single(kafka.SeekedOffsets));
        Assert.Empty(kafka.CommittedOffsets);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheWriteFailsAndThenSucceeds_AppliesTheMessageAndCommitsIt()
    {
        // Arrange - a database that was down and came back, which is the case the
        // retry exists for. The fake's Seek re-queues the message, as the real
        // consumer would redeliver it.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer();
        var repository = new FakeJobRepository { ApplyAssignmentFailuresBeforeSuccess = 1 };
        var message = kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - tried twice, seeked once, committed once, and the assignment
        // landed.
        Assert.Equal(2, repository.ApplyAssignmentAsyncCallCount);
        Assert.Equal(message.TopicPartitionOffset, Assert.Single(kafka.SeekedOffsets));
        Assert.Equal(message.TopicPartitionOffset, Assert.Single(kafka.CommittedOffsets));
        Assert.Equal(JobId, repository.AppliedAssignments[^1].JobId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConsumingFails_KeepsReading()
    {
        // Arrange - a broker or transport failure, which is neither a bad message
        // nor a failed write. There is nothing to commit and nothing to skip.
        using var cts = new CancellationTokenSource();
        var kafka = new FakeKafkaConsumer
        {
            ConsumeExceptionToThrow = new ConsumeException(
                new ConsumeResult<byte[], byte[]>(),
                new Error(ErrorCode.Local_Transport, "The broker was unreachable."))
        };
        var repository = new FakeJobRepository();

        kafka.Enqueue(ValidMessage());

        // Act
        await RunAsync(cts, kafka, repository);

        // Assert - the message behind the failure was still processed.
        Assert.Single(repository.AppliedAssignments);
        Assert.Single(kafka.CommittedOffsets);
    }
}
