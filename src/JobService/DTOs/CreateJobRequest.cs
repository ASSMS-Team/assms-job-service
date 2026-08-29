using System.ComponentModel.DataAnnotations;

namespace JobService.DTOs;

// What the client is allowed to send. Id, JobReference, Status, ScheduledDate
// and CreatedBy are deliberately absent - they are server-controlled, and
// binding them from the request body would be a mass-assignment hole.
public class CreateJobRequest
{
    /// <summary>Id of the customer the job is raised for (a GUID string). Required, and the customer must exist and be active.</summary>
    [Required]
    [MaxLength(36)]
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Id of the asset the job is raised against (a GUID string). Required, and the asset must exist, be active, and belong to this customer.</summary>
    [Required]
    [MaxLength(36)]
    public string AssetId { get; set; } = string.Empty;

    /// <summary>Kind of work requested. Required, and must be one of INSTALLATION, REPAIR, MAINTENANCE, INSPECTION or WARRANTY_CLAIM.</summary>
    [Required]
    [MaxLength(20)]
    [RegularExpression(
        "^(INSTALLATION|REPAIR|MAINTENANCE|INSPECTION|WARRANTY_CLAIM)$",
        ErrorMessage = "ServiceCategory must be INSTALLATION, REPAIR, MAINTENANCE, INSPECTION or WARRANTY_CLAIM.")]
    public string ServiceCategory { get; set; } = string.Empty;

    /// <summary>What the customer reported. Required, up to 1000 characters.</summary>
    [Required]
    [MaxLength(1000)]
    public string ProblemDescription { get; set; } = string.Empty;

    /// <summary>How urgent the work is. Required, and must be one of LOW, MEDIUM, HIGH or URGENT.</summary>
    [Required]
    [MaxLength(10)]
    [RegularExpression(
        "^(LOW|MEDIUM|HIGH|URGENT)$",
        ErrorMessage = "Priority must be LOW, MEDIUM, HIGH or URGENT.")]
    public string Priority { get; set; } = string.Empty;

    /// <summary>Province the work is in. Required, and must be one of the nine Sri Lankan provinces: WESTERN, CENTRAL, SOUTHERN, NORTHERN, EASTERN, NORTH_WESTERN, NORTH_CENTRAL, UVA or SABARAGAMUWA.</summary>
    [Required]
    [MaxLength(20)]
    [RegularExpression(
        "^(WESTERN|CENTRAL|SOUTHERN|NORTHERN|EASTERN|NORTH_WESTERN|NORTH_CENTRAL|UVA|SABARAGAMUWA)$",
        ErrorMessage = "Region must be WESTERN, CENTRAL, SOUTHERN, NORTHERN, EASTERN, NORTH_WESTERN, NORTH_CENTRAL, UVA or SABARAGAMUWA.")]
    public string Region { get; set; } = string.Empty;
}
