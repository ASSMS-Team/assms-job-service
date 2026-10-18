using System.ComponentModel.DataAnnotations;

namespace JobService.DTOs;

// Request body for POST /api/jobs/{id}/work-records.
// The active assigned technician records notes / details of work performed.
// The technician is identified from the bearer token, not from this body.
public class CreateWorkRecordRequest
{
    /// <summary>Description of the work performed.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Work record content is required.")]
    [MaxLength(2000, ErrorMessage = "Work record content cannot exceed 2000 characters.")]
    public string Content { get; set; } = string.Empty;
}
