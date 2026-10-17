using JobService.Models;
using JobService.Repositories;
using MySqlConnector;

var connectionString = Environment.GetEnvironmentVariable("ASSMS_ASE_JOB_LIST_CONNECTION")
    ?? throw new InvalidOperationException("The disposable MySQL connection is missing.");
var settings = new MySqlConnectionStringBuilder(connectionString);
if (settings.Server != "127.0.0.1" || settings.Database != "jobdb")
{
    throw new InvalidOperationException("This check only runs against a local disposable jobdb.");
}

var repository = new JobRepository(new MySqlConnectionFactory(connectionString));
var technicianA = Guid.NewGuid().ToString();
var technicianB = Guid.NewGuid().ToString();

async Task<Job> CreateJobAsync(string reference)
{
    var job = new Job
    {
        Id = Guid.NewGuid().ToString(),
        JobReference = reference,
        CustomerId = Guid.NewGuid().ToString(),
        AssetId = Guid.NewGuid().ToString(),
        ServiceCategory = "REPAIR",
        ProblemDescription = "Synthetic quality check",
        Priority = "MEDIUM",
        Region = "WESTERN",
        CreatedBy = Guid.NewGuid().ToString()
    };
    await repository.CreateAsync(job);
    return job;
}

static void Expect(string label, IReadOnlyList<Job> actual, params string[] references)
{
    var expected = references.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    var observed = actual.Select(job => job.JobReference)
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();
    if (!expected.SequenceEqual(observed))
    {
        throw new InvalidOperationException($"{label}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", observed)}].");
    }
    Console.WriteLine($"PASS {label}: {observed.Length} matching jobs");
}

var created = await CreateJobAsync("JOB-ASE001");
var assignedA = await CreateJobAsync("JOB-ASE002");
var assignedB = await CreateJobAsync("JOB-ASE003");
var startedA = await CreateJobAsync("JOB-ASE004");

foreach (var (job, technician) in new[]
{
    (assignedA, technicianA),
    (assignedB, technicianB),
    (startedA, technicianA)
})
{
    if (!await repository.ApplyAssignmentAsync(job.Id, Guid.NewGuid().ToString(), technician, "TEC-ASE", "ASSIGNED", DateTime.UtcNow))
    {
        throw new InvalidOperationException("Could not prepare a synthetic assignment.");
    }
}
if (!await repository.StartJobAsync(startedA.Id, technicianA, DateTime.UtcNow))
{
    throw new InvalidOperationException("Could not prepare an in-progress job.");
}

Expect("No filters", await repository.ListAsync(null, null),
    created.JobReference, assignedA.JobReference, assignedB.JobReference, startedA.JobReference);
Expect("Status only", await repository.ListAsync("ASSIGNED", null),
    assignedA.JobReference, assignedB.JobReference);
Expect("Technician only", await repository.ListAsync(null, technicianA),
    assignedA.JobReference, startedA.JobReference);
Expect("Status and technician", await repository.ListAsync("ASSIGNED", technicianA),
    assignedA.JobReference);
Expect("SQL-looking status value", await repository.ListAsync("' OR 1=1 --", null));
Expect("SQL-looking technician value", await repository.ListAsync(null, "' OR 1=1 --"));
Expect("Rows remain unchanged", await repository.ListAsync(null, null),
    created.JobReference, assignedA.JobReference, assignedB.JobReference, startedA.JobReference);
