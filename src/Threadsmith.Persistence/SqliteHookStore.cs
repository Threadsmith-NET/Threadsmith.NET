namespace Threadsmith.Persistence;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite-backed exact repository approval and bounded lifecycle-hook audit store.</summary>
public sealed class SqliteHookStore : IHookStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="SqliteHookStore"/> class.Initializes the hook store.</summary>
    public SqliteHookStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<HookRepositoryApproval?> GetApprovalAsync(string repositoryIdentity, HookHandlerIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT configuration_digest, approval_json
            FROM hook_repository_approvals
            WHERE repository_identity = $repository AND handler_id = $handler;
            """;
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        command.Parameters.AddWithValue("$handler", identity.Id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), identity.ConfigurationDigest.Value, StringComparison.Ordinal))
        {
            return null;
        }

        var approval = JsonSerializer.Deserialize<HookRepositoryApproval>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("Stored hook approval is invalid.");
        return approval.HandlerIdentity == identity ? approval : null;
    }

    /// <inheritdoc />
    public async Task SaveApprovalAsync(HookRepositoryApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO hook_repository_approvals(
                repository_identity, handler_id, configuration_digest, approval_json, approved_at)
            VALUES($repository, $handler, $digest, $json, $approved)
            ON CONFLICT(repository_identity, handler_id) DO UPDATE SET
                configuration_digest = excluded.configuration_digest,
                approval_json = excluded.approval_json,
                approved_at = excluded.approved_at;
            """;
        command.Parameters.AddWithValue("$repository", approval.RepositoryIdentity);
        command.Parameters.AddWithValue("$handler", approval.HandlerIdentity.Id.Value);
        command.Parameters.AddWithValue("$digest", approval.HandlerIdentity.ConfigurationDigest.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(approval, JsonOptions));
        command.Parameters.AddWithValue("$approved", approval.ApprovedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RevokeApprovalAsync(string repositoryIdentity, HookHandlerId handlerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM hook_repository_approvals WHERE repository_identity = $repository AND handler_id = $handler;";
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        command.Parameters.AddWithValue("$handler", handlerId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AppendAuditAsync(HookAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO hook_audit(
                invocation_id, repository_identity, handler_id, operation_id, hook_point,
                status, decision, recorded_at, audit_json)
            VALUES($invocation, $repository, $handler, $operation, $point, $status, $decision, $recorded, $json);
            """;
        command.Parameters.AddWithValue("$invocation", record.InvocationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$repository", (object?)record.RepositoryIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$handler", record.HandlerIdentity.Id.Value);
        command.Parameters.AddWithValue("$operation", record.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$point", (int)record.HookPoint);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$decision", (int)record.Decision);
        command.Parameters.AddWithValue("$recorded", record.RecordedAt.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HookAuditRecord>> QueryAuditAsync(string? repositoryIdentity, HookHandlerId? handlerId, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var records = new List<HookAuditRecord>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audit_json FROM hook_audit
            WHERE ($repository IS NULL OR repository_identity = $repository)
              AND ($handler IS NULL OR handler_id = $handler)
            ORDER BY sequence DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$repository", (object?)repositoryIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$handler", (object?)handlerId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(JsonSerializer.Deserialize<HookAuditRecord>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidDataException("Stored hook audit is invalid."));
        }

        return records;
    }

    /// <summary>Deletes old audit rows without affecting repository approvals.</summary>
    public async Task<int> DeleteAuditOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM hook_audit WHERE recorded_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
