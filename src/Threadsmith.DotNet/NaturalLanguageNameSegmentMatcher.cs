namespace Threadsmith.DotNet;

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
            "show", "test", "tests", "that", "the", "their", "this", "through", "to", "tool", "tools", "type",
            "types", "update", "used", "using", "value", "values", "warning", "warnings", "want", "wants", "was",
            "were", "what", "when", "where", "which", "who", "why", "with", "without", "work", "working",
            "works", "write", "writing",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds reusable per-declaration evidence for one natural-language query.</summary>
    public static NaturalLanguageNameSegmentEvidence Create(
        IReadOnlyList<NaturalLanguageNameSegmentCandidate> candidates,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var concepts = ExtractConcepts(query);
        var primaryConcepts = ExtractConcepts(ExtractPrimaryClause(query));
        if (concepts.Count == 0)
        {
            return NaturalLanguageNameSegmentEvidence.Empty;
        }

        var primaryTerms = primaryConcepts
            .Select(concept => concept.Term)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var segmentsByName = CreateSegmentsByName(candidates, cancellationToken);
        var segmentNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matchedConceptsByName = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var hasStrongCoOccurrence = false;
        var hasStrongPrimaryCoOccurrence = false;
        foreach ((var name, var segments) in segmentsByName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var segment in segments)
            {
                segmentNameCounts[segment] = segmentNameCounts.GetValueOrDefault(segment) + 1;
            }

            var matchedConcepts = GetMatchedConcepts(concepts, segments);
            matchedConceptsByName.Add(name, matchedConcepts);
            hasStrongCoOccurrence |= matchedConcepts.Count >= MinimumStrongConceptCount;
            hasStrongPrimaryCoOccurrence |= matchedConcepts.Count(primaryTerms.Contains) >= MinimumStrongConceptCount;
        }

        var conceptsByTerm = concepts.ToDictionary(concept => concept.Term, StringComparer.OrdinalIgnoreCase);
        var matchesByIdentity = new Dictionary<string, NaturalLanguageNameSegmentMatch>(StringComparer.Ordinal);
        var hasRarePrimaryMatch = false;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameKey = NormalizeName(candidate.Name);
            var matchedConcepts = matchedConceptsByName[nameKey];
            if (matchedConcepts.Count == 0)
            {
                continue;
            }

            var declarationSegments = segmentsByName[nameKey];
            var matchedPrimaryConceptCount = matchedConcepts.Count(primaryTerms.Contains);
            var isStrong = matchedConcepts.Count >= MinimumStrongConceptCount;
            var isRareSingleTerm = !hasStrongCoOccurrence
                && matchedConcepts.Count == 1
                && IsRareSegment(
                    conceptsByTerm[matchedConcepts[0]],
                    declarationSegments,
                    segmentNameCounts);
            if (primaryConcepts.Count == 1
                && matchedPrimaryConceptCount == 1
                && IsRareSegment(
                    conceptsByTerm[matchedConcepts.Single(primaryTerms.Contains)],
                    declarationSegments,
                    segmentNameCounts))
            {
                hasRarePrimaryMatch = true;
            }

            matchesByIdentity.TryAdd(
                candidate.Identity,
                new NaturalLanguageNameSegmentMatch(
                    matchedConcepts,
                    matchedPrimaryConceptCount,
                    declarationSegments.Count,
                    isStrong,
                    isRareSingleTerm));
        }

        var hasPrimaryEvidence = primaryConcepts.Count >= MinimumStrongConceptCount
            ? hasStrongPrimaryCoOccurrence
            : primaryConcepts.Count == 1 && hasRarePrimaryMatch;
        return new NaturalLanguageNameSegmentEvidence(
            matchesByIdentity,
            concepts.Count >= MinimumStrongConceptCount,
            primaryConcepts.Count,
            hasStrongCoOccurrence,
            hasPrimaryEvidence);
    }

    private static Dictionary<string, IReadOnlySet<string>> CreateSegmentsByName(
        IReadOnlyList<NaturalLanguageNameSegmentCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var segmentsByName = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameKey = NormalizeName(candidate.Name);
            if (!segmentsByName.ContainsKey(nameKey))
            {
                segmentsByName.Add(nameKey, CreateDeclarationSegments(candidate.Name));
            }
        }

        return segmentsByName;
    }

    private static IReadOnlyList<NaturalLanguageNameSegmentConcept> ExtractConcepts(string query)
    {
        var concepts = new List<NaturalLanguageNameSegmentConcept>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in SplitRuns(query))
        {
            var segments = SplitIdentifierSegments(run).ToArray();
            if (segments.Length == 1 || !run.Contains('_', StringComparison.Ordinal))
            {
                AddConcept(concepts, seen, run);
            }

            if (segments.Length > 1)
            {
                foreach (var segment in segments)
                {
                    AddConcept(concepts, seen, segment);
                }
            }

            if (concepts.Count >= MaximumConcepts)
            {
                break;
            }
        }

        return concepts;
    }

    private static void AddConcept(
        List<NaturalLanguageNameSegmentConcept> concepts,
        HashSet<string> seen,
        string value)
    {
        var term = value.ToLowerInvariant();
        if (concepts.Count >= MaximumConcepts
            || term.Length < MinimumProseSegmentCharacters
            || term.Length > MaximumSegmentCharacters
            || term.All(char.IsDigit)
            || ProseStopWords.Contains(term)
            || !seen.Add(term))
        {
            return;
        }

        concepts.Add(new NaturalLanguageNameSegmentConcept(term, CreateLookupVariants(term)));
    }

    private static IReadOnlySet<string> CreateDeclarationSegments(string name)
    {
        return SplitIdentifierSegments(name)
            .Select(segment => segment.ToLowerInvariant())
            .Where(segment => segment.Length >= MinimumIdentifierSegmentCharacters
                && segment.Length <= MaximumSegmentCharacters
                && !segment.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSegmentsPerDeclaration)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetMatchedConcepts(
        IReadOnlyList<NaturalLanguageNameSegmentConcept> concepts,
        IReadOnlySet<string> declarationSegments)
    {
        return concepts
            .Where(concept => concept.LookupVariants.Any(declarationSegments.Contains))
            .Select(concept => concept.Term)
            .ToArray();
    }

    private static bool IsRareSegment(
        NaturalLanguageNameSegmentConcept concept,
        IReadOnlySet<string> declarationSegments,
        IReadOnlyDictionary<string, int> segmentNameCounts)
    {
        return declarationSegments.Count >= MinimumStrongConceptCount
            && concept.Term.Length >= MinimumRareSegmentCharacters
            && concept.LookupVariants
                .Where(declarationSegments.Contains)
                .Any(variant => segmentNameCounts.GetValueOrDefault(variant)
                    is >= MinimumRareSegmentFrequency
                    and <= MaximumRareSegmentFrequency);
    }

    private static IReadOnlyList<string> CreateLookupVariants(string term)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { term };
        if (term.EndsWith("xes", StringComparison.Ordinal)
            || term.EndsWith("shes", StringComparison.Ordinal)
            || term.EndsWith("sses", StringComparison.Ordinal)
            || term.EndsWith("zzes", StringComparison.Ordinal))
        {
            AddSuffixStrippedVariant(variants, term, 2);
        }
        else if (term.EndsWith("ches", StringComparison.Ordinal)
            || term.EndsWith("ses", StringComparison.Ordinal)
            || term.EndsWith("zes", StringComparison.Ordinal)
            || term.EndsWith("oes", StringComparison.Ordinal))
        {
            AddSuffixStrippedVariant(variants, term, 2);
            AddSuffixStrippedVariant(variants, term, 1);
        }
        else if (term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal))
        {
            AddSuffixStrippedVariant(variants, term, 1);
        }

        return variants.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddSuffixStrippedVariant(HashSet<string> variants, string term, int characterCount)
    {
        if (term.Length >= MinimumProseSegmentCharacters + characterCount)
        {
            variants.Add(term[..^characterCount]);
        }
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
        IReadOnlyList<string> LookupVariants);
}

/// <summary>Minimal catalog identity and declaration name consumed by segment matching.</summary>
internal sealed record NaturalLanguageNameSegmentCandidate(string Identity, string Name);

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
    bool CanEvaluateStrongQueryEvidence,
    int PrimaryConceptCount,
    bool HasStrongCoOccurrence,
    bool HasPrimaryEvidence)
{
    /// <summary>Gets the evidence result for a query with no usable prose concepts.</summary>
    public static NaturalLanguageNameSegmentEvidence Empty { get; } = new(
        new Dictionary<string, NaturalLanguageNameSegmentMatch>(StringComparer.Ordinal),
        false,
        0,
        false,
        false);
}
