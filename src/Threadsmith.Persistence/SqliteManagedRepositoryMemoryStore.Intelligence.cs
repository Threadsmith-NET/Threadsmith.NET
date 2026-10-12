namespace Threadsmith.Persistence;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Intelligence bookkeeping shares managed-memory transactions and candidate revisions.</summary>
public sealed partial class SqliteManagedRepositoryMemoryStore
{
    /// <inheritdoc />
    public async Task<RepositoryIntelligenceChangePage> ReadIntelligenceChangesAsync(
        string repositoryIdentity, long checkpoint, string? afterPath, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        await using var connection = new SqliteConnection(_connectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var command = CreateCommand(
            connection,
            null,
            "SELECT path FROM repository_intelligence_changes WHERE repository_identity = $repo AND sequence <= $checkpoint AND ($after IS NULL OR path > $after) ORDER BY path;",
            repositoryIdentity);
        command.Parameters.AddWithValue("$checkpoint", checkpoint);
        command.Parameters.AddWithValue("$after", (object?)afterPath ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var paths = new List<string>();
        var bytes = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(0);
            var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(path).Length + 1;
            if (bytes + size > maximumBytes && paths.Count > 0)
            {
                return new RepositoryIntelligenceChangePage(paths, paths[^1]);
            }

            paths.Add(path);
            bytes += size;
        }

        return new RepositoryIntelligenceChangePage(paths, null);
    }

    /// <inheritdoc />
    public async Task<long> GetIntelligenceCheckpointAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var command = CreateCommand(connection, null, "SELECT change_sequence FROM repository_intelligence_state WHERE repository_identity = $repo;", repositoryIdentity);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task ClearIntelligenceFaultAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = CreateCommand(connection, transaction, "UPDATE repository_intelligence_state SET fault = NULL WHERE repository_identity = $repo;", repositoryIdentity);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AdvanceRevisionAsync(connection, transaction, repositoryIdentity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReconcileIntelligenceObservationAsync(
        string repositoryIdentity, long checkpoint, IReadOnlyList<RepositoryMemorySupport> observations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        await using var connection = new SqliteConnection(_connectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        const string reconcile = """
            UPDATE managed_memory_supports SET unknown = 0, dirty = MAX(dirty, content_hash != $hash), observed_sequence = $checkpoint
            WHERE repository_identity = $repo AND path = $path AND observed_sequence <= $checkpoint
              AND NOT EXISTS (SELECT 1 FROM repository_intelligence_state WHERE repository_identity = $repo AND gap_sequence > $checkpoint)
              AND NOT EXISTS (SELECT 1 FROM repository_intelligence_changes c WHERE c.repository_identity = $repo AND c.sequence > $checkpoint
                  AND (c.path = managed_memory_supports.path OR substr(managed_memory_supports.path, 1, length(c.path) + 1) = c.path || '/'));
            """;
        await using var command = CreateCommand(connection, transaction, reconcile, repositoryIdentity);
        command.Parameters.AddWithValue("$checkpoint", checkpoint);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var hash = command.Parameters.Add("$hash", SqliteType.Text);
        foreach (var observation in observations)
        {
            path.Value = NormalizeSourcePath(observation.Path);
            hash.Value = observation.ContentHash;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        command.CommandText = "UPDATE repository_intelligence_state SET observation_gap = 0 WHERE repository_identity = $repo AND gap_sequence <= $checkpoint;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AdvanceRevisionAsync(connection, transaction, repositoryIdentity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceState> GetIntelligenceStateAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        await using var connection = new SqliteConnection(_connectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = CreateCommand(connection, transaction, "SELECT onboarded, change_sequence, refreshed_sequence, observation_gap, fault FROM repository_intelligence_state WHERE repository_identity = $repo;", repositoryIdentity);
        var state = new RepositoryIntelligenceState();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                state = new RepositoryIntelligenceState
                {
                    Onboarded = reader.GetBoolean(0),
                    ChangeSequence = reader.GetInt64(1),
                    RefreshedSequence = reader.GetInt64(2),
                    ObservationGap = reader.GetBoolean(3),
                    Fault = NullableString(reader, 4),
                };
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    /// <inheritdoc />
    public async Task RecordIntelligenceChangesAsync(
        string repositoryIdentity, IReadOnlyList<string> paths, bool observationGap, string? fault = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0 && !observationGap && fault is null)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        const string advance = """
            INSERT INTO repository_intelligence_state(repository_identity, change_sequence, observation_gap, gap_sequence, fault)
            VALUES($repo, 1, $gap, CASE WHEN $gap OR $fault IS NOT NULL THEN 1 ELSE 0 END, $fault)
            ON CONFLICT(repository_identity) DO UPDATE SET change_sequence = change_sequence + 1,
                gap_sequence = CASE WHEN $gap OR $fault IS NOT NULL THEN change_sequence + 1 ELSE gap_sequence END,
                observation_gap = MAX(observation_gap, $gap), fault = COALESCE($fault, fault);
            SELECT change_sequence FROM repository_intelligence_state WHERE repository_identity = $repo;
            """;
        await using var command = CreateCommand(connection, transaction, advance, repositoryIdentity);
        command.Parameters.AddWithValue("$gap", observationGap);
        command.Parameters.AddWithValue("$fault", (object?)fault ?? DBNull.Value);
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$sequence", sequence);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        command.CommandText = """
            INSERT INTO repository_intelligence_changes(repository_identity, path, sequence) VALUES($repo, $path, $sequence)
            ON CONFLICT(repository_identity, path) DO UPDATE SET sequence = $sequence;
            UPDATE managed_memory_supports SET dirty = 1 WHERE repository_identity = $repo
                AND (path = $path OR substr(path, 1, length($path) + 1) = $path || '/');
            """;
        foreach (var changed in paths.Distinct(StringComparer.Ordinal))
        {
            path.Value = NormalizeSourcePath(changed);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (observationGap || fault is not null)
        {
            command.CommandText = "UPDATE managed_memory_supports SET unknown = 1 WHERE repository_identity = $repo;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await AdvanceRevisionAsync(connection, transaction, repositoryIdentity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
