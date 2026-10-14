namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Transient collection previews execute the ordinary store path and roll back before returning.</summary>
public sealed partial class SqliteManagedRepositoryMemoryStore
{
    /// <inheritdoc />
    public IManagedRepositoryMemoryStore ForCollection(RepositoryMemoryCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (_collection is not null)
        {
            throw new InvalidOperationException("Collection preparation cannot be nested.");
        }

        return new SqliteManagedRepositoryMemoryStore(_connectionString, _timeProvider, _termResolver) { _collection = collection };
    }

    /// <inheritdoc />
    public async Task CommitCollectionAsync(
        RepositoryMemoryCollection collection, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await collection.Operations.WaitAsync(cancellationToken);
        try
        {
            var identity = collection.Baseline.RepositoryIdentity;
            await using var connection = new SqliteConnection(_connectionString);
            await OpenConnectionAsync(connection, cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var health = CreateCommand(connection, transaction, "SELECT fault FROM repository_intelligence_state WHERE repository_identity = $repo;", identity);
            if (await health.ExecuteScalarAsync(cancellationToken) is string fault)
            {
                throw new InvalidOperationException(fault);
            }

            var existing = await ReadEntriesAsync(connection, transaction, identity, [], cancellationToken: cancellationToken);
            var versions = existing.Select(entry => (entry.Id, entry.Revision)).ToHashSet();
            if (!versions.SetEquals(collection.Baseline.Entries.Select(entry => (entry.Id, entry.Revision))))
            {
                throw new InvalidOperationException("Repository memories changed during collection. The prior collection was preserved; retry to reconcile the current guidance.");
            }

            var prepared = collection.Snapshot(forPublication: true);
            if (prepared.Count > options.Intelligence.MaximumStoredMemories)
            {
                throw new InvalidOperationException("Intelligence capacity changed during collection. The prior collection was preserved; retry using the current capacity.");
            }

            await ApplyPreparedEntriesAsync(connection, transaction, identity, prepared, existing, cancellationToken);
            const string complete = """
                INSERT INTO repository_intelligence_state(repository_identity, onboarded, refreshed_sequence, observation_gap)
                VALUES($repo, 1, $checkpoint, 0)
                ON CONFLICT(repository_identity) DO UPDATE SET onboarded = 1,
                    refreshed_sequence = MAX(refreshed_sequence, $checkpoint);
                """;
            await using var command = CreateCommand(connection, transaction, complete, identity);
            command.Parameters.AddWithValue("$checkpoint", collection.Checkpoint);
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "DELETE FROM repository_intelligence_changes WHERE repository_identity = $repo AND sequence <= $checkpoint AND path = $path;";
            var coveredPath = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in collection.CoveredPaths)
            {
                coveredPath.Value = NormalizeSourcePath(path);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await AdvanceRevisionAsync(connection, transaction, identity, cancellationToken);
            command.CommandText = "DELETE FROM repository_intelligence_checkpoints WHERE repository_identity = $repo;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            collection.Operations.Release();
        }
    }

    private async Task<IDisposable?> EnterCollectionAsync(string repositoryIdentity, CancellationToken cancellationToken)
    {
        if (_collection is null)
        {
            return null;
        }

        if (_collection.Baseline.RepositoryIdentity != repositoryIdentity)
        {
            throw new InvalidOperationException("Collection preparation belongs to another repository.");
        }

        await _collection.Operations.WaitAsync(cancellationToken);
        return new CollectionLease(_collection.Operations);
    }

    private Task ApplyCollectionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        return _collection is null ? Task.CompletedTask
            : ApplyPreparedEntriesAsync(connection, transaction, _collection.Baseline.RepositoryIdentity, _collection.Snapshot(), null, cancellationToken);
    }

    private async Task CompleteTransactionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string repositoryIdentity, bool mutated, CancellationToken cancellationToken)
    {
        if (_collection is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var entries = mutated
            ? await ReadEntriesAsync(connection, transaction, repositoryIdentity, [], cancellationToken: cancellationToken)
            : null;
        await transaction.RollbackAsync(cancellationToken);
        if (entries is not null)
        {
            _collection.SetPrepared(entries.Where(entry => entry.Ownership == RepositoryMemoryOwnership.Intelligence).ToArray());
        }
    }

    private static async Task ApplyPreparedEntriesAsync(
        SqliteConnection connection, SqliteTransaction transaction, string identity, IReadOnlyList<RepositoryMemoryEntry> prepared, List<RepositoryMemoryEntry>? existing, CancellationToken cancellationToken)
    {
        if (prepared.Any(entry => entry.RepositoryIdentity != identity || entry.Ownership != RepositoryMemoryOwnership.Intelligence))
        {
            throw new InvalidOperationException("Prepared collection contains an unauthorized memory.");
        }

        existing ??= await ReadEntriesAsync(connection, transaction, identity, [], cancellationToken: cancellationToken);
        var existingById = existing.ToDictionary(entry => entry.Id);
        var ids = prepared.Select(entry => entry.Id).ToHashSet();
        foreach (var entry in existing.Where(entry => entry.Ownership == RepositoryMemoryOwnership.Intelligence && !ids.Contains(entry.Id)))
        {
            await DeleteEntryAsync(connection, transaction, identity, entry.Id, cancellationToken);
        }

        var replacements = new List<RepositoryMemoryEntry>();
        foreach (var entry in prepared)
        {
            var current = existingById.GetValueOrDefault(entry.Id);
            if (current is not null && current.Ownership != RepositoryMemoryOwnership.Intelligence)
            {
                throw new InvalidOperationException("Collection cannot replace ordinary memories.");
            }

            if (current is not null && current.Revision == entry.Revision)
            {
                if (entry.EmbeddingSpaceId is { } space
                    && (current.EmbeddingSpaceId != space || current.EmbeddingRevision != entry.EmbeddingRevision
                        || !current.Embedding.Span.SequenceEqual(entry.Embedding.Span)))
                {
                    await AttachStoredEmbeddingAsync(connection, transaction, identity, entry.Id, entry.Revision, entry.ContentHash, space, entry.EmbeddingDimensions, entry.Embedding, cancellationToken);
                }

                continue;
            }

            if (current is not null)
            {
                await DeleteEntryAsync(connection, transaction, identity, entry.Id, cancellationToken);
            }

            replacements.Add(entry);
        }

        foreach (var entry in replacements)
        {
            await InsertEntryAsync(connection, transaction, entry, cancellationToken: cancellationToken);
        }
    }

    private sealed class CollectionLease : IDisposable
    {
        private readonly SemaphoreSlim _gate;

        public CollectionLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            _gate.Release();
        }
    }
}
