namespace Threadsmith.DotNet;

using Threadsmith.Core;

/// <summary>Matches natural-language concepts against catalog-derived declaration-name segments.</summary>
internal static class NaturalLanguageNameSegmentMatcher
{
    private const int MaximumConcepts = 16;
    private const int MaximumSegmentsPerDeclaration = 12;
    private const int MinimumIdentifierSegmentCharacters = 2;
    private const int MinimumProseSegmentCharacters = 4;
    private const int MaximumSegmentCharacters = 32;
    private const int MinimumStrongConceptCount = 2;
    private const int MinimumRareSegmentCharacters = 5;
    private const int MinimumRareSegmentFrequency = 2;
    private const int MaximumRareSegmentFrequency = 25;

    private static readonly char[] PrimaryClauseDelimiters = [':', ',', ';', '\r', '\n'];

    private static readonly HashSet<string> ProseStopWords = new(
        [
            "a", "about", "an", "and", "are", "as", "at", "be", "been", "behavior", "behaviour", "being",
            "by", "can", "change", "changes", "check", "class", "classes", "code", "could", "declaration",
            "declarations", "describe", "detail", "details", "did", "directory", "do", "does", "done", "error",
            "errors", "everywhere", "example", "examples", "explain", "file", "files", "folder", "for", "from",
            "function", "functions", "has", "have", "help", "how", "i", "identify", "implemented", "implement",
            "implementation", "implementations", "in", "into", "is", "issue", "issues", "it", "its", "line",
            "lines", "list", "locate", "look", "me", "method", "methods", "name", "names", "need", "needs",
            "of", "on", "or", "please", "problem", "problems", "project", "question", "questions", "rename",
            "show", "test", "tests", "that", "the", "their", "this", "through", "to", "type",
            "types", "update", "used", "using", "value", "values", "warning", "warnings", "want", "wants", "was",
            "were", "what", "when", "where", "which", "who", "why", "with", "without", "work", "working",
            "works", "write", "writing",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds a generation-cached declaration segment index.</summary>
    public static NaturalLanguageNameSegmentIndex CreateIndex(
        IReadOnlyList<NaturalLanguageNameSegmentCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var segmentsByName = new Dictionary<string, DeclarationSegmentProjection>(StringComparer.OrdinalIgnoreCase);
        var segmentsByIdentity = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var segmentOwnersByIdentity = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>(
            StringComparer.Ordinal);
        var literalSegmentCountsByIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        var identitiesBySegment = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var segmentIdentityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameKey = NormalizeName(candidate.Name);
            if (!segmentsByName.TryGetValue(nameKey, out var projection))
            {
                projection = CreateDeclarationSegmentProjection(candidate.Name);
                segmentsByName.Add(nameKey, projection);
            }

            if (!segmentsByIdentity.TryAdd(candidate.Identity, projection.Variants))
            {
                continue;
            }

            segmentOwnersByIdentity.Add(candidate.Identity, projection.OwnersByVariant);
            literalSegmentCountsByIdentity.Add(candidate.Identity, projection.LiteralCount);

            foreach (var segment in projection.Variants)
            {
                if (!identitiesBySegment.TryGetValue(segment, out var identities))
                {
                    identities = [];
                    identitiesBySegment.Add(segment, identities);
                }

                identities.Add(candidate.Identity);
                segmentIdentityCounts[segment] = segmentIdentityCounts.GetValueOrDefault(segment) + 1;
            }
        }

        return new NaturalLanguageNameSegmentIndex(
            segmentsByIdentity,
            segmentOwnersByIdentity,
            literalSegmentCountsByIdentity,
            identitiesBySegment.ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<string>)item.Value,
                StringComparer.OrdinalIgnoreCase),
            segmentIdentityCounts);
    }

    /// <summary>Builds reusable per-declaration evidence for one natural-language query.</summary>
    public static NaturalLanguageNameSegmentEvidence Create(
        NaturalLanguageNameSegmentIndex index,
        IReadOnlySet<string> allowedIdentities,
        string query,
        IReadOnlySet<string> consumedRelationshipTerms,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(allowedIdentities);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var concepts = ExtractConcepts(query, consumedRelationshipTerms);
        var primaryConcepts = ExtractConcepts(ExtractPrimaryClause(query), consumedRelationshipTerms);
        if (concepts.Count == 0)
        {
            return NaturalLanguageNameSegmentEvidence.Empty;
        }

        var primaryTerms = primaryConcepts
            .Select(concept => concept.Term)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conceptCount = concepts.Count;
        var primaryConceptCount = primaryConcepts.Count;
        var candidateIdentities = concepts
            .SelectMany(concept => concept.LookupVariants)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(variant => index.IdentitiesBySegment.GetValueOrDefault(variant) ?? [])
            .Where(allowedIdentities.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var matchedConceptsByIdentity = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var hasStrongCoOccurrence = false;
        var hasStrongPrimaryCoOccurrence = false;
        foreach (var identity in candidateIdentities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = index.SegmentsByIdentity[identity];
            var matchedConcepts = GetMatchedConcepts(
                concepts,
                segments,
                index.SegmentOwnersByIdentity[identity]);
            matchedConceptsByIdentity.Add(identity, matchedConcepts);
            hasStrongCoOccurrence |= matchedConcepts.Count >= MinimumStrongConceptCount;
            hasStrongPrimaryCoOccurrence |= matchedConcepts.Count(primaryTerms.Contains) >= MinimumStrongConceptCount;
        }

        var hasUniquePrimaryLiteralMatch = primaryConcepts.Count == 1
            && candidateIdentities.Count(identity => HasLiteralMatch(
                primaryConcepts[0],
                index.SegmentOwnersByIdentity[identity])) == 1;

        var conceptsByTerm = concepts.ToDictionary(concept => concept.Term, StringComparer.OrdinalIgnoreCase);
        var matchesByIdentity = new Dictionary<string, NaturalLanguageNameSegmentMatch>(StringComparer.Ordinal);
        var hasRarePrimaryMatch = false;
        foreach (var identity in candidateIdentities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchedConcepts = matchedConceptsByIdentity[identity];
            if (matchedConcepts.Count == 0)
            {
                continue;
            }

            var declarationSegments = index.SegmentsByIdentity[identity];
            var matchedPrimaryConceptCount = matchedConcepts.Count(primaryTerms.Contains);
            var isStrong = matchedConcepts.Count >= MinimumStrongConceptCount;
            var isRareSingleTerm = !hasStrongCoOccurrence
                && matchedConcepts.Count == 1
                && IsRareSegment(
                    conceptsByTerm[matchedConcepts[0]],
                    declarationSegments,
                    index.SegmentNameCounts);
            if (primaryConcepts.Count == 1
                && matchedPrimaryConceptCount == 1
                && IsRareSegment(
                    conceptsByTerm[matchedConcepts.Single(primaryTerms.Contains)],
                    declarationSegments,
                    index.SegmentNameCounts))
            {
                hasRarePrimaryMatch = true;
            }

            matchesByIdentity.TryAdd(
                identity,
                new NaturalLanguageNameSegmentMatch(
                    matchedConcepts,
                    matchedPrimaryConceptCount,
                    index.LiteralSegmentCountsByIdentity[identity],
                    isStrong,
                    isRareSingleTerm));
        }

        var hasPrimaryEvidence = primaryConceptCount >= MinimumStrongConceptCount
            ? hasStrongPrimaryCoOccurrence
            : primaryConceptCount == 1 && (hasRarePrimaryMatch || hasUniquePrimaryLiteralMatch);
        return new NaturalLanguageNameSegmentEvidence(
            matchesByIdentity,
            conceptCount,
            conceptCount >= MinimumStrongConceptCount,
            primaryConceptCount,
            hasStrongCoOccurrence,
            hasPrimaryEvidence);
    }

    private static IReadOnlyList<NaturalLanguageNameSegmentConcept> ExtractConcepts(
        string query,
        IReadOnlySet<string> consumedRelationshipTerms)
    {
        var concepts = new List<NaturalLanguageNameSegmentConcept>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in SplitRuns(query))
        {
            var segments = SplitIdentifierSegments(run).ToArray();
            if (segments.Length == 1 || !run.Contains('_', StringComparison.Ordinal))
            {
                AddConcept(concepts, seen, run, consumedRelationshipTerms);
            }

            if (segments.Length > 1)
            {
                foreach (var segment in segments)
                {
                    AddConcept(concepts, seen, segment, consumedRelationshipTerms);
                }
            }
        }

        return concepts.Take(MaximumConcepts).ToArray();
    }

    private static void AddConcept(
        List<NaturalLanguageNameSegmentConcept> concepts,
        HashSet<string> seen,
        string value,
        IReadOnlySet<string> consumedRelationshipTerms)
    {
        var term = value.ToLowerInvariant();
        if (term.Length < MinimumProseSegmentCharacters
            || term.Length > MaximumSegmentCharacters
            || term.All(char.IsDigit)
            || ProseStopWords.Contains(term)
            || consumedRelationshipTerms.Contains(term)
            || !seen.Add(term))
        {
            return;
        }

        var weightedVariants = CodeExploreTermNormalizer.CreateWeightedVariants(term)
            .Where(variant => variant.Kind != CodeExploreTermVariantKind.Alias)
            .ToArray();
        var concept = new NaturalLanguageNameSegmentConcept(
            term,
            weightedVariants.Select(variant => variant.Value).ToArray(),
            weightedVariants
                .Where(variant => variant.Kind == CodeExploreTermVariantKind.Stem)
                .Select(variant => variant.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            term.Length);
        MergeOrAddInflectionConcept(concepts, concept);
    }

    private static DeclarationSegmentProjection CreateDeclarationSegmentProjection(string name)
    {
        var literalSegments = SplitIdentifierSegments(name)
            .Take(MaximumSegmentsPerDeclaration)
            .Select(segment => segment.ToLowerInvariant())
            .Where(segment => segment.Length >= MinimumIdentifierSegmentCharacters
                && segment.Length <= MaximumSegmentCharacters
                && !segment.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownersByVariant = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var literalSegment in literalSegments)
        {
            foreach (var variant in CodeExploreTermNormalizer.CreateWeightedVariants(literalSegment)
                .Where(variant => variant.Kind != CodeExploreTermVariantKind.Alias))
            {
                if (!ownersByVariant.TryGetValue(variant.Value, out var owners))
                {
                    owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ownersByVariant.Add(variant.Value, owners);
                }

                _ = owners.Add(literalSegment);
            }
        }

        return new DeclarationSegmentProjection(
            ownersByVariant.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ownersByVariant.ToDictionary(
                item => item.Key,
                item => (IReadOnlySet<string>)item.Value,
                StringComparer.OrdinalIgnoreCase),
            literalSegments.Length);
    }

    private static IReadOnlyList<string> GetMatchedConcepts(
        IReadOnlyList<NaturalLanguageNameSegmentConcept> concepts,
        IReadOnlySet<string> declarationSegments,
        IReadOnlyDictionary<string, IReadOnlySet<string>> ownersByVariant)
    {
        return concepts
            .Select(concept => new
            {
                concept.Term,
                MatchedOwner = concept.LookupVariants
                    .Where(declarationSegments.Contains)
                    .SelectMany(variant => ownersByVariant.TryGetValue(variant, out var owners)
                        ? (IEnumerable<string>)owners
                        : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(owner => string.Equals(
                        owner,
                        concept.Term,
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(owner => owner, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(),
            })
            .Where(match => match.MatchedOwner is not null)
            .DistinctBy(match => match.MatchedOwner, StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Term)
            .ToArray();
    }

    private static bool HasLiteralMatch(
        NaturalLanguageNameSegmentConcept concept,
        IReadOnlyDictionary<string, IReadOnlySet<string>> ownersByVariant)
    {
        return ownersByVariant.TryGetValue(concept.Term, out var owners)
            && owners.Contains(concept.Term);
    }

    private static bool IsRareSegment(
        NaturalLanguageNameSegmentConcept concept,
        IReadOnlySet<string> declarationSegments,
        IReadOnlyDictionary<string, int> segmentNameCounts)
    {
        return declarationSegments.Count >= MinimumStrongConceptCount
            && concept.MaximumTermLength >= MinimumRareSegmentCharacters
            && concept.LookupVariants
                .Where(declarationSegments.Contains)
                .Any(variant => segmentNameCounts.GetValueOrDefault(variant)
                    is >= MinimumRareSegmentFrequency
                    and <= MaximumRareSegmentFrequency);
    }

    private static void MergeOrAddInflectionConcept(
        List<NaturalLanguageNameSegmentConcept> concepts,
        NaturalLanguageNameSegmentConcept concept)
    {
        if (concept.InflectionStems.Count == 0)
        {
            concepts.Add(concept);
            return;
        }

        var matchingConcepts = concepts
            .Select((candidate, index) => new { Concept = candidate, Index = index })
            .Where(item => item.Concept.InflectionStems.Overlaps(concept.InflectionStems))
            .ToArray();
        if (matchingConcepts.Length == 0)
        {
            concepts.Add(concept);
            return;
        }

        var retainedIndex = matchingConcepts[0].Index;
        var retainedConcept = MergeConcepts(matchingConcepts[0].Concept, concept);
        foreach (var mergedConcept in matchingConcepts.Skip(1).OrderByDescending(item => item.Index))
        {
            retainedConcept = MergeConcepts(retainedConcept, mergedConcept.Concept);
            concepts.RemoveAt(mergedConcept.Index);
        }

        concepts[retainedIndex] = retainedConcept;
    }

    private static NaturalLanguageNameSegmentConcept MergeConcepts(
        NaturalLanguageNameSegmentConcept retained,
        NaturalLanguageNameSegmentConcept added)
    {
        return retained with
        {
            LookupVariants = retained.LookupVariants
                .Concat(added.LookupVariants)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            InflectionStems = retained.InflectionStems
                .Concat(added.InflectionStems)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            MaximumTermLength = Math.Max(retained.MaximumTermLength, added.MaximumTermLength),
        };
    }

    private static string ExtractPrimaryClause(string query)
    {
        var delimiterIndex = query.IndexOfAny(PrimaryClauseDelimiters);
        return delimiterIndex > 0 ? query[..delimiterIndex] : query;
    }

    private static IEnumerable<string> SplitRuns(string value)
    {
        var start = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                start = start < 0 ? index : start;
                continue;
            }

            if (start >= 0)
            {
                yield return value[start..index];
                start = -1;
            }
        }

        if (start >= 0)
        {
            yield return value[start..];
        }
    }

    private static IEnumerable<string> SplitIdentifierSegments(string value)
    {
        foreach (var part in value.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            var start = 0;
            for (var index = 1; index < part.Length; index++)
            {
                var previous = part[index - 1];
                var current = part[index];
                var next = index + 1 < part.Length ? part[index + 1] : '\0';
                var startsNewWord = (char.IsUpper(current)
                        && (char.IsLower(previous) || (char.IsUpper(previous) && char.IsLower(next))))
                    || (char.IsDigit(current) && !char.IsDigit(previous))
                    || (!char.IsDigit(current) && char.IsDigit(previous));
                if (!startsNewWord)
                {
                    continue;
                }

                yield return part[start..index];
                start = index;
            }

            if (start < part.Length)
            {
                yield return part[start..];
            }
        }
    }

    private static string NormalizeName(string value)
    {
        return value.ToLowerInvariant();
    }

    private sealed record NaturalLanguageNameSegmentConcept(
        string Term,
        IReadOnlyList<string> LookupVariants,
        IReadOnlySet<string> InflectionStems,
        int MaximumTermLength);

    private sealed record DeclarationSegmentProjection(
        IReadOnlySet<string> Variants,
        IReadOnlyDictionary<string, IReadOnlySet<string>> OwnersByVariant,
        int LiteralCount);
}

/// <summary>Minimal catalog identity and declaration name consumed by segment matching.</summary>
internal sealed record NaturalLanguageNameSegmentCandidate(string Identity, string Name);

/// <summary>Generation-cached declaration segments and reverse postings.</summary>
internal sealed record NaturalLanguageNameSegmentIndex(
    IReadOnlyDictionary<string, IReadOnlySet<string>> SegmentsByIdentity,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>> SegmentOwnersByIdentity,
    IReadOnlyDictionary<string, int> LiteralSegmentCountsByIdentity,
    IReadOnlyDictionary<string, IReadOnlyList<string>> IdentitiesBySegment,
    IReadOnlyDictionary<string, int> SegmentNameCounts);

/// <summary>Per-declaration concept coverage used by semantic ranking.</summary>
internal sealed record NaturalLanguageNameSegmentMatch(
    IReadOnlyList<string> MatchedConcepts,
    int MatchedPrimaryConceptCount,
    int DeclarationSegmentCount,
    bool IsStrong,
    bool IsRareSingleTerm);

/// <summary>Reusable query-level name-segment evidence and scope-confidence signals.</summary>
internal sealed record NaturalLanguageNameSegmentEvidence(
    IReadOnlyDictionary<string, NaturalLanguageNameSegmentMatch> MatchesByIdentity,
    int ConceptCount,
    bool CanEvaluateStrongQueryEvidence,
    int PrimaryConceptCount,
    bool HasStrongCoOccurrence,
    bool HasPrimaryEvidence)
{
    /// <summary>Gets the evidence result for a query with no usable prose concepts.</summary>
    public static NaturalLanguageNameSegmentEvidence Empty { get; } = new(
        new Dictionary<string, NaturalLanguageNameSegmentMatch>(StringComparer.Ordinal),
        0,
        false,
        0,
        false,
        false);
}
