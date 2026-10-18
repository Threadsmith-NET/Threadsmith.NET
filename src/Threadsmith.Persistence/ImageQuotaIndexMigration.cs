namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Adds ordered quota cleanup access for stores already using image artifacts.</summary>
public sealed class ImageQuotaIndexMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 17;

    /// <inheritdoc />
    public string Name => "Ordered image quota cleanup";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = ArtifactStore.ImageQuotaIndexSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
