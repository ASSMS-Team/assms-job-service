using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace JobService.DTOs;

public class CreateWorkRecordRequest
{
    [Required]
    [JsonPropertyName("technicianId")]
    public string TechnicianId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
