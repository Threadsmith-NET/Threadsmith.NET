namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Partitions managed memory ownership and stores normalized primary-source provenance.</summary>
public sealed class RepositoryIntelligenceSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 14;

    /// <inheritdoc />
    public string Name => "Repository intelligence memory ownership and provenance";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sql FROM sqlite_schema WHERE tbl_name = 'managed_memories' AND type IN ('trigger', 'index')
                AND sql IS NOT NULL AND name NOT IN ('managed_memories_insert', 'managed_memories_delete',
                    'managed_memories_text_update', 'ix_managed_memories_identity', 'ix_managed_memories_repository');
            """;
        var retainedSchema = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                retainedSchema.Add(reader.GetString(0));
            }
        }

        command.CommandText = """
            CREATE TEMP TABLE saved_memory_concepts AS SELECT * FROM managed_memory_concepts;
            DROP TABLE managed_memory_concepts;
            DROP TRIGGER managed_memories_insert;
            DROP TRIGGER managed_memories_delete;
            DROP TRIGGER managed_memories_text_update;
            DROP TABLE managed_memories_fts;
            CREATE TABLE managed_memories_new (
                memory_id TEXT NOT NULL UNIQUE, repository_identity TEXT NOT NULL, text TEXT NOT NULL,
                content_hash TEXT NOT NULL, content_revision INTEGER NOT NULL CHECK(content_revision > 0),
                origin TEXT NOT NULL CHECK(origin IN ('manual', 'model')), sensitivity INTEGER NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                source_session_id TEXT NULL, source_run_id TEXT NULL, source_invocation_id TEXT NULL,
                inclusion_count INTEGER NOT NULL DEFAULT 0 CHECK(inclusion_count >= 0), last_included_at TEXT NULL,
                embedding BLOB NULL, embedding_space_id TEXT NULL, embedding_dimensions INTEGER NOT NULL DEFAULT 0,
                embedding_content_hash TEXT NULL, embedding_revision INTEGER NULL,
                memory_type INTEGER NOT NULL DEFAULT 0 CHECK(memory_type IN (0, 1)),
                kind INTEGER NOT NULL DEFAULT 0 CHECK(kind BETWEEN 0 AND 5),
                ownership INTEGER NOT NULL DEFAULT 0 CHECK(ownership IN (0, 1)),
                CHECK(ownership = 0 OR memory_type = 0),
                UNIQUE(repository_identity, ownership, content_hash));
            INSERT INTO managed_memories_new SELECT *, 0 FROM managed_memories;
            DROP TABLE managed_memories;
            ALTER TABLE managed_memories_new RENAME TO managed_memories;
            CREATE UNIQUE INDEX ix_managed_memories_identity ON managed_memories(repository_identity, memory_id);
            CREATE INDEX ix_managed_memories_repository ON managed_memories(repository_identity, ownership, memory_id);
            CREATE TABLE managed_memory_concepts (
                repository_identity TEXT NOT NULL, memory_id TEXT NOT NULL, concept TEXT NOT NULL,
                PRIMARY KEY(repository_identity, memory_id, concept),
                FOREIGN KEY(repository_identity, memory_id) REFERENCES managed_memories(repository_identity, memory_id) ON DELETE CASCADE);
            INSERT INTO managed_memory_concepts SELECT * FROM saved_memory_concepts;
            DROP TABLE saved_memory_concepts;
            CREATE INDEX ix_managed_memory_concept_lookup ON managed_memory_concepts(repository_identity, concept, memory_id);
            CREATE TABLE managed_memory_supports (
                repository_identity TEXT NOT NULL, memory_id TEXT NOT NULL, path TEXT NOT NULL,
                content_hash TEXT NOT NULL, observed_at TEXT NOT NULL, source_revision TEXT NULL,
                dirty INTEGER NOT NULL DEFAULT 0, unknown INTEGER NOT NULL DEFAULT 0, observed_sequence INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(repository_identity, memory_id, path),
                FOREIGN KEY(repository_identity, memory_id) REFERENCES managed_memories(repository_identity, memory_id) ON DELETE CASCADE);
            CREATE INDEX ix_managed_memory_support_path ON managed_memory_supports(repository_identity, path, memory_id);
            CREATE TABLE repository_intelligence_state (
                repository_identity TEXT PRIMARY KEY, onboarded INTEGER NOT NULL DEFAULT 0,
                change_sequence INTEGER NOT NULL DEFAULT 0, refreshed_sequence INTEGER NOT NULL DEFAULT 0,
                observation_gap INTEGER NOT NULL DEFAULT 1, gap_sequence INTEGER NOT NULL DEFAULT 0, fault TEXT NULL);
            CREATE TABLE repository_intelligence_changes (
                repository_identity TEXT NOT NULL, path TEXT NOT NULL, sequence INTEGER NOT NULL,
                PRIMARY KEY(repository_identity, path));
            CREATE VIRTUAL TABLE managed_memories_fts USING fts5(text, content='managed_memories', content_rowid='rowid', tokenize='unicode61');
            INSERT INTO managed_memories_fts(managed_memories_fts) VALUES('rebuild');
            CREATE TRIGGER managed_memories_insert AFTER INSERT ON managed_memories BEGIN
                INSERT INTO managed_memories_fts(rowid, text) VALUES(new.rowid, new.text);
            END;
            CREATE TRIGGER managed_memories_delete AFTER DELETE ON managed_memories BEGIN
                INSERT INTO managed_memories_fts(managed_memories_fts, rowid, text) VALUES('delete', old.rowid, old.text);
                DELETE FROM managed_memory_inclusions WHERE repository_identity = old.repository_identity AND memory_id = old.memory_id;
            END;
            CREATE TRIGGER managed_memories_text_update AFTER UPDATE OF text ON managed_memories
            WHEN old.text != new.text BEGIN
                INSERT INTO managed_memories_fts(managed_memories_fts, rowid, text) VALUES('delete', old.rowid, old.text);
                INSERT INTO managed_memories_fts(rowid, text) VALUES(new.rowid, new.text);
            END;
            UPDATE managed_memory_repositories SET revision = revision + 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (var sql in retainedSchema)
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
