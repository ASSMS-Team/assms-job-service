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
}
