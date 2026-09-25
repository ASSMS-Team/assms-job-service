using JobService.Models;
using JobService.Repositories;

namespace JobService.Tests;

// Hand-rolled stand-in for the real repository: every call records what it was
// given and hands back whatever the test configured, so a service test can run
// without a database.
public class FakeJobRepository : IJobRepository
{
    // CreateAsync
    public Job? CreatedJob;
    public int CreateAsyncCallCount;
    // Every reference CreateAsync was called with, in order. The service mutates
    // one Job object across its retries, so the reference has to be copied out
    // at each call - CreatedJob only ever holds the last one.
    public readonly List<string> AttemptedReferences = new();
    // How many opening calls throw a duplicate-key error before one is allowed
    // to succeed. Zero, the default, is the happy path. This is what stands in
    // for the unique index rejecting a reference that is already taken.
    public int DuplicateKeyFailuresBeforeSuccess;

    // GetByIdAsync / GetByReferenceAsync - left null so an unconfigured fake
    // stands in for "no such row". On the create path the service falls back to
    // the job it just built when this is null, so the happy path needs no
    // configuration.
    public Job? JobToReturn;
    public string? GetByIdId;
    public string? GetByReferenceJobReference;
    public IReadOnlyList<Job> JobsToReturn = Array.Empty<Job>();
    public string? ListStatus;
    public string? ListAssignedTechnicianId;

    public Task CreateAsync(Job job)
    {
        // Recorded and counted before the throw, so a test that configures a
        // collision can still assert on what each attempt was given.
        CreatedJob = job;
        AttemptedReferences.Add(job.JobReference);
        CreateAsyncCallCount++;

        if (CreateAsyncCallCount <= DuplicateKeyFailuresBeforeSuccess)
        {
            throw MySqlExceptions.DuplicateKey(
                $"Duplicate entry '{job.JobReference}' for key 'jobs.uq_jobs_job_reference'");
        }

        return Task.CompletedTask;
    }

    public Task<Job?> GetByIdAsync(string id)
    {
        GetByIdId = id;

        return Task.FromResult(JobToReturn);
    }

    public Task<Job?> GetByReferenceAsync(string jobReference)
    {
        GetByReferenceJobReference = jobReference;

        return Task.FromResult(JobToReturn);
    }

    public Task<IReadOnlyList<Job>> ListAsync(string? status, string? assignedTechnicianId)
    {
        ListStatus = status;
        ListAssignedTechnicianId = assignedTechnicianId;
        return Task.FromResult(JobsToReturn);
    }

    // ApplyAssignmentAsync - what the JobAssigned consumer calls. Every call is
    // recorded in order rather than only the last, because the redelivery tests
    // turn on how many times the write was attempted and with what.
    public readonly List<AppliedAssignment> AppliedAssignments = new();

    public int ApplyAssignmentAsyncCallCount;

    // What the fake reports back. True by default, standing in for a row that was
    // there and was changed. Set false to exercise the branch where the real
    // UPDATE matches nothing - the event was already applied, it is older than
    // what is stored, or no such job exists here.
    public bool ApplyAssignmentResult = true;

    // Left null for the happy path. Set it to stand in for the database being
    // unreachable, which is the failure the consumer must retry rather than
    // commit past.
    public Exception? ApplyAssignmentExceptionToThrow;

    // How many opening calls throw before one is allowed to succeed. Zero, the
    // default, never fails. Anything higher stands in for a database that was
    // down and came back, which is the case the seek-and-retry exists for.
    public int ApplyAssignmentFailuresBeforeSuccess;

    // Runs after the call is recorded and before the throw. The consumer tests
    // use it to cancel the host token at the moment of failure, so the loop
    // unwinds instead of sleeping out its retry delay.
    public Action? OnApplyAssignment;

    public Task<bool> ApplyAssignmentAsync(
        string jobId,
        string assignmentId,
        string technicianId,
        string technicianReference,
        string status,
        DateTime assignedAt)
    {
        // Recorded and counted before the throw, so a test that configures a
        // failure can still assert on what the write was given.
        AppliedAssignments.Add(
            new AppliedAssignment(jobId, assignmentId, technicianId, technicianReference, status, assignedAt));
        ApplyAssignmentAsyncCallCount++;

        OnApplyAssignment?.Invoke();

        if (ApplyAssignmentAsyncCallCount <= ApplyAssignmentFailuresBeforeSuccess)
        {
            throw ApplyAssignmentExceptionToThrow
                ?? new InvalidOperationException("The database was unreachable.");
        }

        if (ApplyAssignmentExceptionToThrow is not null)
        {
            throw ApplyAssignmentExceptionToThrow;
        }

        return Task.FromResult(ApplyAssignmentResult);
    }

    // StartJobAsync
    public string? StartJobAsyncCalledWithJobId;
    public string? StartJobAsyncCalledWithTechnicianId;
    public DateTime? StartJobAsyncCalledWithStartedAt;
    public int StartJobAsyncCallCount;

    // True by default: the guarded UPDATE matched and the row changed.
    // Set to false to simulate the guard firing (wrong status / wrong technician
    // / no such job), which is the branch that triggers the read-back.
    public bool StartJobAsyncResult = true;

    public Task<bool> StartJobAsync(string jobId, string technicianId, DateTime startedAt)
    {
        StartJobAsyncCalledWithJobId = jobId;
        StartJobAsyncCalledWithTechnicianId = technicianId;
        StartJobAsyncCalledWithStartedAt = startedAt;
        StartJobAsyncCallCount++;

        return Task.FromResult(StartJobAsyncResult);
    }
<<<<<<< Updated upstream
=======

    // AddWorkRecordAsync & GetWorkRecordsByJobIdAsync
    public readonly List<ServiceWorkRecord> StoredWorkRecords = new();
    public ServiceWorkRecord? LastAddedWorkRecord;
    public int AddWorkRecordCallCount;

    public Task<ServiceWorkRecord> AddWorkRecordAsync(ServiceWorkRecord record)
    {
        StoredWorkRecords.Add(record);
        LastAddedWorkRecord = record;
        AddWorkRecordCallCount++;
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<ServiceWorkRecord>> GetWorkRecordsByJobIdAsync(string jobId)
    {
        IReadOnlyList<ServiceWorkRecord> list = StoredWorkRecords
            .Where(r => r.JobId == jobId)
            .OrderBy(r => r.RecordedAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<bool> UpdateWorkRecordAsync(string recordId, string jobId, string content)
    {
        var record = StoredWorkRecords.FirstOrDefault(r => r.Id == recordId && r.JobId == jobId);
        if (record is null)
        {
            return Task.FromResult(false);
        }

        record.Content = content;
        return Task.FromResult(true);
    }
>>>>>>> Stashed changes
}

// One recorded AppliedAssignment call. A record rather than the Job model:
// these are the arguments the consumer mapped out of an event, and asserting on
// them is how the mapping is pinned.
public record AppliedAssignment(
    string JobId,
    string AssignmentId,
    string TechnicianId,
    string TechnicianReference,
    string Status,
    DateTime AssignedAt);
