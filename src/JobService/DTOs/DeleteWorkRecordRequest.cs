namespace JobService.DTOs;

// Request body (or query parameter) for DELETE /api/jobs/{id}/work-records/{recordId}.
// The active assigned technician removes an accidental or draft work record.
public class DeleteWorkRecordRequest
{
    /// <summary>Id of the technician recording the deletion. Must match the job's active assignee.</summary>
    public string TechnicianId { get; set; } = string.Empty;
}
