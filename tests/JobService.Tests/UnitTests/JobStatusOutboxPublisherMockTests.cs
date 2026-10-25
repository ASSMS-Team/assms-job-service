using JobService.Messaging.Contracts;
using JobService.Messaging.Producers;
using JobService.Models;
using JobService.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace JobService.Tests;

public class JobStatusOutboxPublisherMockTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<IJobRepository> _repositoryMock;
    private readonly Mock<IEventPublisher> _publisherMock;
    private readonly Mock<ILogger<JobStatusOutboxPublisher>> _loggerMock;
    private readonly JobStatusOutboxPublisher _outboxPublisher;

    public JobStatusOutboxPublisherMockTests()
    {
        _scopeFactoryMock = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        _scopeMock = new Mock<IServiceScope>(MockBehavior.Strict);
        _serviceProviderMock = new Mock<IServiceProvider>(MockBehavior.Strict);
        _repositoryMock = new Mock<IJobRepository>(MockBehavior.Strict);
        _publisherMock = new Mock<IEventPublisher>(MockBehavior.Strict);
        _loggerMock = new Mock<ILogger<JobStatusOutboxPublisher>>();

        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _scopeMock.Setup(s => s.Dispose());
        _serviceProviderMock
            .Setup(p => p.GetService(typeof(IJobRepository)))
            .Returns(_repositoryMock.Object);

        _outboxPublisher = new JobStatusOutboxPublisher(
            _scopeFactoryMock.Object,
            _publisherMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task PublishPendingAsync_PendingEventsExist_PublishesRawAndMarksPublished()
    {
        // Arrange
        var pendingRecord = new JobStatusOutboxRecord
        {
            Id = "outbox-1",
            JobId = "job-1",
            Topic = "job-status-changed",
            EventType = "JobStatusChanged",
            Payload = "{\"jobId\":\"job-1\"}",
            CreatedAt = DateTime.UtcNow
        };

        _repositoryMock
            .Setup(r => r.GetPendingJobStatusOutboxEventsAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobStatusOutboxRecord> { pendingRecord });

        _publisherMock
            .Setup(p => p.PublishRawAsync(
                "job-status-changed",
                "job-1",
                "{\"jobId\":\"job-1\"}",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.MarkJobStatusOutboxPublishedAsync(
                "outbox-1",
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _outboxPublisher.PublishPendingAsync();

        // Assert
        _publisherMock.Verify(p => p.PublishRawAsync(
            "job-status-changed",
            "job-1",
            "{\"jobId\":\"job-1\"}",
            It.IsAny<CancellationToken>()),
            Times.Once);

        _repositoryMock.Verify(r => r.MarkJobStatusOutboxPublishedAsync(
            "outbox-1",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        _repositoryMock.Verify(r => r.MarkJobStatusOutboxFailedAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishPendingAsync_PublisherThrowsException_MarksOutboxFailed()
    {
        // Arrange
        var pendingRecord = new JobStatusOutboxRecord
        {
            Id = "outbox-2",
            JobId = "job-2",
            Topic = "job-status-changed",
            EventType = "JobStatusChanged",
            Payload = "{\"jobId\":\"job-2\"}",
            CreatedAt = DateTime.UtcNow
        };

        _repositoryMock
            .Setup(r => r.GetPendingJobStatusOutboxEventsAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobStatusOutboxRecord> { pendingRecord });

        _publisherMock
            .Setup(p => p.PublishRawAsync(
                "job-status-changed",
                "job-2",
                "{\"jobId\":\"job-2\"}",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker unreachable"));

        _repositoryMock
            .Setup(r => r.MarkJobStatusOutboxFailedAsync(
                "outbox-2",
                "Broker unreachable",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _outboxPublisher.PublishPendingAsync();

        // Assert
        _publisherMock.Verify(p => p.PublishRawAsync(
            "job-status-changed",
            "job-2",
            "{\"jobId\":\"job-2\"}",
            It.IsAny<CancellationToken>()),
            Times.Once);

        _repositoryMock.Verify(r => r.MarkJobStatusOutboxFailedAsync(
            "outbox-2",
            "Broker unreachable",
            It.IsAny<CancellationToken>()),
            Times.Once);

        _repositoryMock.Verify(r => r.MarkJobStatusOutboxPublishedAsync(
            It.IsAny<string>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishPendingAsync_NoPendingEvents_DoesNotPublishOrUpdate()
    {
        // Arrange
        _repositoryMock
            .Setup(r => r.GetPendingJobStatusOutboxEventsAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobStatusOutboxRecord>());

        // Act
        await _outboxPublisher.PublishPendingAsync();

        // Assert
        _publisherMock.Verify(p => p.PublishRawAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        _repositoryMock.Verify(r => r.MarkJobStatusOutboxPublishedAsync(
            It.IsAny<string>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
