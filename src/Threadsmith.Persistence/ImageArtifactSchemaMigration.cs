namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Adds image reference ownership and metadata-only conversation attachments.</summary>
public sealed class ImageArtifactSchemaMigration : IDatabaseMigration
{
    /// <summary>Image ownership schema shared with standalone artifact-store initialization.</summary>
    internal const string ReferenceSchemaSql = """
            CREATE TABLE IF NOT EXISTS image_artifact_refs (
                id TEXT NOT NULL, session_id TEXT NOT NULL, content_hash TEXT NOT NULL,
                descriptor TEXT NOT NULL, PRIMARY KEY(id, session_id)
            );
            CREATE INDEX IF NOT EXISTS ix_image_refs_hash ON image_artifact_refs(content_hash);
        """;

    /// <inheritdoc />
    public int Version => 16;

    /// <inheritdoc />
    public string Name => "Multimodal conversation artifacts";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE conversation_messages ADD COLUMN images_json TEXT NOT NULL DEFAULT '[]';
            """ + ReferenceSchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
