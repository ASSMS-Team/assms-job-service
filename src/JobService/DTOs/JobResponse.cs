namespace JobService.DTOs;

// What goes back to the client: the whole stored row. Unlike the asset
// response there is nothing internal to leave out - every column on jobs has
// business meaning to the Agent reading it.
public class JobResponse
{
    /// <summary>Server-generated job id (a GUID string). Use it to fetch the job.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable reference, JOB- followed by six characters. Unique, and the handle to quote to a customer.</summary>
    public string JobReference { get; set; } = string.Empty;

    /// <summary>Id of the customer the job was raised for.</summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Id of the asset the job was raised against.</summary>
    public string AssetId { get; set; } = string.Empty;

    /// <summary>Kind of work: INSTALLATION, REPAIR, MAINTENANCE, INSPECTION or WARRANTY_CLAIM.</summary>
    public string ServiceCategory { get; set; } = string.Empty;

    /// <summary>What the customer reported.</summary>
    public string ProblemDescription { get; set; } = string.Empty;

    /// <summary>How urgent the work is: LOW, MEDIUM, HIGH or URGENT.</summary>
    public string Priority { get; set; } = string.Empty;

    /// <summary>Province the work is in, one of the nine Sri Lankan provinces.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Day the work is booked for, or null while the job is unscheduled. A date only - no time of day.</summary>
    public DateOnly? ScheduledDate { get; set; }

    /// <summary>Id of the Agent who raised the job. The all-zero GUID until authentication supplies a real one.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Lifecycle status of the job. Newly created jobs are CREATED.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Database timestamp for when the job was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Database timestamp for when the job was last modified.</summary>
    public DateTime UpdatedAt { get; set; }
}
