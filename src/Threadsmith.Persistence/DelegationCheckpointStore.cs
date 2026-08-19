namespace Threadsmith.Persistence;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite-backed atomic delegation run-tree checkpoint store.</summary>
public sealed class DelegationCheckpointStore : IDelegationCheckpointStore
{
    private const int SupportedSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="DelegationCheckpointStore"/> class.</summary>
    public DelegationCheckpointStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        DelegationCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        Validate(checkpoint);
        var json = JsonSerializer.Serialize(checkpoint, JsonOptions);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO delegation_runs(
                delegation_id, session_id, parent_run_id, schema_version,
                generation, phase, checkpoint_json, updated_at)
            VALUES($delegation, $session, $parent, $version, $generation, $phase, $json, $updated)
            ON CONFLICT(delegation_id) DO UPDATE SET
                session_id = excluded.session_id,
                parent_run_id = excluded.parent_run_id,
                schema_version = excluded.schema_version,
                generation = excluded.generation,
                phase = excluded.phase,
                checkpoint_json = excluded.checkpoint_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$delegation", checkpoint.DelegationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$session", checkpoint.Provenance.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$parent", checkpoint.Provenance.ParentRunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$version", checkpoint.SchemaVersion);
        command.Parameters.AddWithValue("$generation", checkpoint.Provenance.Generation);
        command.Parameters.AddWithValue("$phase", (int)checkpoint.Phase);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$updated", checkpoint.RecordedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DelegationCheckpoint?> GetAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, checkpoint_json
            FROM delegation_runs
            WHERE delegation_id = $delegation;
            """;
        command.Parameters.AddWithValue("$delegation", delegationId.Value.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var version = reader.GetInt32(0);
        if (version != SupportedSchemaVersion)
        {
            throw new NotSupportedException(
                $"Delegation checkpoint schema {version} is inspectable but cannot execute.");
        }

        DelegationCheckpoint checkpoint = JsonSerializer.Deserialize<DelegationCheckpoint>(
            reader.GetString(1),
            JsonOptions) ?? throw new InvalidDataException("Stored delegation checkpoint is invalid.");
        Validate(checkpoint);
        return checkpoint;
    }

    private static void Validate(DelegationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.SchemaVersion != SupportedSchemaVersion
            || checkpoint.DelegationId == default
            || checkpoint.Provenance.SessionId == default
            || checkpoint.Provenance.ParentRunId == default
            || checkpoint.Provenance.Generation < 1)
        {
            throw new InvalidDataException("Delegation checkpoint identity or schema is invalid.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.NextAction);
    }
}
