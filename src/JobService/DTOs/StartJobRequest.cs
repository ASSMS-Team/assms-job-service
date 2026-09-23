using System.ComponentModel.DataAnnotations;

namespace JobService.DTOs;

// Request body for POST /api/jobs/{id}/start. The caller identifies themselves
// as the active assignee; the service verifies this against the stored
// assigned_technician_id before allowing the transition.
//
// Authentication does not exist yet in this service (created_by uses the
// all-zero GUID placeholder). Supplying the technician id in the body is the
// same pattern the rest of the service uses: the caller asserts who they are,
// and the guard in the repository enforces it.
public class StartJobRequest
{
    /// <summary>Id of the technician requesting the start. Must match the job's active assignee.</summary>
    [Required]
    public string TechnicianId { get; set; } = string.Empty;
}
