namespace JobService.Models;

// Internal representation of a row in the jobs table - all thirteen columns.
// Kept separate from the DTOs so the API shape can change without touching
// persistence.
public class Job
{
    public string Id { get; set; } = string.Empty;

    // The handle an Agent quotes: JOB- plus six characters. Server-generated
    // and unique; never supplied by the client.
    public string JobReference { get; set; } = string.Empty;

    // Both of these name rows in customerdb, which this service does not own.
    // They are validated over HTTP before the insert, not by a foreign key.
    public string CustomerId { get; set; } = string.Empty;

    public string AssetId { get; set; } = string.Empty;

    public string ServiceCategory { get; set; } = string.Empty;

    public string ProblemDescription { get; set; } = string.Empty;

    public string Priority { get; set; } = string.Empty;

    // The province the work is in. Dispatch matches this against technician
    // coverage in Sprint 2.
    public string Region { get; set; } = string.Empty;

    // scheduled_date is a DATE, not a TIMESTAMP - the day work is booked for
    // has no time of day, and DateOnly keeps it from acquiring a spurious one.
    // Nullable because a job is raised before it is scheduled.
    public DateOnly? ScheduledDate { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
