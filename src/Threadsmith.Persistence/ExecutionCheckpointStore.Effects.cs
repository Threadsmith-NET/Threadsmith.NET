namespace Threadsmith.Persistence;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Persists direct edit intents alongside the existing execution checkpoint store.</summary>
public sealed partial class ExecutionCheckpointStore
{
    /// <inheritdoc />
    public async IAsyncEnumerable<MutationEffectRecord> ReadRunEffectsAsync(SessionId sessionId, RunId runId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT intent_json, receipt_json FROM mutation_effects WHERE session_id = $session AND run_id = $run ORDER BY rowid;";
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$run", runId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return ReadEffect(reader);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryBeginEffectAsync(MutationEffectRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateEffect(record);
        if (record.HasWriteIntent && record.Receipt is not null)
        {
            throw new ArgumentException("New intent cannot already have a terminal receipt.", nameof(record));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mutation_effects(effect_id, repository_identity, session_id, run_id, intent_json, receipt_json, resolved)
            VALUES($effect, $repository, $session, $run, $intent, $receipt, $resolved)
            ON CONFLICT(effect_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$effect", record.EffectId.ToString("D"));
        command.Parameters.AddWithValue("$repository", record.RepositoryIdentity);
        command.Parameters.AddWithValue("$session", record.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$run", record.RunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$intent", JsonSerializer.Serialize(record, JsonOptions));
        command.Parameters.AddWithValue("$receipt", record.Receipt is null ? DBNull.Value : JsonSerializer.Serialize(record.Receipt, JsonOptions));
        command.Parameters.AddWithValue("$resolved", record.HasWriteIntent ? 0 : 1);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <inheritdoc />
    public async Task<MutationEffectRecord?> GetEffectAsync(Guid effectId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT intent_json, receipt_json FROM mutation_effects WHERE effect_id = $effect;";
        command.Parameters.AddWithValue("$effect", effectId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEffect(reader) : null;
    }

    /// <inheritdoc />
    public async Task CompleteEffectAsync(Guid effectId, SourceEditReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.EffectId != effectId)
        {
            throw new ArgumentException("The effect and receipt identities differ.", nameof(receipt));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mutation_effects SET receipt_json = $receipt, resolved = $resolved
            WHERE effect_id = $effect AND resolved = 0;
            """;
        command.Parameters.AddWithValue("$effect", effectId.ToString("D"));
        command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(receipt, JsonOptions));
        command.Parameters.AddWithValue("$resolved", receipt.Status == SourceEditStatus.RecoveryRequired ? 0 : 1);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The mutation effect has no unresolved durable intent.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MutationEffectRecord>> GetUnresolvedEffectsAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT intent_json, receipt_json FROM mutation_effects
            WHERE repository_identity = $repository AND resolved = 0;
            """;
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var effects = new List<MutationEffectRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            effects.Add(ReadEffect(reader));
        }

        return effects;
    }

    /// <summary>Returns evidence still required by unresolved effects or unfinished run outcomes.</summary>
    public async Task<IReadOnlySet<string>> GetRequiredEffectArtifactsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH required AS (
                SELECT e.intent_json FROM mutation_effects e
                LEFT JOIN execution_runs r ON r.run_id = e.run_id
                WHERE e.resolved = 0 OR r.outcome_json IS NULL
            )
            SELECT json_extract(intent_json, '$.SnapshotArtifact.ContentHash') FROM required
            UNION
            SELECT json_extract(f.value, '$.ContentHash') FROM required,
                json_each(required.intent_json, '$.OriginalFiles') f WHERE f.type = 'object';
            """;
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                hashes.Add(reader.GetString(0));
            }
        }

        return hashes;
    }

    private static MutationEffectRecord ReadEffect(SqliteDataReader reader)
    {
        var record = JsonSerializer.Deserialize<MutationEffectRecord>(reader.GetString(0), JsonOptions)
            ?? throw new InvalidDataException("The mutation effect intent is unreadable.");
        ValidateEffect(record);
        var receipt = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<SourceEditReceipt>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("The mutation effect receipt is unreadable.");
        if (receipt is not null && (receipt.EffectId != record.EffectId || receipt.MutationSetId != record.MutationSetId))
        {
            throw new InvalidDataException("The stored mutation receipt does not match its intent.");
        }

        return record with { Receipt = receipt };
    }

    private static void ValidateEffect(MutationEffectRecord record)
    {
        if (record.SchemaVersion != 1 || record.EffectId == Guid.Empty || record.SessionId == default
            || record.RunId == default || record.WorkspaceId == default || record.MutationSetId == default)
        {
            throw new InvalidDataException("The mutation effect has unsupported schema or invalid host identities.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(record.RepositoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RequestIdentity);
        if (record.HasWriteIntent)
        {
            ArgumentNullException.ThrowIfNull(record.SnapshotArtifact);
        }
        else if (record.SnapshotArtifact is not null || record.Receipt is not { Status: SourceEditStatus.Denied or SourceEditStatus.Conflict, Commit: null } receipt
            || receipt.ChangedFiles.Count != 0 || receipt.EffectId != record.EffectId || receipt.MutationSetId != record.MutationSetId)
        {
            throw new InvalidDataException("A rejected edit must contain only a matching terminal non-write receipt.");
        }
    }
}
