namespace Threadsmith.Persistence;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Version 10: imports explicit active manual memories and retires the automatic repository store.</summary>
public sealed class ManagedRepositoryMemorySchemaMigration : IDatabaseMigration
{
    private readonly int _maximumMemoryCount;

    /// <summary>Initializes a new instance of the <see cref="ManagedRepositoryMemorySchemaMigration"/> class.</summary>
    public ManagedRepositoryMemorySchemaMigration(int maximumMemoryCount = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMemoryCount);
        _maximumMemoryCount = maximumMemoryCount;
    }

    /// <inheritdoc />
    public int Version => 10;

    /// <inheritdoc />
    public string Name => "Explicit repository memories with hybrid retrieval";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS managed_memory_repositories (
                repository_identity TEXT PRIMARY KEY, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS managed_memories (
                memory_id TEXT NOT NULL UNIQUE, repository_identity TEXT NOT NULL, text TEXT NOT NULL,
                content_hash TEXT NOT NULL, content_revision INTEGER NOT NULL CHECK(content_revision > 0),
                origin TEXT NOT NULL CHECK(origin IN ('manual', 'model')), sensitivity INTEGER NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                source_session_id TEXT NULL, source_run_id TEXT NULL, source_invocation_id TEXT NULL,
                inclusion_count INTEGER NOT NULL DEFAULT 0 CHECK(inclusion_count >= 0), last_included_at TEXT NULL,
                embedding BLOB NULL, embedding_space_id TEXT NULL, embedding_dimensions INTEGER NOT NULL DEFAULT 0,
                embedding_content_hash TEXT NULL, embedding_revision INTEGER NULL,
                UNIQUE(repository_identity, content_hash));
            CREATE INDEX IF NOT EXISTS ix_managed_memories_repository ON managed_memories(repository_identity, memory_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS managed_memories_fts USING fts5(text, content='managed_memories', content_rowid='rowid', tokenize='unicode61');
            CREATE TRIGGER IF NOT EXISTS managed_memories_insert AFTER INSERT ON managed_memories BEGIN
                INSERT INTO managed_memories_fts(rowid, text) VALUES(new.rowid, new.text);
            END;
            CREATE TRIGGER IF NOT EXISTS managed_memories_delete AFTER DELETE ON managed_memories BEGIN
                INSERT INTO managed_memories_fts(managed_memories_fts, rowid, text) VALUES('delete', old.rowid, old.text);
                DELETE FROM managed_memory_inclusions WHERE repository_identity = old.repository_identity AND memory_id = old.memory_id;
            END;
            CREATE TRIGGER IF NOT EXISTS managed_memories_text_update AFTER UPDATE OF text ON managed_memories
            WHEN old.text != new.text BEGIN
                INSERT INTO managed_memories_fts(managed_memories_fts, rowid, text) VALUES('delete', old.rowid, old.text);
                INSERT INTO managed_memories_fts(rowid, text) VALUES(new.rowid, new.text);
            END;
            CREATE TABLE IF NOT EXISTS managed_memory_inclusions (
                repository_identity TEXT NOT NULL, run_id TEXT NOT NULL, memory_id TEXT NOT NULL,
                content_revision INTEGER NOT NULL,
                PRIMARY KEY(repository_identity, run_id, memory_id, content_revision));
            CREATE TABLE IF NOT EXISTS memory_inclusion_retention_targets (
                run_id TEXT NOT NULL, repository_identity TEXT NOT NULL,
                repository_root TEXT NOT NULL, database_path TEXT NOT NULL, registered_at TEXT NOT NULL,
                PRIMARY KEY(run_id, repository_identity));
            CREATE TABLE IF NOT EXISTS managed_memory_migration (
                version INTEGER PRIMARY KEY, imported_count INTEGER NOT NULL, dropped_count INTEGER NOT NULL,
                evicted_count INTEGER NOT NULL, oversized_count INTEGER NOT NULL, backup_path TEXT NULL);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken);
        var candidates = await ReadManualEntriesAsync(connection, cancellationToken);
        var imported = 0;
        var oversized = 0;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in candidates.Entries)
        {
            if (!hashes.Add(entry.RepositoryIdentity + ":" + entry.ContentHash))
            {
                continue;
            }

            await SqliteManagedRepositoryMemoryStore.InsertEntryAsync(connection, null, entry, cancellationToken);
            imported++;
            if (entry.Text.Length > 2_000)
            {
                oversized++;
            }
        }

        var evicted = 0;
        foreach (var repository in candidates.Entries.Select(entry => entry.RepositoryIdentity).Distinct(StringComparer.Ordinal))
        {
            var entries = await SqliteManagedRepositoryMemoryStore.ReadEntriesAsync(connection, null, repository, [], cancellationToken);
            evicted += (await SqliteManagedRepositoryMemoryStore.EvictAsync(
                connection, null, repository, entries, _maximumMemoryCount, DateTimeOffset.UtcNow, cancellationToken)).Count;
            await SqliteManagedRepositoryMemoryStore.AdvanceRevisionAsync(connection, null, repository, cancellationToken);
        }

        // MigrationRunner creates and verifies a unique SQLite-consistent backup before this
        // transaction. Conversation archives, compaction artifacts and historical events remain.
        await using var retire = connection.CreateCommand();
        retire.CommandText = """
            INSERT OR IGNORE INTO managed_memory_migration(version, imported_count, dropped_count, evicted_count, oversized_count)
                VALUES(10, $imported, $dropped, $evicted, $oversized);
            DROP TABLE IF EXISTS repository_memory_sources;
            DROP TABLE IF EXISTS repository_memory_scope;
            DROP TABLE IF EXISTS repository_memory_snapshots;
            DROP TABLE IF EXISTS repository_memory_invalidations;
            DROP TABLE IF EXISTS repository_memory;
            """;
        retire.Parameters.AddWithValue("$imported", imported);
        retire.Parameters.AddWithValue("$dropped", candidates.TotalCount - imported);
        retire.Parameters.AddWithValue("$evicted", evicted);
        retire.Parameters.AddWithValue("$oversized", oversized);
        await retire.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LegacyManualEntries> ReadManualEntriesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'repository_memory';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            return new LegacyManualEntries([], 0);
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM repository_memory;";
        var totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT memory_id, repository_identity, content, sensitivity, created_at, updated_at
            FROM repository_memory m WHERE authority = $authority AND validity = $validity
                AND schema_version = 1 AND EXISTS(SELECT 1 FROM repository_memory_sources s
                    WHERE s.memory_id = m.memory_id AND s.source_kind = $source)
            ORDER BY created_at, memory_id;
            """;
        command.Parameters.AddWithValue("$authority", (int)RepositoryMemoryAuthority.UserAuthored);
        command.Parameters.AddWithValue("$validity", (int)RepositoryMemoryValidity.Active);
        command.Parameters.AddWithValue("$source", (int)RepositoryMemorySourceKind.UserCommand);
        var entries = new List<RepositoryMemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id)
                || !DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                || !DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
            {
                continue;
            }

            var text = reader.GetString(2).ReplaceLineEndings("\n").Trim();
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(reader.GetString(1)))
            {
                continue;
            }

            var sensitivity = (ConversationSensitivity)reader.GetInt32(3);
            entries.Add(new RepositoryMemoryEntry
            {
                Id = new RepositoryMemoryId(id),
                RepositoryIdentity = reader.GetString(1),
                Text = text,
                ContentHash = SqliteManagedRepositoryMemoryStore.ComputeHash(text),
                Origin = RepositoryMemoryOrigin.Manual,
                Sensitivity = Enum.IsDefined(sensitivity) ? sensitivity : ConversationSensitivity.Sensitive,
                CreatedAt = created.ToUniversalTime(),
                UpdatedAt = updated.ToUniversalTime(),
            });
        }

        return new LegacyManualEntries(entries, totalCount);
    }

    private sealed record LegacyManualEntries(IReadOnlyList<RepositoryMemoryEntry> Entries, int TotalCount);
}
