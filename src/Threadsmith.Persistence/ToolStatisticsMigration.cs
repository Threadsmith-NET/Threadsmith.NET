namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Adds repository aggregate tool statistics without invocation history.</summary>
public sealed class ToolStatisticsMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 20;

    /// <inheritdoc />
    public string Name => "Tool completion statistics";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_statistics (
                tool_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
                total_count INTEGER NOT NULL CHECK (typeof(total_count) = 'integer' AND total_count >= 0),
                success_count INTEGER NOT NULL CHECK (typeof(success_count) = 'integer' AND success_count >= 0),
                failure_count INTEGER NOT NULL CHECK (typeof(failure_count) = 'integer' AND failure_count >= 0),
                last_invoked_at_utc TEXT NOT NULL,
                total_duration_ms INTEGER NOT NULL CHECK (typeof(total_duration_ms) = 'integer' AND total_duration_ms >= 0),
                measured_invocation_count INTEGER NOT NULL CHECK (typeof(measured_invocation_count) = 'integer' AND measured_invocation_count >= 0),
                last_duration_ms INTEGER CHECK (last_duration_ms IS NULL OR (typeof(last_duration_ms) = 'integer' AND last_duration_ms >= 0)),
                CHECK (success_count <= total_count),
                CHECK (failure_count = total_count - success_count),
                CHECK (measured_invocation_count <= total_count)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
