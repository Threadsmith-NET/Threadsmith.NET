namespace Threadsmith.Core;

using System.Text.Json;

/// <summary>Transient prepared intelligence changes; only the shared store materializes and publishes them.</summary>
public sealed partial class RepositoryMemoryCollection
{
    private readonly Lock _gate = new();
    private readonly HashSet<RepositoryMemoryId> _accepted = [];
    private readonly HashSet<string> _coveredPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryIntelligenceCoverage> _coverage = new(StringComparer.Ordinal);
    private IReadOnlyList<RepositoryIntelligenceCoverage> _initialSurvey = [];
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

        if (checkpoint.InitialSurvey.Count > 0)
        {
            InitializeSurvey(checkpoint.InitialSurvey);
        }

        foreach (var item in checkpoint.Coverage)
        {
            UpdateCoverage(item with
            {
                Status = item.Status is "investigated" or "outOfScope" ? "investigating" : item.Status,
                Supports = item.Supports.Select(support => support with { IsUnknown = true }).ToArray(),
            });
        }

        foreach (var proposal in checkpoint.PendingReconciliations)
        {
            // Resume rechecks every candidate revision through the ordinary search; old acknowledgments confer no authority.
            ConfirmReconciliation(proposal.Key, proposal.Text, [], [], new RepositoryMemoryWrite { Text = proposal.Text, Origin = RepositoryMemoryOrigin.Model, MemoryType = Enum.Parse<RepositoryMemoryType>(proposal.MemoryType, true), Kind = Enum.Parse<ManagedRepositoryMemoryKind>(proposal.Kind, true), Concepts = proposal.Concepts });
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

    /// <summary>Publication requires required investigation with no unfinished required questions or missing follow-ups.</summary>
    public bool HasOpenCoverage
    {
        get
        {
            lock (_gate)
            {
                return _initialSurvey.Count == 0 || _reconciliations.Count > 0 || !_coverage.Values.Any(item => item.Priority == "required") || _coverage.Values.Any(item => (item.Priority == "required" && item.Status is "uninvestigated" or "investigating")
                    || (item.Status == "investigated" && !string.IsNullOrWhiteSpace(item.Gaps))
                    || (item.Status == "investigated" && item.MemoryIds.Any(id => !_accepted.Contains(id) || !_entries.Any(entry => entry.Id == id)))
                    || item.FollowUpKeys.Any(key => !_coverage.ContainsKey(key)));
            }
        }
    }

    /// <summary>Records or reopens an investigation question without consuming memory capacity.</summary>
    public void UpdateCoverage(RepositoryIntelligenceCoverage item, IReadOnlyList<string>? resolvedProposalKeys = null)
    {
        ValidateCoverage(item);

        lock (_gate)
        {
            if (!_coverage.ContainsKey(item.Key) && _coverage.Count >= 64)
            {
                throw new InvalidOperationException("Coverage allows at most 64 investigation questions; refine existing areas instead of adding file inventories.");
            }

            var original = _initialSurvey.FirstOrDefault(area => area.Key == item.Key);
            if (original is not null && (original.Area != item.Area || original.Question != item.Question || original.Priority != item.Priority))
            {
                throw new ArgumentException("Initial survey identities and priorities are preserved. Resolve the original question or mark it outOfScope with evidence; add follow-up questions separately.", nameof(item));
            }

            if (item.Status == "investigated" && item.MemoryIds.Any(id => !_accepted.Contains(id) || !_entries.Any(entry => entry.Id == id)))
            {
                throw new ArgumentException("Investigated coverage must link currently accepted prepared assertions, including after reconciliation or eviction.", nameof(item));
            }

            var resolved = resolvedProposalKeys ?? [];
            if (resolved.Count > 8 || resolved.Any(key => !_reconciliations.ContainsKey(key))
                || (resolved.Count > 0 && item.Status != "investigated"))
            {
                throw new ArgumentException("Resolve only listed pending proposal keys on investigated coverage, linking accepted assertions or giving noMemoryReason. Nothing was changed.", nameof(resolvedProposalKeys));
            }

            foreach (var key in resolved)
            {
                _reconciliations.Remove(key);
            }

            _coverage[item.Key] = item with { Supports = item.Supports.ToArray(), MemoryIds = item.MemoryIds.ToArray(), FollowUpKeys = item.FollowUpKeys.ToArray() };
        }
    }

    /// <summary>Immutable repository-derived areas against which final coverage is checked.</summary>
    public IReadOnlyList<RepositoryIntelligenceCoverage> InitialSurvey
    {
        get
        {
            lock (_gate)
            {
                return _initialSurvey.ToArray();
            }
        }
    }

    /// <summary>Validates and records the entire initial map atomically without discarding previously discovered questions.</summary>
    public void InitializeSurvey(IReadOnlyList<RepositoryIntelligenceCoverage> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 or > 64 || items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != items.Count
            || !items.Any(item => item.Priority == "required"))
        {
            throw new ArgumentException("initializeCoverage requires 1–64 uniquely keyed areas and at least one required question. Retry the complete survey; nothing was saved.", nameof(items));
        }

        foreach (var item in items)
        {
            ValidateCoverage(item);
            if (item.Status != "uninvestigated" || item.MemoryIds.Count != 0 || item.Supports.Count != 0)
            {
                throw new ArgumentException("Initial survey areas must be uninvestigated with no assertion links or support receipts. Retry the complete survey; nothing was saved.", nameof(items));
            }
        }

        lock (_gate)
        {
            if (_initialSurvey.Count > 0)
            {
                if (_initialSurvey.Count == items.Count && items.All(item => _initialSurvey.Any(prior => prior.Key == item.Key && prior.Area == item.Area && prior.Question == item.Question && prior.Priority == item.Priority)))
                {
                    return;
                }

                throw new InvalidOperationException("The initial survey is already saved. Use updateCoverage for investigation and additional areas; its original identities cannot be replaced.");
            }

            if (_coverage.Values.Any(prior => !items.Any(item => item.Key == prior.Key && item.Area == prior.Area && item.Question == prior.Question && item.Priority == prior.Priority)))
            {
                throw new ArgumentException("The complete survey must retain every previously discovered area's key, area, question, and priority. Nothing was saved.", nameof(items));
            }

            _initialSurvey = items.Select(item => item with { Supports = [], MemoryIds = [], FollowUpKeys = item.FollowUpKeys.ToArray() }).ToArray();
            foreach (var item in _initialSurvey)
            {
                _coverage.TryAdd(item.Key, item);
            }
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

    private static void ValidateCoverage(RepositoryIntelligenceCoverage item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 80
            || string.IsNullOrWhiteSpace(item.Area) || item.Area.Length > 160
            || string.IsNullOrWhiteSpace(item.Question) || item.Question.Length > 600
            || item.NoMemoryReason.Length > 600 || item.Conclusion.Length > 1200 || item.Gaps.Length > 600 || item.Limitations.Length > 600
            || item.FollowUpKeys.Count > 8 || item.FollowUpKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 80 || key == item.Key)
            || item.Supports.Count > 8 || item.MemoryIds.Count > 8
            || item.Priority is not ("required" or "optional")
            || (item.Priority == "optional" && string.IsNullOrWhiteSpace(item.Limitations))
            || item.Status is not ("uninvestigated" or "investigating" or "investigated" or "blocked" or "outOfScope")
            || (item.Status == "investigated" && (item.Supports.Count == 0 || string.IsNullOrWhiteSpace(item.Conclusion) || !string.IsNullOrWhiteSpace(item.Gaps)))
            || (item.Status == "investigated" && item.MemoryIds.Count == 0 && string.IsNullOrWhiteSpace(item.NoMemoryReason))
            || (item.Status == "outOfScope" && (item.Supports.Count == 0 || string.IsNullOrWhiteSpace(item.Conclusion) || string.IsNullOrWhiteSpace(item.Limitations) || !string.IsNullOrWhiteSpace(item.Gaps)))
            || (item.Status == "blocked" && string.IsNullOrWhiteSpace(item.Gaps)))
        {
            throw new ArgumentException("Coverage requires bounded fields, a valid status, evidence, conclusion and no unfinished gaps for investigated items, a reason for blocked items, an explicit scope reason for optional investigation, and accepted memoryIds or noMemoryReason for investigated items.", nameof(item));
        }

        if (JsonSerializer.SerializeToUtf8Bytes(item).Length > 24 * 1024)
        {
            throw new ArgumentException("One coverage item must fit 24 KiB of serialized evidence and notes.", nameof(item));
        }
    }
}
