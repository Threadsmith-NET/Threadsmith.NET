namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Adds invocation-keyed direct edit intents without reinterpreting historical plan checkpoints.</summary>
public sealed class MutationEffectSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 13;

    /// <inheritdoc />
    public string Name => "Direct mutation effect journal";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS mutation_effects (
                effect_id TEXT PRIMARY KEY,
                repository_identity TEXT NOT NULL,
                session_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                intent_json TEXT NOT NULL,
                receipt_json TEXT NULL,
                resolved INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_mutation_effects_unresolved
                ON mutation_effects(repository_identity) WHERE resolved = 0;
            CREATE INDEX IF NOT EXISTS ix_mutation_effects_run ON mutation_effects(run_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
