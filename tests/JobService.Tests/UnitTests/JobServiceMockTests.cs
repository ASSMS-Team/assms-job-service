using JobService.DTOs;
using JobService.Messaging.Contracts;
using JobService.Messaging.Producers;
using JobService.Models;
using JobService.Repositories;
using JobService.Services;
using Microsoft.Extensions.Logging;
using Moq;
using MySqlConnector;
using Xunit;

namespace JobService.Tests;

public class JobServiceMockTests
{
    private const string CustomerId = "11111111-1111-1111-1111-111111111111";
    private const string AssetId = "22222222-2222-2222-2222-222222222222";
    private const string TechnicianId = "33333333-3333-3333-3333-333333333333";
    private const string OtherTechnicianId = "44444444-4444-4444-4444-444444444444";
    private const string JobId = "55555555-5555-5555-5555-555555555555";
    private const string JobRef = "JOB-ABC123";

    private readonly Mock<IJobRepository> _repositoryMock;
    private readonly Mock<IAssetValidationClient> _validationClientMock;
    private readonly Mock<IEventPublisher> _eventPublisherMock;
    private readonly Mock<ILogger<Services.JobService>> _loggerMock;
    private readonly Services.JobService _service;

    public JobServiceMockTests()
    {
        _repositoryMock = new Mock<IJobRepository>(MockBehavior.Strict);
        _validationClientMock = new Mock<IAssetValidationClient>(MockBehavior.Strict);
        _eventPublisherMock = new Mock<IEventPublisher>(MockBehavior.Strict);
        _loggerMock = new Mock<ILogger<Services.JobService>>();

        _service = new Services.JobService(
            _repositoryMock.Object,
            _validationClientMock.Object,
            _eventPublisherMock.Object,
            _loggerMock.Object);
    }

    private static CreateJobRequest CreateValidRequest() => new()
    {
        CustomerId = CustomerId,
        AssetId = AssetId,
        ServiceCategory = "REPAIR",
        ProblemDescription = "AC unit does not turn on.",
        Priority = "HIGH",
        Region = "WESTERN"
    };

    private static Job CreateSampleJob(string status = "CREATED", string? assignedTechnicianId = null) => new()
    {
        Id = JobId,
        JobReference = JobRef,
        CustomerId = CustomerId,
        AssetId = AssetId,
        ServiceCategory = "REPAIR",
        ProblemDescription = "AC unit does not turn on.",
        Priority = "HIGH",
        Region = "WESTERN",
        Status = status,
        CreatedBy = "00000000-0000-0000-0000-000000000000",
        AssignedTechnicianId = assignedTechnicianId,
        AssignedTechnicianReference = assignedTechnicianId != null ? "TEC-001" : null,
        AssignmentId = assignedTechnicianId != null ? Guid.NewGuid().ToString() : null,
        AssignedAt = assignedTechnicianId != null ? DateTime.UtcNow : null,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesJobPublishesEventAndReturnsSuccess()
    {
        // Arrange
        var request = CreateValidRequest();
        _validationClientMock
            .Setup(v => v.ValidateAsync(AssetId, CustomerId))
            .ReturnsAsync(AssetValidationOutcome.Valid);

        _repositoryMock
            .Setup(r => r.CreateAsync(It.Is<Job>(j =>
                j.CustomerId == CustomerId &&
                j.AssetId == AssetId &&
                j.Status == "CREATED")))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => CreateSampleJob("CREATED"));

        _eventPublisherMock
            .Setup(p => p.PublishAsync(
                JobCreatedPayload.Topic,
                JobCreatedPayload.EventType,
                JobCreatedPayload.EventVersion,
                It.IsAny<string>(),
                It.IsAny<JobCreatedPayload>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.AddStatusHistoryAsync(It.Is<JobStatusHistory>(h =>
                h.NewStatus == "CREATED" &&
                h.PreviousStatus == null)))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(JobId, result.Value.Id);
        Assert.Equal("CREATED", result.Value.Status);

        _validationClientMock.Verify(v => v.ValidateAsync(AssetId, CustomerId), Times.Once);
        _repositoryMock.Verify(r => r.CreateAsync(It.IsAny<Job>()), Times.Once);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            JobCreatedPayload.Topic,
            JobCreatedPayload.EventType,
            JobCreatedPayload.EventVersion,
            It.IsAny<string>(),
            It.Is<JobCreatedPayload>(payload => payload.CustomerId == CustomerId && payload.AssetId == AssetId)),
            Times.Once);
        _repositoryMock.Verify(r => r.AddStatusHistoryAsync(It.IsAny<JobStatusHistory>()), Times.Once);
    }

    [Theory]
    [InlineData(AssetValidationOutcome.AssetNotFound, ServiceError.AssetNotFound)]
    [InlineData(AssetValidationOutcome.CustomerMismatch, ServiceError.CustomerMismatch)]
    [InlineData(AssetValidationOutcome.AssetInactive, ServiceError.AssetInactive)]
    [InlineData(AssetValidationOutcome.CustomerInactive, ServiceError.CustomerInactive)]
    public async Task CreateAsync_ValidationFails_ReturnsExpectedFailureWithoutPersisting(
        AssetValidationOutcome outcome,
        ServiceError expectedError)
    {
        // Arrange
        var request = CreateValidRequest();
        _validationClientMock
            .Setup(v => v.ValidateAsync(AssetId, CustomerId))
            .ReturnsAsync(outcome);

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.Error);

        _validationClientMock.Verify(v => v.ValidateAsync(AssetId, CustomerId), Times.Once);
        _repositoryMock.Verify(r => r.CreateAsync(It.IsAny<Job>()), Times.Never);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
        _repositoryMock.Verify(r => r.AddStatusHistoryAsync(It.IsAny<JobStatusHistory>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_ValidationClientThrowsUnavailable_PropagatesExceptionWithoutPersisting()
    {
        // Arrange
        var request = CreateValidRequest();
        _validationClientMock
            .Setup(v => v.ValidateAsync(AssetId, CustomerId))
            .ThrowsAsync(new AssetValidationUnavailableException("Customer service down"));

        // Act & Assert
        await Assert.ThrowsAsync<AssetValidationUnavailableException>(() => _service.CreateAsync(request));

        _repositoryMock.Verify(r => r.CreateAsync(It.IsAny<Job>()), Times.Never);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAsync_DuplicateReferenceCollisionOnFirstAttempt_RetriesAndSucceeds()
    {
        // Arrange
        var request = CreateValidRequest();
        _validationClientMock
            .Setup(v => v.ValidateAsync(AssetId, CustomerId))
            .ReturnsAsync(AssetValidationOutcome.Valid);

        var attempts = 0;
        _repositoryMock
            .Setup(r => r.CreateAsync(It.IsAny<Job>()))
            .Returns(() =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw MySqlExceptions.DuplicateKey("Duplicate entry for key 'job_reference'");
                }
                return Task.CompletedTask;
            });

        _repositoryMock
            .Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync(CreateSampleJob("CREATED"));

        _eventPublisherMock
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.AddStatusHistoryAsync(It.IsAny<JobStatusHistory>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(2, attempts);
        _repositoryMock.Verify(r => r.CreateAsync(It.IsAny<Job>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateAsync_EventPublishingThrows_CatchesErrorAndStillReturnsSuccess()
    {
        // Arrange (fire-and-forget publish pattern)
        var request = CreateValidRequest();
        _validationClientMock
            .Setup(v => v.ValidateAsync(AssetId, CustomerId))
            .ReturnsAsync(AssetValidationOutcome.Valid);

        _repositoryMock
            .Setup(r => r.CreateAsync(It.IsAny<Job>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync(CreateSampleJob("CREATED"));

        _eventPublisherMock
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()))
            .ThrowsAsync(new Exception("Kafka broker connection timeout"));

        _repositoryMock
            .Setup(r => r.AddStatusHistoryAsync(It.IsAny<JobStatusHistory>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Once);
        _repositoryMock.Verify(r => r.AddStatusHistoryAsync(It.IsAny<JobStatusHistory>()), Times.Once);
    }

    #endregion

    #region GetByIdAsync and GetByReferenceAsync Tests

    [Fact]
    public async Task GetByIdAsync_JobExistsWithAssignment_ReturnsMappedResponse()
    {
        // Arrange
        var job = CreateSampleJob("ASSIGNED", TechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        // Act
        var response = await _service.GetByIdAsync(JobId);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(JobId, response.Id);
        Assert.Equal(JobRef, response.JobReference);
        Assert.NotNull(response.Assignment);
        Assert.Equal(TechnicianId, response.Assignment.TechnicianId);
        _repositoryMock.Verify(r => r.GetByIdAsync(JobId), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_JobExistsWithoutAssignment_ReturnsResponseWithNullAssignment()
    {
        // Arrange
        var job = CreateSampleJob("CREATED", assignedTechnicianId: null);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        // Act
        var response = await _service.GetByIdAsync(JobId);

        // Assert
        Assert.NotNull(response);
        Assert.Null(response.Assignment);
        _repositoryMock.Verify(r => r.GetByIdAsync(JobId), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_JobDoesNotExist_ReturnsNull()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync("nonexistent-id")).ReturnsAsync((Job?)null);

        // Act
        var response = await _service.GetByIdAsync("nonexistent-id");

        // Assert
        Assert.Null(response);
        _repositoryMock.Verify(r => r.GetByIdAsync("nonexistent-id"), Times.Once);
    }

    [Fact]
    public async Task GetByReferenceAsync_JobExists_ReturnsMappedResponse()
    {
        // Arrange
        var job = CreateSampleJob("CREATED");
        _repositoryMock.Setup(r => r.GetByReferenceAsync(JobRef)).ReturnsAsync(job);

        // Act
        var response = await _service.GetByReferenceAsync(JobRef);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(JobRef, response.JobReference);
        _repositoryMock.Verify(r => r.GetByReferenceAsync(JobRef), Times.Once);
    }

    [Fact]
    public async Task GetByReferenceAsync_JobDoesNotExist_ReturnsNull()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByReferenceAsync("JOB-NOTFND")).ReturnsAsync((Job?)null);

        // Act
        var response = await _service.GetByReferenceAsync("JOB-NOTFND");

        // Assert
        Assert.Null(response);
        _repositoryMock.Verify(r => r.GetByReferenceAsync("JOB-NOTFND"), Times.Once);
    }

    #endregion

    #region ListAsync Tests

    [Theory]
    [InlineData(null, null)]
    [InlineData("ASSIGNED", null)]
    [InlineData(null, TechnicianId)]
    [InlineData("IN_PROGRESS", TechnicianId)]
    public async Task ListAsync_GivenFilters_QueriesRepositoryAndMapsResults(string? status, string? technicianId)
    {
        // Arrange
        var jobs = new List<Job> { CreateSampleJob(status ?? "ASSIGNED", technicianId) };
        _repositoryMock.Setup(r => r.ListAsync(status, technicianId)).ReturnsAsync(jobs);

        // Act
        var results = await _service.ListAsync(status, technicianId);

        // Assert
        Assert.Single(results);
        Assert.Equal(JobId, results[0].Id);
        _repositoryMock.Verify(r => r.ListAsync(status, technicianId), Times.Once);
    }

    #endregion

    #region StartJobAsync Tests

    [Fact]
    public async Task StartJobAsync_ValidAssignedJobAndCaller_StartsJobAndPublishesOutboxEvent()
    {
        // Arrange
        var updatedJob = CreateSampleJob("IN_PROGRESS", TechnicianId);
        updatedJob.StartedAt = DateTime.UtcNow;

        _repositoryMock
            .Setup(r => r.StartJobAsync(JobId, TechnicianId, It.IsAny<DateTime>()))
            .ReturnsAsync(true);

        _repositoryMock
            .Setup(r => r.GetByIdAsync(JobId))
            .ReturnsAsync(updatedJob);

        _repositoryMock
            .Setup(r => r.InsertJobStatusOutboxAsync(It.Is<JobStatusOutboxRecord>(o =>
                o.JobId == JobId &&
                o.Topic == JobStatusChangedPayload.Topic), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _eventPublisherMock
            .Setup(p => p.PublishAsync(
                JobStatusChangedPayload.Topic,
                JobStatusChangedPayload.EventType,
                JobStatusChangedPayload.EventVersion,
                JobId,
                It.Is<JobStatusChangedPayload>(payload =>
                    payload.JobId == JobId &&
                    payload.OldStatus == "ASSIGNED" &&
                    payload.NewStatus == "IN_PROGRESS")))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.MarkJobStatusOutboxPublishedAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.AddStatusHistoryAsync(It.Is<JobStatusHistory>(h =>
                h.JobId == JobId &&
                h.PreviousStatus == "ASSIGNED" &&
                h.NewStatus == "IN_PROGRESS" &&
                h.ActorId == TechnicianId)))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.StartJobAsync(JobId, TechnicianId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("IN_PROGRESS", result.Value.Status);

        _repositoryMock.Verify(r => r.StartJobAsync(JobId, TechnicianId, It.IsAny<DateTime>()), Times.Once);
        _repositoryMock.Verify(r => r.InsertJobStatusOutboxAsync(It.IsAny<JobStatusOutboxRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            JobStatusChangedPayload.Topic,
            JobStatusChangedPayload.EventType,
            JobStatusChangedPayload.EventVersion,
            JobId,
            It.IsAny<JobStatusChangedPayload>()), Times.Once);
        _repositoryMock.Verify(r => r.MarkJobStatusOutboxPublishedAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _repositoryMock.Verify(r => r.AddStatusHistoryAsync(It.IsAny<JobStatusHistory>()), Times.Once);
    }

    [Fact]
    public async Task StartJobAsync_GuardedUpdateFails_JobNotFound_ReturnsJobNotFound()
    {
        // Arrange
        _repositoryMock
            .Setup(r => r.StartJobAsync(JobId, TechnicianId, It.IsAny<DateTime>()))
            .ReturnsAsync(false);

        _repositoryMock
            .Setup(r => r.GetByIdAsync(JobId))
            .ReturnsAsync((Job?)null);

        // Act
        var result = await _service.StartJobAsync(JobId, TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotFound, result.Error);

        _repositoryMock.Verify(r => r.InsertJobStatusOutboxAsync(It.IsAny<JobStatusOutboxRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Theory]
    [InlineData("CREATED")]
    [InlineData("IN_PROGRESS")]
    [InlineData("COMPLETED")]
    public async Task StartJobAsync_GuardedUpdateFails_WrongStatus_ReturnsNotAssigned(string currentStatus)
    {
        // Arrange
        var existingJob = CreateSampleJob(currentStatus, TechnicianId);

        _repositoryMock
            .Setup(r => r.StartJobAsync(JobId, TechnicianId, It.IsAny<DateTime>()))
            .ReturnsAsync(false);

        _repositoryMock
            .Setup(r => r.GetByIdAsync(JobId))
            .ReturnsAsync(existingJob);

        // Act
        var result = await _service.StartJobAsync(JobId, TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotAssigned, result.Error);

        _repositoryMock.Verify(r => r.InsertJobStatusOutboxAsync(It.IsAny<JobStatusOutboxRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task StartJobAsync_GuardedUpdateFails_CallerIsNotAssignee_ReturnsNotTheAssignee()
    {
        // Arrange
        var existingJob = CreateSampleJob("ASSIGNED", OtherTechnicianId);

        _repositoryMock
            .Setup(r => r.StartJobAsync(JobId, TechnicianId, It.IsAny<DateTime>()))
            .ReturnsAsync(false);

        _repositoryMock
            .Setup(r => r.GetByIdAsync(JobId))
            .ReturnsAsync(existingJob);

        // Act
        var result = await _service.StartJobAsync(JobId, TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotTheAssignee, result.Error);

        _repositoryMock.Verify(r => r.InsertJobStatusOutboxAsync(It.IsAny<JobStatusOutboxRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task StartJobAsync_EventPublishThrows_MarksOutboxFailedAndStillReturnsSuccess()
    {
        // Arrange
        var updatedJob = CreateSampleJob("IN_PROGRESS", TechnicianId);
        updatedJob.StartedAt = DateTime.UtcNow;

        _repositoryMock
            .Setup(r => r.StartJobAsync(JobId, TechnicianId, It.IsAny<DateTime>()))
            .ReturnsAsync(true);

        _repositoryMock
            .Setup(r => r.GetByIdAsync(JobId))
            .ReturnsAsync(updatedJob);

        _repositoryMock
            .Setup(r => r.InsertJobStatusOutboxAsync(It.IsAny<JobStatusOutboxRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _eventPublisherMock
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()))
            .ThrowsAsync(new Exception("Kafka publish failed"));

        _repositoryMock
            .Setup(r => r.MarkJobStatusOutboxFailedAsync(It.IsAny<string>(), "Kafka publish failed", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.AddStatusHistoryAsync(It.IsAny<JobStatusHistory>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.StartJobAsync(JobId, TechnicianId);

        // Assert
        Assert.True(result.IsSuccess);
        _repositoryMock.Verify(r => r.MarkJobStatusOutboxFailedAsync(It.IsAny<string>(), "Kafka publish failed", It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region CompleteJobAsync Tests

    [Fact]
    public async Task CompleteJobAsync_ValidConditions_CompletesJobAndPublishesOutboxEvent()
    {
        // Arrange
        var inProgressJob = CreateSampleJob("IN_PROGRESS", TechnicianId);
        var completedJob = CreateSampleJob("COMPLETED", TechnicianId);
        completedJob.CompletedAt = DateTime.UtcNow;

        var workRecords = new List<ServiceWorkRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                JobId = JobId,
                Content = "Replaced faulty capacitor and tested voltage.",
                TechnicianId = TechnicianId
            }
        };

        _repositoryMock
            .SetupSequence(r => r.GetByIdAsync(JobId))
            .ReturnsAsync(inProgressJob)
            .ReturnsAsync(completedJob);

        _repositoryMock
            .Setup(r => r.GetWorkRecordsByJobIdAsync(JobId))
            .ReturnsAsync(workRecords);

        _repositoryMock
            .Setup(r => r.CompleteJobAsync(JobId, TechnicianId, It.IsAny<DateTime>()))
            .ReturnsAsync(true);

        _repositoryMock
            .Setup(r => r.InsertJobStatusOutboxAsync(It.IsAny<JobStatusOutboxRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _eventPublisherMock
            .Setup(p => p.PublishAsync(
                JobStatusChangedPayload.Topic,
                JobStatusChangedPayload.EventType,
                JobStatusChangedPayload.EventVersion,
                JobId,
                It.Is<JobStatusChangedPayload>(payload =>
                    payload.JobId == JobId &&
                    payload.OldStatus == "IN_PROGRESS" &&
                    payload.NewStatus == "COMPLETED")))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.MarkJobStatusOutboxPublishedAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(r => r.AddStatusHistoryAsync(It.Is<JobStatusHistory>(h =>
                h.JobId == JobId &&
                h.PreviousStatus == "IN_PROGRESS" &&
                h.NewStatus == "COMPLETED" &&
                h.ActorId == TechnicianId)))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CompleteJobAsync(JobId, TechnicianId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("COMPLETED", result.Value.Status);

        _repositoryMock.Verify(r => r.CompleteJobAsync(JobId, TechnicianId, It.IsAny<DateTime>()), Times.Once);
        _repositoryMock.Verify(r => r.InsertJobStatusOutboxAsync(It.IsAny<JobStatusOutboxRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            JobStatusChangedPayload.Topic,
            JobStatusChangedPayload.EventType,
            JobStatusChangedPayload.EventVersion,
            JobId,
            It.IsAny<JobStatusChangedPayload>()), Times.Once);
        _repositoryMock.Verify(r => r.MarkJobStatusOutboxPublishedAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteJobAsync_JobNotFound_ReturnsJobNotFound()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync((Job?)null);

        // Act
        var result = await _service.CompleteJobAsync(JobId, TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotFound, result.Error);
        _repositoryMock.Verify(r => r.CompleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Theory]
    [InlineData("CREATED")]
    [InlineData("ASSIGNED")]
    [InlineData("COMPLETED")]
    public async Task CompleteJobAsync_JobNotInProgress_ReturnsJobNotInProgress(string invalidStatus)
    {
        // Arrange
        var job = CreateSampleJob(invalidStatus, TechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        // Act
        var result = await _service.CompleteJobAsync(JobId, TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotInProgress, result.Error);
        _repositoryMock.Verify(r => r.CompleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task CompleteJobAsync_CallerIsNotAssignee_ReturnsNotTheAssignee()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", OtherTechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        // Act
        var result = await _service.CompleteJobAsync(JobId, TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotTheAssignee, result.Error);
        _repositoryMock.Verify(r => r.CompleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task CompleteJobAsync_ZeroWorkRecords_ReturnsWorkRecordsRequired()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);
        _repositoryMock.Setup(r => r.GetWorkRecordsByJobIdAsync(JobId)).ReturnsAsync(new List<ServiceWorkRecord>());

        // Act
        var result = await _service.CompleteJobAsync(JobId, TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.WorkRecordsRequired, result.Error);
        _repositoryMock.Verify(r => r.CompleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
        _eventPublisherMock.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task CompleteJobAsync_CompleteRepoUpdateFails_ReturnsJobNotInProgress()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        var workRecords = new List<ServiceWorkRecord>
        {
            new() { Id = Guid.NewGuid().ToString(), JobId = JobId, Content = "Fixed", TechnicianId = TechnicianId }
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);
        _repositoryMock.Setup(r => r.GetWorkRecordsByJobIdAsync(JobId)).ReturnsAsync(workRecords);
        _repositoryMock.Setup(r => r.CompleteJobAsync(JobId, TechnicianId, It.IsAny<DateTime>())).ReturnsAsync(false);

        // Act
        var result = await _service.CompleteJobAsync(JobId, TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotInProgress, result.Error);
        _repositoryMock.Verify(r => r.InsertJobStatusOutboxAsync(It.IsAny<JobStatusOutboxRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region AddWorkRecordAsync Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task AddWorkRecordAsync_MissingOrWhitespaceContent_ReturnsMissingContentWithoutQueryingDb(string? content)
    {
        // Act
        var result = await _service.AddWorkRecordAsync(JobId, TechnicianId, content!);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.MissingContent, result.Error);

        _repositoryMock.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
        _repositoryMock.Verify(r => r.AddWorkRecordAsync(It.IsAny<ServiceWorkRecord>()), Times.Never);
    }

    [Fact]
    public async Task AddWorkRecordAsync_ValidRequest_PersistsTrimmedContentAndReturnsSuccess()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        var persistedRecord = new ServiceWorkRecord
        {
            Id = Guid.NewGuid().ToString(),
            JobId = JobId,
            JobReference = JobRef,
            TechnicianId = TechnicianId,
            TechnicianReference = "TEC-001",
            Content = "Cleaned the outdoor condenser fan blades.",
            RecordedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _repositoryMock
            .Setup(r => r.AddWorkRecordAsync(It.Is<ServiceWorkRecord>(w =>
                w.JobId == JobId &&
                w.TechnicianId == TechnicianId &&
                w.Content == "Cleaned the outdoor condenser fan blades.")))
            .ReturnsAsync(persistedRecord);

        // Act
        var result = await _service.AddWorkRecordAsync(JobId, TechnicianId, "   Cleaned the outdoor condenser fan blades.   ");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Cleaned the outdoor condenser fan blades.", result.Value.Content);
        Assert.Equal(JobId, result.Value.JobId);

        _repositoryMock.Verify(r => r.GetByIdAsync(JobId), Times.Once);
        _repositoryMock.Verify(r => r.AddWorkRecordAsync(It.IsAny<ServiceWorkRecord>()), Times.Once);
    }

    [Fact]
    public async Task AddWorkRecordAsync_JobNotFound_ReturnsJobNotFound()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync((Job?)null);

        // Act
        var result = await _service.AddWorkRecordAsync(JobId, TechnicianId, "Some work note");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotFound, result.Error);
        _repositoryMock.Verify(r => r.AddWorkRecordAsync(It.IsAny<ServiceWorkRecord>()), Times.Never);
    }

    [Theory]
    [InlineData("CREATED")]
    [InlineData("ASSIGNED")]
    [InlineData("COMPLETED")]
    public async Task AddWorkRecordAsync_JobNotInProgress_ReturnsJobNotInProgress(string invalidStatus)
    {
        // Arrange
        var job = CreateSampleJob(invalidStatus, TechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        // Act
        var result = await _service.AddWorkRecordAsync(JobId, TechnicianId, "Some work note");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotInProgress, result.Error);
        _repositoryMock.Verify(r => r.AddWorkRecordAsync(It.IsAny<ServiceWorkRecord>()), Times.Never);
    }

    [Fact]
    public async Task AddWorkRecordAsync_CallerIsNotAssignee_ReturnsNotTheAssignee()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", OtherTechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        // Act
        var result = await _service.AddWorkRecordAsync(JobId, TechnicianId, "Some work note");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotTheAssignee, result.Error);
        _repositoryMock.Verify(r => r.AddWorkRecordAsync(It.IsAny<ServiceWorkRecord>()), Times.Never);
    }

    #endregion

    #region GetWorkRecordsAsync Tests

    [Fact]
    public async Task GetWorkRecordsAsync_JobExists_ReturnsMappedRecords()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        var record = new ServiceWorkRecord
        {
            Id = "rec-1",
            JobId = JobId,
            JobReference = JobRef,
            TechnicianId = TechnicianId,
            TechnicianReference = "TEC-001",
            Content = "Inspected filter",
            RecordedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);
        _repositoryMock.Setup(r => r.GetWorkRecordsByJobIdAsync(JobId)).ReturnsAsync(new List<ServiceWorkRecord> { record });

        // Act
        var results = await _service.GetWorkRecordsAsync(JobId);

        // Assert
        Assert.NotNull(results);
        Assert.Single(results);
        Assert.Equal("rec-1", results[0].Id);
        Assert.Equal("Inspected filter", results[0].Content);
        _repositoryMock.Verify(r => r.GetByIdAsync(JobId), Times.Once);
        _repositoryMock.Verify(r => r.GetWorkRecordsByJobIdAsync(JobId), Times.Once);
    }

    [Fact]
    public async Task GetWorkRecordsAsync_JobDoesNotExist_ReturnsNull()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync((Job?)null);

        // Act
        var results = await _service.GetWorkRecordsAsync(JobId);

        // Assert
        Assert.Null(results);
        _repositoryMock.Verify(r => r.GetByIdAsync(JobId), Times.Once);
        _repositoryMock.Verify(r => r.GetWorkRecordsByJobIdAsync(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region UpdateWorkRecordAsync Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateWorkRecordAsync_MissingOrWhitespaceContent_ReturnsMissingContentWithoutDbQuery(string? content)
    {
        // Act
        var result = await _service.UpdateWorkRecordAsync(JobId, "rec-1", TechnicianId, content!);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.MissingContent, result.Error);
        _repositoryMock.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateWorkRecordAsync_ValidRequest_UpdatesAndReturnsSuccess()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        var updatedRecord = new ServiceWorkRecord
        {
            Id = "rec-1",
            JobId = JobId,
            JobReference = JobRef,
            TechnicianId = TechnicianId,
            TechnicianReference = "TEC-001",
            Content = "Updated content",
            RecordedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);
        _repositoryMock.Setup(r => r.UpdateWorkRecordAsync("rec-1", JobId, "Updated content")).ReturnsAsync(true);
        _repositoryMock.Setup(r => r.GetWorkRecordsByJobIdAsync(JobId)).ReturnsAsync(new List<ServiceWorkRecord> { updatedRecord });

        // Act
        var result = await _service.UpdateWorkRecordAsync(JobId, "rec-1", TechnicianId, "   Updated content   ");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Updated content", result.Value.Content);
        _repositoryMock.Verify(r => r.UpdateWorkRecordAsync("rec-1", JobId, "Updated content"), Times.Once);
    }

    [Fact]
    public async Task UpdateWorkRecordAsync_JobNotFound_ReturnsJobNotFound()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync((Job?)null);

        // Act
        var result = await _service.UpdateWorkRecordAsync(JobId, "rec-1", TechnicianId, "Updated");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotFound, result.Error);
        _repositoryMock.Verify(r => r.UpdateWorkRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateWorkRecordAsync_CallerIsNotAssignee_ReturnsNotTheAssignee()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", OtherTechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        // Act
        var result = await _service.UpdateWorkRecordAsync(JobId, "rec-1", TechnicianId, "Updated");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotTheAssignee, result.Error);
        _repositoryMock.Verify(r => r.UpdateWorkRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateWorkRecordAsync_RepositoryUpdateFails_ReturnsWorkRecordNotFound()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);
        _repositoryMock.Setup(r => r.UpdateWorkRecordAsync("rec-1", JobId, "Updated")).ReturnsAsync(false);

        // Act
        var result = await _service.UpdateWorkRecordAsync(JobId, "rec-1", TechnicianId, "Updated");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.WorkRecordNotFound, result.Error);
    }

    #endregion

    #region DeleteWorkRecordAsync Tests

    [Fact]
    public async Task DeleteWorkRecordAsync_ValidConditions_DeletesAndReturnsSuccess()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);
        _repositoryMock.Setup(r => r.DeleteWorkRecordAsync("rec-1", JobId)).ReturnsAsync(true);

        // Act
        var result = await _service.DeleteWorkRecordAsync(JobId, "rec-1", TechnicianId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        _repositoryMock.Verify(r => r.DeleteWorkRecordAsync("rec-1", JobId), Times.Once);
    }

    [Fact]
    public async Task DeleteWorkRecordAsync_JobNotFound_ReturnsJobNotFound()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync((Job?)null);

        // Act
        var result = await _service.DeleteWorkRecordAsync(JobId, "rec-1", TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotFound, result.Error);
        _repositoryMock.Verify(r => r.DeleteWorkRecordAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteWorkRecordAsync_CallerIsNotAssignee_ReturnsNotTheAssignee()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", OtherTechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);

        // Act
        var result = await _service.DeleteWorkRecordAsync(JobId, "rec-1", TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotTheAssignee, result.Error);
        _repositoryMock.Verify(r => r.DeleteWorkRecordAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteWorkRecordAsync_RepositoryDeleteReturnsFalse_ReturnsWorkRecordNotFound()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);
        _repositoryMock.Setup(r => r.DeleteWorkRecordAsync("rec-1", JobId)).ReturnsAsync(false);

        // Act
        var result = await _service.DeleteWorkRecordAsync(JobId, "rec-1", TechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.WorkRecordNotFound, result.Error);
    }

    #endregion

    #region GetStatusHistoryAsync Tests

    [Fact]
    public async Task GetStatusHistoryAsync_JobExists_ReturnsMappedHistoryEntries()
    {
        // Arrange
        var job = CreateSampleJob("IN_PROGRESS", TechnicianId);
        var history = new List<JobStatusHistory>
        {
            new()
            {
                Id = "hist-1",
                JobId = JobId,
                PreviousStatus = null,
                NewStatus = "CREATED",
                ActorId = "00000000-0000-0000-0000-000000000000",
                CreatedAt = DateTime.UtcNow.AddHours(-2)
            },
            new()
            {
                Id = "hist-2",
                JobId = JobId,
                PreviousStatus = "ASSIGNED",
                NewStatus = "IN_PROGRESS",
                ActorId = TechnicianId,
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            }
        };

        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync(job);
        _repositoryMock.Setup(r => r.GetStatusHistoryAsync(JobId)).ReturnsAsync(history);

        // Act
        var result = await _service.GetStatusHistoryAsync(JobId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("CREATED", result[0].NewStatus);
        Assert.Equal("IN_PROGRESS", result[1].NewStatus);

        _repositoryMock.Verify(r => r.GetByIdAsync(JobId), Times.Once);
        _repositoryMock.Verify(r => r.GetStatusHistoryAsync(JobId), Times.Once);
    }

    [Fact]
    public async Task GetStatusHistoryAsync_JobDoesNotExist_ReturnsNull()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync((Job?)null);

        // Act
        var result = await _service.GetStatusHistoryAsync(JobId);

        // Assert
        Assert.Null(result);
        _repositoryMock.Verify(r => r.GetByIdAsync(JobId), Times.Once);
        _repositoryMock.Verify(r => r.GetStatusHistoryAsync(It.IsAny<string>()), Times.Never);
    }

    #endregion
}
