namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Version 11: classifies managed memories and indexes only situational content for lexical retrieval.</summary>
public sealed class RepositoryMemoryTypeSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 11;

    /// <inheritdoc />
    public string Name => "Repository memory types";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE managed_memories ADD COLUMN memory_type INTEGER NOT NULL DEFAULT 0 CHECK(memory_type IN (0, 1));
            DROP TRIGGER IF EXISTS managed_memories_insert;
            DROP TRIGGER IF EXISTS managed_memories_delete;
            DROP TRIGGER IF EXISTS managed_memories_text_update;
            DROP TABLE IF EXISTS managed_memories_fts;
            CREATE VIRTUAL TABLE managed_memories_fts USING fts5(text, content='managed_memories', content_rowid='rowid', tokenize='unicode61');
            INSERT INTO managed_memories_fts(rowid, text)
                SELECT rowid, text FROM managed_memories WHERE memory_type = 0;
            CREATE TRIGGER managed_memories_insert AFTER INSERT ON managed_memories
            WHEN new.memory_type = 0 BEGIN
                INSERT INTO managed_memories_fts(rowid, text) VALUES(new.rowid, new.text);
            END;
            CREATE TRIGGER managed_memories_delete AFTER DELETE ON managed_memories BEGIN
                INSERT INTO managed_memories_fts(managed_memories_fts, rowid, text)
                    SELECT 'delete', old.rowid, old.text WHERE old.memory_type = 0;
                DELETE FROM managed_memory_inclusions WHERE repository_identity = old.repository_identity AND memory_id = old.memory_id;
            END;
            CREATE TRIGGER managed_memories_text_update AFTER UPDATE OF text, memory_type ON managed_memories BEGIN
                INSERT INTO managed_memories_fts(managed_memories_fts, rowid, text)
                    SELECT 'delete', old.rowid, old.text WHERE old.memory_type = 0;
                INSERT INTO managed_memories_fts(rowid, text)
                    SELECT new.rowid, new.text WHERE new.memory_type = 0;
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
