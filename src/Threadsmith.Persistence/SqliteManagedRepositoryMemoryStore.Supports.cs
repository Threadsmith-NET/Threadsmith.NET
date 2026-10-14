namespace Threadsmith.Persistence;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Normalized primary-source storage within managed-memory transactions.</summary>
public sealed partial class SqliteManagedRepositoryMemoryStore
{
    private static string NormalizeSourcePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static async Task ReadSupportsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string repositoryIdentity, List<RepositoryMemoryEntry> entries, RepositoryMemoryId? memoryId, RepositoryMemoryOwnership? ownership, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT memory_id, path, content_hash, observed_at, source_revision, dirty, unknown, observed_sequence
            FROM managed_memory_supports WHERE repository_identity = $repo ORDER BY memory_id, path;
            """;
        await using var command = CreateCommand(connection, transaction, sql, repositoryIdentity);
        ApplyReadFilters(command, ownership, memoryId);
        var supports = new Dictionary<RepositoryMemoryId, List<RepositoryMemorySupport>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var supportId = new RepositoryMemoryId(Guid.Parse(reader.GetString(0)));
            if (!supports.TryGetValue(supportId, out var sources))
            {
                sources = [];
                supports.Add(supportId, sources);
            }

            sources.Add(new RepositoryMemorySupport
            {
                Path = reader.GetString(1),
                ContentHash = reader.GetString(2),
                ObservedAt = ParseTimestamp(reader.GetString(3)),
                Revision = NullableString(reader, 4),
                IsDirty = reader.GetBoolean(5),
                IsUnknown = reader.GetBoolean(6),
                ObservedChangeSequence = reader.GetInt64(7),
            });
        }

        for (var index = 0; index < entries.Count; index++)
        {
            if (supports.TryGetValue(entries[index].Id, out var sources))
            {
                entries[index] = entries[index] with { Supports = sources.ToArray() };
            }
        }
    }

    private static async Task ReplaceSupportsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, RepositoryMemoryEntry entry, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "DELETE FROM managed_memory_supports WHERE repository_identity = $repo AND memory_id = $id;", entry.RepositoryIdentity);
        command.Parameters.AddWithValue("$id", entry.Id.Value.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            INSERT INTO managed_memory_supports(repository_identity, memory_id, path, content_hash, observed_at, source_revision, dirty, unknown, observed_sequence)
            VALUES($repo, $id, $path, $hash, $observed, $revision,
                MAX($dirty, EXISTS(SELECT 1 FROM repository_intelligence_changes WHERE repository_identity = $repo
                    AND sequence > $sequence AND (path = $path OR substr($path, 1, length(path) + 1) = path || '/'))),
                MAX($unknown, EXISTS(SELECT 1 FROM repository_intelligence_state WHERE repository_identity = $repo
                    AND gap_sequence > $sequence)), $sequence);
            """;
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var hash = command.Parameters.Add("$hash", SqliteType.Text);
        var observed = command.Parameters.Add("$observed", SqliteType.Text);
        var revision = command.Parameters.Add("$revision", SqliteType.Text);
        var dirty = command.Parameters.Add("$dirty", SqliteType.Integer);
        var unknown = command.Parameters.Add("$unknown", SqliteType.Integer);
        var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
        foreach (var support in entry.Supports)
        {
            path.Value = NormalizeSourcePath(support.Path);
            hash.Value = support.ContentHash;
            observed.Value = support.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            revision.Value = (object?)support.Revision ?? DBNull.Value;
            dirty.Value = support.IsDirty;
            unknown.Value = support.IsUnknown;
            sequence.Value = support.ObservedChangeSequence;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
