namespace Threadsmith.Persistence;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite-backed atomic execution checkpoint and terminal-outcome store.</summary>
public sealed class ExecutionCheckpointStore : IExecutionCheckpointStore
{
    private const int SupportedSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="ExecutionCheckpointStore"/> class.</summary>
    public ExecutionCheckpointStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public Task SaveCheckpointAsync(
        ExecutionContinuation checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateCheckpoint(checkpoint);
        return UpsertAsync(
            checkpoint.RunId,
            checkpoint.SessionId,
            checkpoint.SchemaVersion,
            JsonSerializer.Serialize(checkpoint, JsonOptions),
            outcomeJson: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExecutionContinuation?> GetCheckpointAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(runId, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        if (stored.SchemaVersion != SupportedSchemaVersion)
        {
            throw new NotSupportedException(
                $"Execution checkpoint schema {stored.SchemaVersion} is inspectable but cannot resume.");
        }

        var checkpoint = JsonSerializer.Deserialize<ExecutionContinuation>(
            stored.CheckpointJson,
            JsonOptions) ?? throw new InvalidDataException("Stored execution checkpoint is invalid.");
        ValidateCheckpoint(checkpoint);
        return checkpoint;
    }

    /// <inheritdoc />
    public Task SaveOutcomeAsync(
        ExecutionOutcomeProjection outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.SchemaVersion != SupportedSchemaVersion)
        {
            throw new NotSupportedException($"Unsupported execution outcome schema {outcome.SchemaVersion}.");
        }

        return UpsertAsync(
            outcome.RunId,
            outcome.SessionId,
            outcome.SchemaVersion,
            checkpointJson: null,
            JsonSerializer.Serialize(outcome, JsonOptions),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExecutionOutcomeProjection?> GetOutcomeAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(runId, cancellationToken);
        if (stored?.OutcomeJson is null)
        {
            return null;
        }

        if (stored.SchemaVersion != SupportedSchemaVersion)
        {
            throw new NotSupportedException(
                $"Execution outcome schema {stored.SchemaVersion} is inspectable but unsupported.");
        }

        return JsonSerializer.Deserialize<ExecutionOutcomeProjection>(stored.OutcomeJson, JsonOptions)
            ?? throw new InvalidDataException("Stored execution outcome is invalid.");
    }

    private async Task UpsertAsync(
        RunId runId,
        SessionId sessionId,
        int schemaVersion,
        string? checkpointJson,
        string? outcomeJson,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_runs(
                run_id, session_id, schema_version, checkpoint_json, outcome_json, updated_at)
            VALUES($run, $session, $version, $checkpoint, $outcome, $updated)
            ON CONFLICT(run_id) DO UPDATE SET
                session_id = excluded.session_id,
                schema_version = excluded.schema_version,
                checkpoint_json = COALESCE(excluded.checkpoint_json, execution_runs.checkpoint_json),
                outcome_json = COALESCE(excluded.outcome_json, execution_runs.outcome_json),
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$run", runId.Value.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$version", schemaVersion);
        command.Parameters.AddWithValue("$checkpoint", (object?)checkpointJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", (object?)outcomeJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<StoredExecution?> ReadAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, checkpoint_json, outcome_json
            FROM execution_runs WHERE run_id = $run;
            """;
        command.Parameters.AddWithValue("$run", runId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        bool checkpointIsNull = await reader.IsDBNullAsync(1, cancellationToken);
        bool outcomeIsNull = await reader.IsDBNullAsync(2, cancellationToken);
        return new StoredExecution(
            reader.GetInt32(0),
            checkpointIsNull ? string.Empty : reader.GetString(1),
            outcomeIsNull ? null : reader.GetString(2));
    }

    private static void ValidateCheckpoint(ExecutionContinuation checkpoint)
    {
        if (checkpoint.SchemaVersion != SupportedSchemaVersion)
        {
            throw new NotSupportedException(
                $"Unsupported execution checkpoint schema {checkpoint.SchemaVersion}.");
        }

        if (checkpoint.SessionId == default
            || checkpoint.RunId == default
            || checkpoint.WorkspaceId == default
            || checkpoint.PlanRevision < 1
            || checkpoint.MutationBaselineGeneration < 0
            || checkpoint.CorrectionAttempts < 0
            || checkpoint.CorrectionBudget < 0)
        {
            throw new InvalidDataException("Execution checkpoint contains invalid host-owned identity or budget state.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.PlanHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.DiagnosticBaselineIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.MutationBaselineIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.NextAction);
    }

    private sealed record StoredExecution(
        int SchemaVersion,
        string CheckpointJson,
        string? OutcomeJson);
}

/// <summary>Adapts the content-addressed artifact store to execution-owned references.</summary>
public sealed class ExecutionArtifactPublisher : IExecutionArtifactPublisher
{
    private readonly IArtifactStore _artifacts;

    /// <summary>Initializes a new instance of the <see cref="ExecutionArtifactPublisher"/> class.</summary>
    public ExecutionArtifactPublisher(IArtifactStore artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        _artifacts = artifacts;
    }

    /// <inheritdoc />
    public async Task<ExecutionArtifactReference> PublishAsync(
        SessionId sessionId,
        string kind,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var metadata = await _artifacts.StoreAsync(
            content,
            kind,
            sessionId,
            cancellationToken);
        return new ExecutionArtifactReference(metadata.ContentHash, metadata.Kind, metadata.Length);
    }

    /// <inheritdoc />
    public async Task<string?> ReadAsync(
        ExecutionArtifactReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string? content = await _artifacts.ReadAsync(reference.ContentHash, cancellationToken);
        if (content is null || System.Text.Encoding.UTF8.GetByteCount(content) != reference.Length)
        {
            return null;
        }

        string actualHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
        return string.Equals(actualHash, reference.ContentHash, StringComparison.Ordinal)
            ? content
            : null;
    }
}
