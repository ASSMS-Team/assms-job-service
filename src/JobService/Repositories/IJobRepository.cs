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
}
