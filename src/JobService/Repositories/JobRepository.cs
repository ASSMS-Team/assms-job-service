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
                   created_by, status, assignment_id, assigned_technician_id,
                   assigned_technician_reference, assigned_at, started_at,
                   created_at, updated_at
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

    public async Task<bool> ApplyAssignmentAsync(
        string jobId,
        string assignmentId,
        string technicianId,
        string technicianReference,
        string status,
        DateTime assignedAt)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        // The second half of the WHERE clause is the whole guard, and it does two
        // jobs at once.
        //
        // Duplicate safety: a redelivered JobAssigned carries the same assignedAt
        // as the delivery that already landed, so the strict < is false, no row
        // matches, and nothing is written twice. Kafka delivers at least once and
        // the consumer commits its offset after this call, so a redelivery is
        // guaranteed to happen eventually - it has to be harmless.
        //
        // Stale-event protection: an older assignment arriving after a newer one
        // is refused for the same reason. Within one job that cannot happen
        // today, because every event about a job is keyed by jobId and therefore
        // ordered inside its partition - but it becomes possible the moment US-15
        // allows reassignment and someone replays the topic from the beginning.
        // Ordering by the event's own timestamp rather than by arrival means the
        // row ends up carrying the latest assignment either way.
        //
        // Comparing timestamps rather than checking assignment_id is deliberate.
        // An id comparison can only answer "is this the same event", which stops
        // a duplicate but cannot tell an older assignment from a newer one.
        //
        // updated_at is not written here - the column's ON UPDATE clause moves it
        // whenever any of these columns actually changes, and naming it would be
        // a second place to keep that in step.
        command.CommandText = @"
            UPDATE jobs
            SET status                        = @status,
                assignment_id                 = @assignmentId,
                assigned_technician_id        = @technicianId,
                assigned_technician_reference = @technicianReference,
                assigned_at                   = @assignedAt
            WHERE id = @jobId
              AND (assigned_at IS NULL OR assigned_at < @assignedAt);";

        command.Parameters.AddWithValue("@jobId", jobId);
        command.Parameters.AddWithValue("@assignmentId", assignmentId);
        command.Parameters.AddWithValue("@technicianId", technicianId);
        command.Parameters.AddWithValue("@technicianReference", technicianReference);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@assignedAt", assignedAt);

        var rowsAffected = await command.ExecuteNonQueryAsync();

        return rowsAffected > 0;
    }

    // Updates status to IN_PROGRESS and records started_at in a single
    // statement. The WHERE clause is the whole guard:
    //   status = 'ASSIGNED'         — rejects any invalid lifecycle transition
    //   assigned_technician_id = @technicianId — rejects a non-assignee caller
    //   id = @jobId                  — scopes to the requested job
    //
    // updated_at is not named: the ON UPDATE clause on the column moves it
    // automatically whenever any other column actually changes.
    public async Task<bool> StartJobAsync(string jobId, string technicianId, DateTime startedAt)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE jobs
            SET    status     = 'IN_PROGRESS',
                   started_at = @startedAt
            WHERE  id                     = @jobId
              AND  status                 = 'ASSIGNED'
              AND  assigned_technician_id = @technicianId;";

        command.Parameters.AddWithValue("@jobId", jobId);
        command.Parameters.AddWithValue("@technicianId", technicianId);
        command.Parameters.AddWithValue("@startedAt", startedAt);

        var rowsAffected = await command.ExecuteNonQueryAsync();

        return rowsAffected > 0;
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

    public async Task<IReadOnlyList<Job>> ListAsync(string? status, string? assignedTechnicianId)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        var predicates = new List<string>();

        if (status is not null)
        {
            predicates.Add("status = @status");
            command.Parameters.AddWithValue("@status", status);
        }

        if (assignedTechnicianId is not null)
        {
            predicates.Add("assigned_technician_id = @assignedTechnicianId");
            command.Parameters.AddWithValue("@assignedTechnicianId", assignedTechnicianId);
        }

        command.CommandText = SelectColumns
            + (predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates))
            + " ORDER BY created_at DESC, job_reference ASC;";

        await using var reader = (MySqlDataReader)await command.ExecuteReaderAsync();
        var jobs = new List<Job>();
        while (await reader.ReadAsync()) jobs.Add(Read(reader));
        return jobs;
    }

    private static async Task<Job?> ReadSingleAsync(MySqlCommand command)
    {
        await using var reader = (MySqlDataReader)await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return Read(reader);
    }

    private static Job Read(MySqlDataReader reader)
    {
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
        var assignmentIdOrdinal = reader.GetOrdinal("assignment_id");
        var assignedTechnicianIdOrdinal = reader.GetOrdinal("assigned_technician_id");
        var assignedTechnicianReferenceOrdinal = reader.GetOrdinal("assigned_technician_reference");
        var assignedAtOrdinal = reader.GetOrdinal("assigned_at");
        var startedAtOrdinal = reader.GetOrdinal("started_at");
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
            AssignmentId = reader.IsDBNull(assignmentIdOrdinal) ? null : reader.GetValue(assignmentIdOrdinal).ToString(),
            AssignedTechnicianId = reader.IsDBNull(assignedTechnicianIdOrdinal) ? null : reader.GetValue(assignedTechnicianIdOrdinal).ToString(),
            AssignedTechnicianReference = reader.IsDBNull(assignedTechnicianReferenceOrdinal) ? null : reader.GetString(assignedTechnicianReferenceOrdinal),
            AssignedAt = reader.IsDBNull(assignedAtOrdinal) ? null : reader.GetDateTime(assignedAtOrdinal),
            StartedAt = reader.IsDBNull(startedAtOrdinal) ? null : reader.GetDateTime(startedAtOrdinal),
            CreatedAt = reader.GetDateTime(createdAtOrdinal),
            UpdatedAt = reader.GetDateTime(updatedAtOrdinal)
        };
    }
<<<<<<< Updated upstream
=======

    public async Task<ServiceWorkRecord> AddWorkRecordAsync(ServiceWorkRecord record)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO service_work_records (
                id,
                job_id,
                job_reference,
                technician_id,
                technician_reference,
                content,
                recorded_at,
                created_at
            ) VALUES (
                @id,
                @jobId,
                @jobReference,
                @technicianId,
                @technicianReference,
                @content,
                @recordedAt,
                @createdAt
            );";

        command.Parameters.AddWithValue("@id", record.Id);
        command.Parameters.AddWithValue("@jobId", record.JobId);
        command.Parameters.AddWithValue("@jobReference", record.JobReference);
        command.Parameters.AddWithValue("@technicianId", record.TechnicianId);
        command.Parameters.AddWithValue("@technicianReference", record.TechnicianReference);
        command.Parameters.AddWithValue("@content", record.Content);
        command.Parameters.AddWithValue("@recordedAt", record.RecordedAt);
        command.Parameters.AddWithValue("@createdAt", record.CreatedAt);

        await command.ExecuteNonQueryAsync();
        return record;
    }

    public async Task<IReadOnlyList<ServiceWorkRecord>> GetWorkRecordsByJobIdAsync(string jobId)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, job_id, job_reference, technician_id, technician_reference, content, recorded_at, created_at
            FROM service_work_records
            WHERE job_id = @jobId
            ORDER BY recorded_at ASC;";

        command.Parameters.AddWithValue("@jobId", jobId);

        await using var reader = (MySqlDataReader)await command.ExecuteReaderAsync();
        var records = new List<ServiceWorkRecord>();
        while (await reader.ReadAsync())
        {
            var idOrdinal = reader.GetOrdinal("id");
            var jobIdOrdinal = reader.GetOrdinal("job_id");
            var jobReferenceOrdinal = reader.GetOrdinal("job_reference");
            var technicianIdOrdinal = reader.GetOrdinal("technician_id");
            var technicianReferenceOrdinal = reader.GetOrdinal("technician_reference");
            var contentOrdinal = reader.GetOrdinal("content");
            var recordedAtOrdinal = reader.GetOrdinal("recorded_at");
            var createdAtOrdinal = reader.GetOrdinal("created_at");

            records.Add(new ServiceWorkRecord
            {
                Id = reader.GetValue(idOrdinal)?.ToString() ?? string.Empty,
                JobId = reader.GetValue(jobIdOrdinal)?.ToString() ?? string.Empty,
                JobReference = reader.GetString(jobReferenceOrdinal),
                TechnicianId = reader.GetValue(technicianIdOrdinal)?.ToString() ?? string.Empty,
                TechnicianReference = reader.GetString(technicianReferenceOrdinal),
                Content = reader.GetString(contentOrdinal),
                RecordedAt = reader.GetDateTime(recordedAtOrdinal),
                CreatedAt = reader.GetDateTime(createdAtOrdinal)
            });
        }

        return records;
    }

    public async Task<bool> UpdateWorkRecordAsync(string recordId, string jobId, string content)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE service_work_records
            SET content = @content
            WHERE id = @recordId AND job_id = @jobId;";

        command.Parameters.AddWithValue("@recordId", recordId);
        command.Parameters.AddWithValue("@jobId", jobId);
        command.Parameters.AddWithValue("@content", content);

        var rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected > 0;
    }
>>>>>>> Stashed changes
}
