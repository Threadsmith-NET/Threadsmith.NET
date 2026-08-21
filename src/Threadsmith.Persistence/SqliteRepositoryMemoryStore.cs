namespace Threadsmith.Persistence;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Persists repository-scoped memory in the repository-local SQLite database.</summary>
public sealed class SqliteRepositoryMemoryStore : IRepositoryMemoryStore
{
    private const int MaximumWarnings = 32;

    private readonly string _connectionString;
    private readonly IOutputSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="SqliteRepositoryMemoryStore"/> class.</summary>
    public SqliteRepositoryMemoryStore(
        string connectionString,
        IOutputSanitizer sanitizer,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _connectionString = connectionString;
        _sanitizer = sanitizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryItem> UpsertAsync(
        RepositoryMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        return await UpsertWithStateUpdatesAsync(item, [], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryItem> UpsertWithStateUpdatesAsync(
        RepositoryMemoryItem item,
        IReadOnlyList<RepositoryMemoryStateUpdate> stateUpdates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(stateUpdates);
        var sanitized = _sanitizer.Sanitize(item.Content);
        var now = _timeProvider.GetUtcNow();
        var persisted = item with
        {
            Content = sanitized,
            ContentHash = ComputeContentHash(sanitized),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt,
            SchemaVersion = RepositoryMemorySchemaVersions.Item,
        };
        ValidateMemory(persisted);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ApplyStateUpdatesAsync(
            connection,
            transaction,
            item.RepositoryIdentity,
            stateUpdates,
            now,
            cancellationToken);
        await UpsertCoreAsync(connection, transaction, persisted, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return persisted;
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryRememberResult> InsertBoundedAsync(
        RepositoryMemoryItem item,
        int maximumActiveItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActiveItems);
        var persisted = PrepareForPersistence(item);
        ValidateMemory(persisted);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var duplicateId = await FindActiveDuplicateAsync(connection, transaction, persisted, cancellationToken);
        if (duplicateId is { } existingId)
        {
            await transaction.RollbackAsync(cancellationToken);
            var snapshot = await GetSnapshotAsync(persisted.RepositoryIdentity, cancellationToken);
            var duplicate = snapshot.Items.FirstOrDefault(existing => existing.Id == existingId)
                ?? throw new InvalidOperationException("Concurrent repository memory changed after duplicate detection.");
            return new RepositoryMemoryRememberResult(duplicate, false, []);
        }

        var candidates = await ReadCapacityCandidatesAsync(
            connection,
            transaction,
            persisted.RepositoryIdentity,
            cancellationToken);
        candidates.Add(new CapacityCandidate(
            persisted.Id,
            persisted.Authority,
            persisted.Kind,
            persisted.CreatedAt,
            IsIncoming: true));
        var overflow = candidates
            .OrderByDescending(candidate => PreservationOrder(candidate.Authority, candidate.Kind))
            .ThenBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id.Value)
            .Take(Math.Max(0, candidates.Count - maximumActiveItems))
            .ToArray();
        const string reason = "Repository memory active-item bound was exceeded.";
        var updates = overflow.Select(candidate => new RepositoryMemoryStateUpdate(
            candidate.Id,
            RepositoryMemoryValidity.Active,
            RepositoryMemoryValidity.Stale,
            reason)).ToArray();
        await ApplyStateUpdatesAsync(
            connection,
            transaction,
            persisted.RepositoryIdentity,
            updates.Where(update => update.MemoryId != persisted.Id).ToArray(),
            persisted.UpdatedAt,
            cancellationToken);
        if (overflow.Any(candidate => candidate.IsIncoming))
        {
            persisted = persisted with
            {
                Validity = RepositoryMemoryValidity.Stale,
                StateReason = reason,
            };
        }

        await UpsertCoreAsync(connection, transaction, persisted, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RepositoryMemoryRememberResult(persisted, true, updates);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryItem> SupersedeAsync(
        string repositoryIdentity,
        RepositoryMemoryId supersededId,
        RepositoryMemoryItem replacement,
        IReadOnlyList<RepositoryMemoryStateUpdate> stateUpdates,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(stateUpdates);
        if (supersededId == default)
        {
            throw new ArgumentException("Repository memory requires a non-default superseded identifier.", nameof(supersededId));
        }

        if (!string.Equals(repositoryIdentity, replacement.RepositoryIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentException("Replacement memory must belong to the target repository.", nameof(replacement));
        }

        var sanitized = _sanitizer.Sanitize(replacement.Content);
        var now = _timeProvider.GetUtcNow();
        var persisted = replacement with
        {
            Content = sanitized,
            ContentHash = ComputeContentHash(sanitized),
            SupersedesId = supersededId,
            CreatedAt = replacement.CreatedAt == default ? now : replacement.CreatedAt,
            UpdatedAt = replacement.UpdatedAt == default ? now : replacement.UpdatedAt,
            SchemaVersion = RepositoryMemorySchemaVersions.Item,
        };
        ValidateMemory(persisted);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ApplyStateUpdatesAsync(
            connection,
            transaction,
            repositoryIdentity,
            stateUpdates,
            now,
            cancellationToken);
        await UpsertCoreAsync(connection, transaction, persisted, cancellationToken);
        var updated = await UpdateValidityCoreAsync(
            connection,
            transaction,
            repositoryIdentity,
            supersededId,
            expectedValidity: null,
            RepositoryMemoryValidity.Superseded,
            _sanitizer.Sanitize(reason),
            now,
            cancellationToken);
        if (!updated)
        {
            throw new InvalidOperationException("The repository memory item to supersede was not found.");
        }

        await transaction.CommitAsync(cancellationToken);
        return persisted;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateValidityAsync(
        string repositoryIdentity,
        RepositoryMemoryId memoryId,
        RepositoryMemoryValidity validity,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (memoryId == default)
        {
            throw new ArgumentException("Repository memory requires a non-default identifier.", nameof(memoryId));
        }

        if (!Enum.IsDefined(validity))
        {
            throw new ArgumentOutOfRangeException(nameof(validity));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var updated = await UpdateValidityCoreAsync(
            connection,
            transaction,
            repositoryIdentity,
            memoryId,
            expectedValidity: null,
            validity,
            _sanitizer.Sanitize(reason),
            _timeProvider.GetUtcNow(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    /// <inheritdoc />
    public async Task<RepositoryMemorySnapshot> GetSnapshotAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var warnings = new List<string>();
        var sources = await ReadSourcesAsync(connection, repositoryIdentity, warnings, cancellationToken);
        var scopes = await ReadScopesAsync(connection, repositoryIdentity, warnings, cancellationToken);
        var items = new List<RepositoryMemoryItem>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT memory_id, kind, authority, validity, sensitivity, content, content_hash,
                   repository_revision, supersedes_id, state_reason, created_at, updated_at, schema_version
            FROM repository_memory
            WHERE repository_identity = $repository
            ORDER BY created_at, memory_id;
            """;
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaVersion = reader.GetInt32(12);
            if (schemaVersion != RepositoryMemorySchemaVersions.Item)
            {
                AddWarning(warnings, $"UnsupportedRepositoryMemorySchema:{schemaVersion}");
                continue;
            }

            if (!Guid.TryParse(reader.GetString(0), out var idValue)
                || idValue == Guid.Empty
                || !Enum.IsDefined((RepositoryMemoryKind)reader.GetInt32(1))
                || !Enum.IsDefined((RepositoryMemoryAuthority)reader.GetInt32(2))
                || !Enum.IsDefined((RepositoryMemoryValidity)reader.GetInt32(3))
                || !Enum.IsDefined((ConversationSensitivity)reader.GetInt32(4))
                || !DateTimeOffset.TryParse(
                    reader.GetString(10),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var createdAt)
                || !DateTimeOffset.TryParse(
                    reader.GetString(11),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var updatedAt))
            {
                AddWarning(warnings, "MalformedRepositoryMemoryItem");
                continue;
            }

            RepositoryMemoryId? supersedesId = null;
            if (!await reader.IsDBNullAsync(8))
            {
                if (!Guid.TryParse(reader.GetString(8), out var supersedesValue)
                    || supersedesValue == Guid.Empty
                    || supersedesValue == idValue)
                {
                    AddWarning(warnings, "MalformedRepositoryMemorySupersession");
                    continue;
                }

                supersedesId = new RepositoryMemoryId(supersedesValue);
            }

            var id = new RepositoryMemoryId(idValue);
            sources.TryGetValue(id, out var itemSources);
            if (itemSources is null or { Count: 0 })
            {
                AddWarning(warnings, "MalformedRepositoryMemoryProvenance");
                continue;
            }

            scopes.TryGetValue(id, out var itemScope);
            items.Add(new RepositoryMemoryItem
            {
                Id = id,
                RepositoryIdentity = repositoryIdentity,
                Kind = (RepositoryMemoryKind)reader.GetInt32(1),
                Authority = (RepositoryMemoryAuthority)reader.GetInt32(2),
                Validity = (RepositoryMemoryValidity)reader.GetInt32(3),
                Sensitivity = (ConversationSensitivity)reader.GetInt32(4),
                Content = reader.GetString(5),
                ContentHash = reader.GetString(6),
                RepositoryRevision = await reader.IsDBNullAsync(7) ? null : reader.GetString(7),
                SupersedesId = supersedesId,
                StateReason = await reader.IsDBNullAsync(9) ? null : reader.GetString(9),
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                SchemaVersion = schemaVersion,
                Sources = itemSources ?? [],
                Scope = itemScope ?? new RepositoryMemoryScope(),
            });
        }

        return new RepositoryMemorySnapshot
        {
            RepositoryIdentity = repositoryIdentity,
            Items = items,
            Warnings = warnings,
        };
    }

    private static async Task<RepositoryMemoryId?> FindActiveDuplicateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryMemoryItem item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT memory_id, content
            FROM repository_memory
            WHERE repository_identity = $repository
              AND validity = $active
              AND kind = $kind
            ORDER BY created_at, memory_id;
            """;
        command.Parameters.AddWithValue("$repository", item.RepositoryIdentity);
        command.Parameters.AddWithValue("$active", (int)RepositoryMemoryValidity.Active);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        var normalizedContent = NormalizeContent(item.Content);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var id)
                && id != Guid.Empty
                && string.Equals(
                    NormalizeContent(reader.GetString(1)),
                    normalizedContent,
                    StringComparison.Ordinal))
            {
                return new RepositoryMemoryId(id);
            }
        }

        return null;
    }

    private static async Task<List<CapacityCandidate>> ReadCapacityCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryIdentity,
        CancellationToken cancellationToken)
    {
        var candidates = new List<CapacityCandidate>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT memory_id, authority, kind, created_at
            FROM repository_memory
            WHERE repository_identity = $repository AND validity = $active;
            """;
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        command.Parameters.AddWithValue("$active", (int)RepositoryMemoryValidity.Active);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id)
                || id == Guid.Empty
                || !Enum.IsDefined((RepositoryMemoryAuthority)reader.GetInt32(1))
                || !Enum.IsDefined((RepositoryMemoryKind)reader.GetInt32(2))
                || !DateTimeOffset.TryParse(
                    reader.GetString(3),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var createdAt))
            {
                throw new InvalidOperationException("Malformed active repository memory prevents bounded insertion.");
            }

            candidates.Add(new CapacityCandidate(
                new RepositoryMemoryId(id),
                (RepositoryMemoryAuthority)reader.GetInt32(1),
                (RepositoryMemoryKind)reader.GetInt32(2),
                createdAt,
                IsIncoming: false));
        }

        return candidates;
    }

    private static string NormalizeContent(string content)
    {
        return string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }

    private static int PreservationOrder(RepositoryMemoryAuthority authority, RepositoryMemoryKind kind)
    {
        var authorityOrder = authority switch
        {
            RepositoryMemoryAuthority.UserAuthored => 0,
            RepositoryMemoryAuthority.HostObserved => 1,
            RepositoryMemoryAuthority.EvidenceBacked => 2,
            RepositoryMemoryAuthority.ModelProposedValidated => 3,
            _ => 4,
        };
        var kindOrder = kind switch
        {
            RepositoryMemoryKind.UserConstraint => 0,
            RepositoryMemoryKind.UserPreference => 1,
            RepositoryMemoryKind.ArchitectureDecision => 2,
            RepositoryMemoryKind.RepositoryConvention => 3,
            RepositoryMemoryKind.WorkflowFact => 4,
            RepositoryMemoryKind.KnownFailure => 5,
            RepositoryMemoryKind.UnresolvedQuestion => 6,
            RepositoryMemoryKind.EvidenceBackedRepositoryFact => 7,
            _ => 8,
        };
        return (authorityOrder * 16) + kindOrder;
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaximumWarnings)
        {
            warnings.Add(warning);
        }
    }

    private static string ComputeContentHash(string content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static async Task UpsertCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryMemoryItem item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO repository_memory(
                memory_id, repository_identity, kind, authority, validity, sensitivity,
                content, content_hash, repository_revision, supersedes_id, state_reason,
                created_at, updated_at, schema_version)
            VALUES($memory, $repository, $kind, $authority, $validity, $sensitivity,
                $content, $hash, $revision, $supersedes, $reason, $createdAt, $updatedAt, $schemaVersion)
            ON CONFLICT(memory_id) DO UPDATE SET
                repository_identity = excluded.repository_identity,
                kind = excluded.kind,
                authority = excluded.authority,
                validity = excluded.validity,
                sensitivity = excluded.sensitivity,
                content = excluded.content,
                content_hash = excluded.content_hash,
                repository_revision = excluded.repository_revision,
                supersedes_id = excluded.supersedes_id,
                state_reason = excluded.state_reason,
                updated_at = excluded.updated_at,
                schema_version = excluded.schema_version;
            """;
        command.Parameters.AddWithValue("$memory", item.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$repository", item.RepositoryIdentity);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$authority", (int)item.Authority);
        command.Parameters.AddWithValue("$validity", (int)item.Validity);
        command.Parameters.AddWithValue("$sensitivity", (int)item.Sensitivity);
        command.Parameters.AddWithValue("$content", item.Content);
        command.Parameters.AddWithValue("$hash", item.ContentHash);
        command.Parameters.AddWithValue("$revision", (object?)item.RepositoryRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$supersedes", (object?)item.SupersedesId?.Value.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)item.StateReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$schemaVersion", item.SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await DeleteChildrenAsync(connection, transaction, item.Id, cancellationToken);
        await InsertSourcesAsync(connection, transaction, item.Id, item.Sources, cancellationToken);
        await InsertScopesAsync(connection, transaction, item.Id, "path", item.Scope.Paths, cancellationToken);
        await InsertScopesAsync(connection, transaction, item.Id, "symbol", item.Scope.Symbols, cancellationToken);
        await InsertScopesAsync(connection, transaction, item.Id, "project", item.Scope.Projects, cancellationToken);
    }

    private async Task ApplyStateUpdatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryIdentity,
        IReadOnlyList<RepositoryMemoryStateUpdate> stateUpdates,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        foreach (var stateUpdate in stateUpdates)
        {
            if (stateUpdate.MemoryId == default
                || !Enum.IsDefined(stateUpdate.PreviousValidity)
                || !Enum.IsDefined(stateUpdate.Validity))
            {
                throw new ArgumentException("Repository memory state updates must be valid.", nameof(stateUpdates));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(stateUpdate.Reason);
            var updated = await UpdateValidityCoreAsync(
                connection,
                transaction,
                repositoryIdentity,
                stateUpdate.MemoryId,
                stateUpdate.PreviousValidity,
                stateUpdate.Validity,
                _sanitizer.Sanitize(stateUpdate.Reason),
                updatedAt,
                cancellationToken);
            if (!updated)
            {
                throw new InvalidOperationException(
                    "Repository memory changed concurrently while enforcing the active-item bound.");
            }
        }
    }

    private static async Task<bool> UpdateValidityCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryIdentity,
        RepositoryMemoryId memoryId,
        RepositoryMemoryValidity? expectedValidity,
        RepositoryMemoryValidity validity,
        string reason,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE repository_memory
            SET validity = $validity, state_reason = $reason, updated_at = $updatedAt
            WHERE repository_identity = $repository
              AND memory_id = $memory
              AND ($expectedValidity IS NULL OR validity = $expectedValidity)
              AND (validity <> $validity OR state_reason IS NULL OR state_reason <> $reason);
            """;
        command.Parameters.AddWithValue("$validity", (int)validity);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        command.Parameters.AddWithValue("$memory", memoryId.Value.ToString("D"));
        command.Parameters.AddWithValue("$expectedValidity", (object?)(int?)expectedValidity ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task DeleteChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryMemoryId memoryId,
        CancellationToken cancellationToken)
    {
        await using var sources = connection.CreateCommand();
        sources.Transaction = transaction;
        sources.CommandText = "DELETE FROM repository_memory_sources WHERE memory_id = $memory;";
        sources.Parameters.AddWithValue("$memory", memoryId.Value.ToString("D"));
        await sources.ExecuteNonQueryAsync(cancellationToken);

        await using var scopes = connection.CreateCommand();
        scopes.Transaction = transaction;
        scopes.CommandText = "DELETE FROM repository_memory_scope WHERE memory_id = $memory;";
        scopes.Parameters.AddWithValue("$memory", memoryId.Value.ToString("D"));
        await scopes.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryMemoryId memoryId,
        IReadOnlyList<RepositoryMemorySource> sources,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < sources.Count; ordinal++)
        {
            var source = sources[ordinal];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO repository_memory_sources(memory_id, source_kind, source_id, description, ordinal)
                VALUES($memory, $kind, $source, $description, $ordinal);
                """;
            command.Parameters.AddWithValue("$memory", memoryId.Value.ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)source.Kind);
            command.Parameters.AddWithValue("$source", source.SourceId);
            command.Parameters.AddWithValue("$description", (object?)source.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertScopesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryMemoryId memoryId,
        string kind,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < values.Count; ordinal++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO repository_memory_scope(memory_id, scope_kind, scope_value, ordinal)
                VALUES($memory, $kind, $value, $ordinal);
                """;
            command.Parameters.AddWithValue("$memory", memoryId.Value.ToString("D"));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$value", values[ordinal]);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<Dictionary<RepositoryMemoryId, IReadOnlyList<RepositoryMemorySource>>> ReadSourcesAsync(
        SqliteConnection connection,
        string repositoryIdentity,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var raw = new Dictionary<RepositoryMemoryId, List<RepositoryMemorySource>>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.memory_id, s.source_kind, s.source_id, s.description
            FROM repository_memory_sources s
            JOIN repository_memory m ON m.memory_id = s.memory_id
            WHERE m.repository_identity = $repository
            ORDER BY s.memory_id, s.ordinal;
            """;
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var memoryIdValue)
                || memoryIdValue == Guid.Empty
                || !Enum.IsDefined((RepositoryMemorySourceKind)reader.GetInt32(1))
                || string.IsNullOrWhiteSpace(reader.GetString(2)))
            {
                AddWarning(warnings, "MalformedRepositoryMemorySource");
                continue;
            }

            var memoryId = new RepositoryMemoryId(memoryIdValue);
            if (!raw.TryGetValue(memoryId, out var sources))
            {
                sources = [];
                raw.Add(memoryId, sources);
            }

            sources.Add(new RepositoryMemorySource
            {
                Kind = (RepositoryMemorySourceKind)reader.GetInt32(1),
                SourceId = reader.GetString(2),
                Description = await reader.IsDBNullAsync(3) ? null : reader.GetString(3),
            });
        }

        return raw.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<RepositoryMemorySource>)[.. pair.Value]);
    }

    private static async Task<Dictionary<RepositoryMemoryId, RepositoryMemoryScope>> ReadScopesAsync(
        SqliteConnection connection,
        string repositoryIdentity,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var raw = new Dictionary<RepositoryMemoryId, List<(string Kind, string Value, int Ordinal)>>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.memory_id, s.scope_kind, s.scope_value, s.ordinal
            FROM repository_memory_scope s
            JOIN repository_memory m ON m.memory_id = s.memory_id
            WHERE m.repository_identity = $repository
            ORDER BY s.memory_id, s.scope_kind, s.ordinal;
            """;
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var scopeKind = reader.GetString(1);
            var scopeValue = reader.GetString(2);
            if (!Guid.TryParse(reader.GetString(0), out var memoryIdValue)
                || memoryIdValue == Guid.Empty
                || scopeKind is not ("path" or "symbol" or "project")
                || string.IsNullOrWhiteSpace(scopeValue))
            {
                AddWarning(warnings, "MalformedRepositoryMemoryScope");
                continue;
            }

            var memoryId = new RepositoryMemoryId(memoryIdValue);
            if (!raw.TryGetValue(memoryId, out var scopes))
            {
                scopes = [];
                raw.Add(memoryId, scopes);
            }

            scopes.Add((scopeKind, scopeValue, reader.GetInt32(3)));
        }

        return raw.ToDictionary(
            pair => pair.Key,
            pair => new RepositoryMemoryScope
            {
                Paths = pair.Value.Where(scope => scope.Kind == "path").OrderBy(scope => scope.Ordinal)
                    .Select(scope => scope.Value).ToArray(),
                Symbols = pair.Value.Where(scope => scope.Kind == "symbol").OrderBy(scope => scope.Ordinal)
                    .Select(scope => scope.Value).ToArray(),
                Projects = pair.Value.Where(scope => scope.Kind == "project").OrderBy(scope => scope.Ordinal)
                    .Select(scope => scope.Value).ToArray(),
            });
    }

    private static void ValidateMemory(RepositoryMemoryItem item)
    {
        if (item.Id == default)
        {
            throw new ArgumentException("Repository memory requires a non-default identifier.", nameof(item));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(item.RepositoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Content);
        if (!Enum.IsDefined(item.Kind)
            || !Enum.IsDefined(item.Authority)
            || !Enum.IsDefined(item.Validity)
            || !Enum.IsDefined(item.Sensitivity))
        {
            throw new ArgumentException("Repository memory enum values must be defined.", nameof(item));
        }

        if (item.Sources.Count == 0)
        {
            throw new ArgumentException("Repository memory requires explicit provenance.", nameof(item));
        }

        if (item.SupersedesId == item.Id)
        {
            throw new ArgumentException("Repository memory cannot supersede itself.", nameof(item));
        }

        if (item.Authority is RepositoryMemoryAuthority.EvidenceBacked
            && !item.Sources.Any(source => source.Kind is
                RepositoryMemorySourceKind.Evidence or RepositoryMemorySourceKind.ValidationResult))
        {
            throw new ArgumentException(
                "Evidence-backed repository memory requires governed evidence or validation provenance.",
                nameof(item));
        }
    }

    private RepositoryMemoryItem PrepareForPersistence(RepositoryMemoryItem item)
    {
        var sanitized = _sanitizer.Sanitize(item.Content);
        var now = _timeProvider.GetUtcNow();
        return item with
        {
            Content = sanitized,
            ContentHash = ComputeContentHash(sanitized),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt,
            SchemaVersion = RepositoryMemorySchemaVersions.Item,
        };
    }

    private sealed record CapacityCandidate(
        RepositoryMemoryId Id,
        RepositoryMemoryAuthority Authority,
        RepositoryMemoryKind Kind,
        DateTimeOffset CreatedAt,
        bool IsIncoming);
}
