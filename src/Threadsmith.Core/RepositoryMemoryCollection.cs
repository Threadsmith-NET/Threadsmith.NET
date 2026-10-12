namespace Threadsmith.Core;

/// <summary>Transient prepared intelligence changes; only the shared store materializes and publishes them.</summary>
public sealed class RepositoryMemoryCollection
{
    private readonly Lock _gate = new();
    private readonly HashSet<RepositoryMemoryId> _accepted = [];
    private readonly HashSet<string> _coveredPaths = new(StringComparer.Ordinal);
    private IReadOnlyList<RepositoryMemoryEntry> _entries;
    private long _generation;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryCollection"/> class from a committed snapshot.</summary>
    public RepositoryMemoryCollection(RepositoryMemoryReadSnapshot baseline, bool replace, long checkpoint)
    {
        Baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        Replace = replace;
        Checkpoint = checkpoint;
        _entries = baseline.Entries.Where(entry => entry.Ownership == RepositoryMemoryOwnership.Intelligence).ToArray();
    }

    /// <summary>Distinct cache identity for this preparation lifetime.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Committed content against which final publication is fenced.</summary>
    public RepositoryMemoryReadSnapshot Baseline { get; }

    /// <summary>Re-onboarding retains only assertions accepted during this collection.</summary>
    public bool Replace { get; }

    /// <summary>Only pending changes through this checkpoint may be acknowledged.</summary>
    public long Checkpoint { get; }

    /// <summary>Serializes short shared-store previews; never held across inference.</summary>
    public SemaphoreSlim Operations { get; } = new(1, 1);

    /// <summary>Generation of the current detached prepared contents.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Captures the replacement selection together with a completed history checkpoint.</summary>
    public IReadOnlyList<RepositoryMemoryId> AcceptedIds
    {
        get
        {
            lock (_gate)
            {
                return _accepted.ToArray();
            }
        }
    }

    /// <summary>Restores previously accepted preparation; current source coverage must be established again.</summary>
    public void Restore(RepositoryIntelligenceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.RepositoryIdentity != Baseline.RepositoryIdentity)
        {
            throw new InvalidOperationException("Onboarding checkpoint belongs to another repository.");
        }

        SetPrepared(checkpoint.Entries.Select(entry => entry with
        {
            Supports = entry.Supports.Select(support => support with { IsUnknown = true }).ToArray(),
        }).ToArray());
        lock (_gate)
        {
            _accepted.UnionWith(checkpoint.AcceptedIds.Where(id => _entries.Any(entry => entry.Id == id)));
        }
    }

    /// <summary>Returns pending paths explicitly investigated during collection.</summary>
    public IReadOnlyList<string> CoveredPaths
    {
        get
        {
            lock (_gate)
            {
                return _coveredPaths.ToArray();
            }
        }
    }

    /// <summary>Records an explicit coverage acknowledgment, published only on success.</summary>
    public void CoverChanges(IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            _coveredPaths.UnionWith(paths);
        }
    }

    /// <summary>Returns a detached preparation snapshot, optionally restricted to replacement selections.</summary>
    public IReadOnlyList<RepositoryMemoryEntry> Snapshot(bool forPublication = false)
    {
        lock (_gate)
        {
            return forPublication && Replace ? _entries.Where(entry => _accepted.Contains(entry.Id)).ToArray() : _entries;
        }
    }

    /// <summary>Captures successful shared-store preview mutations after their SQLite transaction rolls back.</summary>
    public void SetPrepared(IReadOnlyList<RepositoryMemoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Any(entry => entry.Ownership != RepositoryMemoryOwnership.Intelligence || entry.RepositoryIdentity != Baseline.RepositoryIdentity))
        {
            throw new InvalidOperationException("Collection preparation cannot change another ownership partition or repository.");
        }

        lock (_gate)
        {
            _entries = entries.ToArray();
            Interlocked.Increment(ref _generation);
        }
    }

    /// <summary>Records assertions explicitly accepted through the shared memory operation.</summary>
    public void Accept(RepositoryMemoryOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Entry is { Ownership: RepositoryMemoryOwnership.Intelligence } entry
            && result.Outcome is "added" or "updated" or "unchanged" or "duplicate")
        {
            lock (_gate)
            {
                _accepted.Add(entry.Id);
            }
        }
    }
}
