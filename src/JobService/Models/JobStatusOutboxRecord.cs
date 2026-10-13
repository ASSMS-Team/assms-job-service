namespace JobService.Models;

/// <summary>
/// A persistent record in the job status outbox awaiting or acknowledged by Kafka.
/// </summary>
public sealed record JobStatusOutboxRecord
{
    public string Id { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string Topic { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public int PublishAttempts { get; init; } = 0;
    public string? LastError { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? PublishedAt { get; init; }
}
