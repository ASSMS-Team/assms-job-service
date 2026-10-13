using JobService.Messaging.Contracts;
using JobService.Messaging.Producers;
using JobService.Models;
using JobService.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobService.Tests.UnitTests;

public class JobStatusOutboxPublisherTests
{
    [Fact]
    public async Task PublishPendingAsync_WhenKafkaAcknowledges_MarksTheOutboxEventPublished()
    {
        var repository = new FakeJobRepository();
        repository.OutboxRecords.Add(new JobStatusOutboxRecord
        {
            Id = "outbox-1",
            JobId = "job-1",
            EventType = JobStatusChangedPayload.EventType,
            Topic = JobStatusChangedPayload.Topic,
            Payload = "{\"eventId\":\"outbox-1\",\"jobId\":\"job-1\"}",
            PublishAttempts = 0
        });

        var publisher = new FakeEventPublisher();
        var worker = CreateWorker(repository, publisher);

        await worker.PublishPendingAsync();

        Assert.Equal(1, publisher.PublishRawAsyncCallCount);
        Assert.Single(publisher.PublishedRawEvents);
        Assert.Equal("job-status-changed", publisher.PublishedTopic);
        Assert.Equal("job-1", publisher.PublishedKey);
        Assert.Contains("job-1", publisher.PublishedRawPayload);

        Assert.Equal(["outbox-1"], repository.PublishedEventIds);
        Assert.Empty(repository.FailedEvents);
        Assert.NotNull(repository.OutboxRecords[0].PublishedAt);
        Assert.Equal(1, repository.OutboxRecords[0].PublishAttempts);
    }

    [Fact]
    public async Task PublishPendingAsync_WhenKafkaFails_KeepsTheOutboxEventPendingForRetry()
    {
        var repository = new FakeJobRepository();
        repository.OutboxRecords.Add(new JobStatusOutboxRecord
        {
            Id = "outbox-1",
            JobId = "job-1",
            EventType = JobStatusChangedPayload.EventType,
            Topic = JobStatusChangedPayload.Topic,
            Payload = "{\"eventId\":\"outbox-1\",\"jobId\":\"job-1\"}",
            PublishAttempts = 0
        });

        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException("Broker unavailable.")
        };
        var worker = CreateWorker(repository, publisher);

        await worker.PublishPendingAsync();

        Assert.Equal(1, publisher.PublishRawAsyncCallCount);
        Assert.Empty(repository.PublishedEventIds);
        var failed = Assert.Single(repository.FailedEvents);
        Assert.Equal("outbox-1", failed.EventId);
        Assert.Contains("Broker unavailable", failed.Error);

        Assert.Null(repository.OutboxRecords[0].PublishedAt);
        Assert.Equal(1, repository.OutboxRecords[0].PublishAttempts);
        Assert.Equal("Broker unavailable.", repository.OutboxRecords[0].LastError);
    }

    [Fact]
    public async Task PublishPendingAsync_PublishesMultiplePendingEventsInOrder()
    {
        var repository = new FakeJobRepository();
        repository.OutboxRecords.Add(new JobStatusOutboxRecord
        {
            Id = "outbox-1",
            JobId = "job-1",
            EventType = JobStatusChangedPayload.EventType,
            Topic = JobStatusChangedPayload.Topic,
            Payload = "{\"eventId\":\"outbox-1\",\"jobId\":\"job-1\",\"status\":\"IN_PROGRESS\"}",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        repository.OutboxRecords.Add(new JobStatusOutboxRecord
        {
            Id = "outbox-2",
            JobId = "job-1",
            EventType = JobStatusChangedPayload.EventType,
            Topic = JobStatusChangedPayload.Topic,
            Payload = "{\"eventId\":\"outbox-2\",\"jobId\":\"job-1\",\"status\":\"COMPLETED\"}",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var publisher = new FakeEventPublisher();
        var worker = CreateWorker(repository, publisher);

        await worker.PublishPendingAsync();

        Assert.Equal(2, publisher.PublishRawAsyncCallCount);
        Assert.Equal(["outbox-1", "outbox-2"], repository.PublishedEventIds);
        Assert.NotNull(repository.OutboxRecords[0].PublishedAt);
        Assert.NotNull(repository.OutboxRecords[1].PublishedAt);
    }

    private static JobStatusOutboxPublisher CreateWorker(IJobRepository repository, IEventPublisher publisher)
    {
        var services = new ServiceCollection()
            .AddScoped<IJobRepository>(_ => repository)
            .BuildServiceProvider();

        return new JobStatusOutboxPublisher(
            services.GetRequiredService<IServiceScopeFactory>(),
            publisher,
            NullLogger<JobStatusOutboxPublisher>.Instance);
    }
}
