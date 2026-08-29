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
}
