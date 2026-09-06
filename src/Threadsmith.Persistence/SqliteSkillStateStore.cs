namespace Threadsmith.Persistence;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite-backed atomic skill pin and workflow checkpoint store.</summary>
public sealed class SqliteSkillStateStore : ISkillStateStore
{
    private const int SupportedCheckpointSchema = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="SqliteSkillStateStore"/> class.</summary>
    public SqliteSkillStateStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task SaveVerificationAsync(
        SkillVerificationRecord verification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ValidateIdentity(verification.Package.SkillId, verification.Package);
        ArgumentException.ThrowIfNullOrWhiteSpace(verification.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(verification.Reason);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO skill_package_provenance(
                digest, skill_id, package_id, version, publisher, scope, source,
                verification_state, verification_reason, signer_id, verified_at)
            VALUES(
                $digest, $skill, $package, $version, $publisher, $scope, $source,
                $state, $reason, $signer, $verified)
            ON CONFLICT(digest, scope, source) DO UPDATE SET
                skill_id = excluded.skill_id,
                package_id = excluded.package_id,
                version = excluded.version,
                publisher = excluded.publisher,
                verification_state = excluded.verification_state,
                verification_reason = excluded.verification_reason,
                signer_id = excluded.signer_id,
                verified_at = excluded.verified_at;
            """;
        command.Parameters.AddWithValue("$digest", verification.Package.Digest.Value);
        command.Parameters.AddWithValue("$skill", verification.Package.SkillId.Value);
        command.Parameters.AddWithValue("$package", verification.Package.PackageId);
        command.Parameters.AddWithValue("$version", verification.Package.Version);
        command.Parameters.AddWithValue("$publisher", verification.Package.Publisher);
        command.Parameters.AddWithValue("$scope", (int)verification.Scope);
        command.Parameters.AddWithValue("$source", verification.Source);
        command.Parameters.AddWithValue("$state", (int)verification.State);
        command.Parameters.AddWithValue("$reason", verification.Reason);
        command.Parameters.AddWithValue("$signer", (object?)verification.SignerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$verified", verification.VerifiedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SkillVerificationRecord?> GetVerificationAsync(
        SkillDigest digest,
        SkillScope scope,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT skill_id, package_id, version, publisher, verification_state,
                   verification_reason, signer_id, verified_at
            FROM skill_package_provenance
            WHERE digest = $digest AND scope = $scope AND source = $source;
            """;
        command.Parameters.AddWithValue("$digest", digest.Value);
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$source", source);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var signerId = await reader.IsDBNullAsync(6, cancellationToken)
            ? null
            : reader.GetString(6);
        return new SkillVerificationRecord
        {
            Package = new SkillPackageIdentity(
                new SkillId(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                digest,
                reader.GetString(3)),
            Scope = scope,
            Source = source,
            State = (SkillVerificationState)reader.GetInt32(4),
            Reason = reader.GetString(5),
            SignerId = signerId,
            VerifiedAt = DateTimeOffset.Parse(
                reader.GetString(7),
                System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <inheritdoc />
    public async Task SaveCheckpointAsync(
        SkillWorkflowCheckpoint checkpoint,
        SkillCheckpointVersion? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        Validate(checkpoint);
        var json = JsonSerializer.Serialize(checkpoint, JsonOptions);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expectedVersion is null
            ? """
                INSERT INTO skill_workflow_checkpoints(
                    invocation_id, workflow_id, session_id, run_id, skill_id, version,
                    digest, schema_version, status, generation, checkpoint_json, updated_at)
                VALUES(
                    $invocation, $workflow, $session, $run, $skill, $version,
                    $digest, $schema, $status, $generation, $json, $updated)
                ON CONFLICT(invocation_id) DO NOTHING;
                """
            : """
                UPDATE skill_workflow_checkpoints
                SET workflow_id = $workflow,
                    session_id = $session,
                    run_id = $run,
                    skill_id = $skill,
                    version = $version,
                    digest = $digest,
                    schema_version = $schema,
                    status = $status,
                    generation = $generation,
                    checkpoint_json = $json,
                    updated_at = $updated
                WHERE invocation_id = $invocation
                  AND generation = $expectedGeneration
                  AND status = $expectedStatus;
                """;
        command.Parameters.AddWithValue("$invocation", checkpoint.InvocationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$workflow", checkpoint.WorkflowId.Value.ToString("D"));
        command.Parameters.AddWithValue("$session", checkpoint.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$run", checkpoint.RunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$skill", checkpoint.Package.SkillId.Value);
        command.Parameters.AddWithValue("$version", checkpoint.Package.Version);
        command.Parameters.AddWithValue("$digest", checkpoint.Package.Digest.Value);
        command.Parameters.AddWithValue("$schema", checkpoint.SchemaVersion);
        command.Parameters.AddWithValue("$status", (int)checkpoint.Status);
        command.Parameters.AddWithValue("$generation", checkpoint.Generation);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$updated", checkpoint.RecordedAt.ToString("O"));
        if (expectedVersion is not null)
        {
            command.Parameters.AddWithValue("$expectedGeneration", expectedVersion.Generation);
            command.Parameters.AddWithValue("$expectedStatus", (int)expectedVersion.Status);
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new SkillCheckpointConflictException();
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SkillWorkflowCheckpoint?> GetCheckpointAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, checkpoint_json
            FROM skill_workflow_checkpoints
            WHERE invocation_id = $invocation;
            """;
        command.Parameters.AddWithValue("$invocation", invocationId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var schema = reader.GetInt32(0);
        if (schema != SupportedCheckpointSchema)
        {
            throw new NotSupportedException(
                $"Skill workflow checkpoint schema {schema} is inspectable but cannot execute.");
        }

        var checkpoint = JsonSerializer.Deserialize<SkillWorkflowCheckpoint>(
            reader.GetString(1),
            JsonOptions) ?? throw new InvalidDataException("Stored skill workflow checkpoint is invalid.");
        Validate(checkpoint);
        return checkpoint;
    }

    /// <inheritdoc />
    public async Task<bool> HasActivePackageReferenceAsync(
        SkillPackageIdentity package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM skill_workflow_checkpoints
                WHERE skill_id = $skillId
                  AND version = $version
                  AND digest = $digest
                  AND status NOT IN ($completed, $failed, $cancelled));
            """;
        command.Parameters.AddWithValue("$skillId", package.SkillId.Value);
        command.Parameters.AddWithValue("$version", package.Version);
        command.Parameters.AddWithValue("$digest", package.Digest.Value);
        command.Parameters.AddWithValue("$completed", (int)SkillInvocationStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)SkillInvocationStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)SkillInvocationStatus.Cancelled);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1L;
    }

    /// <summary>Deletes workflow checkpoints older than the configured retention cutoff.</summary>
    public async Task<int> DeleteCheckpointsOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM skill_workflow_checkpoints
            WHERE updated_at < $cutoff
              AND status IN ($completed, $failed, $cancelled);
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        command.Parameters.AddWithValue("$completed", (int)SkillInvocationStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)SkillInvocationStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)SkillInvocationStatus.Cancelled);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SavePinAsync(
        SkillId skillId,
        SkillPackageIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(skillId, identity);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO skill_pins(skill_id, package_identity_json, updated_at)
            VALUES($skill, $identity, $updated)
            ON CONFLICT(skill_id) DO UPDATE SET
                package_identity_json = excluded.package_identity_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$skill", skillId.Value);
        command.Parameters.AddWithValue("$identity", JsonSerializer.Serialize(identity, JsonOptions));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SkillPackageIdentity?> GetPinAsync(
        SkillId skillId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skillId);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT package_identity_json FROM skill_pins WHERE skill_id = $skill;";
        command.Parameters.AddWithValue("$skill", skillId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json)
        {
            return null;
        }

        var identity = JsonSerializer.Deserialize<SkillPackageIdentity>(json, JsonOptions)
            ?? throw new InvalidDataException("Stored skill pin is invalid.");
        ValidateIdentity(skillId, identity);
        return identity;
    }

    private static void Validate(SkillWorkflowCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.SchemaVersion != SupportedCheckpointSchema
            || checkpoint.InvocationId == default
            || checkpoint.WorkflowId == default
            || checkpoint.SessionId == default
            || checkpoint.RunId == default
            || checkpoint.Attempt < 1
            || checkpoint.Generation < 1
            || string.IsNullOrWhiteSpace(checkpoint.InputJson)
            || string.IsNullOrWhiteSpace(checkpoint.NextAction))
        {
            throw new InvalidDataException("Skill workflow checkpoint identity, schema, or continuation is invalid.");
        }

        ValidateIdentity(checkpoint.Package.SkillId, checkpoint.Package);
    }

    private static void ValidateIdentity(SkillId skillId, SkillPackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(skillId);
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(skillId.Value, identity.SkillId.Value, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(skillId.Value)
            || string.IsNullOrWhiteSpace(identity.Version)
            || !string.Equals(identity.Digest.Algorithm, "sha256", StringComparison.Ordinal)
            || identity.Digest.Value.Length != 64)
        {
            throw new InvalidDataException("Skill package identity is invalid.");
        }
    }
}
