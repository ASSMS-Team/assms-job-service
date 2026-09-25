namespace JobService.DTOs;

// Returned when a service work record is created or retrieved.
public class ServiceWorkRecordResponse
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
