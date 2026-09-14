namespace JobService.Messaging.Contracts;

// The JobAssigned payload as this service reads it. Published by the Dispatch
// Service to job-assigned; see the note on EventEnvelope for why this is a
// redefinition rather than a shared type.
//
// All six payload fields the contract defines are declared here, and they are
// deliberately identical in name, type and order to
// DispatchService.Messaging.Contracts.JobAssignedPayload and to the Reporting
// Service's copy. Because each service redefines the payload independently,
// nothing at compile time catches a field that has drifted - a renamed field
// simply deserializes as null forever. Keeping the three copies literally the
// same is the only thing standing in for that missing check.
//
// The technician is identified by TechnicianReference, not by a name. Dispatch
// publishes the reference because it is the stable handle a person quotes, and
// this service has no technician table to resolve it against.
public class JobAssignedPayload
{
    public const string Topic = "job-assigned";

    // The consumer group this service reads job-assigned with, named by the
    // contract document's assms-<consuming-service>-<topic> convention. Its own
    // group, never shared with Reporting: a shared group would make Kafka hand
    // each event to only one of the two, and both need every event.
    public const string ConsumerGroup = "assms-job-job-assigned";

    public const string EventType = "JobAssigned";

    // First version of this payload. It goes up only if a field is removed,
    // renamed, retyped, or changes meaning.
    public const int EventVersion = 1;

    // The lifecycle status a job takes on assignment. Dispatch does not publish
    // a status on this event - the event type is the status change - so the
    // value is named here rather than read off the payload.
    public const string AssignedStatus = "ASSIGNED";

    public string AssignmentId { get; set; } = string.Empty;

    public string JobId { get; set; } = string.Empty;

    public string JobReference { get; set; } = string.Empty;

    public string TechnicianId { get; set; } = string.Empty;

    public string TechnicianReference { get; set; } = string.Empty;

    // When Dispatch committed the assignment. Carried at sub-second precision
    // and compared against the stored value to reject a stale or redelivered
    // event - see JobRepository.ApplyAssignmentAsync.
    public DateTime AssignedAt { get; set; }
}
