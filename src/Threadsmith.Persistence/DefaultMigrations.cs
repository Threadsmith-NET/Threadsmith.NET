namespace Threadsmith.Persistence;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Version 0: the initial schema (the M1 event store tables).</summary>
public sealed class InitialSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 0;

    /// <inheritdoc />
    public string Name => "Initial schema";

    /// <inheritdoc />
    public Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        // The M1 event store creates these tables itself; declaring the migration keeps the
        // schema_version table authoritative and idempotent for fresh databases.
        return Task.CompletedTask;
    }
}

/// <summary>
/// Version 1: adds artifact metadata storage and indexes to support plan-18 artifact storage (§19.3).
/// Idempotent — re-running against a database that already has the artifacts table is a no-op.
/// </summary>
public sealed class ArtifactSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public string Name => "Artifact storage schema";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS artifacts (
                content_hash TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                length INTEGER NOT NULL,
                session_id TEXT,
                relative_path TEXT NOT NULL,
                recorded_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_artifacts_session
                ON artifacts(session_id);
            CREATE INDEX IF NOT EXISTS ix_artifacts_kind
                ON artifacts(kind);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Version 2: adds durable conversation archive, governed memory, provenance, summaries, and mode state.</summary>
public sealed class ConversationSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 2;

    /// <inheritdoc />
    public string Name => "Conversation archive and governed memory schema";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conversation_sessions (
                session_id TEXT PRIMARY KEY,
                mode INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS conversation_messages (
                message_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                role INTEGER NOT NULL,
                body TEXT,
                artifact_id TEXT,
                content_hash TEXT NOT NULL,
                estimated_tokens INTEGER NOT NULL,
                sensitivity INTEGER NOT NULL,
                repository_revision TEXT,
                occurred_at TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                UNIQUE(session_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_conversation_messages_session_sequence
                ON conversation_messages(session_id, sequence);
            CREATE TABLE IF NOT EXISTS conversation_memory (
                memory_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                content TEXT NOT NULL,
                repository_revision TEXT,
                repository_dependent INTEGER NOT NULL,
                supersedes_id TEXT,
                validity INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                schema_version INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_conversation_memory_session_kind_validity
                ON conversation_memory(session_id, kind, validity);
            CREATE TABLE IF NOT EXISTS conversation_memory_sources (
                memory_id TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                source_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY(memory_id, source_kind, source_id)
            );
            CREATE TABLE IF NOT EXISTS conversation_summaries (
                session_id TEXT PRIMARY KEY,
                version INTEGER NOT NULL,
                through_message_sequence INTEGER NOT NULL,
                repository_revision TEXT,
                memory_index_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                schema_version INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Version 3: adds atomic approved-plan execution checkpoints and terminal outcomes.</summary>
public sealed class ExecutionOrchestrationSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 3;

    /// <inheritdoc />
    public string Name => "Execution orchestration checkpoints and outcomes";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS execution_runs (
                run_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                checkpoint_json TEXT,
                outcome_json TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_execution_runs_session
                ON execution_runs(session_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Version 4: adds bounded delegation run trees, worktree leases, and checkpoints.</summary>
public sealed class DelegationSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 4;

    /// <inheritdoc />
    public string Name => "Parallel-agent delegation run trees";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS delegation_runs (
                delegation_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                parent_run_id TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                generation INTEGER NOT NULL,
                phase INTEGER NOT NULL,
                checkpoint_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_delegation_runs_parent
                ON delegation_runs(parent_run_id);
            CREATE INDEX IF NOT EXISTS ix_delegation_runs_session
                ON delegation_runs(session_id);
            CREATE TABLE IF NOT EXISTS delegation_worktree_leases (
                delegation_id TEXT NOT NULL,
                assignment_id TEXT NOT NULL,
                child_run_id TEXT NOT NULL,
                repository_path TEXT NOT NULL,
                baseline_identity TEXT NOT NULL,
                state TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(delegation_id, assignment_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Version 5: adds immutable skill pins, verification provenance, and workflow checkpoints.</summary>
public sealed class SkillWorkflowSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 5;

    /// <inheritdoc />
    public string Name => "Governed skill catalog and workflow checkpoints";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS skill_package_provenance (
                digest TEXT NOT NULL,
                skill_id TEXT NOT NULL,
                package_id TEXT NOT NULL,
                version TEXT NOT NULL,
                publisher TEXT NOT NULL,
                scope INTEGER NOT NULL,
                source TEXT NOT NULL,
                verification_state INTEGER NOT NULL,
                verification_reason TEXT NOT NULL,
                signer_id TEXT NULL,
                verified_at TEXT NOT NULL,
                PRIMARY KEY(digest, scope, source)
            );
            CREATE INDEX IF NOT EXISTS ix_skill_package_identity
                ON skill_package_provenance(skill_id, version);
            CREATE TABLE IF NOT EXISTS skill_pins (
                skill_id TEXT PRIMARY KEY,
                package_identity_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS skill_workflow_checkpoints (
                invocation_id TEXT PRIMARY KEY,
                workflow_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                skill_id TEXT NOT NULL,
                version TEXT NOT NULL,
                digest TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                status INTEGER NOT NULL,
                generation INTEGER NOT NULL,
                checkpoint_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_skill_workflow_session
                ON skill_workflow_checkpoints(session_id, updated_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Version 6: adds exact external repository hook approvals and bounded lifecycle audit.</summary>
public sealed class LifecycleHookSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 6;

    /// <inheritdoc />
    public string Name => "Lifecycle hook approvals and audit";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS hook_repository_approvals (
                repository_identity TEXT NOT NULL,
                handler_id TEXT NOT NULL,
                configuration_digest TEXT NOT NULL,
                approval_json TEXT NOT NULL,
                approved_at TEXT NOT NULL,
                PRIMARY KEY(repository_identity, handler_id)
            );
            CREATE TABLE IF NOT EXISTS hook_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                invocation_id TEXT NOT NULL UNIQUE,
                repository_identity TEXT NULL,
                handler_id TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                hook_point INTEGER NOT NULL,
                status INTEGER NOT NULL,
                decision INTEGER NOT NULL,
                recorded_at TEXT NOT NULL,
                audit_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_hook_audit_repository_time
                ON hook_audit(repository_identity, recorded_at);
            CREATE INDEX IF NOT EXISTS ix_hook_audit_handler_time
                ON hook_audit(handler_id, recorded_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Migration 7 irreversibly removes reasoning text persisted by the pre-M16 event fan-out.</summary>
public sealed class ReasoningPrivacyMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 7;

    /// <inheritdoc />
    public string Name => "reasoning-privacy";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'domain_events';";
        object? tableCount = await tableCheck.ExecuteScalarAsync(cancellationToken);
        if (Convert.ToInt64(tableCount, System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM domain_events WHERE event_name = 'modelReasoningObserved';";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Version 8: adds repository-bound session catalog, model selection, clone provenance, and usage.</summary>
public sealed class SessionLifecycleSchemaMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 8;

    /// <inheritdoc />
    public string Name => "Interactive session lifecycle catalog";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS session_catalog (
                session_id TEXT PRIMARY KEY,
                repository_identity TEXT NOT NULL,
                repository_display_name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                state INTEGER NOT NULL,
                preview TEXT NULL,
                message_count INTEGER NOT NULL,
                conversation_mode INTEGER NOT NULL,
                clone_source_session_id TEXT NULL,
                provider_id TEXT NULL,
                profile_id TEXT NULL,
                reasoning_level TEXT NULL,
                selection_generation INTEGER NULL,
                selection_schema_version INTEGER NULL,
                is_writable INTEGER NOT NULL,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                usage_is_estimate INTEGER NOT NULL,
                has_unknown_usage INTEGER NOT NULL,
                has_usage_observation INTEGER NOT NULL,
                inherited_input_tokens INTEGER NOT NULL,
                inherited_output_tokens INTEGER NOT NULL,
                schema_version INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_session_catalog_repository_updated
                ON session_catalog(repository_identity, updated_at DESC, session_id);
            CREATE INDEX IF NOT EXISTS ix_session_catalog_clone_source
                ON session_catalog(clone_source_session_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await BackfillLegacySessionsAsync(connection, cancellationToken);
    }

    private static async Task BackfillLegacySessionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'domain_events';";
        object? tableCount = await tableCheck.ExecuteScalarAsync(cancellationToken);
        if (Convert.ToInt64(tableCount, System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return;
        }

        var sessions = new Dictionary<SessionId, LegacySession>();
        await using var readEvents = connection.CreateCommand();
        readEvents.CommandText = """
            SELECT session_id, event_name, schema_version, payload FROM domain_events
            WHERE session_id NOT IN (SELECT session_id FROM session_catalog) ORDER BY sequence;
            """;
        await using var reader = await readEvents.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDomainEvent domainEvent;
            try
            {
                domainEvent = DomainEventJson.Deserialize(reader.GetString(1), reader.GetInt32(2), reader.GetString(3));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            var sessionId = new SessionId(Guid.Parse(reader.GetString(0)));
            sessions.TryGetValue(sessionId, out var current);
            current ??= new LegacySession(sessionId, domainEvent.OccurredAt, domainEvent.OccurredAt);
            current.UpdatedAt = domainEvent.OccurredAt > current.UpdatedAt ? domainEvent.OccurredAt : current.UpdatedAt;
            if (domainEvent is RepositoryOpened repository)
            {
                current.RepositoryPath = repository.Path;
            }

            sessions[sessionId] = current;
        }

        await reader.DisposeAsync();
        foreach (var session in sessions.Values.Where(item => !string.IsNullOrWhiteSpace(item.RepositoryPath)))
        {
            await BackfillLegacySessionAsync(connection, session, cancellationToken);
        }
    }

    private static async Task BackfillLegacySessionAsync(
        SqliteConnection connection,
        LegacySession session,
        CancellationToken cancellationToken)
    {
        string canonicalRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.RepositoryPath));
        string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? canonicalRepository.ToUpperInvariant() : canonicalRepository)));
        string displayName = Path.GetFileName(canonicalRepository);
        displayName = string.IsNullOrWhiteSpace(displayName) ? canonicalRepository : displayName;

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO session_catalog(
                session_id, repository_identity, repository_display_name, created_at, updated_at,
                state, preview, message_count, conversation_mode, clone_source_session_id,
                provider_id, profile_id, reasoning_level, selection_generation, selection_schema_version,
                is_writable, input_tokens, output_tokens, usage_is_estimate, has_unknown_usage,
                has_usage_observation, inherited_input_tokens, inherited_output_tokens, schema_version)
            SELECT $session, $repository, $display, $created,
                   COALESCE((SELECT MAX(occurred_at) FROM conversation_messages WHERE session_id = $session), $updated),
                   $state,
                   (SELECT substr(body, 1, 240) FROM conversation_messages
                    WHERE session_id = $session AND body IS NOT NULL ORDER BY sequence DESC LIMIT 1),
                   (SELECT COUNT(*) FROM conversation_messages WHERE session_id = $session),
                   COALESCE((SELECT mode FROM conversation_sessions WHERE session_id = $session), $mode),
                   NULL, NULL, NULL, NULL, NULL, NULL, 1, 0, 0, 0, 0, 0, 0, 0, 1;
            """;
        insert.Parameters.AddWithValue("$session", session.SessionId.Value.ToString("D"));
        insert.Parameters.AddWithValue("$repository", identity);
        insert.Parameters.AddWithValue("$display", displayName);
        insert.Parameters.AddWithValue("$created", session.CreatedAt.ToString("O"));
        insert.Parameters.AddWithValue("$updated", session.UpdatedAt.ToString("O"));
        insert.Parameters.AddWithValue("$state", (int)SessionLifecycleState.Idle);
        insert.Parameters.AddWithValue("$mode", (int)ConversationContextMode.ConversationAware);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class LegacySession(SessionId sessionId, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        internal SessionId SessionId { get; } = sessionId;

        internal DateTimeOffset CreatedAt { get; } = createdAt;

        internal DateTimeOffset UpdatedAt { get; set; } = updatedAt;

        internal string RepositoryPath { get; set; } = string.Empty;
    }
}

/// <summary>The default ordered migration set for the persistence layer.</summary>
public sealed class DefaultMigrations
{
    /// <summary>Gets the default ordered migrations.</summary>
    public static IReadOnlyList<IDatabaseMigration> All { get; } =
    [
        new InitialSchemaMigration(),
        new ArtifactSchemaMigration(),
        new ConversationSchemaMigration(),
        new ExecutionOrchestrationSchemaMigration(),
        new DelegationSchemaMigration(),
        new SkillWorkflowSchemaMigration(),
        new LifecycleHookSchemaMigration(),
        new ReasoningPrivacyMigration(),
        new SessionLifecycleSchemaMigration(),
    ];
}
