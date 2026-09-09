namespace Threadsmith.Persistence;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Durable ownership routing for memory receipts when sessions and repository memories use different databases.</summary>
public sealed partial class SqliteEventStore
{
    /// <summary>Durably records the host-selected memory database before a run can write an inclusion receipt there.</summary>
    public async Task RegisterMemoryInclusionStoreAsync(
        RunId runId,
        string repositoryRoot,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Inclusion retention requires the owning user run.", nameof(runId));
        }

        // The owning event database is already authorized, including a configured custom filename.
        // Its receipts use the local retention query and need no external-path registration.
        var ownDatabase = Path.GetFullPath(new SqliteConnectionStringBuilder(_connectionString).DataSource);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(ownDatabase, Path.GetFullPath(databasePath), comparison))
        {
            return;
        }

        var path = RepositoryMemoryDatabasePath.Verify(repositoryRoot, databasePath);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_inclusion_retention_targets(run_id, repository_identity, repository_root, database_path, registered_at)
            VALUES($run, $repo, $root, $path, $now)
            ON CONFLICT(run_id, repository_identity) DO UPDATE SET
                repository_root = excluded.repository_root, database_path = excluded.database_path,
                registered_at = excluded.registered_at;
            """;
        command.Parameters.AddWithValue("$run", runId.Value.ToString("D"));
        command.Parameters.AddWithValue("$repo", RepositoryIdentity.Create(repositoryRoot));
        command.Parameters.AddWithValue("$root", Path.GetFullPath(repositoryRoot));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Prunes local and rebound receipts and returns bounded metadata diagnostics for foreign targets deferred until retry.</summary>
    public async Task<IReadOnlyList<string>> PruneExpiredMemoryInclusionsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'managed_memory_inclusions';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            return [];
        }

        // The owning database remains authoritative for local receipt retention.
        await using var local = connection.CreateCommand();
        local.CommandText = """
            DELETE FROM managed_memory_inclusions WHERE run_id IN (
                SELECT json_extract(payload, '$.RunId.Value') FROM domain_events
                WHERE session_id IN (
                    SELECT session_id FROM domain_events GROUP BY session_id
                    HAVING MAX(json_extract(payload, '$.OccurredAt')) < $cutoff
                ) AND json_valid(payload)
            );
            """;
        local.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
        await local.ExecuteNonQueryAsync(cancellationToken);
        var warnings = new List<string>();
        var failureCount = 0;
        var failedDestinations = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string? afterRun = null;
        string? afterRepository = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = await ReadExpiredMemoryTargetsAsync(connection, cutoff, afterRun, afterRepository, cancellationToken);
            if (targets.Count == 0)
            {
                if (failureCount > warnings.Count)
                {
                    warnings.Add($"{failureCount - warnings.Count} additional memory receipt retention targets were deferred.");
                }

                return warnings;
            }

            foreach (var target in targets)
            {
                afterRun = target.RunId;
                afterRepository = target.RepositoryIdentity;
                var destination = target.DatabasePath;
                try
                {
                    destination = Path.GetFullPath(target.DatabasePath);
                    if (failedDestinations.Contains(destination))
                    {
                        continue;
                    }

                    await PruneMemoryTargetAsync(target, cancellationToken);
                }
                catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    failedDestinations.Add(destination);
                    failureCount++;
                    if (warnings.Count < 31)
                    {
                        var runLabel = Guid.TryParse(target.RunId, out var run) ? run.ToString("D") : "invalid";
                        warnings.Add($"Memory receipt retention for run {runLabel} was deferred ({exception.GetType().Name}); its durable target is retained for retry.");
                    }

                    continue;
                }

                await using var remove = connection.CreateCommand();
                remove.CommandText = """
                    DELETE FROM memory_inclusion_retention_targets WHERE run_id = $run
                        AND repository_identity = $repo AND registered_at = $registered;
                    """;
                remove.Parameters.AddWithValue("$run", target.RunId);
                remove.Parameters.AddWithValue("$repo", target.RepositoryIdentity);
                remove.Parameters.AddWithValue("$registered", target.RegisteredAt);
                await remove.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task<IReadOnlyList<MemoryRetentionTarget>> ReadExpiredMemoryTargetsAsync(
        SqliteConnection connection,
        DateTimeOffset cutoff,
        string? afterRun,
        string? afterRepository,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH run_sessions AS (
                SELECT DISTINCT json_extract(payload, '$.RunId.Value') AS run_id, session_id
                FROM domain_events WHERE json_valid(payload)
            ), expired_sessions AS (
                SELECT session_id FROM domain_events GROUP BY session_id
                HAVING MAX(json_extract(payload, '$.OccurredAt')) < $cutoff
            )
            SELECT t.run_id, t.repository_identity, t.repository_root, t.database_path, t.registered_at
            FROM memory_inclusion_retention_targets t
            WHERE (EXISTS(SELECT 1 FROM run_sessions r JOIN expired_sessions e USING(session_id) WHERE r.run_id = t.run_id)
                OR (t.registered_at < $cutoff AND NOT EXISTS(SELECT 1 FROM run_sessions r WHERE r.run_id = t.run_id)))
                AND ($afterRun IS NULL OR t.run_id > $afterRun OR (t.run_id = $afterRun AND t.repository_identity > $afterRepository))
            ORDER BY t.run_id, t.repository_identity LIMIT 128;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$afterRun", (object?)afterRun ?? DBNull.Value);
        command.Parameters.AddWithValue("$afterRepository", (object?)afterRepository ?? DBNull.Value);
        var targets = new List<MemoryRetentionTarget>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            targets.Add(new MemoryRetentionTarget(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return targets;
    }

    private static async Task PruneMemoryTargetAsync(MemoryRetentionTarget target, CancellationToken cancellationToken)
    {
        var path = RepositoryMemoryDatabasePath.Verify(target.RepositoryRoot, target.DatabasePath);
        if (!Guid.TryParse(target.RunId, out var runId)
            || !string.Equals(RepositoryIdentity.Create(target.RepositoryRoot), target.RepositoryIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Memory retention identity does not match its registered root or run.");
        }

        if (!File.Exists(path))
        {
            return;
        }

        // ReadWrite prevents creating files after removal. A locked foreign database is deferred quickly.
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        };
        await using var connection = new SqliteConnection(options.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM managed_memory_inclusions WHERE repository_identity = $repo AND run_id = $run;
            """;
        command.Parameters.AddWithValue("$repo", target.RepositoryIdentity);
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record MemoryRetentionTarget(
        string RunId,
        string RepositoryIdentity,
        string RepositoryRoot,
        string DatabasePath,
        string RegisteredAt);
}
