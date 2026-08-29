using JobService.Models;
using MySqlConnector;

namespace JobService.Repositories;

public class JobRepository : IJobRepository
{
    // The SELECT list is written once: GetByIdAsync and GetByReferenceAsync
    // differ only in their WHERE clause, and two copies would be two things to
    // keep in step when a column is added.
    private const string SelectColumns = @"
            SELECT id, job_reference, customer_id, asset_id, service_category,
                   problem_description, priority, region, scheduled_date,
                   created_by, status, created_at, updated_at
            FROM jobs";

    private readonly IDbConnectionFactory _connectionFactory;

    public JobRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    // status, created_at and updated_at have database defaults, so none of the
    // three is written here. status is left to its DEFAULT 'CREATED' rather
    // than sent explicitly: the column already says what a new job starts as,
    // and naming it here would be a second place to keep that in step.
    public async Task CreateAsync(Job job)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO jobs
                (id, job_reference, customer_id, asset_id, service_category,
                 problem_description, priority, region, scheduled_date, created_by)
            VALUES
                (@id, @jobReference, @customerId, @assetId, @serviceCategory,
                 @problemDescription, @priority, @region, @scheduledDate, @createdBy);";

        command.Parameters.AddWithValue("@id", job.Id);
        command.Parameters.AddWithValue("@jobReference", job.JobReference);
        command.Parameters.AddWithValue("@customerId", job.CustomerId);
        command.Parameters.AddWithValue("@assetId", job.AssetId);
        command.Parameters.AddWithValue("@serviceCategory", job.ServiceCategory);
        command.Parameters.AddWithValue("@problemDescription", job.ProblemDescription);
        command.Parameters.AddWithValue("@priority", job.Priority);
        command.Parameters.AddWithValue("@region", job.Region);
        // MySqlConnector takes a DateOnly parameter as-is against a DATE column -
        // no conversion to DateTime is needed on the way in. Null is the normal
        // case here: nothing schedules a job at creation.
        command.Parameters.AddWithValue("@scheduledDate", (object?)job.ScheduledDate ?? DBNull.Value);
        // Unlike status this has no column default, so it is always written.
        command.Parameters.AddWithValue("@createdBy", job.CreatedBy);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<Job?> GetByIdAsync(string id)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + @"
            WHERE id = @id;";

        command.Parameters.AddWithValue("@id", id);

        return await ReadSingleAsync(command);
    }

    public async Task<Job?> GetByReferenceAsync(string jobReference)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        // No LIMIT is needed: job_reference carries a unique index, so this
        // matches at most one row by construction.
        command.CommandText = SelectColumns + @"
            WHERE job_reference = @jobReference;";

        command.Parameters.AddWithValue("@jobReference", jobReference);

        return await ReadSingleAsync(command);
    }

    private static async Task<Job?> ReadSingleAsync(MySqlCommand command)
    {
        await using var reader = (MySqlDataReader)await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        // Ordinals are looked up by name so that reordering the SELECT list
        // cannot silently shift the mapping.
        var idOrdinal = reader.GetOrdinal("id");
        var jobReferenceOrdinal = reader.GetOrdinal("job_reference");
        var customerIdOrdinal = reader.GetOrdinal("customer_id");
        var assetIdOrdinal = reader.GetOrdinal("asset_id");
        var serviceCategoryOrdinal = reader.GetOrdinal("service_category");
        var problemDescriptionOrdinal = reader.GetOrdinal("problem_description");
        var priorityOrdinal = reader.GetOrdinal("priority");
        var regionOrdinal = reader.GetOrdinal("region");
        var scheduledDateOrdinal = reader.GetOrdinal("scheduled_date");
        var createdByOrdinal = reader.GetOrdinal("created_by");
        var statusOrdinal = reader.GetOrdinal("status");
        var createdAtOrdinal = reader.GetOrdinal("created_at");
        var updatedAtOrdinal = reader.GetOrdinal("updated_at");

        return new Job
        {
            // MySqlConnector reads CHAR(36) as a Guid by default (GuidFormat=Char36),
            // so GetString throws on these columns - go through the boxed value instead.
            Id = reader.GetValue(idOrdinal)?.ToString() ?? string.Empty,
            JobReference = reader.GetString(jobReferenceOrdinal),
            CustomerId = reader.GetValue(customerIdOrdinal)?.ToString() ?? string.Empty,
            AssetId = reader.GetValue(assetIdOrdinal)?.ToString() ?? string.Empty,
            ServiceCategory = reader.GetString(serviceCategoryOrdinal),
            ProblemDescription = reader.GetString(problemDescriptionOrdinal),
            Priority = reader.GetString(priorityOrdinal),
            Region = reader.GetString(regionOrdinal),
            // A DATE column reports its field type as DateTime and GetValue boxes
            // one, so GetDateTime would hand back a midnight DateTime. Asking for
            // the value as DateOnly is what makes MySqlConnector do the conversion.
            ScheduledDate = reader.IsDBNull(scheduledDateOrdinal)
                ? null
                : reader.GetFieldValue<DateOnly>(scheduledDateOrdinal),
            CreatedBy = reader.GetValue(createdByOrdinal)?.ToString() ?? string.Empty,
            Status = reader.GetString(statusOrdinal),
            CreatedAt = reader.GetDateTime(createdAtOrdinal),
            UpdatedAt = reader.GetDateTime(updatedAtOrdinal)
        };
    }
}
