namespace JobService.Models;

public class JobStatusHistory
{
    public required string Id { get; init; }
    public required string JobId { get; init; }
    public string? PreviousStatus { get; init; }
    public required string NewStatus { get; init; }
    public required string ActorId { get; init; }
    public DateTime CreatedAt { get; init; }
}
