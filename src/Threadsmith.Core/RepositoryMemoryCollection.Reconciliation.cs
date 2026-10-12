namespace Threadsmith.Core;

/// <summary>An unfinished collection proposal and exact revisions explicitly reviewed as distinct.</summary>
public sealed record RepositoryMemoryReconciliation(string Key, string Text, IReadOnlyList<RepositoryMemoryInclusion> Confirmations)
{
    /// <summary>Proposed selection type retained for an unchanged retry.</summary>
    public string MemoryType { get; init; } = "situational";

    /// <summary>Proposed classification retained for an unchanged retry.</summary>
    public string Kind { get; init; } = "unspecified";

    /// <summary>Normalized search concepts retained for an unchanged retry.</summary>
    public IReadOnlyList<string> Concepts { get; init; } = [];

    /// <summary>Next character offset when tool inspection pages the proposed text.</summary>
    public int? NextTextOffset { get; init; }
}

/// <summary>Collection-owned reconciliation lifetime and revision acknowledgments.</summary>
public sealed partial class RepositoryMemoryCollection
{
    private readonly Dictionary<string, RepositoryMemoryReconciliation> _reconciliations = new(StringComparer.Ordinal);

    /// <summary>Unfinished proposals remain separate from accepted assertions.</summary>
    public IReadOnlyList<RepositoryMemoryReconciliation> PendingReconciliations
    {
        get
        {
            lock (_gate)
            {
                return _reconciliations.Values.Select(item => item with { Confirmations = item.Confirmations.ToArray() }).ToArray();
            }
        }
    }

    /// <summary>Accumulates explicit confirmations for the same normalized proposal, retaining only current revisions.</summary>
    public IReadOnlyList<RepositoryMemoryInclusion> ConfirmReconciliation(
        string key, string text, IReadOnlyList<RepositoryMemoryInclusion> confirmations, IReadOnlyList<RepositoryMemoryEntry> entries, RepositoryMemoryWrite? proposal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(confirmations);
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            if (!_reconciliations.ContainsKey(key) && _reconciliations.Count >= 64)
            {
                throw new InvalidOperationException("Resolve existing reconciliation proposals before opening more than 64 proposals.");
            }

            var current = entries.Select(entry => new RepositoryMemoryInclusion(entry.Id, entry.Revision)).ToHashSet();
            var combined = (_reconciliations.GetValueOrDefault(key)?.Confirmations ?? []).Concat(confirmations).Where(current.Contains).Distinct().ToArray();
            _reconciliations[key] = new(key, text, combined)
            {
                MemoryType = proposal?.MemoryType == RepositoryMemoryType.StandingPreference ? "standingPreference" : "situational",
                Kind = (proposal?.Kind ?? ManagedRepositoryMemoryKind.Unspecified).ToString().ToLowerInvariant(),
                Concepts = proposal?.Concepts.ToArray() ?? [],
            };
            return combined;
        }
    }

    /// <summary>A successful shared-store operation resolves proposals for the accepted text.</summary>
    public void CompleteReconciliation(string text)
    {
        lock (_gate)
        {
            foreach (var key in _reconciliations.Values.Where(item => item.Text == text).Select(item => item.Key).ToArray())
            {
                _reconciliations.Remove(key);
            }
        }
    }
}
