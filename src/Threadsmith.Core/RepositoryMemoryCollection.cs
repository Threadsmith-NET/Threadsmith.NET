namespace Threadsmith.Core;

using System.Text.Json;

/// <summary>Transient prepared intelligence changes; only the shared store materializes and publishes them.</summary>
public sealed class RepositoryMemoryCollection
{
    private readonly Lock _gate = new();
    private readonly HashSet<RepositoryMemoryId> _accepted = [];
    private readonly HashSet<string> _coveredPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryIntelligenceCoverage> _coverage = new(StringComparer.Ordinal);
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

        foreach (var item in checkpoint.Coverage)
        {
            UpdateCoverage(item with
            {
                Status = item.Status == "investigated" ? "investigating" : item.Status,
                Supports = item.Supports.Select(support => support with { IsUnknown = true }).ToArray(),
            });
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

    /// <summary>Captures bounded investigation state for the existing onboarding checkpoint.</summary>
    public IReadOnlyList<RepositoryIntelligenceCoverage> Coverage
    {
        get
        {
            lock (_gate)
            {
                return _coverage.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>Publication requires a nonempty map with no unfinished questions or missing follow-ups.</summary>
    public bool HasOpenCoverage
    {
        get
        {
            lock (_gate)
            {
                return _coverage.Count == 0 || _coverage.Values.Any(item => item.Status is "uninvestigated" or "investigating"
                    || (item.Status == "investigated" && !string.IsNullOrWhiteSpace(item.Gaps))
                    || item.FollowUpKeys.Any(key => !_coverage.ContainsKey(key)));
            }
        }
    }

    /// <summary>Records or reopens an investigation question without consuming memory capacity.</summary>
    public void UpdateCoverage(RepositoryIntelligenceCoverage item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 80
            || string.IsNullOrWhiteSpace(item.Area) || item.Area.Length > 160
            || string.IsNullOrWhiteSpace(item.Question) || item.Question.Length > 600
            || item.Conclusion.Length > 1200 || item.Gaps.Length > 600 || item.Limitations.Length > 600
            || item.FollowUpKeys.Count > 8 || item.FollowUpKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 80 || key == item.Key)
            || item.Supports.Count > 8 || item.MemoryIds.Count > 8
            || item.Status is not ("uninvestigated" or "investigating" or "investigated" or "blocked")
            || (item.Status == "investigated" && (item.Supports.Count == 0 || string.IsNullOrWhiteSpace(item.Conclusion) || !string.IsNullOrWhiteSpace(item.Gaps)))
            || (item.Status == "blocked" && string.IsNullOrWhiteSpace(item.Gaps)))
        {
            throw new ArgumentException("Coverage requires bounded fields, a valid status, evidence, conclusion and no unfinished gaps for investigated items, and a reason for blocked items.", nameof(item));
        }

        if (JsonSerializer.SerializeToUtf8Bytes(item).Length > 24 * 1024)
        {
            throw new ArgumentException("One coverage item must fit 24 KiB of serialized evidence and notes.", nameof(item));
        }

        lock (_gate)
        {
            if (!_coverage.ContainsKey(item.Key) && _coverage.Count >= 64)
            {
                throw new InvalidOperationException("Coverage allows at most 64 investigation questions; refine existing areas instead of adding file inventories.");
            }

            _coverage[item.Key] = item with { Supports = item.Supports.ToArray(), MemoryIds = item.MemoryIds.ToArray(), FollowUpKeys = item.FollowUpKeys.ToArray() };
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
