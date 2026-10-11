using System.ComponentModel.DataAnnotations;

namespace JobService.DTOs;

// Request body for PUT /api/jobs/{id}/work-records/{recordId}.
// The active assigned technician updates an existing work record.
// The technician is identified from the bearer token, not from this body.
public class UpdateWorkRecordRequest
{
    /// <summary>Description of the work performed.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Work record content is required.")]
    [MaxLength(2000, ErrorMessage = "Work record content cannot exceed 2000 characters.")]
    public string Content { get; set; } = string.Empty;
}
