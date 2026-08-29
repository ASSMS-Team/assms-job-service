namespace JobService.Messaging.Contracts;

// The six-field envelope every ASSMS event carries, as fixed by
// docs/kafka/event-contracts.md in assms-platform-infrastructure. Event-specific
// data lives only in Payload; the other five fields are identical in shape
// across all three topics, which is what lets a consumer route on EventType
// without knowing the payload.
public class EventEnvelope<TPayload>
{
    /// <summary>Unique id of this event instance. A redelivery carries the same value, so consumers use it to detect duplicates.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>The event name in PascalCase, for example JobCreated.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Schema version of Payload. Not a semantic version - an integer that starts at 1 per event type.</summary>
    public int EventVersion { get; set; }

    /// <summary>When the business fact happened in this service, not when the message was published.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>Service that emitted the event, using the repository service name.</summary>
    public string Producer { get; set; } = string.Empty;

    /// <summary>The event-specific body.</summary>
    public TPayload Payload { get; set; } = default!;
}
