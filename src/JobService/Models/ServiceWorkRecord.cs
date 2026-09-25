namespace JobService.Models;

public class ServiceWorkRecord
{
    public string Id { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string JobReference { get; set; } = string.Empty;
    public string TechnicianId { get; set; } = string.Empty;
    public string TechnicianReference { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
