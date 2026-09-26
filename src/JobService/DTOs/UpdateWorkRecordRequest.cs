using System.ComponentModel.DataAnnotations;

namespace JobService.DTOs;

// Request body for PUT /api/jobs/{id}/work-records/{recordId}.
// The active assigned technician updates an existing work record.
public class UpdateWorkRecordRequest
{
    /// <summary>Id of the technician recording the work. Must match the job's active assignee.</summary>
    [Required]
    public string TechnicianId { get; set; } = string.Empty;

    /// <summary>Description of the work performed.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Work record content is required.")]
    [MaxLength(2000, ErrorMessage = "Work record content cannot exceed 2000 characters.")]
    public string Content { get; set; } = string.Empty;
}
