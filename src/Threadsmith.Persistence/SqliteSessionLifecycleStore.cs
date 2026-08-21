namespace Threadsmith.Persistence;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite-backed repository-scoped session catalog and atomic governed clone store.</summary>
public sealed class SqliteSessionLifecycleStore : ISessionLifecycleStore
{
    private const int MaximumPreviewCharacters = 240;
    private const int MaximumRepositoryDisplayCharacters = 128;
    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="SqliteSessionLifecycleStore"/> class.</summary>
    public SqliteSessionLifecycleStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<SessionCatalogEntry> CreateAsync(
        SessionCatalogEntry entry,
        SessionDurableUsage usage,
        CancellationToken cancellationToken = default)
    {
        ValidateEntry(entry);
        ValidateUsage(usage);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertAsync(connection, transaction, entry, usage, insertOnly: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    /// <inheritdoc />
    public async Task<SessionCatalogEntry?> GetAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateSelectCommand(connection);
        command.CommandText += " WHERE session_id = $session LIMIT 1;";
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionCatalogEntry>> ListAsync(
        string repositoryIdentity,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateSelectCommand(connection);
        command.CommandText += " WHERE repository_identity = $repository ORDER BY updated_at DESC, session_id LIMIT $limit;";
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var entries = new List<SessionCatalogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task<SessionCatalogEntry> CheckpointAsync(
        SessionCatalogEntry entry,
        SessionDurableUsage usage,
        CancellationToken cancellationToken = default)
    {
        ValidateEntry(entry);
        ValidateUsage(usage);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var enriched = await EnrichFromConversationAsync(connection, transaction, entry, cancellationToken);
        await UpsertAsync(connection, transaction, enriched, usage, insertOnly: false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return enriched;
    }

    /// <inheritdoc />
    public async Task<SessionDurableUsage> GetUsageAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_tokens, output_tokens, usage_is_estimate, has_unknown_usage,
                   has_usage_observation, inherited_input_tokens, inherited_output_tokens
            FROM session_catalog WHERE session_id = $session;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SessionDurableUsage(0, 0, false, false, false);
        }

        return new SessionDurableUsage(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }

    /// <inheritdoc />
    public async Task<SessionCatalogEntry> CloneAsync(
        SessionId sourceSessionId,
        SessionCatalogEntry destination,
        SessionDurableUsage usage,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sourceSessionId);
        ValidateEntry(destination);
        ValidateUsage(usage);
        if (destination.CloneSourceSessionId != sourceSessionId)
        {
            throw new ArgumentException("Clone provenance must identify the source session.", nameof(destination));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await CopyConversationAsync(connection, transaction, sourceSessionId, destination.SessionId, cancellationToken);
        var enriched = await EnrichFromConversationAsync(connection, transaction, destination, cancellationToken);
        await UpsertAsync(connection, transaction, enriched, usage, insertOnly: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return enriched;
    }

    private static async Task CopyConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId source,
        SessionId destination,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TEMP TABLE clone_message_map(old_id TEXT PRIMARY KEY, new_id TEXT NOT NULL);
            CREATE TEMP TABLE clone_memory_map(old_id TEXT PRIMARY KEY, new_id TEXT NOT NULL);
            INSERT INTO clone_message_map(old_id, new_id)
                SELECT message_id, lower(hex(randomblob(16))) FROM conversation_messages WHERE session_id = $source;
            INSERT INTO clone_memory_map(old_id, new_id)
                SELECT memory_id, lower(hex(randomblob(16))) FROM conversation_memory WHERE session_id = $source;
            INSERT INTO conversation_sessions(session_id, mode, updated_at)
                SELECT $destination, mode, $updatedAt FROM conversation_sessions WHERE session_id = $source;
            INSERT INTO conversation_messages(
                message_id, session_id, run_id, sequence, role, body, artifact_id, content_hash,
                estimated_tokens, sensitivity, repository_revision, occurred_at, schema_version)
                SELECT map.new_id, $destination, lower(hex(randomblob(16))), source.sequence, source.role,
                       source.body, source.artifact_id, source.content_hash, source.estimated_tokens,
                       source.sensitivity, source.repository_revision, source.occurred_at, source.schema_version
                FROM conversation_messages source JOIN clone_message_map map ON map.old_id = source.message_id;
            INSERT INTO conversation_memory(
                memory_id, session_id, kind, content, repository_revision, repository_dependent,
                supersedes_id, validity, created_at, updated_at, schema_version)
                SELECT map.new_id, $destination, source.kind, source.content, source.repository_revision,
                       source.repository_dependent, superseded.new_id, source.validity, source.created_at,
                       source.updated_at, source.schema_version
                FROM conversation_memory source
                JOIN clone_memory_map map ON map.old_id = source.memory_id
                LEFT JOIN clone_memory_map superseded ON superseded.old_id = source.supersedes_id;
            INSERT INTO conversation_memory_sources(memory_id, source_kind, source_id, ordinal)
                SELECT memory_map.new_id, source.source_kind,
                       CASE WHEN source.source_kind = 'message' THEN COALESCE(message_map.new_id, source.source_id)
                            WHEN source.source_kind = 'memory' THEN COALESCE(source_memory_map.new_id, source.source_id)
                            ELSE source.source_id END,
                       source.ordinal
                FROM conversation_memory_sources source
                JOIN clone_memory_map memory_map ON memory_map.old_id = source.memory_id
                LEFT JOIN clone_message_map message_map ON source.source_kind = 'message' AND message_map.old_id = source.source_id
                LEFT JOIN clone_memory_map source_memory_map ON source.source_kind = 'memory' AND source_memory_map.old_id = source.source_id;
            INSERT INTO conversation_summaries(
                session_id, version, through_message_sequence, repository_revision,
                memory_index_json, created_at, schema_version)
                SELECT $destination, version, through_message_sequence, repository_revision,
                       '{}', created_at, schema_version
                FROM conversation_summaries WHERE session_id = $source;
            """;
        command.Parameters.AddWithValue("$source", source.Value.ToString("D"));
        command.Parameters.AddWithValue("$destination", destination.Value.ToString("D"));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await RemapSummaryAsync(connection, transaction, source, destination, cancellationToken);
        await using var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = "DROP TABLE clone_message_map; DROP TABLE clone_memory_map;";
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RemapSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId source,
        SessionId destination,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT memory_index_json FROM conversation_summaries WHERE session_id = $session;";
        read.Parameters.AddWithValue("$session", source.Value.ToString("D"));
        if (await read.ExecuteScalarAsync(cancellationToken) is not string json)
        {
            return;
        }

        var sourceIndex =
            JsonSerializer.Deserialize<Dictionary<ConversationMemoryKind, IReadOnlyList<ConversationMemoryId>>>(json)
            ?? [];
        var mapped = new Dictionary<ConversationMemoryKind, IReadOnlyList<ConversationMemoryId>>();
        foreach ((var kind, var ids) in sourceIndex)
        {
            var destinationIds = new List<ConversationMemoryId>();
            foreach (var id in ids)
            {
                await using var map = connection.CreateCommand();
                map.Transaction = transaction;
                map.CommandText = "SELECT new_id FROM clone_memory_map WHERE old_id = $id;";
                map.Parameters.AddWithValue("$id", id.Value.ToString("D"));
                if (await map.ExecuteScalarAsync(cancellationToken) is string mappedId)
                {
                    destinationIds.Add(new ConversationMemoryId(Guid.Parse(mappedId)));
                }
            }

            mapped[kind] = destinationIds;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE conversation_summaries SET memory_index_json = $json WHERE session_id = $session;";
        update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(mapped));
        update.Parameters.AddWithValue("$session", destination.Value.ToString("D"));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SessionCatalogEntry> EnrichFromConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   (SELECT body FROM conversation_messages WHERE session_id = $session
                    AND body IS NOT NULL ORDER BY sequence DESC LIMIT 1),
                   COALESCE((SELECT mode FROM conversation_sessions WHERE session_id = $session), $mode)
            FROM conversation_messages WHERE session_id = $session;
            """;
        command.Parameters.AddWithValue("$session", entry.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$mode", (int)entry.ConversationMode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        _ = await reader.ReadAsync(cancellationToken);
        var preview = await reader.IsDBNullAsync(1) ? entry.Preview : Bound(reader.GetString(1), MaximumPreviewCharacters);
        var mode = reader.GetInt32(2);
        return entry with
        {
            MessageCount = reader.GetInt64(0),
            Preview = preview,
            ConversationMode = Enum.IsDefined((ConversationContextMode)mode)
                ? (ConversationContextMode)mode
                : ConversationContextMode.ConversationAware,
        };
    }

    private static SqliteCommand CreateSelectCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, repository_identity, repository_display_name, created_at, updated_at,
                   state, preview, message_count, conversation_mode, clone_source_session_id,
                   provider_id, profile_id, reasoning_level, selection_generation,
                   selection_schema_version, is_writable, schema_version FROM session_catalog
            """;
        return command;
    }

    private static SessionCatalogEntry ReadEntry(SqliteDataReader reader)
    {
        var selection = reader.IsDBNull(10) ? null : new SessionModelSelectionRecord
        {
            ProviderId = reader.GetString(10),
            ProfileId = new ModelProfileId(Guid.Parse(reader.GetString(11))),
            ReasoningLevel = reader.GetString(12),
            Generation = reader.GetInt64(13),
            SchemaVersion = reader.GetInt32(14),
        };
        return new SessionCatalogEntry
        {
            SessionId = new SessionId(Guid.Parse(reader.GetString(0))),
            RepositoryIdentity = reader.GetString(1),
            RepositoryDisplayName = reader.GetString(2),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            State = (SessionLifecycleState)reader.GetInt32(5),
            Preview = reader.IsDBNull(6) ? null : reader.GetString(6),
            MessageCount = reader.GetInt64(7),
            ConversationMode = (ConversationContextMode)reader.GetInt32(8),
            CloneSourceSessionId = reader.IsDBNull(9) ? null : new SessionId(Guid.Parse(reader.GetString(9))),
            ModelSelection = selection,
            IsWritable = reader.GetBoolean(15),
            SchemaVersion = reader.GetInt32(16),
        };
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionCatalogEntry entry,
        SessionDurableUsage usage,
        bool insertOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_catalog(
                session_id, repository_identity, repository_display_name, created_at, updated_at,
                state, preview, message_count, conversation_mode, clone_source_session_id,
                provider_id, profile_id, reasoning_level, selection_generation, selection_schema_version,
                is_writable, input_tokens, output_tokens, usage_is_estimate, has_unknown_usage,
                has_usage_observation, inherited_input_tokens, inherited_output_tokens, schema_version)
            VALUES($session, $repository, $display, $created, $updated, $state, $preview, $count, $mode,
                $source, $provider, $profile, $reasoning, $generation, $selectionSchema, $writable,
                $input, $output, $estimate, $unknown, $observed, $inheritedInput, $inheritedOutput, $schema)
            ON CONFLICT(session_id) DO UPDATE SET
                repository_identity=excluded.repository_identity, repository_display_name=excluded.repository_display_name,
                updated_at=excluded.updated_at, state=excluded.state, preview=excluded.preview,
                message_count=excluded.message_count, conversation_mode=excluded.conversation_mode,
                provider_id=excluded.provider_id, profile_id=excluded.profile_id,
                reasoning_level=excluded.reasoning_level, selection_generation=excluded.selection_generation,
                selection_schema_version=excluded.selection_schema_version, is_writable=excluded.is_writable,
                input_tokens=excluded.input_tokens, output_tokens=excluded.output_tokens,
                usage_is_estimate=excluded.usage_is_estimate, has_unknown_usage=excluded.has_unknown_usage,
                has_usage_observation=excluded.has_usage_observation,
                inherited_input_tokens=excluded.inherited_input_tokens,
                inherited_output_tokens=excluded.inherited_output_tokens, schema_version=excluded.schema_version
            """ + (insertOnly ? " WHERE false;" : ";");
        var selection = entry.ModelSelection;
        command.Parameters.AddWithValue("$session", entry.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$repository", entry.RepositoryIdentity);
        command.Parameters.AddWithValue("$display", Bound(entry.RepositoryDisplayName, MaximumRepositoryDisplayCharacters));
        command.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", entry.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$preview", (object?)entry.Preview ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", entry.MessageCount);
        command.Parameters.AddWithValue("$mode", (int)entry.ConversationMode);
        command.Parameters.AddWithValue("$source", entry.CloneSourceSessionId is { } source ? source.Value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$provider", (object?)selection?.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$profile", selection is null ? DBNull.Value : selection.ProfileId.Value.ToString("D"));
        command.Parameters.AddWithValue("$reasoning", selection is null ? DBNull.Value : selection.ReasoningLevel);
        command.Parameters.AddWithValue("$generation", selection is null ? DBNull.Value : selection.Generation);
        command.Parameters.AddWithValue("$selectionSchema", selection is null ? DBNull.Value : selection.SchemaVersion);
        command.Parameters.AddWithValue("$writable", entry.IsWritable);
        command.Parameters.AddWithValue("$input", usage.InputTokens);
        command.Parameters.AddWithValue("$output", usage.OutputTokens);
        command.Parameters.AddWithValue("$estimate", usage.IsEstimate);
        command.Parameters.AddWithValue("$unknown", usage.HasUnknownUsage);
        command.Parameters.AddWithValue("$observed", usage.HasObservation);
        command.Parameters.AddWithValue("$inheritedInput", usage.InheritedInputTokens);
        command.Parameters.AddWithValue("$inheritedOutput", usage.InheritedOutputTokens);
        command.Parameters.AddWithValue("$schema", entry.SchemaVersion);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException($"Session {entry.SessionId.Value:D} already exists.");
        }
    }

    private static string Bound(string value, int maximumCharacters)
    {
        var safe = string.Concat(value.Take(maximumCharacters).Select(character => char.IsControl(character) ? ' ' : character));
        return safe.Trim();
    }

    private static void ValidateEntry(SessionCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateSessionId(entry.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RepositoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RepositoryDisplayName);
        if (!Enum.IsDefined(entry.State) || !Enum.IsDefined(entry.ConversationMode) || entry.MessageCount < 0)
        {
            throw new ArgumentException("Session metadata contains invalid values.", nameof(entry));
        }
    }

    private static void ValidateSessionId(SessionId sessionId)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }
    }

    private static void ValidateUsage(SessionDurableUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.InputTokens < 0 || usage.OutputTokens < 0
            || usage.InheritedInputTokens < 0 || usage.InheritedOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usage));
        }
    }
}
