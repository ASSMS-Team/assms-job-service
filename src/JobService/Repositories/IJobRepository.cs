using JobService.Models;

namespace JobService.Repositories;

public interface IJobRepository
{
    // Throws MySqlException 1062 when the generated reference is already taken.
    // The caller is expected to catch that, generate a fresh reference and try
    // again - the unique index is what settles the race, not a pre-check.
    Task CreateAsync(Job job);

    // The reference is what an Agent has to hand, so it is a lookup in its own
    // right rather than a filter over the id path.
    Task<Job?> GetByReferenceAsync(string jobReference);

    Task<Job?> GetByIdAsync(string id);

    /// <summary>Lists Job Service-owned rows, optionally narrowed by status and locally projected technician id.</summary>
    Task<IReadOnlyList<Job>> ListAsync(string? status, string? assignedTechnicianId);

    // Records the assignment Dispatch published: the technician, the assignment,
    // the time, and the status the job takes as a result. Driven only by a
    // JobAssigned event - this service does not decide assignments.
    //
    // Returns true when the row was changed and false when it was not. False is
    // not an error in either direction; it means the update was correctly
    // declined, for one of two reasons the caller logs but does not retry:
    //
    //   the job is not in this database, or
    //   the job already carries an assignment at or after assignedAt.
    //
    // The second case covers both duplicate safety and stale-event protection,
    // and neither improves on a second attempt.
    Task<bool> ApplyAssignmentAsync(
        string jobId,
        string assignmentId,
        string technicianId,
        string technicianReference,
        string status,
        DateTime assignedAt);
}
