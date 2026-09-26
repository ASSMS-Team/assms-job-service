using JobService.DTOs;
using JobService.Messaging.Contracts;
using JobService.Models;
using JobService.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace JobService.Tests;

public class JobServiceTests
{
    private const string CustomerId = "11111111-1111-1111-1111-111111111111";
    private const string AssetId = "22222222-2222-2222-2222-222222222222";
    private const string UnattributedCreatedBy = "00000000-0000-0000-0000-000000000000";

    private static CreateJobRequest ValidRequest() => new()
    {
        CustomerId = CustomerId,
        AssetId = AssetId,
        ServiceCategory = "REPAIR",
        ProblemDescription = "Not cooling and trips the breaker after ten minutes.",
        Priority = "HIGH",
        Region = "WESTERN",
    };

    // Builds the service with fakes the caller can still configure, so each
    // test changes only the one thing it is about.
    private static Services.JobService BuildService(
        FakeJobRepository repository,
        FakeAssetValidationClient validationClient,
        FakeEventPublisher eventPublisher) =>
        new(repository, validationClient, eventPublisher, NullLogger<Services.JobService>.Instance);

    private static Services.JobService BuildService(FakeJobRepository repository) =>
        BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());


    [Fact]
    public async Task CreateAsync_WithValidRequest_CreatesJob()
    {
        // Arrange - the validation client defaults to Valid and the repository
        // to no collisions, so the happy path needs no configuration.
        var repository = new FakeJobRepository();
        var validationClient = new FakeAssetValidationClient();
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act
        var result = await service.CreateAsync(ValidRequest());

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(ServiceError.None, result.Error);

        var written = Assert.IsType<Job>(repository.CreatedJob);
        // The id is the server's, not the client's - the request has no id field
        // at all, so a real GUID here is the only acceptable outcome.
        Assert.True(Guid.TryParse(written.Id, out _));
        Assert.Equal(CustomerId, written.CustomerId);
        Assert.Equal(AssetId, written.AssetId);
        Assert.Equal("REPAIR", written.ServiceCategory);
        Assert.Equal("Not cooling and trips the breaker after ten minutes.", written.ProblemDescription);
        Assert.Equal("HIGH", written.Priority);
        Assert.Equal("WESTERN", written.Region);
        Assert.Equal("CREATED", written.Status);
        // Nothing schedules a job at creation.
        Assert.Null(written.ScheduledDate);
        // No authentication yet, so the audit column takes the placeholder
        // rather than an empty string the driver could not read back.
        Assert.Equal(UnattributedCreatedBy, written.CreatedBy);

        // Generated, not supplied, and in the documented shape.
        Assert.StartsWith("JOB-", written.JobReference, StringComparison.Ordinal);
        Assert.Equal(10, written.JobReference.Length);

        // The response carries the id the service generated, so the controller's
        // Location header points at a row that exists.
        Assert.Equal(written.Id, result.Value!.Id);
        Assert.Equal(written.JobReference, result.Value.JobReference);
    }

    [Fact]
    public async Task CreateAsync_ValidatesTheAssetAgainstTheCustomer_BeforeWriting()
    {
        // Arrange
        var repository = new FakeJobRepository();
        var validationClient = new FakeAssetValidationClient();
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act
        await service.CreateAsync(ValidRequest());

        // Assert - both ids go to the customer service, in that order: the
        // question is whether this asset belongs to this customer, and asking
        // it with one of the two would answer something else.
        Assert.Equal(1, validationClient.ValidateAsyncCallCount);
        Assert.Equal(AssetId, validationClient.ValidatedAssetId);
        Assert.Equal(CustomerId, validationClient.ValidatedCustomerId);
    }

    [Theory]
    [InlineData(AssetValidationOutcome.AssetNotFound, ServiceError.AssetNotFound)]
    [InlineData(AssetValidationOutcome.CustomerMismatch, ServiceError.CustomerMismatch)]
    [InlineData(AssetValidationOutcome.AssetInactive, ServiceError.AssetInactive)]
    [InlineData(AssetValidationOutcome.CustomerInactive, ServiceError.CustomerInactive)]
    public async Task CreateAsync_WithFailedValidation_ReturnsTheMatchingError(
        AssetValidationOutcome outcome,
        ServiceError expected)
    {
        // Arrange
        var repository = new FakeJobRepository();
        var validationClient = new FakeAssetValidationClient { OutcomeToReturn = outcome };
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act
        var result = await service.CreateAsync(ValidRequest());

        // Assert - the four refusals stay distinct all the way out of the
        // service, because the controller renders a different message for each.
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error);
        Assert.Null(result.Value);

        // Nothing is written and nothing is published for a job that was never
        // created - a refused request must leave no trace.
        Assert.Equal(0, repository.CreateAsyncCallCount);
        Assert.Equal(0, publisher.PublishAsyncCallCount);
    }

    [Fact]
    public async Task CreateAsync_WhenTheReferenceCollides_RetriesWithAFreshOne()
    {
        // Arrange - the first insert is rejected by the unique index, exactly as
        // it would be if the drawn reference were already taken.
        var repository = new FakeJobRepository { DuplicateKeyFailuresBeforeSuccess = 1 };
        var validationClient = new FakeAssetValidationClient();
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act
        var result = await service.CreateAsync(ValidRequest());

        // Assert - the collision is absorbed, not surfaced.
        Assert.True(result.IsSuccess);
        Assert.Equal(2, repository.CreateAsyncCallCount);

        // The retry draws a new reference rather than re-sending the one that
        // just collided, which would collide again forever.
        Assert.Equal(2, repository.AttemptedReferences.Count);
        Assert.NotEqual(repository.AttemptedReferences[0], repository.AttemptedReferences[1]);

        // What the caller gets back is the reference that actually went in.
        Assert.Equal(repository.AttemptedReferences[1], result.Value!.JobReference);
    }

    [Fact]
    public async Task CreateAsync_WhenTheReferenceCollidesTwice_StillSucceedsOnTheThirdAttempt()
    {
        // Arrange - two collisions is the most the three-attempt budget absorbs.
        var repository = new FakeJobRepository { DuplicateKeyFailuresBeforeSuccess = 2 };
        var validationClient = new FakeAssetValidationClient();
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act
        var result = await service.CreateAsync(ValidRequest());

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(3, repository.CreateAsyncCallCount);
        Assert.Equal(3, repository.AttemptedReferences.Distinct().Count());
    }

    [Fact]
    public async Task CreateAsync_WhenEveryAttemptCollides_LetsTheFailureSurface()
    {
        // Arrange - three collisions exhausts the budget. If three independently
        // drawn references all collide, the generator is wrong, and turning that
        // into a business error would hide it.
        var repository = new FakeJobRepository { DuplicateKeyFailuresBeforeSuccess = 3 };
        var validationClient = new FakeAssetValidationClient();
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act + Assert
        await Assert.ThrowsAsync<MySqlConnector.MySqlException>(
            () => service.CreateAsync(ValidRequest()));

        Assert.Equal(3, repository.CreateAsyncCallCount);
        Assert.Equal(0, publisher.PublishAsyncCallCount);
    }

    [Fact]
    public async Task CreateAsync_PublishesJobCreated_KeyedOnTheJobId()
    {
        // Arrange - the read-back returns a row with database timestamps, which
        // is what the event should carry rather than default(DateTime).
        var repository = new FakeJobRepository();
        var validationClient = new FakeAssetValidationClient();
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act
        var result = await service.CreateAsync(ValidRequest());

        // Assert
        Assert.Equal(1, publisher.PublishAsyncCallCount);
        Assert.Equal("job-created", publisher.PublishedTopic);
        Assert.Equal("JobCreated", publisher.PublishedEventType);
        Assert.Equal(1, publisher.PublishedEventVersion);

        // Keyed on the job id so every event about this job lands on one
        // partition, in order - that is what stops Dispatch seeing an
        // assignment before the creation it belongs to.
        Assert.Equal(result.Value!.Id, publisher.PublishedKey);

        var payload = Assert.IsType<JobCreatedPayload>(publisher.PublishedPayload);
        Assert.Equal(result.Value.Id, payload.JobId);
        Assert.Equal(result.Value.JobReference, payload.JobReference);
        Assert.Equal(CustomerId, payload.CustomerId);
        Assert.Equal(AssetId, payload.AssetId);
        Assert.Equal("REPAIR", payload.ServiceCategory);
        Assert.Equal("HIGH", payload.Priority);
        // What Dispatch filters on in Sprint 2.
        Assert.Equal("WESTERN", payload.Region);
        Assert.Equal("CREATED", payload.Status);
    }

    [Fact]
    public async Task CreateAsync_WhenPublishingFails_StillReturnsTheCreatedJob()
    {
        // The job is already committed by the time the event is published, so a
        // broker that is down must not turn a successful creation into a failed
        // request. This is the criterion the whole try/catch exists for.

        // Arrange
        var repository = new FakeJobRepository();
        var validationClient = new FakeAssetValidationClient();
        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException("Broker unavailable.")
        };
        var service = BuildService(repository, validationClient, publisher);

        // Act
        var result = await service.CreateAsync(ValidRequest());

        // Assert - the request succeeds and the caller gets the job back.
        Assert.True(result.IsSuccess);
        Assert.Equal(ServiceError.None, result.Error);
        Assert.NotNull(result.Value);

        // The row is still there: the failure happened after the insert, and
        // nothing rolls it back.
        Assert.Equal(1, repository.CreateAsyncCallCount);
        Assert.Equal(result.Value!.Id, repository.CreatedJob!.Id);
        Assert.Equal(result.Value.JobReference, repository.CreatedJob.JobReference);

        // Publishing was attempted, not skipped - the event is lost, which is a
        // problem to find in the log, not one to hide by never trying.
        Assert.Equal(1, publisher.PublishAsyncCallCount);
        Assert.Equal("job-created", publisher.PublishedTopic);
    }

    [Fact]
    public async Task CreateAsync_ReadsTheRowBack_SoDatabaseDefaultsAreReturned()
    {
        // Arrange - status, created_at and updated_at are written by the column
        // defaults, so the values the caller sees have to come from a read.
        var stored = new Job
        {
            Id = "33333333-3333-3333-3333-333333333333",
            JobReference = "JOB-7K2M9X",
            CustomerId = CustomerId,
            AssetId = AssetId,
            ServiceCategory = "REPAIR",
            ProblemDescription = "Not cooling and trips the breaker after ten minutes.",
            Priority = "HIGH",
            Region = "WESTERN",
            ScheduledDate = null,
            CreatedBy = UnattributedCreatedBy,
            Status = "CREATED",
            CreatedAt = new DateTime(2026, 8, 29, 9, 14, 32, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 29, 9, 14, 32, DateTimeKind.Utc),
        };

        var repository = new FakeJobRepository { JobToReturn = stored };
        var validationClient = new FakeAssetValidationClient();
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act
        var result = await service.CreateAsync(ValidRequest());

        // Assert - the read-back is by the id the service generated, and its
        // timestamps are what comes back rather than default(DateTime).
        Assert.Equal(repository.CreatedJob!.Id, repository.GetByIdId);
        Assert.Equal(stored.CreatedAt, result.Value!.CreatedAt);
        Assert.Equal(stored.UpdatedAt, result.Value.UpdatedAt);

        // And the event carries the database timestamp too, not an in-memory zero.
        var payload = Assert.IsType<JobCreatedPayload>(publisher.PublishedPayload);
        Assert.Equal(stored.CreatedAt, payload.CreatedAt);
    }

    [Fact]
    public async Task CreateAsync_WhenValidationIsUnavailable_LetsTheFailureSurface()
    {
        // Arrange - the customer service not answering is not a verdict on the
        // request, so it must not be turned into a business refusal.
        var repository = new FakeJobRepository();
        var validationClient = new FakeAssetValidationClient
        {
            ExceptionToThrow = new AssetValidationUnavailableException(
                "The Customer & Asset Service could not be reached.")
        };
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, validationClient, publisher);

        // Act + Assert - it travels up to the controller, which renders 503.
        await Assert.ThrowsAsync<AssetValidationUnavailableException>(
            () => service.CreateAsync(ValidRequest()));

        Assert.Equal(0, repository.CreateAsyncCallCount);
        Assert.Equal(0, publisher.PublishAsyncCallCount);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsTheJob_OrNullWhenThereIsNone()
    {
        var repository = new FakeJobRepository();
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        Assert.Null(await service.GetByIdAsync("44444444-4444-4444-4444-444444444444"));
        Assert.Equal("44444444-4444-4444-4444-444444444444", repository.GetByIdId);

        repository.JobToReturn = new Job { Id = "55555555-5555-5555-5555-555555555555", JobReference = "JOB-7K2M9X" };

        var found = await service.GetByIdAsync("55555555-5555-5555-5555-555555555555");

        Assert.Equal("JOB-7K2M9X", found!.JobReference);
    }

    [Fact]
    public async Task GetByReferenceAsync_LooksUpByTheReference_NotTheId()
    {
        var repository = new FakeJobRepository();
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        Assert.Null(await service.GetByReferenceAsync("JOB-7K2M9X"));
        // The reference goes to the reference lookup, which is a different query
        // from the id one - the two are not interchangeable.
        Assert.Equal("JOB-7K2M9X", repository.GetByReferenceJobReference);
        Assert.Null(repository.GetByIdId);

        repository.JobToReturn = new Job { Id = "55555555-5555-5555-5555-555555555555", JobReference = "JOB-7K2M9X" };

        var found = await service.GetByReferenceAsync("JOB-7K2M9X");

        Assert.Equal("55555555-5555-5555-5555-555555555555", found!.Id);
    }

    [Fact]
    public async Task ListAsync_MapsTheLocalAssignmentProjection_AndPassesBothFilters()
    {
        var technicianId = "66666666-6666-6666-6666-666666666666";
        var repository = new FakeJobRepository
        {
            JobsToReturn = new[]
            {
                new Job
                {
                    Id = "55555555-5555-5555-5555-555555555555",
                    JobReference = "JOB-7K2M9X",
                    Priority = "HIGH",
                    Status = "ASSIGNED",
                    AssignmentId = "77777777-7777-7777-7777-777777777777",
                    AssignedTechnicianId = technicianId,
                    AssignedTechnicianReference = "TEC-032",
                    AssignedAt = new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc)
                }
            }
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        var jobs = await service.ListAsync("ASSIGNED", technicianId);

        Assert.Equal("ASSIGNED", repository.ListStatus);
        Assert.Equal(technicianId, repository.ListAssignedTechnicianId);
        var job = Assert.Single(jobs);
        Assert.Equal("JOB-7K2M9X", job.JobReference);
        Assert.Equal("ASSIGNED", job.Status);
        Assert.NotNull(job.Assignment);
        Assert.Equal("TEC-032", job.Assignment!.TechnicianReference);
    }

    [Fact]
    public async Task ListAsync_LeavesAssignmentNull_ForAnUnassignedJob()
    {
        var repository = new FakeJobRepository
        {
            JobsToReturn = new[] { new Job { Id = "55555555-5555-5555-5555-555555555555", JobReference = "JOB-7K2M9X", Status = "CREATED" } }
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        var job = Assert.Single(await service.ListAsync(null, null));

        Assert.Null(job.Assignment);
    }

    // -----------------------------------------------------------------------
    // StartJobAsync (US-10A)
    // -----------------------------------------------------------------------

    private const string AssignedJobId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string ActiveTechnicianId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string OtherTechnicianId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string AssignmentId = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    // The fully populated ASSIGNED job that represents the happy-path state
    // before the start call.
    private static Job AssignedJob() => new()
    {
        Id = AssignedJobId,
        JobReference = "JOB-START1",
        CustomerId = CustomerId,
        AssetId = AssetId,
        Status = "ASSIGNED",
        AssignmentId = AssignmentId,
        AssignedTechnicianId = ActiveTechnicianId,
        AssignedTechnicianReference = "TEC-099",
        AssignedAt = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc)
    };

    // The row as it reads back after the update: status moved, started_at set.
    private static Job InProgressJob(DateTime startedAt) => new()
    {
        Id = AssignedJobId,
        JobReference = "JOB-START1",
        CustomerId = CustomerId,
        AssetId = AssetId,
        Status = "IN_PROGRESS",
        AssignmentId = AssignmentId,
        AssignedTechnicianId = ActiveTechnicianId,
        AssignedTechnicianReference = "TEC-099",
        AssignedAt = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc),
        StartedAt = startedAt,
        CreatedAt = new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc),
        UpdatedAt = startedAt
    };

    // AC1: The active assignee can move an ASSIGNED job to IN_PROGRESS.
    [Fact]
    public async Task StartJobAsync_ActiveAssignee_TransitionsJobToInProgress()
    {
        // Arrange - repository signals the UPDATE matched (row changed).
        var startedAt = new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc);
        var repository = new FakeJobRepository
        {
            StartJobAsyncResult = true,
            // Read-back returns the updated row.
            JobToReturn = InProgressJob(startedAt)
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        // Act
        var result = await service.StartJobAsync(AssignedJobId, ActiveTechnicianId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(ServiceError.None, result.Error);
        Assert.Equal("IN_PROGRESS", result.Value!.Status);

        // The repository received the right arguments.
        Assert.Equal(AssignedJobId, repository.StartJobAsyncCalledWithJobId);
        Assert.Equal(ActiveTechnicianId, repository.StartJobAsyncCalledWithTechnicianId);
        Assert.NotNull(repository.StartJobAsyncCalledWithStartedAt);
    }

    // AC2: The start timestamp and status-history record are persisted.
    // (The timestamp is what this service can observe; the status-history record
    // is verified by the repository returning it on read-back.)
    [Fact]
    public async Task StartJobAsync_ReadsTheRowBack_SoStartedAtIsReturnedFromTheDatabase()
    {
        // Arrange - the read-back carries a specific started_at from the DB.
        var dbStartedAt = new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc);
        var repository = new FakeJobRepository
        {
            StartJobAsyncResult = true,
            JobToReturn = InProgressJob(dbStartedAt)
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        // Act
        var result = await service.StartJobAsync(AssignedJobId, ActiveTechnicianId);

        // Assert - started_at comes from the read-back, not from an in-memory value.
        Assert.Equal(dbStartedAt, result.Value!.StartedAt);
        // The read-back is by the job id the service was given.
        Assert.Equal(AssignedJobId, repository.GetByIdId);
    }

    // AC3: A Technician who is not the active assignee is forbidden.
    [Fact]
    public async Task StartJobAsync_NonAssigneeCaller_ReturnsNotTheAssignee()
    {
        // Arrange - the guarded UPDATE returns false because the WHERE clause
        // filtered out the non-matching assigned_technician_id.
        var repository = new FakeJobRepository
        {
            StartJobAsyncResult = false,
            // The job exists and is ASSIGNED, so the only reason the guard fired
            // is the technician mismatch.
            JobToReturn = AssignedJob()
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        // Act
        var result = await service.StartJobAsync(AssignedJobId, OtherTechnicianId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotTheAssignee, result.Error);
        Assert.Null(result.Value);
    }

    // AC4a: A job that has never been assigned (CREATED status) is rejected.
    [Fact]
    public async Task StartJobAsync_JobNotAssigned_WhenCreated_ReturnsNotAssigned()
    {
        var repository = new FakeJobRepository
        {
            StartJobAsyncResult = false,
            JobToReturn = new Job { Id = AssignedJobId, Status = "CREATED" }
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        var result = await service.StartJobAsync(AssignedJobId, ActiveTechnicianId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotAssigned, result.Error);
    }

    // AC4b: An already-started job (IN_PROGRESS) is rejected.
    [Fact]
    public async Task StartJobAsync_JobNotAssigned_WhenInProgress_ReturnsNotAssigned()
    {
        var repository = new FakeJobRepository
        {
            StartJobAsyncResult = false,
            JobToReturn = new Job { Id = AssignedJobId, Status = "IN_PROGRESS" }
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        var result = await service.StartJobAsync(AssignedJobId, ActiveTechnicianId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotAssigned, result.Error);
    }

    // AC4c: A job that does not exist is reported as not found.
    [Fact]
    public async Task StartJobAsync_UnknownJob_ReturnsJobNotFound()
    {
        // Arrange - guard fires and the read-back finds nothing (null).
        var repository = new FakeJobRepository
        {
            StartJobAsyncResult = false,
            JobToReturn = null   // no such row
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), new FakeEventPublisher());

        var result = await service.StartJobAsync("99999999-9999-9999-9999-999999999999", ActiveTechnicianId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotFound, result.Error);
    }

    // AC5: A successful change publishes one valid JobStatusChanged event.
    [Fact]
    public async Task StartJobAsync_OnSuccess_PublishesJobStatusChangedEvent()
    {
        // Arrange
        var startedAt = new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc);
        var updatedJob = InProgressJob(startedAt);
        var repository = new FakeJobRepository
        {
            StartJobAsyncResult = true,
            JobToReturn = updatedJob
        };
        var publisher = new FakeEventPublisher();
        var service = BuildService(repository, new FakeAssetValidationClient(), publisher);

        // Act
        await service.StartJobAsync(AssignedJobId, ActiveTechnicianId);

        // Assert - exactly one event on the correct topic, keyed by job id.
        Assert.Equal(1, publisher.PublishAsyncCallCount);
        Assert.Equal("job-status-changed", publisher.PublishedTopic);
        Assert.Equal("JobStatusChanged", publisher.PublishedEventType);
        Assert.Equal(1, publisher.PublishedEventVersion);
        Assert.Equal(AssignedJobId, publisher.PublishedKey);

        var payload = Assert.IsType<JobStatusChangedPayload>(publisher.PublishedPayload);
        Assert.Equal(AssignedJobId, payload.JobId);
        Assert.Equal("JOB-START1", payload.JobReference);
        Assert.Equal(AssignmentId, payload.AssignmentId);
        Assert.Equal(ActiveTechnicianId, payload.TechnicianId);
        Assert.Equal("TEC-099", payload.TechnicianReference);
        Assert.Equal("ASSIGNED", payload.OldStatus);
        Assert.Equal("IN_PROGRESS", payload.NewStatus);
        Assert.Equal(startedAt, payload.OccurredAt);
    }

    // AC5 (negative): A broker failure must not fail the request. The job is
    // already committed when the publish runs, so a failed publish is logged
    // and swallowed, matching the same guarantee on job creation.
    [Fact]
    public async Task StartJobAsync_WhenPublishingFails_StillReturnsTheUpdatedJob()
    {
        // Arrange
        var startedAt = new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc);
        var repository = new FakeJobRepository
        {
            StartJobAsyncResult = true,
            JobToReturn = InProgressJob(startedAt)
        };
        var publisher = new FakeEventPublisher
        {
            ExceptionToThrow = new InvalidOperationException("Broker unavailable.")
        };
        var service = BuildService(repository, new FakeAssetValidationClient(), publisher);

        // Act
        var result = await service.StartJobAsync(AssignedJobId, ActiveTechnicianId);

        // Assert - the status transition succeeded.
        Assert.True(result.IsSuccess);
        Assert.Equal("IN_PROGRESS", result.Value!.Status);
        Assert.Equal(startedAt, result.Value.StartedAt);

        // Publishing was attempted, not skipped.
        Assert.Equal(1, publisher.PublishAsyncCallCount);
    }


    // =========================================================================
    // US-11A: Add Service Work Record Tests
    // =========================================================================

    // AC1 & AC2: The active assignee can add a valid work record to an active
    // (IN_PROGRESS) job. The record stores the job, technician, content and timestamp.
    [Fact]
    public async Task AddWorkRecordAsync_ByActiveAssigneeOnInProgressJob_SucceedsAndPersists()
    {
        // Arrange
        var repository = new FakeJobRepository
        {
            JobToReturn = InProgressJob(DateTime.UtcNow)
        };
        var service = BuildService(repository);
        const string content = "Replaced capacitor and verified cooling cycle.";

        // Act
        var result = await service.AddWorkRecordAsync(AssignedJobId, ActiveTechnicianId, content);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(AssignedJobId, result.Value.JobId);
        Assert.Equal("JOB-START1", result.Value.JobReference);
        Assert.Equal(ActiveTechnicianId, result.Value.TechnicianId);
        Assert.Equal("TEC-099", result.Value.TechnicianReference);
        Assert.Equal(content, result.Value.Content);
        Assert.True(result.Value.RecordedAt > DateTime.MinValue);

        // Repository was called and stored the record
        Assert.Equal(1, repository.AddWorkRecordCallCount);
        Assert.NotNull(repository.LastAddedWorkRecord);
        Assert.Equal(content, repository.LastAddedWorkRecord.Content);
        Assert.Equal(ActiveTechnicianId, repository.LastAddedWorkRecord.TechnicianId);
    }

    // AC3: Missing required content is rejected.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AddWorkRecordAsync_WithMissingOrWhitespaceContent_ReturnsMissingContent(string? content)
    {
        // Arrange
        var repository = new FakeJobRepository
        {
            JobToReturn = InProgressJob(DateTime.UtcNow)
        };
        var service = BuildService(repository);

        // Act
        var result = await service.AddWorkRecordAsync(AssignedJobId, ActiveTechnicianId, content!);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.MissingContent, result.Error);
        Assert.Equal(0, repository.AddWorkRecordCallCount);
    }

    // AC4: A technician who is not the active assignee cannot add records.
    [Fact]
    public async Task AddWorkRecordAsync_ByNonAssignee_ReturnsNotTheAssignee()
    {
        // Arrange
        var repository = new FakeJobRepository
        {
            JobToReturn = InProgressJob(DateTime.UtcNow)
        };
        var service = BuildService(repository);
        const string otherTechnicianId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";

        // Act
        var result = await service.AddWorkRecordAsync(AssignedJobId, otherTechnicianId, "Checked unit.");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotTheAssignee, result.Error);
        Assert.Equal(0, repository.AddWorkRecordCallCount);
    }

    // AC1: Invalid lifecycle states are rejected without adding records.
    [Theory]
    [InlineData("CREATED")]
    [InlineData("ASSIGNED")]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    public async Task AddWorkRecordAsync_WhenJobIsNotInProgress_ReturnsJobNotInProgress(string invalidStatus)
    {
        // Arrange
        var job = AssignedJob();
        job.Status = invalidStatus;
        var repository = new FakeJobRepository
        {
            JobToReturn = job
        };
        var service = BuildService(repository);

        // Act
        var result = await service.AddWorkRecordAsync(AssignedJobId, ActiveTechnicianId, "Checked unit.");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotInProgress, result.Error);
        Assert.Equal(0, repository.AddWorkRecordCallCount);
    }

    [Fact]
    public async Task AddWorkRecordAsync_WhenJobDoesNotExist_ReturnsJobNotFound()
    {
        // Arrange
        var repository = new FakeJobRepository
        {
            JobToReturn = null
        };
        var service = BuildService(repository);

        // Act
        var result = await service.AddWorkRecordAsync("missing-job-id", ActiveTechnicianId, "Some work.");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.JobNotFound, result.Error);
        Assert.Equal(0, repository.AddWorkRecordCallCount);
    }

    [Fact]
    public async Task GetWorkRecordsAsync_ReturnsStoredRecordsForJob()
    {
        // Arrange
        var repository = new FakeJobRepository
        {
            JobToReturn = InProgressJob(DateTime.UtcNow)
        };
        repository.StoredWorkRecords.Add(new ServiceWorkRecord
        {
            Id = Guid.NewGuid().ToString(),
            JobId = AssignedJobId,
            JobReference = "JOB-START1",
            TechnicianId = ActiveTechnicianId,
            TechnicianReference = "TEC-099",
            Content = "First record",
            RecordedAt = DateTime.UtcNow.AddMinutes(-10),
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        repository.StoredWorkRecords.Add(new ServiceWorkRecord
        {
            Id = Guid.NewGuid().ToString(),
            JobId = AssignedJobId,
            JobReference = "JOB-START1",
            TechnicianId = ActiveTechnicianId,
            TechnicianReference = "TEC-099",
            Content = "Second record",
            RecordedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        var service = BuildService(repository);

        // Act
        var records = await service.GetWorkRecordsAsync(AssignedJobId);

        // Assert
        Assert.NotNull(records);
        Assert.Equal(2, records.Count);
        Assert.Equal("First record", records[0].Content);
        Assert.Equal("Second record", records[1].Content);
    }

    [Fact]
    public async Task GetWorkRecordsAsync_WhenJobDoesNotExist_ReturnsNull()
    {
        // Arrange
        var repository = new FakeJobRepository
        {
            JobToReturn = null
        };
        var service = BuildService(repository);

        // Act
        var records = await service.GetWorkRecordsAsync("non-existent-job");

        // Assert
        Assert.Null(records);
    }

    [Fact]
    public async Task UpdateWorkRecordAsync_ByActiveAssigneeOnInProgressJob_SucceedsAndPersists()
    {
        var repository = new FakeJobRepository { JobToReturn = InProgressJob(DateTime.UtcNow) };
        var recordId = Guid.NewGuid().ToString();
        repository.StoredWorkRecords.Add(new ServiceWorkRecord
        {
            Id = recordId,
            JobId = AssignedJobId,
            Content = "Old content"
        });
        var service = BuildService(repository);

        var result = await service.UpdateWorkRecordAsync(AssignedJobId, recordId, ActiveTechnicianId, "Updated work notes.");

        Assert.True(result.IsSuccess);
        Assert.Equal("Updated work notes.", result.Value!.Content);
        Assert.Equal("Updated work notes.", Assert.Single(repository.StoredWorkRecords).Content);
    }

    [Fact]
    public async Task UpdateWorkRecordAsync_WhenRecordDoesNotExist_ReturnsWorkRecordNotFound()
    {
        var repository = new FakeJobRepository { JobToReturn = InProgressJob(DateTime.UtcNow) };
        var service = BuildService(repository);

        var result = await service.UpdateWorkRecordAsync(AssignedJobId, "missing-record", ActiveTechnicianId, "New content");

        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.WorkRecordNotFound, result.Error);
    }

    [Fact]
    public async Task UpdateWorkRecordAsync_ByNonAssignee_ReturnsNotTheAssignee()
    {
        var repository = new FakeJobRepository { JobToReturn = InProgressJob(DateTime.UtcNow) };
        var service = BuildService(repository);

        var result = await service.UpdateWorkRecordAsync(AssignedJobId, Guid.NewGuid().ToString(), "different-technician", "New content");

        Assert.False(result.IsSuccess);
        Assert.Equal(ServiceError.NotTheAssignee, result.Error);
    }
}

