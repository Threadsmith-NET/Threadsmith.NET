namespace Threadsmith.Persistence;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Persists sanitized conversation archive and governed memory with deterministic session ordering.</summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private const int DefaultArtifactThresholdCharacters = 16_384;
    private const int MaximumWarnings = 32;

    private readonly IArtifactStore _artifactStore;
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _archiveGates = new();
    private readonly string _connectionString;
    private readonly IDomainEventStream? _events;
    private readonly int _artifactThresholdCharacters;
    private readonly IOutputSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="SqliteConversationStore"/> class.</summary>
    public SqliteConversationStore(
        string connectionString,
        IArtifactStore artifactStore,
        IOutputSanitizer sanitizer,
        IDomainEventStream? events = null,
        int artifactThresholdCharacters = DefaultArtifactThresholdCharacters,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(artifactThresholdCharacters);
        _connectionString = connectionString;
        _artifactStore = artifactStore;
        _sanitizer = sanitizer;
        _events = events;
        _artifactThresholdCharacters = artifactThresholdCharacters;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ConversationMessage> ArchiveMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateMessage(message);
        cancellationToken.ThrowIfCancellationRequested();
        var sanitized = _sanitizer.Sanitize(message.Content ?? string.Empty);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)));
        var artifactId = message.ArtifactId;
        var inlineBody = sanitized;
        if (sanitized.Length > _artifactThresholdCharacters)
        {
            var artifact = await _artifactStore.StoreAsync(
                sanitized,
                "conversation-message",
                message.SessionId,
                cancellationToken);
            artifactId = artifact.ContentHash;
            inlineBody = null;
        }

        var archiveGate = _archiveGates.GetOrAdd(message.SessionId, static _ => new SemaphoreSlim(1, 1));
        await archiveGate.WaitAsync(cancellationToken);
        try
        {
            return await ArchiveCoreAsync(message, sanitized, hash, artifactId, inlineBody, cancellationToken);
        }
        finally
        {
            archiveGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetModeAsync(
        SessionId sessionId,
        ConversationContextMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_sessions(session_id, mode, updated_at)
            VALUES($session, $mode, $updatedAt)
            ON CONFLICT(session_id) DO UPDATE SET mode = excluded.mode, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$mode", (int)mode);
        command.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (_events is not null)
        {
            await _events.PublishAsync(
                new ConversationModeChanged(sessionId, _timeProvider.GetUtcNow(), mode),
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task ReplaceSummaryAsync(
        SessionId sessionId,
        IReadOnlyList<ConversationMemoryItem> items,
        ConversationSummarySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSummary(sessionId, items, snapshot);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSessionAsync(connection, transaction, sessionId, cancellationToken);
        var newlyPromoted = new List<ConversationMemoryItem>();
        foreach (var item in items)
        {
            if (!await MemoryExistsAsync(connection, transaction, item.Id, cancellationToken))
            {
                newlyPromoted.Add(item);
            }

            await UpsertMemoryAsync(connection, transaction, item, cancellationToken);
        }

        var memoryIndex = JsonSerializer.Serialize(snapshot.MemoryIdsByKind);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_summaries(
                    session_id, version, through_message_sequence, repository_revision,
                    memory_index_json, created_at, schema_version)
                VALUES($session, $version, $through, $revision, $index, $createdAt, $schemaVersion)
                ON CONFLICT(session_id) DO UPDATE SET
                    version = excluded.version,
                    through_message_sequence = excluded.through_message_sequence,
                    repository_revision = excluded.repository_revision,
                    memory_index_json = excluded.memory_index_json,
                    created_at = excluded.created_at,
                    schema_version = excluded.schema_version;
                """;
            command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
            command.Parameters.AddWithValue("$version", snapshot.Version);
            command.Parameters.AddWithValue("$through", snapshot.ThroughMessageSequence);
            command.Parameters.AddWithValue("$revision", (object?)snapshot.RepositoryRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$index", memoryIndex);
            command.Parameters.AddWithValue("$createdAt", snapshot.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$schemaVersion", snapshot.SchemaVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (_events is not null)
        {
            foreach (var item in newlyPromoted)
            {
                await _events.PublishAsync(
                    new ConversationMemoryPromoted(sessionId, item.CreatedAt, item.Id, item.Kind),
                    cancellationToken);
                if (item.SupersedesId is { } supersededId)
                {
                    await _events.PublishAsync(
                        new ConversationMemorySuperseded(
                            sessionId,
                            item.UpdatedAt,
                            supersededId,
                            item.Id),
                        cancellationToken);
                }
            }

            await _events.PublishAsync(
                new ConversationSummarySnapshotReplaced(
                    sessionId,
                    snapshot.CreatedAt,
                    snapshot.Version,
                    snapshot.ThroughMessageSequence,
                    snapshot.MemoryIdsByKind.Values.Sum(ids => ids.Count)),
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task UpdateMemoryAsync(
        ConversationMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateMemory(item);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertMemoryAsync(connection, transaction, item, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (_events is not null && item.Validity is MemoryValidity.Stale or MemoryValidity.Invalid)
        {
            await _events.PublishAsync(
                new ConversationMemoryInvalidated(
                    item.SessionId,
                    item.UpdatedAt,
                    item.Id,
                    item.Validity.ToString()),
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ConversationStateSnapshot> GetSnapshotAsync(
        SessionId sessionId,
        bool includeBodies = true,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var warnings = new List<string>();
        var mode = await ReadModeAsync(connection, sessionId, warnings, cancellationToken);
        var messages = await ReadMessagesAsync(
            connection,
            sessionId,
            includeBodies,
            warnings,
            cancellationToken);
        var memory = await ReadMemoryAsync(
            connection,
            sessionId,
            warnings,
            cancellationToken);
        var summary = await ReadSummaryAsync(
            connection,
            sessionId,
            warnings,
            cancellationToken);
        if (messages.Count == 0 && memory.Count == 0 && summary is null)
        {
            AddWarning(warnings, "LegacySessionWithoutConversationArchive");
        }

        return new ConversationStateSnapshot
        {
            SessionId = sessionId,
            Mode = mode,
            Messages = messages,
            MemoryItems = memory,
            Summary = summary,
            Warnings = warnings,
        };
    }

    /// <inheritdoc />
    public async Task<int> RemoveMessageBodiesOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        string[] artifactIds;
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT DISTINCT artifact_id FROM conversation_messages
                WHERE artifact_id IS NOT NULL AND occurred_at < $cutoff;
                """;
            select.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            var candidates = new List<string>();
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(reader.GetString(0));
            }

            artifactIds = [.. candidates];
        }

        int removed;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE conversation_messages SET body = NULL, artifact_id = NULL
                WHERE (body IS NOT NULL OR artifact_id IS NOT NULL) AND occurred_at < $cutoff;
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            removed = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var artifactId in artifactIds)
        {
            await using var referenceCheck = connection.CreateCommand();
            referenceCheck.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM conversation_messages WHERE artifact_id = $artifact LIMIT 1);
                """;
            referenceCheck.Parameters.AddWithValue("$artifact", artifactId);
            var referencesRemain = (long)(await referenceCheck.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (referencesRemain == 0)
            {
                await _artifactStore.DeleteAsync(artifactId, cancellationToken);
            }
        }

        return removed;
    }

    private async Task<ConversationMessage> ArchiveCoreAsync(
        ConversationMessage message,
        string sanitized,
        string hash,
        string? artifactId,
        string? inlineBody,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var sequence = await GetNextSequenceAsync(connection, transaction, message.SessionId, cancellationToken);
        var archived = message with
        {
            Sequence = sequence,
            Content = inlineBody,
            ArtifactId = artifactId,
            ContentHash = hash,
            EstimatedTokens = Math.Max(1, (sanitized.Length + 3) / 4),
            SchemaVersion = ConversationSchemaVersions.Message,
        };
        await EnsureSessionAsync(connection, transaction, message.SessionId, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_messages(
                    message_id, session_id, run_id, sequence, role, body, artifact_id,
                    content_hash, estimated_tokens, sensitivity, repository_revision,
                    occurred_at, schema_version)
                VALUES($message, $session, $run, $sequence, $role, $body, $artifact,
                    $hash, $tokens, $sensitivity, $revision, $occurredAt, $schemaVersion);
                """;
            AddMessageParameters(command, archived);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (_events is not null)
        {
            await _events.PublishAsync(
                new ConversationMessageArchived(
                    archived.SessionId,
                    archived.OccurredAt,
                    archived.Id,
                    archived.RunId,
                    archived.Sequence,
                    archived.Role,
                    archived.Sensitivity,
                    archived.ArtifactId is not null),
                cancellationToken);
        }

        return archived;
    }

    private static void AddMessageParameters(SqliteCommand command, ConversationMessage message)
    {
        command.Parameters.AddWithValue("$message", message.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$session", message.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$run", message.RunId.Value.ToString("D"));
        command.Parameters.AddWithValue("$sequence", message.Sequence);
        command.Parameters.AddWithValue("$role", (int)message.Role);
        command.Parameters.AddWithValue("$body", (object?)message.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifact", (object?)message.ArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", message.ContentHash);
        command.Parameters.AddWithValue("$tokens", message.EstimatedTokens);
        command.Parameters.AddWithValue("$sensitivity", (int)message.Sensitivity);
        command.Parameters.AddWithValue("$revision", (object?)message.RepositoryRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$occurredAt", message.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$schemaVersion", message.SchemaVersion);
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaximumWarnings)
        {
            warnings.Add(warning);
        }
    }

    private static async Task EnsureSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO conversation_sessions(session_id, mode, updated_at)
            VALUES($session, $mode, $updatedAt);
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$mode", (int)ConversationContextMode.ConversationAware);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> GetNextSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(sequence), 0) + 1
            FROM conversation_messages WHERE session_id = $session;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<ConversationContextMode> ReadModeAsync(
        SqliteConnection connection,
        SessionId sessionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT mode FROM conversation_sessions WHERE session_id = $session;";
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            return ConversationContextMode.ConversationAware;
        }

        var value = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        if (!Enum.IsDefined((ConversationContextMode)value))
        {
            AddWarning(warnings, $"UnknownConversationMode:{value}");
            return ConversationContextMode.ConversationAware;
        }

        return (ConversationContextMode)value;
    }

    private async Task<IReadOnlyList<ConversationMessage>> ReadMessagesAsync(
        SqliteConnection connection,
        SessionId sessionId,
        bool includeBodies,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var messages = new List<ConversationMessage>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, run_id, sequence, role, body, artifact_id, content_hash,
                   estimated_tokens, sensitivity, repository_revision, occurred_at, schema_version
            FROM conversation_messages WHERE session_id = $session ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaVersion = reader.GetInt32(11);
            if (schemaVersion != ConversationSchemaVersions.Message)
            {
                AddWarning(warnings, $"UnsupportedConversationMessageSchema:{schemaVersion}");
                continue;
            }

            var body = includeBodies && !await reader.IsDBNullAsync(4)
                ? reader.GetString(4)
                : null;
            var artifactId = await reader.IsDBNullAsync(5) ? null : reader.GetString(5);
            if (includeBodies && body is null && artifactId is not null)
            {
                body = await _artifactStore.ReadAsync(artifactId, cancellationToken);
                if (body is null)
                {
                    AddWarning(warnings, $"MissingConversationArtifact:{artifactId}");
                }
                else
                {
                    var actualHash = Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes(body)));
                    if (!string.Equals(actualHash, reader.GetString(6), StringComparison.Ordinal))
                    {
                        AddWarning(warnings, $"ConversationArtifactHashMismatch:{artifactId}");
                        body = null;
                    }
                }
            }

            messages.Add(new ConversationMessage
            {
                Id = new ConversationMessageId(Guid.Parse(reader.GetString(0))),
                SessionId = sessionId,
                RunId = new RunId(Guid.Parse(reader.GetString(1))),
                Sequence = reader.GetInt64(2),
                Role = (ConversationRole)reader.GetInt32(3),
                Content = body,
                ArtifactId = artifactId,
                ContentHash = reader.GetString(6),
                EstimatedTokens = reader.GetInt32(7),
                Sensitivity = (ConversationSensitivity)reader.GetInt32(8),
                RepositoryRevision = await reader.IsDBNullAsync(9) ? null : reader.GetString(9),
                OccurredAt = DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                SchemaVersion = schemaVersion,
            });
        }

        return messages;
    }

    private static async Task<IReadOnlyList<ConversationMemoryItem>> ReadMemoryAsync(
        SqliteConnection connection,
        SessionId sessionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var sourceMap = await ReadSourcesAsync(connection, sessionId, cancellationToken);
        var items = new List<ConversationMemoryItem>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT memory_id, kind, content, repository_revision, repository_dependent,
                   supersedes_id, validity, created_at, updated_at, schema_version
            FROM conversation_memory WHERE session_id = $session ORDER BY created_at, memory_id;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaVersion = reader.GetInt32(9);
            if (schemaVersion != ConversationSchemaVersions.Memory)
            {
                AddWarning(warnings, $"UnsupportedConversationMemorySchema:{schemaVersion}");
                continue;
            }

            var id = new ConversationMemoryId(Guid.Parse(reader.GetString(0)));
            sourceMap.TryGetValue(id, out var sources);
            items.Add(new ConversationMemoryItem
            {
                Id = id,
                SessionId = sessionId,
                Kind = (ConversationMemoryKind)reader.GetInt32(1),
                Content = reader.GetString(2),
                SourceMessageIds = sources?.Messages ?? [],
                SourceRunIds = sources?.Runs ?? [],
                SourceEvidenceIds = sources?.Evidence ?? [],
                SourceArtifactIds = sources?.Artifacts ?? [],
                RepositoryRevision = await reader.IsDBNullAsync(3) ? null : reader.GetString(3),
                RepositoryDependent = reader.GetInt32(4) != 0,
                SupersedesId = await reader.IsDBNullAsync(5)
                    ? null
                    : new ConversationMemoryId(Guid.Parse(reader.GetString(5))),
                Validity = (MemoryValidity)reader.GetInt32(6),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                SchemaVersion = schemaVersion,
            });
        }

        return items;
    }

    private static async Task<Dictionary<ConversationMemoryId, MemorySources>> ReadSourcesAsync(
        SqliteConnection connection,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var raw = new Dictionary<ConversationMemoryId, List<(string Kind, string Id, int Ordinal)>>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.memory_id, s.source_kind, s.source_id, s.ordinal
            FROM conversation_memory_sources s
            JOIN conversation_memory m ON m.memory_id = s.memory_id
            WHERE m.session_id = $session ORDER BY s.memory_id, s.source_kind, s.ordinal;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var memoryId = new ConversationMemoryId(Guid.Parse(reader.GetString(0)));
            if (!raw.TryGetValue(memoryId, out var sources))
            {
                sources = [];
                raw.Add(memoryId, sources);
            }

            sources.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return raw.ToDictionary(
            pair => pair.Key,
            pair => new MemorySources(
                pair.Value.Where(source => source.Kind == "message").OrderBy(source => source.Ordinal)
                    .Select(source => new ConversationMessageId(Guid.Parse(source.Id))).ToArray(),
                pair.Value.Where(source => source.Kind == "run").OrderBy(source => source.Ordinal)
                    .Select(source => new RunId(Guid.Parse(source.Id))).ToArray(),
                pair.Value.Where(source => source.Kind == "evidence").OrderBy(source => source.Ordinal)
                    .Select(source => new EvidenceId(Guid.Parse(source.Id))).ToArray(),
                pair.Value.Where(source => source.Kind == "artifact").OrderBy(source => source.Ordinal)
                    .Select(source => source.Id).ToArray()));
    }

    private static async Task<ConversationSummarySnapshot?> ReadSummaryAsync(
        SqliteConnection connection,
        SessionId sessionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, through_message_sequence, repository_revision, memory_index_json,
                   created_at, schema_version
            FROM conversation_summaries WHERE session_id = $session;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var schemaVersion = reader.GetInt32(5);
        if (schemaVersion != ConversationSchemaVersions.Summary)
        {
            AddWarning(warnings, $"UnsupportedConversationSummarySchema:{schemaVersion}");
            return null;
        }

        IReadOnlyDictionary<ConversationMemoryKind, IReadOnlyList<ConversationMemoryId>> index;
        try
        {
            index = JsonSerializer.Deserialize<Dictionary<ConversationMemoryKind, IReadOnlyList<ConversationMemoryId>>>(
                reader.GetString(3)) ?? [];
        }
        catch (JsonException)
        {
            AddWarning(warnings, "InvalidConversationSummaryIndex");
            return null;
        }

        return new ConversationSummarySnapshot
        {
            SessionId = sessionId,
            Version = reader.GetInt64(0),
            ThroughMessageSequence = reader.GetInt64(1),
            RepositoryRevision = await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
            MemoryIdsByKind = index,
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            SchemaVersion = schemaVersion,
        };
    }

    private static async Task<bool> MemoryExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationMemoryId memoryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM conversation_memory WHERE memory_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", memoryId.Value.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task UpsertMemoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationMemoryItem item,
        CancellationToken cancellationToken)
    {
        ValidateMemory(item);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_memory(
                    memory_id, session_id, kind, content, repository_revision,
                    repository_dependent, supersedes_id, validity, created_at, updated_at, schema_version)
                VALUES($id, $session, $kind, $content, $revision, $dependent, $supersedes,
                    $validity, $createdAt, $updatedAt, $schemaVersion)
                ON CONFLICT(memory_id) DO UPDATE SET
                    kind = excluded.kind, content = excluded.content,
                    repository_revision = excluded.repository_revision,
                    repository_dependent = excluded.repository_dependent,
                    supersedes_id = excluded.supersedes_id, validity = excluded.validity,
                    updated_at = excluded.updated_at, schema_version = excluded.schema_version;
                """;
            command.Parameters.AddWithValue("$id", item.Id.Value.ToString("D"));
            command.Parameters.AddWithValue("$session", item.SessionId.Value.ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)item.Kind);
            command.Parameters.AddWithValue("$content", item.Content);
            command.Parameters.AddWithValue("$revision", (object?)item.RepositoryRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$dependent", item.RepositoryDependent ? 1 : 0);
            command.Parameters.AddWithValue("$supersedes", (object?)item.SupersedesId?.Value.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$validity", (int)item.Validity);
            command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$schemaVersion", item.SchemaVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM conversation_memory_sources WHERE memory_id = $id;";
            delete.Parameters.AddWithValue("$id", item.Id.Value.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertSourcesAsync(connection, transaction, item.Id, "message", item.SourceMessageIds.Select(id => id.Value.ToString("D")), cancellationToken);
        await InsertSourcesAsync(connection, transaction, item.Id, "run", item.SourceRunIds.Select(id => id.Value.ToString("D")), cancellationToken);
        await InsertSourcesAsync(connection, transaction, item.Id, "evidence", item.SourceEvidenceIds.Select(id => id.Value.ToString("D")), cancellationToken);
        await InsertSourcesAsync(connection, transaction, item.Id, "artifact", item.SourceArtifactIds, cancellationToken);
    }

    private static async Task InsertSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationMemoryId memoryId,
        string kind,
        IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var ordinal = 0;
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_memory_sources(memory_id, source_kind, source_id, ordinal)
                VALUES($memory, $kind, $source, $ordinal);
                """;
            command.Parameters.AddWithValue("$memory", memoryId.Value.ToString("D"));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$source", id);
            command.Parameters.AddWithValue("$ordinal", ordinal++);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidateMessage(ConversationMessage message)
    {
        if (message.Id == default || message.SessionId == default || message.RunId == default)
        {
            throw new ArgumentException("Conversation messages require non-default identifiers.", nameof(message));
        }

        if (!Enum.IsDefined(message.Role) || !Enum.IsDefined(message.Sensitivity))
        {
            throw new ArgumentException("Conversation message enum values must be defined.", nameof(message));
        }

        if (message.Content is null && message.ArtifactId is null)
        {
            throw new ArgumentException("A conversation message requires visible content or an artifact.", nameof(message));
        }
    }

    private static void ValidateMemory(ConversationMemoryItem item)
    {
        if (item.Id == default || item.SessionId == default)
        {
            throw new ArgumentException("Conversation memory requires non-default identifiers.", nameof(item));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(item.Content);
        if (item.SourceMessageIds.Count == 0)
        {
            throw new ArgumentException("Conversation memory requires message provenance.", nameof(item));
        }

        if (item.RepositoryDependent
            && (item.SourceEvidenceIds.Count == 0 || string.IsNullOrWhiteSpace(item.RepositoryRevision)))
        {
            throw new ArgumentException(
                "Repository-dependent memory requires evidence and revision provenance.",
                nameof(item));
        }
    }

    private static void ValidateSummary(
        SessionId sessionId,
        IReadOnlyList<ConversationMemoryItem> items,
        ConversationSummarySnapshot snapshot)
    {
        if (snapshot.SessionId != sessionId || items.Any(item => item.SessionId != sessionId))
        {
            throw new ArgumentException("Summary state must belong to one session.", nameof(snapshot));
        }

        HashSet<ConversationMemoryId> ids = [.. items.Select(item => item.Id)];
        if (snapshot.MemoryIdsByKind.Values.SelectMany(value => value).Any(id => !ids.Contains(id)))
        {
            throw new ArgumentException("Summary references memory not included in the atomic write.", nameof(snapshot));
        }
    }

    private sealed record MemorySources(
        IReadOnlyList<ConversationMessageId> Messages,
        IReadOnlyList<RunId> Runs,
        IReadOnlyList<EvidenceId> Evidence,
        IReadOnlyList<string> Artifacts);
}
