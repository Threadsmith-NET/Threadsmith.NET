namespace Threadsmith.Persistence;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

        // Retired automatic memory rows and indexes are historical data, never restoration input.
        if (messages.Count == 0)
        {
            AddWarning(warnings, "LegacySessionWithoutConversationArchive");
        }

        return new ConversationStateSnapshot
        {
            SessionId = sessionId,
            Mode = mode,
            Messages = messages,
            MemoryItems = [],
            Summary = null,
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
            if (schemaVersion is < 1 or > ConversationSchemaVersions.Message)
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
}
