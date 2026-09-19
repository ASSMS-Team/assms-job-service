using JobService.Repositories;

namespace JobService.Services;

/// <summary>Applies packaged Job schema migrations once and in filename order.</summary>
///
// Mirrors DispatchMigrationRunner and ReportingMigrationRunner. Job Service was
// the only one of the three without a runner, which meant its migrations had to
// be applied by hand - and V02 adds the columns the JobAssigned consumer writes,
// so a deployment that forgot the manual step would start a consumer that failed
// on every message with an unknown-column error.
//
// Not a hosted service and not run at startup. Program.cs invokes it only for
// `--apply-migrations`, so the deployment applies the schema as its own step and
// a running instance never races another one mid-migration.
public sealed class JobMigrationRunner
{
    private readonly IDbConnectionFactory _connectionFactory;

    public JobMigrationRunner(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "migrations");

        // Thrown on rather than skipped. An empty or missing folder means the
        // migrations were not published into the image, and applying nothing
        // silently would let the service start against a schema that has not
        // been brought up to date.
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Job migrations were not published to '{directory}'.");

        // Ordinal, so V02 always sorts after V01 regardless of the host's
        // culture. A culture-sensitive sort is not guaranteed to agree.
        var files = Directory.GetFiles(directory, "V*__*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var history = connection.CreateCommand())
        {
            history.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    filename VARCHAR(255) NOT NULL PRIMARY KEY,
                    applied_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
                );
                """;
            await history.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);

            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE filename = @name;";
                check.Parameters.AddWithValue("@name", name);

                if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) > 0) continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                await using (var apply = connection.CreateCommand())
                {
                    apply.Transaction = transaction;
                    apply.CommandText = await File.ReadAllTextAsync(file, cancellationToken);
                    await apply.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText = "INSERT INTO schema_migrations (filename) VALUES (@name);";
                    record.Parameters.AddWithValue("@name", name);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                // MySQL commits DDL implicitly, so this rollback cannot undo an
                // ALTER that already ran. What it does undo is the
                // schema_migrations row, so a partially applied file is not
                // recorded as done and the failure surfaces on the next attempt
                // rather than being skipped. V02 is guarded against
                // information_schema for exactly this reason - re-running it
                // after a partial failure is a no-op, not error 1060.
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }
}
