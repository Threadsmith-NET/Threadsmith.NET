namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Adds scoped receipt ownership and bounded artifact seek metadata.</summary>
public sealed class ReceiptEvidenceMigration : IDatabaseMigration
{
    /// <summary>Shared startup and migration schema for artifact evidence metadata.</summary>
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS receipt_evidence (
            session_id TEXT NOT NULL, evidence_id TEXT NOT NULL, message_id TEXT NOT NULL,
            run_id TEXT NOT NULL, content_hash TEXT NOT NULL, descriptor TEXT NOT NULL,
            length INTEGER NOT NULL, recorded_at TEXT NOT NULL,
            PRIMARY KEY(session_id, evidence_id),
            FOREIGN KEY(content_hash) REFERENCES artifacts(content_hash) ON DELETE CASCADE);
        CREATE INDEX IF NOT EXISTS ix_receipt_evidence_quota ON receipt_evidence(session_id, run_id, length);
        CREATE INDEX IF NOT EXISTS ix_receipt_evidence_hash ON receipt_evidence(content_hash);
        CREATE TABLE IF NOT EXISTS artifact_text_chunks (
            content_hash TEXT NOT NULL, ordinal INTEGER NOT NULL, byte_offset INTEGER NOT NULL,
            byte_length INTEGER NOT NULL, start_line INTEGER NOT NULL, start_column INTEGER NOT NULL,
            checksum TEXT NOT NULL, PRIMARY KEY(content_hash, ordinal),
            FOREIGN KEY(content_hash) REFERENCES artifacts(content_hash) ON DELETE CASCADE);
        CREATE INDEX IF NOT EXISTS ix_artifact_text_seek ON artifact_text_chunks(content_hash, start_line, start_column);
        """;

    /// <inheritdoc />
    public int Version => 19;

    /// <inheritdoc />
    public string Name => "Receipt evidence ownership and text paging";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
