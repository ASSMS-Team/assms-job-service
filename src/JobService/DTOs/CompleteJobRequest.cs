using System.ComponentModel.DataAnnotations;

namespace JobService.DTOs;

// Request body for POST /api/jobs/{id}/complete.
// The active assigned technician completes the in-progress job.
public class CompleteJobRequest
{
    /// <summary>Id of the technician completing the job. Must match the job's active assignee.</summary>
    [Required]
    public string TechnicianId { get; set; } = string.Empty;
}
