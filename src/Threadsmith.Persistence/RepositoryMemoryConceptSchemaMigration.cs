namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Adds managed metadata and makes the shared text index searchable across memory types.</summary>
public sealed class RepositoryMemoryConceptSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 12;

    /// <inheritdoc />
    public string Name => "Memory concepts and reconciliation";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE managed_memories ADD COLUMN kind INTEGER NOT NULL DEFAULT 0 CHECK(kind BETWEEN 0 AND 5);
            CREATE UNIQUE INDEX ix_managed_memories_identity ON managed_memories(repository_identity, memory_id);
            CREATE TABLE managed_memory_concepts (
                repository_identity TEXT NOT NULL, memory_id TEXT NOT NULL, concept TEXT NOT NULL,
                PRIMARY KEY(repository_identity, memory_id, concept),
                FOREIGN KEY(repository_identity, memory_id) REFERENCES managed_memories(repository_identity, memory_id) ON DELETE CASCADE);
            CREATE INDEX ix_managed_memory_concept_lookup ON managed_memory_concepts(repository_identity, concept, memory_id);
            DROP TRIGGER managed_memories_insert;
            DROP TRIGGER managed_memories_delete;
            DROP TRIGGER managed_memories_text_update;
            DROP TABLE managed_memories_fts;
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
    }
}
