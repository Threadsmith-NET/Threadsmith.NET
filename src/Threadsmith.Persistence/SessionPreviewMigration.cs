namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Rebuilds cached session previews without including execution receipts.</summary>
public sealed class SessionPreviewMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 21;

    /// <inheritdoc />
    public string Name => "Visible session catalog previews";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE session_catalog AS catalog SET preview = {SqliteSessionLifecycleStore.LatestVisiblePreviewSql};
            """;
        command.Parameters.AddWithValue("$previewCharacters", SqliteSessionLifecycleStore.MaximumPreviewCharacters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
