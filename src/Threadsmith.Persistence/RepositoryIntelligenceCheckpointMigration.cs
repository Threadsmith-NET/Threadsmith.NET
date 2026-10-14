namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Version 15: retains completed onboarding batches outside the published memory partition.</summary>
public sealed class RepositoryIntelligenceCheckpointMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 15;

    /// <inheritdoc />
    public string Name => "Resumable repository onboarding";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE repository_intelligence_checkpoints (
                repository_identity TEXT PRIMARY KEY,
                checkpoint_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
