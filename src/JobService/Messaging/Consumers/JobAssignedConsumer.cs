using System.Text.Json;

using Confluent.Kafka;

using JobService.Messaging.Contracts;
using JobService.Repositories;

namespace JobService.Messaging.Consumers;

/// <summary>Consumes Dispatch JobAssigned events and records assignment context on the job.</summary>
///
// This service produces JobCreated and consumes JobAssigned, which closes the
// loop: it raises the job, Dispatch decides who takes it, and the decision comes
// back here to be written onto the row an Agent reads.
//
// A BackgroundService rather than anything triggered by a request: the assignment
// happens in Dispatch on its own schedule, and the job has to already show it when
// someone next looks the job up.
public class JobAssignedConsumer : BackgroundService
{
    // How long to wait before re-reading a message whose write failed. Without it
    // a database that is down turns the retry into a hot loop that reopens a
    // connection thousands of times a second and fills the log with one line.
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromSeconds(5);

    // camelCase, mirroring how Dispatch serializes the envelope. The policy
    // applies on the way in as well as out, so this is what maps the published
    // "technicianReference" onto TechnicianReference.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConsumerConfig _consumerConfig;
    private readonly Func<ConsumerConfig, IConsumer<string, string>> _consumerFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobAssignedConsumer> _logger;

    public JobAssignedConsumer(
        string bootstrapServers,
        IServiceScopeFactory scopeFactory,
        ILogger<JobAssignedConsumer> logger)
        : this(
            bootstrapServers,
            scopeFactory,
            logger,
            config => new ConsumerBuilder<string, string>(config).Build())
    {
    }

    // The factory exists so a test can hand the loop a consumer it controls.
    // Building one inside ExecuteAsync would tie the whole of this class to a
    // running broker, and the branch worth testing - commit past the message or
    // seek back to it - is exactly the branch that would then be unreachable.
    internal JobAssignedConsumer(
        string bootstrapServers,
        IServiceScopeFactory scopeFactory,
        ILogger<JobAssignedConsumer> logger,
        Func<ConsumerConfig, IConsumer<string, string>> consumerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _consumerFactory = consumerFactory;

        _consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = JobAssignedPayload.ConsumerGroup,
            // The offset is committed by this code, after the row is written.
            // Auto-commit moves it on a timer with no idea whether the write
            // succeeded, so a crash between the timer firing and the write would
            // leave a job permanently showing as CREATED when a technician is in
            // fact already on their way to it.
            EnableAutoCommit = false,
            // A group reading a topic for the first time starts at the beginning,
            // so a deployment made after assignments already exist catches up on
            // them rather than starting blank.
            AutoOffsetReset = AutoOffsetReset.Earliest
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ExecuteAsync runs inline on the host's startup path until its first
        // await, and Consume blocks the thread it is called on. Yielding here
        // hands the rest of this method to a thread-pool thread, so the web host
        // finishes starting and serves requests while the loop runs.
        await Task.Yield();

        using var consumer = _consumerFactory(_consumerConfig);

        consumer.Subscribe(JobAssignedPayload.Topic);

        _logger.LogInformation(
            "Subscribed to {Topic} as group {GroupId}.",
            JobAssignedPayload.Topic,
            JobAssignedPayload.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> message;

                try
                {
                    message = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    // A broker or transport failure, not a message this loop was
                    // handed. There is nothing to commit and nothing to skip -
                    // the client reconnects on its own, so read again.
                    _logger.LogError(exception, "Consuming from {Topic} failed.", JobAssignedPayload.Topic);
                    continue;
                }

                // Two failures with opposite right answers, which is why they are
                // handled separately.
                //
                //   A message that will not deserialize will not deserialize on
                //   the thousandth attempt either. Not committing it would stop
                //   this loop dead on that one message and every assignment
                //   behind it, so its offset is committed and the loop moves past
                //   it. It is logged in full first, because committing past a
                //   message is the one thing here that discards data.
                //
                //   A write that failed, failed for a reason outside the message
                //   - the database was down, a connection dropped. The same
                //   message will succeed once that is fixed, so its offset is not
                //   committed and it is read again.
                EventEnvelope<JobAssignedPayload>? envelope;

                try
                {
                    envelope = Deserialize(message.Message.Value);
                }
                catch (JsonException exception)
                {
                    _logger.LogError(
                        exception,
                        "Discarding malformed JobAssigned event at {Offset}.",
                        message.TopicPartitionOffset);

                    consumer.Commit(message);
                    continue;
                }

                if (!IsUsable(envelope))
                {
                    // Valid JSON, but not an event this loop can act on: a bare
                    // null, an envelope with no payload, or one missing a field
                    // the update cannot be written without. No retry will make
                    // any of those appear, so it is treated as malformed.
                    _logger.LogError(
                        "Discarding invalid JobAssigned event at {Offset}.",
                        message.TopicPartitionOffset);

                    consumer.Commit(message);
                    continue;
                }

                try
                {
                    await ApplyAsync(envelope!.Payload, message);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Recording the assignment of job {JobId} at {Offset} failed. It will be retried.",
                        envelope!.Payload.JobId,
                        message.TopicPartitionOffset);

                    // Not committing is not enough on its own. The consumer's
                    // in-memory position has already moved past this message, so
                    // the next Consume would return the one after it and this
                    // event would only come back around after a rebalance.
                    // Seeking to its offset is what makes the next read return
                    // this message again.
                    consumer.Seek(message.TopicPartitionOffset);

                    await Task.Delay(WriteRetryDelay, stoppingToken);
                    continue;
                }

                // Commit last. The row is written and the update is idempotent, so
                // if the process dies between here and the commit the event is
                // redelivered and the update declines to change anything - the
                // same outcome. That is the whole reason the write goes first.
                consumer.Commit(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Consume and Delay both throw this when the host stops, and
            // neither is an error.
            _logger.LogInformation("Stopping the {Topic} consumer.", JobAssignedPayload.Topic);
        }
        finally
        {
            // Leave the group deliberately rather than waiting to be timed out, so
            // the partitions are reassigned immediately instead of going unread
            // for the length of the session timeout.
            consumer.Close();
        }
    }

    // Internal rather than private so the contract tests can pin the mapping from
    // a real published message onto the types this service redefined - the drift
    // those redefinitions risk is the whole reason to test them.
    internal static EventEnvelope<JobAssignedPayload>? Deserialize(string value) =>
        JsonSerializer.Deserialize<EventEnvelope<JobAssignedPayload>>(value, SerializerOptions);

    // Every field named here is one the UPDATE cannot be written without, which
    // is why an event missing any of them is discarded rather than retried.
    // JobReference is not checked: the job row already holds it, and this service
    // does not overwrite it from the event.
    internal static bool IsUsable(EventEnvelope<JobAssignedPayload>? envelope) =>
        envelope?.Payload is not null
        && !string.IsNullOrWhiteSpace(envelope.Payload.JobId)
        && !string.IsNullOrWhiteSpace(envelope.Payload.AssignmentId)
        && !string.IsNullOrWhiteSpace(envelope.Payload.TechnicianId)
        && !string.IsNullOrWhiteSpace(envelope.Payload.TechnicianReference)
        && envelope.Payload.AssignedAt != default;

    // The repository is scoped and this class is a singleton, so it cannot be
    // taken in the constructor: a scoped dependency resolved once would outlive
    // every scope and hold what it captured for the life of the process. One
    // scope per message keeps it to the lifetime it was registered with.
    private async Task ApplyAsync(JobAssignedPayload payload, ConsumeResult<string, string> message)
    {
        using var scope = _scopeFactory.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var applied = await repository.ApplyAssignmentAsync(
            payload.JobId,
            payload.AssignmentId,
            payload.TechnicianId,
            payload.TechnicianReference,
            JobAssignedPayload.AssignedStatus,
            payload.AssignedAt);

        if (applied)
        {
            _logger.LogInformation(
                "Job {JobId} is now {Status}, assigned to technician {TechnicianReference} ({TechnicianId}) "
                + "under assignment {AssignmentId}.",
                payload.JobId,
                JobAssignedPayload.AssignedStatus,
                payload.TechnicianReference,
                payload.TechnicianId,
                payload.AssignmentId);

            return;
        }

        // No row changed, and that is not an error. Either this event was already
        // applied - the expected outcome of a redelivery or a replay, and proof
        // the guard worked - or the job is not in this database at all, which
        // would mean Dispatch assigned something this service never raised. Worth
        // telling apart in the log, but neither is worth retrying: reading the
        // message again would reach the same answer.
        _logger.LogInformation(
            "Job {JobId} at {Offset} was not changed by assignment {AssignmentId}. It already carries an "
            + "assignment at or after {AssignedAt}, or no such job exists here.",
            payload.JobId,
            message.TopicPartitionOffset,
            payload.AssignmentId,
            payload.AssignedAt);
    }
}
