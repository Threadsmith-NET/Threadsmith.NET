namespace Threadsmith.Persistence;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Stores explicit memories in SQLite with atomic bounded writes and portable little-endian float32 vectors.</summary>
public sealed partial class SqliteManagedRepositoryMemoryStore : IManagedRepositoryMemoryStore
{
    private const int MaximumTextCharacters = 2_000;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="SqliteManagedRepositoryMemoryStore"/> class.</summary>
    public SqliteManagedRepositoryMemoryStore(string connectionString, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(
        string repositoryIdentity,
        IReadOnlyList<string> lexicalTerms,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentNullException.ThrowIfNull(lexicalTerms);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var warnings = new List<string>();
        var entries = await ReadEntriesAsync(connection, transaction, repositoryIdentity, warnings, cancellationToken);
        await using var generation = CreateCommand(connection, transaction, "SELECT revision FROM managed_memory_repositories WHERE repository_identity = $repo;", repositoryIdentity);
        var revision = Convert.ToInt64(await generation.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var matches = await ReadLexicalMatchesAsync(connection, transaction, repositoryIdentity, lexicalTerms, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RepositoryMemoryReadSnapshot(repositoryIdentity, revision, entries, matches, warnings);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryWriteResult> AddAsync(
        string repositoryIdentity,
        RepositoryMemoryWrite write,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateWrite(repositoryIdentity, write, model, embedding, options);
        var hash = ComputeHash(write.Text);
        var now = _timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var entries = await ReadEntriesAsync(connection, transaction, repositoryIdentity, [], cancellationToken);
        var duplicate = entries.FirstOrDefault(entry => entry.ContentHash == hash && entry.Text == write.Text);
        if (duplicate is not null)
        {
            return new RepositoryMemoryWriteResult(RepositoryMemoryWriteStatus.Duplicate, duplicate, []);
        }

        var evicted = await EvictAsync(connection, transaction, repositoryIdentity, entries, options.MaxNumberOfRepoMemories - 1, now, cancellationToken);
        var entry = CreateEntry(repositoryIdentity, RepositoryMemoryId.New(), write, hash, now, now, 1, model, embedding);
        await InsertEntryAsync(connection, transaction, entry, cancellationToken);
        await AdvanceRevisionAsync(connection, transaction, repositoryIdentity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RepositoryMemoryWriteResult(RepositoryMemoryWriteStatus.Added, entry, evicted);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryWriteResult> UpdateAsync(
        string repositoryIdentity,
        RepositoryMemoryId id,
        long expectedRevision,
        RepositoryMemoryWrite write,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateWrite(repositoryIdentity, write, model, embedding, options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        var hash = ComputeHash(write.Text);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var entries = await ReadEntriesAsync(connection, transaction, repositoryIdentity, [], cancellationToken);
        var existing = entries.FirstOrDefault(entry => entry.Id == id);
        if (existing is null)
        {
            return new RepositoryMemoryWriteResult(RepositoryMemoryWriteStatus.NotFound, null, []);
        }

        if (existing.Revision != expectedRevision)
        {
            return new RepositoryMemoryWriteResult(RepositoryMemoryWriteStatus.Conflict, existing, []);
        }

        if (existing.ContentHash == hash && existing.Text == write.Text)
        {
            return new RepositoryMemoryWriteResult(RepositoryMemoryWriteStatus.Unchanged, existing, []);
        }

        var duplicate = entries.FirstOrDefault(entry => entry.Id != id && entry.ContentHash == hash && entry.Text == write.Text);
        if (duplicate is not null)
        {
            return new RepositoryMemoryWriteResult(RepositoryMemoryWriteStatus.Duplicate, duplicate, []);
        }

        var updated = CreateEntry(repositoryIdentity, id, write, hash, existing.CreatedAt, _timeProvider.GetUtcNow(), checked(existing.Revision + 1), model, embedding);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$repo", repositoryIdentity);
        command.CommandText = """
            UPDATE managed_memories SET text = $text, content_hash = $hash, content_revision = $revision,
                origin = $origin, sensitivity = $sensitivity, updated_at = $updated,
                source_session_id = $session, source_run_id = $run, source_invocation_id = $invocation,
                inclusion_count = 0, last_included_at = NULL, embedding = $embedding,
                embedding_space_id = $space, embedding_dimensions = $dimensions,
                embedding_content_hash = $hash, embedding_revision = $revision
            WHERE repository_identity = $repo AND memory_id = $id;
            DELETE FROM managed_memory_inclusions WHERE repository_identity = $repo AND memory_id = $id;
            """;
        AddEntryParameters(command, updated);
        await command.ExecuteNonQueryAsync(cancellationToken);
        entries.Remove(existing);
        entries.Add(updated);
        var evicted = await EvictAsync(connection, transaction, repositoryIdentity, entries, options.MaxNumberOfRepoMemories, _timeProvider.GetUtcNow(), cancellationToken);
        await AdvanceRevisionAsync(connection, transaction, repositoryIdentity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RepositoryMemoryWriteResult(RepositoryMemoryWriteStatus.Updated, updated, evicted);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryEntry?> RemoveAsync(string repositoryIdentity, RepositoryMemoryId id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var entries = await ReadEntriesAsync(connection, transaction, repositoryIdentity, [], cancellationToken);
        var existing = entries.FirstOrDefault(entry => entry.Id == id);
        if (existing is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await DeleteEntryAsync(connection, transaction, repositoryIdentity, id, cancellationToken);
        await AdvanceRevisionAsync(connection, transaction, repositoryIdentity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return existing;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(
        string repositoryIdentity,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var entries = await ReadEntriesAsync(connection, transaction, repositoryIdentity, [], cancellationToken);
        var evicted = await EvictAsync(connection, transaction, repositoryIdentity, entries, options.MaxNumberOfRepoMemories, _timeProvider.GetUtcNow(), cancellationToken);
        if (evicted.Count > 0)
        {
            await AdvanceRevisionAsync(connection, transaction, repositoryIdentity, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return evicted;
    }

    /// <inheritdoc />
    public async Task<bool> AttachEmbeddingAsync(
        string repositoryIdentity,
        RepositoryMemoryId id,
        long expectedRevision,
        string expectedContentHash,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedContentHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        ValidateEmbedding(model, embedding);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$repo", repositoryIdentity);
        command.CommandText = """
            UPDATE managed_memories SET embedding = $embedding, embedding_space_id = $space,
                embedding_dimensions = $dimensions, embedding_content_hash = $hash, embedding_revision = $revision
            WHERE repository_identity = $repo AND memory_id = $id
                AND content_revision = $revision AND content_hash = $hash;
            """;
        command.Parameters.AddWithValue("$id", id.Value.ToString());
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$hash", expectedContentHash);
        command.Parameters.AddWithValue("$embedding", EncodeVector(embedding.Vector.Span));
        command.Parameters.AddWithValue("$space", model.SpaceId);
        command.Parameters.AddWithValue("$dimensions", model.Dimensions);
        var attached = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (attached)
        {
            await AdvanceRevisionAsync(connection, transaction, repositoryIdentity, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return attached;
    }

    /// <inheritdoc />
    public async Task RecordInclusionsAsync(
        string repositoryIdentity,
        RunId runId,
        IReadOnlyList<RepositoryMemoryInclusion> inclusions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentNullException.ThrowIfNull(inclusions);
        var now = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var inclusion in inclusions.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$repo", repositoryIdentity);
            command.CommandText = """
                INSERT OR IGNORE INTO managed_memory_inclusions(repository_identity, run_id, memory_id, content_revision)
                SELECT repository_identity, $run, memory_id, content_revision FROM managed_memories
                WHERE repository_identity = $repo AND memory_id = $id AND content_revision = $revision;
                UPDATE managed_memories SET inclusion_count = inclusion_count + changes(),
                    last_included_at = CASE WHEN last_included_at IS NULL OR last_included_at < $now THEN $now ELSE last_included_at END
                WHERE repository_identity = $repo AND memory_id = $id AND content_revision = $revision;
                """;
            command.Parameters.AddWithValue("$run", runId.Value.ToString());
            command.Parameters.AddWithValue("$id", inclusion.Id.Value.ToString());
            command.Parameters.AddWithValue("$revision", inclusion.Revision);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task PruneInclusionsAsync(string repositoryIdentity, RunId runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, "DELETE FROM managed_memory_inclusions WHERE repository_identity = $repo AND run_id = $run;", repositoryIdentity);
        command.Parameters.AddWithValue("$run", runId.Value.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Hashes complete normalized text for content and vector revision fencing.</summary>
    internal static string ComputeHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void ValidateWrite(
        string repositoryIdentity,
        RepositoryMemoryWrite write,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        RepositoryMemoryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Text);
        if (write.Text.Length > MaximumTextCharacters || write.Text != write.Text.ReplaceLineEndings("\n").Trim())
        {
            throw new ArgumentException("Memory text must be normalized and contain at most 2,000 characters; normalize before embedding.", nameof(write));
        }

        if (!Enum.IsDefined(write.Origin) || !Enum.IsDefined(write.Sensitivity))
        {
            throw new ArgumentException("Memory origin and sensitivity must be host-owned supported values.", nameof(write));
        }

        ValidateEmbedding(model, embedding);
    }

    private static void ValidateEmbedding(TextEmbeddingModelDescriptor model, TextEmbeddingResult embedding)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.SpaceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(model.Dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(model.MaxInputTokens);
        if (embedding.WasTruncated || embedding.InputTokenCount <= 0 || embedding.InputTokenCount > model.MaxInputTokens
            || !IsValidVector(embedding.Vector.Span, model.Dimensions))
        {
            throw new ArgumentException("Memory embedding must represent the complete input and be finite, normalized and dimensionally compatible.", nameof(embedding));
        }
    }

    private static RepositoryMemoryEntry CreateEntry(
        string repositoryIdentity, RepositoryMemoryId id, RepositoryMemoryWrite write, string hash, DateTimeOffset createdAt, DateTimeOffset updatedAt, long revision, TextEmbeddingModelDescriptor model, TextEmbeddingResult embedding) => new()
        {
            Id = id,
            RepositoryIdentity = repositoryIdentity,
            Text = write.Text,
            ContentHash = hash,
            Revision = revision,
            Origin = write.Origin,
            Sensitivity = write.Sensitivity,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            SourceSessionId = write.SourceSessionId,
            SourceRunId = write.SourceRunId,
            SourceInvocationId = write.SourceInvocationId,
            Embedding = embedding.Vector.ToArray(),
            EmbeddingSpaceId = model.SpaceId,
            EmbeddingDimensions = model.Dimensions,
            EmbeddingContentHash = hash,
            EmbeddingRevision = revision,
        };
}
