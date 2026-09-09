namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Rebinds explicit memories to the active repository database with an identity fence per operation.</summary>
public sealed class RepositoryBoundMemoryStore : IManagedRepositoryMemoryStore, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly SqliteEventStore? _retentionOwner;
    private readonly SemaphoreSlim _bindGate = new(1, 1);
    private readonly Binding _initialBinding;
    private Binding _binding;

    /// <summary>Initializes a new instance of the <see cref="RepositoryBoundMemoryStore"/> class.</summary>
    /// <remarks>The initial database is initialized by the composition root before constructing the router.</remarks>
    public RepositoryBoundMemoryStore(string initialConnectionString, string initialRepositoryRoot, TimeProvider? timeProvider = null, SqliteEventStore? retentionOwner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialRepositoryRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retentionOwner = retentionOwner;
        _initialBinding = new Binding(
            RepositoryIdentity.Create(initialRepositoryRoot),
            Path.GetFullPath(initialRepositoryRoot),
            Path.GetFullPath(new SqliteConnectionStringBuilder(initialConnectionString).DataSource),
            new SqliteManagedRepositoryMemoryStore(initialConnectionString, _timeProvider),
            null);
        _binding = _initialBinding;
    }

    /// <summary>Most recent verified backup location for the currently bound repository, when a migration occurred.</summary>
    public string? MigrationBackupPath => Volatile.Read(ref _binding).BackupPath;

    /// <summary>Initializes the host-selected repository database, then atomically publishes the new binding.</summary>
    public Task BindRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        => BindRepositoryAsync(repositoryRoot, 20, cancellationToken);

    /// <summary>Uses the repository effective capacity during any pending manual-memory migration.</summary>
    public async Task BindRepositoryAsync(string repositoryRoot, int maximumMemoryCount, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMemoryCount);
        var identity = RepositoryIdentity.Create(repositoryRoot);
        await _bindGate.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref _binding);
            var existing = current.RepositoryIdentity == identity ? current : _initialBinding.RepositoryIdentity == identity ? _initialBinding : null;
            if (existing is not null)
            {
                await existing.Store.EnforceCapacityAsync(identity, new RepositoryMemoryOptions { MaxNumberOfRepoMemories = maximumMemoryCount }, cancellationToken);
                Volatile.Write(ref _binding, existing);
                return;
            }

            var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".threadsmith");
            var path = Path.Combine(directory, "threadsmith.db");
            if ((Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidOperationException("Repository memory database must not be redirected through a linked path.");
            }

            Directory.CreateDirectory(directory);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
            await new SqliteEventStore(connectionString).InitializeAsync(cancellationToken);
            var migrations = new MigrationRunner(connectionString, DefaultMigrations.ForRepositoryMemoryCapacity(maximumMemoryCount));
            await migrations.RunAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _binding, new Binding(
                identity,
                Path.GetFullPath(repositoryRoot),
                path,
                new SqliteManagedRepositoryMemoryStore(connectionString, _timeProvider),
                migrations.LastBackupPath));
        }
        finally
        {
            _bindGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, IReadOnlyList<string> lexicalTerms, CancellationToken cancellationToken = default)
        => GetStore(repositoryIdentity).GetSnapshotAsync(repositoryIdentity, lexicalTerms, cancellationToken);

    /// <inheritdoc />
    public Task<RepositoryMemoryWriteResult> AddAsync(
        string repositoryIdentity,
        RepositoryMemoryWrite write,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default)
        => GetStore(repositoryIdentity).AddAsync(repositoryIdentity, write, model, embedding, options, cancellationToken);

    /// <inheritdoc />
    public Task<RepositoryMemoryWriteResult> UpdateAsync(
        string repositoryIdentity,
        RepositoryMemoryId id,
        long expectedRevision,
        RepositoryMemoryWrite write,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default)
        => GetStore(repositoryIdentity).UpdateAsync(repositoryIdentity, id, expectedRevision, write, model, embedding, options, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string repositoryIdentity, RepositoryMemoryId id, CancellationToken cancellationToken = default)
        => GetStore(repositoryIdentity).RemoveAsync(repositoryIdentity, id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
        => GetStore(repositoryIdentity).EnforceCapacityAsync(repositoryIdentity, options, cancellationToken);

    /// <inheritdoc />
    public Task<bool> AttachEmbeddingAsync(
        string repositoryIdentity,
        RepositoryMemoryId id,
        long expectedRevision,
        string expectedContentHash,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        CancellationToken cancellationToken = default)
        => GetStore(repositoryIdentity).AttachEmbeddingAsync(repositoryIdentity, id, expectedRevision, expectedContentHash, model, embedding, cancellationToken);

    /// <inheritdoc />
    public async Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inclusions);
        var binding = GetBinding(repositoryIdentity);
        if (_retentionOwner is not null && inclusions.Count > 0)
        {
            await _retentionOwner.RegisterMemoryInclusionStoreAsync(runId, binding.RepositoryRoot, binding.DatabasePath, cancellationToken);
        }

        await binding.Store.RecordInclusionsAsync(repositoryIdentity, runId, inclusions, cancellationToken);
    }

    /// <inheritdoc />
    public Task PruneInclusionsAsync(string repositoryIdentity, RunId runId, CancellationToken cancellationToken = default)
        => GetStore(repositoryIdentity).PruneInclusionsAsync(repositoryIdentity, runId, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _bindGate.Dispose();

    private SqliteManagedRepositoryMemoryStore GetStore(string repositoryIdentity) => GetBinding(repositoryIdentity).Store;

    private Binding GetBinding(string repositoryIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        var binding = Volatile.Read(ref _binding);
        if (binding.RepositoryIdentity != repositoryIdentity)
        {
            throw new InvalidOperationException("Memory operation belongs to a repository that is no longer bound.");
        }

        return binding;
    }

    private sealed record Binding(string RepositoryIdentity, string RepositoryRoot, string DatabasePath, SqliteManagedRepositoryMemoryStore Store, string? BackupPath);
}
