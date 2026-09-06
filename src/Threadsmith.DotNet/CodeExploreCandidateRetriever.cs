namespace Threadsmith.DotNet;

using Microsoft.CodeAnalysis;
using Threadsmith.Core;

/// <summary>Retrieves bounded declaration candidates from a generation-owned catalog index.</summary>
internal static class CodeExploreCandidateRetriever
{
    /// <summary>Gets the shortest query term eligible for prefix expansion.</summary>
    internal const int MinimumPrefixTermLength = 3;

    /// <summary>Gets the shortest normalized name eligible for fuzzy retrieval.</summary>
    internal const int MinimumFuzzyNameLength = 5;

    private const int MaximumPrefixKeysPerTerm = 32;
    private const int MaximumFuzzyNames = 8;
    private const int MaximumFuzzyDistance = 2;

    /// <summary>Merges independent index channels into deterministic retrieval candidates.</summary>
    public static CodeExploreRetrievedCandidate[] Retrieve(
        CodeExploreCandidateIndex index,
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> allowedEntries,
        CodeExploreCandidateRetrievalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(allowedEntries);
        ArgumentNullException.ThrowIfNull(request);
        var allowed = new HashSet<CodeExploreDeclarationCatalogEntry>();
        foreach (var entry in allowedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = allowed.Add(entry);
        }

        var candidates = new Dictionary<CodeExploreDeclarationCatalogEntry, CodeExploreRetrievedCandidate>();
        AddIndexedCandidates(
            candidates,
            allowed,
            index.Identities,
            request.StableSymbolIds,
            1000,
            CodeExploreCandidateTier.Pinned,
            CodeExploreSelectionReason.Pinned | CodeExploreSelectionReason.ExactIdentifier,
            cancellationToken);
        AddIndexedCandidates(
            candidates,
            allowed,
            index.ExactNames,
            request.ExactNames,
            0,
            CodeExploreCandidateTier.Peripheral,
            CodeExploreSelectionReason.None,
            cancellationToken);
        AddIndexedCandidates(
            candidates,
            allowed,
            index.ToolFamilies,
            request.ToolCapabilityFamilies,
            0,
            CodeExploreCandidateTier.Peripheral,
            CodeExploreSelectionReason.None,
            cancellationToken);
        foreach (var entry in request.CompositionEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MergeCandidate(
                candidates,
                entry,
                520,
                CodeExploreCandidateTier.MultiTermStructural,
                CodeExploreSelectionReason.MultiTerm | CodeExploreSelectionReason.UserFocus);
        }

        AddIndexedCandidates(
            candidates,
            allowed,
            index.QualifiedNames,
            request.QualifiedNames,
            0,
            CodeExploreCandidateTier.Peripheral,
            CodeExploreSelectionReason.None,
            cancellationToken);
        AddIndexedCandidates(
            candidates,
            allowed,
            index.Terms,
            request.Terms.SelectMany(term => CodeExploreTermNormalizer
                .CreateWeightedVariants(term)
                .Select(variant => variant.Value)),
            0,
            CodeExploreCandidateTier.Peripheral,
            CodeExploreSelectionReason.None,
            cancellationToken);
        AddPrefixCandidates(candidates, allowed, index, request.Terms, cancellationToken);
        AddIndexedCandidates(
            candidates,
            allowed,
            index.Identities,
            request.NameSegmentIdentities,
            0,
            CodeExploreCandidateTier.Peripheral,
            CodeExploreSelectionReason.None,
            cancellationToken);
        foreach (var path in request.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddIndexedCandidates(
                candidates,
                allowed,
                index.Paths,
                [path.NormalizedPath],
                900,
                CodeExploreCandidateTier.ExactQualified,
                CodeExploreSelectionReason.Path,
                cancellationToken);
            foreach (var entry in path.MatchedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MergeCandidate(
                    candidates,
                    entry,
                    900,
                    CodeExploreCandidateTier.ExactQualified,
                    CodeExploreSelectionReason.Path);
            }
        }

        AddFuzzyNameCandidates(
            candidates,
            allowed,
            index,
            request.FuzzyNames,
            cancellationToken);
        return
        [
            .. candidates.Values
                .OrderBy(candidate => candidate.Entry.RelativeFilePath, PathComparer)
                .ThenBy(candidate => candidate.Entry.Range.StartLine)
                .ThenBy(candidate => candidate.Entry.Identity.Id, StringComparer.Ordinal),
        ];
    }

    private static void AddIndexedCandidates(
        Dictionary<CodeExploreDeclarationCatalogEntry, CodeExploreRetrievedCandidate> candidates,
        IReadOnlySet<CodeExploreDeclarationCatalogEntry> allowed,
        IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> postings,
        IEnumerable<string> keys,
        int score,
        CodeExploreCandidateTier tier,
        CodeExploreSelectionReason reasons,
        CancellationToken cancellationToken)
    {
        foreach (var key in keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!postings.TryGetValue(key, out var entries))
            {
                continue;
            }

            foreach (var entry in entries.Where(allowed.Contains))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MergeCandidate(candidates, entry, score, tier, reasons);
            }
        }
    }

    private static void AddPrefixCandidates(
        Dictionary<CodeExploreDeclarationCatalogEntry, CodeExploreRetrievedCandidate> candidates,
        IReadOnlySet<CodeExploreDeclarationCatalogEntry> allowed,
        CodeExploreCandidateIndex index,
        IEnumerable<string> terms,
        CancellationToken cancellationToken)
    {
        var variants = terms
            .SelectMany(CodeExploreTermNormalizer.CreateWeightedVariants)
            .Where(variant => variant.Value.Length >= MinimumPrefixTermLength)
            .GroupBy(variant => variant.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(variant => variant.Kind).First())
            .OrderBy(variant => variant.Kind)
            .ThenBy(variant => variant.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var variant in variants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchingKeys = new List<string>();
            var prefixStart = FindPrefixStart(index.NameTermVocabulary, variant.Value);
            for (var indexPosition = prefixStart;
                indexPosition < index.NameTermVocabulary.Count;
                indexPosition++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = index.NameTermVocabulary[indexPosition];
                if (!key.StartsWith(variant.Value, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                AddBoundedPrefixKey(matchingKeys, key);
            }

            AddIndexedCandidates(
                candidates,
                allowed,
                index.NameTerms,
                matchingKeys
                    .OrderBy(key => key.Length)
                    .ThenBy(key => key, StringComparer.OrdinalIgnoreCase),
                CodeExploreRelevancePolicy.CalculatePrefixMatchScore(variant.Kind),
                CodeExploreCandidateTier.Peripheral,
                CodeExploreSelectionReason.UserFocus,
                cancellationToken);
        }
    }

    private static void AddBoundedPrefixKey(List<string> matchingKeys, string key)
    {
        if (matchingKeys.Count < MaximumPrefixKeysPerTerm)
        {
            matchingKeys.Add(key);
            return;
        }

        var leastRelevantIndex = 0;
        for (var index = 1; index < matchingKeys.Count; index++)
        {
            if (ComparePrefixKeys(matchingKeys[index], matchingKeys[leastRelevantIndex]) > 0)
            {
                leastRelevantIndex = index;
            }
        }

        if (ComparePrefixKeys(key, matchingKeys[leastRelevantIndex]) < 0)
        {
            matchingKeys[leastRelevantIndex] = key;
        }
    }

    private static int ComparePrefixKeys(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0
            ? lengthComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int FindPrefixStart(IReadOnlyList<string> vocabulary, string prefix)
    {
        var lower = 0;
        var upper = vocabulary.Count;
        while (lower < upper)
        {
            var midpoint = lower + ((upper - lower) / 2);
            if (StringComparer.OrdinalIgnoreCase.Compare(vocabulary[midpoint], prefix) < 0)
            {
                lower = midpoint + 1;
            }
            else
            {
                upper = midpoint;
            }
        }

        return lower;
    }

    private static void AddFuzzyNameCandidates(
        Dictionary<CodeExploreDeclarationCatalogEntry, CodeExploreRetrievedCandidate> candidates,
        IReadOnlySet<CodeExploreDeclarationCatalogEntry> allowed,
        CodeExploreCandidateIndex index,
        IReadOnlyList<string> queryNames,
        CancellationToken cancellationToken)
    {
        foreach (var queryName in queryNames
            .Where(name => name.Length >= MinimumFuzzyNameLength)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchingNames = new List<(string Name, int Distance)>();
            foreach (var name in index.ExactNameVocabulary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Math.Abs(name.Length - queryName.Length) > MaximumFuzzyDistance)
                {
                    continue;
                }

                var distance = CalculateBoundedEditDistance(queryName, name, MaximumFuzzyDistance);
                if (distance <= MaximumFuzzyDistance)
                {
                    matchingNames.Add((name, distance));
                }
            }

            foreach (var match in matchingNames
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumFuzzyNames))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!index.ExactNames.TryGetValue(match.Name, out var entries))
                {
                    continue;
                }

                var score = Math.Max(40, 100 - (match.Distance * 25));
                foreach (var entry in entries.Where(allowed.Contains))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MergeCandidate(
                        candidates,
                        entry,
                        score,
                        CodeExploreCandidateTier.Peripheral,
                        CodeExploreSelectionReason.UserFocus);
                }
            }
        }
    }

    private static void MergeCandidate(
        Dictionary<CodeExploreDeclarationCatalogEntry, CodeExploreRetrievedCandidate> candidates,
        CodeExploreDeclarationCatalogEntry entry,
        int score,
        CodeExploreCandidateTier tier,
        CodeExploreSelectionReason reasons)
    {
        if (!candidates.TryGetValue(entry, out var existing))
        {
            candidates.Add(entry, new CodeExploreRetrievedCandidate(entry, tier, reasons, score));
            return;
        }

        candidates[entry] = existing with
        {
            Tier = (CodeExploreCandidateTier)Math.Min((int)existing.Tier, (int)tier),
            Reasons = existing.Reasons | reasons,
            Score = Math.Max(existing.Score, score),
        };
    }

    private static int CalculateBoundedEditDistance(string left, string right, int maximumDistance)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (Math.Abs(left.Length - right.Length) > maximumDistance)
        {
            return maximumDistance + 1;
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            var rowMinimum = current[0];
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost = char.ToUpperInvariant(left[leftIndex - 1])
                    == char.ToUpperInvariant(right[rightIndex - 1])
                    ? 0
                    : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitutionCost);
                rowMinimum = Math.Min(rowMinimum, current[rightIndex]);
            }

            if (rowMinimum > maximumDistance)
            {
                return maximumDistance + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

/// <summary>Normalized inputs for independent catalog retrieval channels.</summary>
internal sealed record CodeExploreCandidateRetrievalRequest(
    IReadOnlyList<string> StableSymbolIds,
    IReadOnlyList<string> ExactNames,
    IReadOnlyList<string> ToolCapabilityFamilies,
    IReadOnlyList<CodeExploreDeclarationCatalogEntry> CompositionEntries,
    IReadOnlyList<string> QualifiedNames,
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> NameSegmentIdentities,
    IReadOnlyList<CodeExplorePathRetrievalRequest> Paths,
    IReadOnlyList<string> FuzzyNames);

/// <summary>One normalized path posting plus direct suffix matches.</summary>
internal sealed record CodeExplorePathRetrievalRequest(
    string NormalizedPath,
    IReadOnlyList<CodeExploreDeclarationCatalogEntry> MatchedEntries);

/// <summary>Generation-owned compiler declaration catalog and its retrieval index.</summary>
internal sealed record CodeExploreDeclarationCatalog(
    string Key,
    long WorkspaceGeneration,
    IReadOnlyList<CodeExploreDeclarationCatalogEntry> Entries,
    CodeExploreCandidateIndex Index,
    bool IsComplete,
    IReadOnlyList<string> Omissions);

/// <summary>Deterministic postings and statistics for catalog candidate retrieval.</summary>
internal sealed record CodeExploreCandidateIndex(
    IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> Identities,
    IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> ExactNames,
    IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> NameTerms,
    IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> QualifiedNames,
    IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> Terms,
    IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> Paths,
    IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> ToolFamilies,
    IReadOnlyList<string> ExactNameVocabulary,
    IReadOnlyList<string> NameTermVocabulary,
    IReadOnlyList<string> TermVocabulary,
    IReadOnlyDictionary<string, int> TermCounts,
    IReadOnlyDictionary<string, int> NameCounts,
    NaturalLanguageNameSegmentIndex NameSegments);

/// <summary>Compiler-known declaration projected into the code-explore catalog.</summary>
internal sealed record CodeExploreDeclarationCatalogEntry(
    SemanticSymbolIdentity Identity,
    string Name,
    string MetadataName,
    string DisplayName,
    string FullyQualifiedName,
    string? ContainingType,
    string? ContainingNamespace,
    string Kind,
    Accessibility DeclaredAccessibility,
    string ProjectName,
    string TargetFramework,
    string FilePath,
    string RelativeFilePath,
    SourceRange Range,
    bool IsGenerated,
    bool IsLinked,
    bool IsTest,
    IReadOnlyList<string> NameTerms,
    IReadOnlyList<string> SignatureTerms,
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> ContextTerms,
    IReadOnlyList<string> ReferencedTypeNames,
    IReadOnlyList<string> QualifiedNames)
{
    /// <summary>Gets structural evidence for the agent-facing tool capability this declaration belongs to.</summary>
    public CodeExploreToolCapabilityEvidence ToolCapability { get; init; } =
        CodeExploreToolCapabilityEvidence.None;
}

/// <summary>Catalog declaration admitted by one or more retrieval channels.</summary>
internal sealed record CodeExploreRetrievedCandidate(
    CodeExploreDeclarationCatalogEntry Entry,
    CodeExploreCandidateTier Tier,
    CodeExploreSelectionReason Reasons,
    int Score);
