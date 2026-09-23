namespace JobService.Messaging.Contracts;

// The payload of a JobStatusChanged event. Published to job-status-changed by
// any lifecycle transition that this service owns. US-10A (Start Assigned Job)
// is the first transition to use it; later stories (complete, cancel, etc.) will
// reuse the same event type and topic, differing only in OldStatus / NewStatus.
//
// Consumers must be tolerant of unknown OldStatus / NewStatus values: new
// lifecycle states are added as new user stories ship, and a consumer that
// hard-codes the set of legal values will break silently on any value it has
// never seen.
//
// The technician context is carried so Reporting can attribute the transition
// without a cross-service lookup. It is null when the transition was not made by
// an identified technician (e.g. a system-initiated status change).
public class JobStatusChangedPayload
{
    public const string Topic = "job-status-changed";

    public const string EventType = "JobStatusChanged";

    // First version of this payload. It goes up only if a field is removed,
    // renamed, retyped, or changes meaning.
    public const int EventVersion = 1;

    public string JobId { get; set; } = string.Empty;

    public string JobReference { get; set; } = string.Empty;

    // The assignment that was active when the transition happened. Null if the
    // job was not assigned at the time.
    public string? AssignmentId { get; set; }

    // The technician who triggered the transition, or null for system-initiated
    // transitions. Both id and reference are carried so the consumer can display
    // the human-readable handle without a lookup.
    public string? TechnicianId { get; set; }

    public string? TechnicianReference { get; set; }

    // The status the job held immediately before the transition.
    public string OldStatus { get; set; } = string.Empty;

    // The status the job holds after the transition.
    public string NewStatus { get; set; } = string.Empty;

    // When the transition was committed to the database. Carried so Reporting
    // can order events by their business time rather than their arrival time.
    public DateTime OccurredAt { get; set; }
}
