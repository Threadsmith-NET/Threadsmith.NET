namespace Threadsmith.Core;

/// <summary>Independent eligibility policy for automatic situational recall.</summary>
public enum SituationalConceptFilterMode
{
    /// <summary>Preserves baseline retrieval.</summary>
    Off,

    /// <summary>Records decisions without changing retrieval.</summary>
    Shadow,

    /// <summary>Excludes tagged nonmatches when current intent is known.</summary>
    Enforce,
}

/// <summary>Normalized hints observed after admission, including admitted calls that failed.</summary>
public sealed record MemoryIntentHint(
    int Round,
    ToolInvocationId InvocationId,
    bool Succeeded,
    IReadOnlyList<string> Concepts);

/// <summary>Bounded runtime evidence for one repository, run and instruction revision.</summary>
public sealed record MemoryIntentSnapshot(
    string RepositoryIdentity,
    RunId RunId,
    long Epoch,
    IReadOnlyList<MemoryIntentHint> Hints)
{
    /// <summary>Version of the exact-match policy.</summary>
    public const int PolicyVersion = 1;

    /// <summary>False when bounded evidence omitted hints; incomplete provenance cannot exclude candidates.</summary>
    public bool IsComplete { get; init; } = true;

    private Evidence _evidence = Prepare(Hints);

    /// <summary>Detached provenance; replacing it also rebuilds the derived eligibility data.</summary>
    public IReadOnlyList<MemoryIntentHint> Hints
    {
        get => _evidence.Hints;
        init => _evidence = Prepare(value);
    }

    /// <summary>Separate invocations with differing hints cannot establish a single concern.</summary>
    public bool MixedOrigins => _evidence.MixedOrigins;

    /// <summary>Bounded concepts computed once for this detached snapshot.</summary>
    public IReadOnlyList<string> Concepts => _evidence.Concepts;

    /// <summary>Retrieval identity depends on eligibility, not repeated observation identities.</summary>
    public string CacheKey => string.Join(':', PolicyVersion, RepositoryIdentity, RunId, Epoch, IsComplete && !MixedOrigins && Concepts.Count > 0 ? "tags:" + _evidence.ConceptKey : "fallback");

    /// <summary>Checks candidate tags against the prepared concept set.</summary>
    internal bool Matches(IReadOnlyList<string> concepts) => concepts.Any(_evidence.ConceptSet.Contains);

    private static Evidence Prepare(IReadOnlyList<MemoryIntentHint> hints)
    {
        ArgumentNullException.ThrowIfNull(hints);
        var detached = hints.Select(hint => hint with { Concepts = Array.AsReadOnly(hint.Concepts.ToArray()) }).ToArray();
        var concepts = detached.SelectMany(hint => hint.Concepts).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Take(MemoryConcepts.MaximumPerTurn).ToArray();
        var mixed = detached.Skip(1).Any(hint => !hint.Concepts.SequenceEqual(detached[0].Concepts, StringComparer.Ordinal));
        return new(Array.AsReadOnly(detached), Array.AsReadOnly(concepts), concepts.ToHashSet(StringComparer.Ordinal), mixed, concepts.Length == 0 ? "fallback" : string.Join(',', concepts));
    }

    private sealed record Evidence(IReadOnlyList<MemoryIntentHint> Hints, IReadOnlyList<string> Concepts,
        HashSet<string> ConceptSet, bool MixedOrigins, string ConceptKey);
}

/// <summary>Exact-match eligibility without changing discovery, scoring or reconciliation.</summary>
public static class MemoryIntentPolicy
{
    /// <summary>Returns a bounded reason code for a discovered candidate.</summary>
    public static string Decide(RepositoryMemorySearchRequest request, RepositoryMemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entry);
        if (request.SituationalConceptFilterMode == SituationalConceptFilterMode.Off
            || request.Ownership != RepositoryMemoryOwnership.Ordinary
            || entry.Ownership != RepositoryMemoryOwnership.Ordinary
            || entry.MemoryType != RepositoryMemoryType.Situational)
        {
            return "not_applicable";
        }

        var snapshot = request.IntentConcepts;
        if (snapshot is null || snapshot.RepositoryIdentity != request.RepositoryIdentity
            || snapshot.RunId != request.UserTurnId || snapshot.Epoch != request.IntentEpoch
            || !snapshot.IsComplete || snapshot.MixedOrigins || snapshot.Concepts.Count == 0)
        {
            return "no_current_concepts";
        }

        if (entry.Concepts.Count == 0)
        {
            return "untagged_fallback";
        }

        return snapshot.Matches(entry.Concepts)
            ? "exact_match" : "concept_nonmatch_policy";
    }
}
