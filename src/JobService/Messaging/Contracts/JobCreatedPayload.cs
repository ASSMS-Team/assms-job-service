namespace JobService.Messaging.Contracts;

// The payload of a JobCreated event. Published to job-created and read
// independently by Dispatch and Reporting.
//
// The fields are the job as this service actually stores it. The contract doc
// in assms-platform-infrastructure was written before the jobs table existed
// and names jobType, description and location; those are serviceCategory,
// problemDescription and region here, and the doc has to be corrected to match
// before Dispatch builds against it.
//
// scheduled_date and created_by are not published: nothing sets a schedule at
// creation, and created_by holds a placeholder until authentication exists.
// Both are additive when they carry real values, which under the contract's
// versioning rules does not change EventVersion.
public class JobCreatedPayload
{
    public const string Topic = "job-created";

    public const string EventType = "JobCreated";

    // First version of this payload. It goes up only if a field is removed,
    // renamed, retyped, or changes meaning.
    public const int EventVersion = 1;

    public string JobId { get; set; } = string.Empty;

    public string JobReference { get; set; } = string.Empty;

    public string CustomerId { get; set; } = string.Empty;

    public string AssetId { get; set; } = string.Empty;

    public string ServiceCategory { get; set; } = string.Empty;

    public string ProblemDescription { get; set; } = string.Empty;

    public string Priority { get; set; } = string.Empty;

    // What Dispatch matches against technician coverage in Sprint 2.
    public string Region { get; set; } = string.Empty;

    // Always CREATED in this event, but carried so a consumer reads the status
    // field the same way here as on job-status-changed.
    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
