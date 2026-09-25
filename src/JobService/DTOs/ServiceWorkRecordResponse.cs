using System.Text.Json.Serialization;

namespace JobService.DTOs;

public class ServiceWorkRecordResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("jobReference")]
    public string JobReference { get; set; } = string.Empty;

    [JsonPropertyName("technicianId")]
    public string TechnicianId { get; set; } = string.Empty;

    [JsonPropertyName("technicianReference")]
    public string TechnicianReference { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("recordedAt")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}
