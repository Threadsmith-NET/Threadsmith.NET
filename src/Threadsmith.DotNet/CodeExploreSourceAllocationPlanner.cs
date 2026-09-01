namespace Threadsmith.DotNet;

/// <summary>Ranks source candidates and reserves output before any source section is rendered.</summary>
internal static class CodeExploreSourceAllocationPlanner
{
    /// <summary>Minimum source reservation that can carry useful declaration evidence.</summary>
    internal const int MinimumAdmittedSourceCharacters = 700;

    private const double SourceCliffRatio = 0.15;
    private const double MaximumSourceCliffWeight = 10;
    private const double MaximumSingleCandidateBudgetRatio = 0.70;
    private const int EstimatedFileOutputOverheadCharacters = 200;

    /// <summary>Creates deterministic source reservations for one projection phase.</summary>
    public static CodeExploreSourceAllocationPlan Create(
        IReadOnlyList<CodeExploreSourceAllocationCandidate> candidates,
        int totalCharacters,
        int maximumCandidates,
        int maximumPerCandidateCharacters)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || totalCharacters <= 0 || maximumCandidates <= 0)
        {
            return CodeExploreSourceAllocationPlan.Empty;
        }

        var strongestRawWeight = candidates.Max(EffectiveWeight);
        var ranked = candidates
            .OrderByDescending(candidate => candidate.IsPinned)
            .ThenByDescending(candidate => candidate.IsSpine)
            .ThenByDescending(candidate => AllocationWeight(candidate, strongestRawWeight))
            .ThenBy(candidate => candidate.AllocationRank)
            .ThenBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .ToArray();
        var strongestWeight = ranked.Max(candidate => AllocationWeight(candidate, strongestRawWeight));
        var cliff = Math.Min(strongestWeight * SourceCliffRatio, MaximumSourceCliffWeight);
        var admitted = ranked
            .Where((candidate, index) => index == 0
                || candidate.IsPinned
                || candidate.IsSpine
                || AllocationWeight(candidate, strongestRawWeight) >= cliff)
            .Take(maximumCandidates)
            .ToList();
        if (admitted.Count == 0)
        {
            admitted = [ranked[0]];
        }

        while (admitted.Count > 1
            && !CanAffordUsefulReservations(totalCharacters, admitted.Count))
        {
            var removableIndex = admitted
                .Select((candidate, index) => new { Candidate = candidate, Index = index })
                .Where(item => !item.Candidate.IsPinned && !item.Candidate.IsSpine)
                .OrderBy(item => AllocationWeight(item.Candidate, strongestRawWeight))
                .ThenByDescending(item => item.Candidate.AllocationRank)
                .Select(item => item.Index)
                .FirstOrDefault(-1);
            if (removableIndex < 0)
            {
                break;
            }

            admitted.RemoveAt(removableIndex);
        }

        var canFundNormalFloor = CanAffordUsefulReservations(totalCharacters, admitted.Count);
        var sourcePool = canFundNormalFloor
            ? Math.Max(1, totalCharacters - (EstimatedFileOutputOverheadCharacters * admitted.Count))
            : totalCharacters;
        var fairShareCeiling = admitted.Count == 1
            ? sourcePool
            : Math.Max(
                MinimumAdmittedSourceCharacters,
                (int)Math.Floor(sourcePool * MaximumSingleCandidateBudgetRatio));
        var maximumShare = Math.Min(maximumPerCandidateCharacters, fairShareCeiling);
        var usefulFloor = Math.Max(
            1,
            Math.Min(MinimumAdmittedSourceCharacters, sourcePool / admitted.Count));
        var reservations = admitted.ToDictionary(
            candidate => candidate.StableKey,
            _ => usefulFloor,
            StringComparer.Ordinal);
        var remaining = Math.Max(0, sourcePool - (usefulFloor * admitted.Count));
        var active = admitted.ToList();
        while (remaining > 0 && active.Count > 0)
        {
            var totalWeight = active.Sum(candidate => Math.Max(
                1,
                AllocationWeight(candidate, strongestRawWeight)));
            var distributed = 0;
            foreach (var candidate in active.ToArray())
            {
                var current = reservations[candidate.StableKey];
                var capacity = maximumShare - current;
                if (capacity <= 0)
                {
                    _ = active.Remove(candidate);
                    continue;
                }

                var candidateWeight = Math.Max(1, AllocationWeight(candidate, strongestRawWeight));
                var proportional = Math.Max(
                    1,
                    (int)Math.Floor(remaining * (candidateWeight / totalWeight)));
                var addition = Math.Min(capacity, Math.Min(proportional, remaining - distributed));
                if (addition <= 0)
                {
                    continue;
                }

                reservations[candidate.StableKey] = current + addition;
                distributed += addition;
                if (reservations[candidate.StableKey] >= maximumShare)
                {
                    _ = active.Remove(candidate);
                }

                if (distributed >= remaining)
                {
                    break;
                }
            }

            if (distributed == 0)
            {
                break;
            }

            remaining -= distributed;
        }

        var admittedKeys = admitted
            .Select(candidate => candidate.StableKey)
            .ToHashSet(StringComparer.Ordinal);
        return new CodeExploreSourceAllocationPlan(
            reservations,
            candidates
                .Where(candidate => !admittedKeys.Contains(candidate.StableKey))
                .Select(candidate => candidate.StableKey)
                .ToHashSet(StringComparer.Ordinal));
    }

    private static bool CanAffordUsefulReservations(int totalCharacters, int candidateCount)
    {
        var perCandidateCost = MinimumAdmittedSourceCharacters + EstimatedFileOutputOverheadCharacters;
        return totalCharacters >= perCandidateCost * candidateCount;
    }

    private static double EffectiveWeight(CodeExploreSourceAllocationCandidate candidate)
    {
        return Math.Max(0, candidate.Weight) * Math.Clamp(candidate.SourceWorth, 0, 1);
    }

    private static double AllocationWeight(
        CodeExploreSourceAllocationCandidate candidate,
        double strongestRawWeight)
    {
        var rawWeight = EffectiveWeight(candidate);
        return candidate.IsPinned
            ? Math.Max(Math.Max(rawWeight, strongestRawWeight), 1)
            : rawWeight;
    }
}

/// <summary>Minimal source-candidate evidence consumed by the allocation planner.</summary>
internal sealed record CodeExploreSourceAllocationCandidate(
    string StableKey,
    double Weight,
    double SourceWorth,
    bool IsPinned,
    bool IsSpine,
    int AllocationRank);

/// <summary>Immutable reservations and relevance-cliff outcomes for one source projection phase.</summary>
internal sealed record CodeExploreSourceAllocationPlan(
    IReadOnlyDictionary<string, int> Reservations,
    IReadOnlySet<string> CliffedKeys)
{
    /// <summary>Gets the empty allocation plan.</summary>
    public static CodeExploreSourceAllocationPlan Empty { get; } = new(
        new Dictionary<string, int>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}
