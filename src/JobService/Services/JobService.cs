using JobService.DTOs;
using JobService.Extensions;
using JobService.Messaging.Contracts;
using JobService.Messaging.Producers;
using JobService.Models;
using JobService.Repositories;

using MySqlConnector;

namespace JobService.Services;

public class JobService
{
    private const int DuplicateEntryErrorNumber = 1062;

    // The reference is six characters from a 32-character alphabet, so a
    // collision is remote - but the unique index makes one a failed insert
    // rather than a duplicate row, and three tries is far past the point where
    // repeated collisions mean something other than bad luck.
    private const int MaxCreateAttempts = 3;

    // There is no authentication yet, so there is no Agent id to record. The
    // all-zero GUID is written rather than an empty string because created_by
    // is a CHAR(36) that MySqlConnector reads back as a Guid - an empty value
    // would parse as nothing and break every read of the row.
    private const string UnattributedCreatedBy = "00000000-0000-0000-0000-000000000000";

    private const string CreatedStatus = "CREATED";

    private readonly IJobRepository _repository;
    // customerdb belongs to another service, so whether the asset and customer
    // are usable is a question that can only be asked over HTTP.
    private readonly IAssetValidationClient _validationClient;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<JobService> _logger;

    public JobService(
        IJobRepository repository,
        IAssetValidationClient validationClient,
        IEventPublisher eventPublisher,
        ILogger<JobService> logger)
    {
        _repository = repository;
        _validationClient = validationClient;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<Result<JobResponse>> CreateAsync(CreateJobRequest request)
    {
        // Throws AssetValidationUnavailableException if the customer service
        // cannot answer. That is deliberately not caught here: an infrastructure
        // failure is not a reason to reject a job that may well be valid, so it
        // travels up to the controller and becomes a 503.
        var outcome = await _validationClient.ValidateAsync(request.AssetId, request.CustomerId);

        if (outcome != AssetValidationOutcome.Valid)
        {
            return Result<JobResponse>.Failure(MapValidationOutcome(outcome));
        }

        var job = new Job
        {
            Id = Guid.NewGuid().ToString(),
            CustomerId = request.CustomerId,
            AssetId = request.AssetId,
            ServiceCategory = request.ServiceCategory,
            ProblemDescription = request.ProblemDescription,
            Priority = request.Priority,
            Region = request.Region,
            // Nothing schedules a job at creation; a later story fills this in.
            ScheduledDate = null,
            CreatedBy = UnattributedCreatedBy,
            // Not sent to the database - the column's DEFAULT 'CREATED' is what
            // actually writes it. It is set here so the fallback below has the
            // status the row will hold, rather than an empty string, if the
            // read-back misses.
            Status = CreatedStatus
        };

        await InsertWithFreshReferenceAsync(job);

        // created_at, updated_at and status are database defaults, so the row is
        // read back rather than returning default(DateTime) for the timestamps.
        var created = await _repository.GetByIdAsync(job.Id) ?? job;

        await PublishJobCreatedAsync(created);

        return Result<JobResponse>.Success(MapToResponse(created));
    }

    public async Task<JobResponse?> GetByIdAsync(string id)
    {
        var job = await _repository.GetByIdAsync(id);

        return job is null ? null : MapToResponse(job);
    }

    public async Task<JobResponse?> GetByReferenceAsync(string jobReference)
    {
        var job = await _repository.GetByReferenceAsync(jobReference);

        return job is null ? null : MapToResponse(job);
    }

    // Generates a reference and inserts, treating a duplicate-key failure as a
    // reason to draw a new reference rather than as an error. The unique index
    // is what detects the collision - there is no pre-check, because two
    // requests could both pass one before either inserted.
    //
    // The last attempt is not caught: if three independently drawn references
    // all collide, the alphabet or the generator is wrong, and swallowing that
    // into a business error would hide it. The exception surfaces as a 500.
    private async Task InsertWithFreshReferenceAsync(Job job)
    {
        for (var attempt = 1; ; attempt++)
        {
            job.JobReference = JobReferenceGenerator.Generate();

            try
            {
                await _repository.CreateAsync(job);
                return;
            }
            catch (MySqlException ex)
                when (ex.Number == DuplicateEntryErrorNumber && attempt < MaxCreateAttempts)
            {
                _logger.LogWarning(
                    "Job reference {JobReference} was already taken on attempt {Attempt} of {MaxAttempts}; generating another.",
                    job.JobReference,
                    attempt,
                    MaxCreateAttempts);
            }
        }
    }

    // The job is already committed by the time this runs. A messaging failure
    // must not invalidate it, so everything is caught and logged: Dispatch and
    // Reporting missing an event is a problem to fix from the log, whereas
    // failing the request would leave the Agent believing no job was raised
    // when the row is sitting in the database.
    private async Task PublishJobCreatedAsync(Job job)
    {
        try
        {
            var payload = new JobCreatedPayload
            {
                JobId = job.Id,
                JobReference = job.JobReference,
                CustomerId = job.CustomerId,
                AssetId = job.AssetId,
                ServiceCategory = job.ServiceCategory,
                ProblemDescription = job.ProblemDescription,
                Priority = job.Priority,
                Region = job.Region,
                Status = job.Status,
                CreatedAt = job.CreatedAt
            };

            await _eventPublisher.PublishAsync(
                JobCreatedPayload.Topic,
                JobCreatedPayload.EventType,
                JobCreatedPayload.EventVersion,
                // Keyed on the job id, so every event about this job lands on
                // one partition in order.
                job.Id,
                payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Job {JobId} ({JobReference}) was created but the {EventType} event could not be published.",
                job.Id,
                job.JobReference,
                JobCreatedPayload.EventType);
        }
    }

    private static ServiceError MapValidationOutcome(AssetValidationOutcome outcome) => outcome switch
    {
        AssetValidationOutcome.AssetNotFound => ServiceError.AssetNotFound,
        AssetValidationOutcome.CustomerMismatch => ServiceError.CustomerMismatch,
        AssetValidationOutcome.AssetInactive => ServiceError.AssetInactive,
        AssetValidationOutcome.CustomerInactive => ServiceError.CustomerInactive,
        // Valid never reaches here, and a value added to the enum without a
        // mapping should fail loudly rather than silently become a success.
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unmapped asset validation outcome.")
    };

    private static JobResponse MapToResponse(Job job) => new()
    {
        Id = job.Id,
        JobReference = job.JobReference,
        CustomerId = job.CustomerId,
        AssetId = job.AssetId,
        ServiceCategory = job.ServiceCategory,
        ProblemDescription = job.ProblemDescription,
        Priority = job.Priority,
        Region = job.Region,
        ScheduledDate = job.ScheduledDate,
        CreatedBy = job.CreatedBy,
        Status = job.Status,
        CreatedAt = job.CreatedAt,
        UpdatedAt = job.UpdatedAt
    };
}
