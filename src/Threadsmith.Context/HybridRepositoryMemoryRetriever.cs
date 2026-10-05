namespace Threadsmith.Context;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Small exact hybrid search with snapshot-consistent candidates and bounded turn-query caches.</summary>
public sealed partial class HybridRepositoryMemoryRetriever : IHybridRepositoryMemoryRetriever, IRepositoryMemorySearch, IDisposable
{
    private const double FusionConstant = 60;
    private static readonly HashSet<string> StopWords = new(
        ["a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "how", "i", "in", "is", "it", "of", "on", "or", "please", "that", "the", "this", "to", "was", "we", "what", "when", "where", "which", "with", "you"],
        StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITextEmbeddingGenerator _generator;
    private readonly ITextCrossEncoder? _crossEncoder;
    private readonly Dictionary<string, TextEmbeddingResult> _queryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryMemoryRetrievalResult> _rankingCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _conceptCache = new(StringComparer.Ordinal);
    private readonly IManagedRepositoryMemoryStore _store;

    /// <summary>Initializes a new instance of the <see cref="HybridRepositoryMemoryRetriever"/> class.</summary>
    public HybridRepositoryMemoryRetriever(
        IManagedRepositoryMemoryStore store,
        ITextEmbeddingGenerator generator,
        ITextCrossEncoder? crossEncoder = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(generator);
        _store = store;
        _generator = generator;
        _crossEncoder = crossEncoder;
    }

    /// <inheritdoc />
    public Task<RepositoryMemoryRetrievalResult> RetrieveAsync(
        RepositoryMemoryRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Validate();
        var query = BuildQuery(request, out var bounded);
        return SearchAsync(
            new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = request.RepositoryIdentity,
            Query = query,
            QueryBounded = bounded,
            Options = RepositoryMemorySearchOptions.ForRecall(request.Options),
            UserTurnId = request.UserTurnId,
            Concepts = request.Concepts,
            RetainedMemories = request.RetainedMemories,
        },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryRetrievalResult> SearchAsync(
        RepositoryMemorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryIdentity);
        request.Options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var concepts = request.Options.ConceptRecallEnabled ? request.Concepts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MemoryConcepts.MaximumPerTurn).ToArray() : [];
        var semanticMinimum = request.Options.SemanticMinimum;
        var query = request.Query;
        var bounded = request.QueryBounded;
        IReadOnlyList<RepositoryMemoryEntry> standingPreferences = [];
        long? memorySetRevision = null;
        var timer = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var terms = Tokenize(query).Where(term => !StopWords.Contains(term)).Take(request.Options.MaximumQueryTerms).ToArray();
            var snapshot = await _store.GetSnapshotAsync(request.RepositoryIdentity, terms, request.Options.Lexical, cancellationToken);
            memorySetRevision = snapshot.Revision;
            standingPreferences = SelectStandingPreferences(snapshot);
            snapshot = request.IncludeStandingPreferences ? snapshot : SelectSituationalSnapshot(snapshot);
            if (request.Options.MaximumResults == 0 || string.IsNullOrWhiteSpace(query) || snapshot.Entries.Count == 0)
            {
                var earlyDiagnostics = request.Options.MaximumResults == 0
                    ? [.. snapshot.Warnings, "Memory selection is disabled by the search result limit."]
                    : string.IsNullOrWhiteSpace(query)
                        ? [.. snapshot.Warnings, "The current situational-memory query is empty."]
                        : snapshot.Warnings;
                return new RepositoryMemoryRetrievalResult([], earlyDiagnostics, snapshot.Revision)
                {
                    StandingPreferences = standingPreferences,
                    SearchDetails = new RepositoryMemorySearchDetails
                    {
                        Lexical = RepositoryMemorySearchBranchStatus.Completed,
                        FuzzyLexical = snapshot.FuzzyLexicalStatus,
                        LexicalExpansions = snapshot.LexicalExpansions,
                        Semantic = RepositoryMemorySearchBranchStatus.NotRequired,
                        Reranker = request.Options.RerankerEnabled ? RepositoryMemorySearchBranchStatus.NotRequired : RepositoryMemorySearchBranchStatus.Disabled,
                        Concepts = request.Options.ConceptRecallEnabled ? RepositoryMemorySearchBranchStatus.NotRequired : RepositoryMemorySearchBranchStatus.Disabled,
                    },
                };
            }

            var model = _generator.Model;
            var queryKey = model.SpaceId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)));

            // Retrieval settings affect selections, while the embedding cache remains model/query-only.
            var minimumKey = string.Join(':', semanticMinimum.ToString("R", CultureInfo.InvariantCulture), request.Options.MaximumQueryTerms, request.Options.MaximumDiagnostics);
            var diagnostics = new List<string>(snapshot.Warnings.Take(request.Options.MaximumDiagnostics));
            var crossEncoderModel = request.Options.RerankerEnabled ? GetCrossEncoderModel(diagnostics) : null;
            var rerankerKey = request.Options.RerankerEnabled
                ? string.Join(':', crossEncoderModel?.ModelId ?? "unavailable", crossEncoderModel?.MaxInputTokens, crossEncoderModel?.MaxBatchSize, request.Options.RerankerCandidateLimit)
                : "disabled";
            var rankKey = string.Join(':', request.RepositoryIdentity, snapshot.Revision, request.IncludeStandingPreferences, request.QueryBounded, request.Options.ConceptRecallEnabled, string.Join(',', concepts), string.Join(',', request.RetainedMemories), request.Options.ConceptCandidateLimit, request.Options.ConceptFuzzyEnabled, request.Options.ConceptFuzzyMaximumDistance, queryKey, request.Options, minimumKey, rerankerKey);
            var degradedKey = request.UserTurnId is { } turn ? rankKey + ":degraded:" + turn.Value.ToString("D") : null;
            if (_rankingCache.TryGetValue(rankKey, out var cached)
                || (degradedKey is not null && _rankingCache.TryGetValue(degradedKey, out cached)))
            {
                return cached with { QueryEmbeddingCacheHit = _queryCache.ContainsKey(queryKey), RankingCacheHit = true };
            }

            var queryCacheHit = _queryCache.TryGetValue(queryKey, out var embedding);
            if (!queryCacheHit && request.PreparedEmbedding is { } prepared
                && request.PreparedEmbeddingSpaceId == model.SpaceId)
            {
                if (prepared.WasTruncated || prepared.InputTokenCount > model.MaxInputTokens
                    || !EmbeddingValidation.IsValid(model, prepared))
                {
                    throw new ArgumentException("Prepared memory query embedding must represent the complete query.", nameof(request));
                }

                embedding = prepared;
                AddBounded(_queryCache, queryKey, embedding, request.Options.MaximumCacheEntries);
            }

            if (embedding is null)
            {
                try
                {
                    embedding = await _generator.GenerateAsync(query, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!EmbeddingValidation.IsValid(model, embedding))
                    {
                        throw new InvalidOperationException("The query embedding is invalid.");
                    }

                    AddBounded(_queryCache, queryKey, embedding, request.Options.MaximumCacheEntries);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    embedding = null;
                    diagnostics.Add($"Semantic retrieval is unavailable ({exception.GetType().Name}); using qualified lexical matches only.");
                }
            }

            if (embedding is not null)
            {
                var rebuilt = await RebuildEmbeddingsAsync(snapshot, model, diagnostics, cancellationToken);
                if (rebuilt)
                {
                    // Both rankings must observe precisely the same content and vector generation after rebuild.
                    snapshot = await _store.GetSnapshotAsync(request.RepositoryIdentity, terms, request.Options.Lexical, cancellationToken);
                    memorySetRevision = snapshot.Revision;
                    standingPreferences = SelectStandingPreferences(snapshot);
                    snapshot = request.IncludeStandingPreferences ? snapshot : SelectSituationalSnapshot(snapshot);
                    rankKey = string.Join(':', request.RepositoryIdentity, snapshot.Revision, request.IncludeStandingPreferences, request.QueryBounded, request.Options.ConceptRecallEnabled, string.Join(',', concepts), string.Join(',', request.RetainedMemories), request.Options.ConceptCandidateLimit, request.Options.ConceptFuzzyEnabled, request.Options.ConceptFuzzyMaximumDistance, queryKey, request.Options, minimumKey, rerankerKey);
                }
            }

            var lexical = snapshot.LexicalMatches.OrderBy(match => match.IsFuzzy).ThenBy(match => match.Bm25).ThenBy(match => match.Id.Value)
                .Select((match, index) => (match.Id, Rank: index + 1)).ToDictionary(match => match.Id, match => match.Rank);
            var semantic = embedding is null
                ? []
                : snapshot.Entries.Where(entry => IsCompatible(entry, model))
                    .Select(entry => (entry.Id, Similarity: Cosine(embedding.Vector.Span, entry.Embedding.Span)))
                    .Where(match => match.Similarity > semanticMinimum)
                    .OrderByDescending(match => match.Similarity).ThenBy(match => match.Id.Value)
                    .Select((match, index) => (match.Id, match.Similarity, Rank: index + 1))
                    .ToDictionary(match => match.Id, match => (match.Similarity, match.Rank));
            var resolvedConcepts = new HashSet<string>(StringComparer.Ordinal);
            var vocabulary = snapshot.Entries.SelectMany(entry => entry.Concepts).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var pending = new List<string>();
            string ConceptKey(string concept) => string.Join(':', request.RepositoryIdentity, snapshot.Revision, request.IncludeStandingPreferences, request.Options.ConceptFuzzyEnabled, request.Options.ConceptFuzzyMaximumDistance, concept);
            foreach (var concept in concepts)
            {
                if (_conceptCache.TryGetValue(ConceptKey(concept), out var resolved))
                {
                    resolvedConcepts.UnionWith(resolved);
                }
                else if (vocabulary.Contains(concept, StringComparer.Ordinal))
                {
                    resolvedConcepts.Add(concept);
                    AddBounded(_conceptCache, ConceptKey(concept), (IReadOnlyList<string>)[concept], request.Options.MaximumCacheEntries);
                }
                else
                {
                    pending.Add(concept);
                }
            }

            MemoryTermResolution resolution = new([]);
            if (pending.Count > 0 && request.Options.ConceptFuzzyEnabled && _store is IRepositoryMemoryTermResolver resolver)
            {
                resolution = await resolver.ResolveTermsAsync(new RepositoryMemoryVocabularySnapshot(request.RepositoryIdentity, snapshot.Revision, vocabulary), pending, request.Options.ConceptFuzzyMaximumDistance, cancellationToken);
            }
            else if (pending.Count > 0 && request.Options.ConceptFuzzyEnabled)
            {
                resolution = new MemoryTermResolution([], "Concept fuzzy resolver unavailable; using exact concept matches only.");
            }

            if (resolution.DegradedReason is { } reason)
            {
                diagnostics.Add(reason);
            }

            foreach (var concept in pending)
            {
                var resolved = resolution.Matches.Where(match => match.Query == concept).Select(match => match.Term).ToArray();
                resolvedConcepts.UnionWith(resolved);
                if (resolution.DegradedReason is null)
                {
                    AddBounded(_conceptCache, ConceptKey(concept), (IReadOnlyList<string>)resolved, request.Options.MaximumCacheEntries);
                }
            }

            foreach (var match in resolution.Matches)
            {
                diagnostics.Add($"Concept {match.Query} -> {match.Term}: spellfix distance={match.Distance}.");
            }

            var conceptMatches = snapshot.Entries
                .Select(entry => (entry.Id, Matches: entry.Concepts.Intersect(resolvedConcepts, StringComparer.Ordinal).ToArray()))
                .Where(item => item.Matches.Length > 0).ToDictionary(item => item.Id, item => item.Matches);
            var ranked = snapshot.Entries.Where(entry => lexical.ContainsKey(entry.Id) || semantic.ContainsKey(entry.Id) || conceptMatches.ContainsKey(entry.Id))
                .Select(entry =>
                {
                    int? lexicalRank = lexical.TryGetValue(entry.Id, out var rank) ? rank : null;
                    var hasSemantic = semantic.TryGetValue(entry.Id, out var vectorMatch);
                    int? semanticRank = hasSemantic ? vectorMatch.Rank : null;
                    var score = (lexicalRank is { } left ? 1d / (FusionConstant + left) : 0)
                        + (semanticRank is { } right ? 1d / (FusionConstant + right) : 0);
                    return new RepositoryMemoryRetrievalCandidate(entry, score, lexicalRank, semanticRank, hasSemantic ? vectorMatch.Similarity : null)
                    {
                        ConceptMatches = conceptMatches.GetValueOrDefault(entry.Id) ?? [],
                    };
                })
                .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Entry.Id.Value).ToArray();
            var hybridCandidates = ranked.Count(candidate => candidate.LexicalRank is not null || candidate.SemanticRank is not null);
            var conceptOnlyCandidates = ranked.Length - hybridCandidates;
            var comparedCandidates = request.Options.RerankerEnabled
                ? Math.Min(hybridCandidates, request.Options.RerankerCandidateLimit)
                    + Math.Min(conceptOnlyCandidates, request.Options.ConceptRecallEnabled ? request.Options.ConceptCandidateLimit : 0)
                : 0;
            var (selected, rerankerStatus, pairTruncated) = await RerankAsync(
                ranked, query, request.Options, crossEncoderModel, diagnostics, cancellationToken);
            if (request.Options.ConceptRecallEnabled && request.RetainedMemories.Count > 0)
            {
                var entriesById = snapshot.Entries.ToDictionary(entry => entry.Id);
                var qualifiedById = selected.ToDictionary(candidate => candidate.Entry.Id);
                var retained = request.RetainedMemories
                    .Where(item => entriesById.TryGetValue(item.Id, out var entry) && entry.Revision == item.Revision)
                    .Select(item => qualifiedById.GetValueOrDefault(item.Id)
                        ?? new RepositoryMemoryRetrievalCandidate(entriesById[item.Id], 0, null, null, null));
                selected = [.. retained.Concat(selected).DistinctBy(candidate => candidate.Entry.Id)];
            }

            var resultLimitOmissions = Math.Max(0, selected.Length - request.Options.MaximumResults);
            selected = [.. selected.Take(request.Options.MaximumResults)];

            foreach (var expansion in snapshot.LexicalExpansions)
            {
                diagnostics.Add($"Lexical {expansion.Query} -> {expansion.Term}: spellfix distance={expansion.Distance}.");
            }

            if (concepts.Length > 0)
            {
                diagnostics.Add($"Active memory concepts: {string.Join(", ", concepts)}; concept-qualified candidates={conceptMatches.Count}.");
            }

            var truncated = bounded || embedding?.WasTruncated == true || embedding?.InputTokenCount > model.MaxInputTokens;
            if (truncated)
            {
                diagnostics.Add("The query was bounded or truncated; current instructions precede task context.");
            }

            diagnostics.Add($"Memory search used embedding space {model.SpaceId}; semanticMinimum={minimumKey}, lexical={lexical.Count}, semantic={semantic.Count}, selected={selected.Length}, elapsed={timer.Elapsed.TotalMilliseconds:F1}ms.");
            foreach (var entry in snapshot.Entries.Where(entry => !selected.Any(candidate => candidate.Entry.Id == entry.Id)).Take(request.Options.MaximumDiagnostics))
            {
                diagnostics.Add($"Memory {entry.Id.Value:D}: omitted by branch qualification, reranking, or configured selection limit.");
            }

            IReadOnlyList<string> boundedDiagnostics = diagnostics.Count <= request.Options.MaximumDiagnostics
                ? diagnostics : [.. diagnostics.Take(request.Options.MaximumDiagnostics - 1), $"Omitted {diagnostics.Count - request.Options.MaximumDiagnostics + 1} additional memory diagnostics."];
            var degraded = snapshot.FuzzyLexicalStatus == RepositoryMemorySearchBranchStatus.Unavailable || embedding is null || rerankerStatus is RepositoryMemorySearchBranchStatus.Unavailable or RepositoryMemorySearchBranchStatus.Incomplete || resolution.DegradedReason is not null || snapshot.Entries.Any(entry => !IsCompatible(entry, model));
            var result = new RepositoryMemoryRetrievalResult(selected, boundedDiagnostics, snapshot.Revision, truncated, queryCacheHit, false)
            {
                StandingPreferences = standingPreferences,
                IsComplete = !degraded && !truncated,
                SearchDetails = new RepositoryMemorySearchDetails
                {
                    Lexical = RepositoryMemorySearchBranchStatus.Completed,
                    FuzzyLexical = snapshot.FuzzyLexicalStatus,
                    LexicalExpansions = snapshot.LexicalExpansions,
                    Semantic = embedding is null ? RepositoryMemorySearchBranchStatus.Unavailable
                        : truncated || snapshot.Entries.Any(entry => !IsCompatible(entry, model)) ? RepositoryMemorySearchBranchStatus.Incomplete
                        : RepositoryMemorySearchBranchStatus.Completed,
                    Reranker = rerankerStatus,
                    Concepts = !request.Options.ConceptRecallEnabled ? RepositoryMemorySearchBranchStatus.Disabled
                        : resolution.DegradedReason is not null ? RepositoryMemorySearchBranchStatus.Unavailable : RepositoryMemorySearchBranchStatus.Completed,
                    HybridCandidates = hybridCandidates,
                    ConceptOnlyCandidates = conceptOnlyCandidates,
                    ComparedCandidates = comparedCandidates,
                    CandidateWindowOmissions = request.Options.RerankerEnabled ? hybridCandidates + conceptOnlyCandidates - comparedCandidates : 0,
                    ResultLimitOmissions = resultLimitOmissions,
                    PairTruncated = pairTruncated,
                },
                ConceptResolutionPending = resolution.DegradedReason is not null || snapshot.FuzzyLexicalStatus == RepositoryMemorySearchBranchStatus.Unavailable,
            };
            if (!degraded)
            {
                AddBounded(_rankingCache, rankKey, result, request.Options.MaximumCacheEntries);
            }
            else if (resolution.DegradedReason is null && snapshot.FuzzyLexicalStatus != RepositoryMemorySearchBranchStatus.Unavailable && request.UserTurnId is { } userTurn)
            {
                AddBounded(_rankingCache, rankKey + ":degraded:" + userTurn.Value.ToString("D"), result, request.Options.MaximumCacheEntries);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (memorySetRevision is null)
            {
                try
                {
                    // A failed lexical index must not suppress readable standing preferences.
                    var snapshot = await _store.GetSnapshotAsync(request.RepositoryIdentity, [], cancellationToken);
                    standingPreferences = SelectStandingPreferences(snapshot);
                    memorySetRevision = snapshot.Revision;
                }
                catch (Exception readException) when (readException is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "Repository memories could not be read, so standing preferences cannot be assembled. Check the repository memory database and retry.",
                        readException);
                }
            }

            return new RepositoryMemoryRetrievalResult([], [$"Repository memory search failed ({exception.GetType().Name}); matches were omitted."], memorySetRevision)
            {
                IsComplete = false,
                SearchDetails = new RepositoryMemorySearchDetails
                {
                    Lexical = RepositoryMemorySearchBranchStatus.Unavailable,
                    FuzzyLexical = request.Options.Lexical.FuzzyEnabled ? RepositoryMemorySearchBranchStatus.Unavailable : RepositoryMemorySearchBranchStatus.Disabled,
                    Semantic = RepositoryMemorySearchBranchStatus.Unavailable,
                    Reranker = request.Options.RerankerEnabled ? RepositoryMemorySearchBranchStatus.Unavailable : RepositoryMemorySearchBranchStatus.Disabled,
                    Concepts = request.Options.ConceptRecallEnabled ? RepositoryMemorySearchBranchStatus.Unavailable : RepositoryMemorySearchBranchStatus.Disabled,
                },
                StandingPreferences = standingPreferences,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    private static IReadOnlyList<RepositoryMemoryEntry> SelectStandingPreferences(RepositoryMemoryReadSnapshot snapshot)
    {
        return [.. snapshot.Entries.Where(entry => entry.MemoryType == RepositoryMemoryType.StandingPreference).OrderBy(entry => entry.Id.Value)];
    }

    private static RepositoryMemoryReadSnapshot SelectSituationalSnapshot(RepositoryMemoryReadSnapshot snapshot)
    {
        var entries = snapshot.Entries.Where(entry => entry.MemoryType == RepositoryMemoryType.Situational).ToArray();
        var ids = entries.Select(entry => entry.Id).ToHashSet();
        return snapshot with { Entries = entries, LexicalMatches = [.. snapshot.LexicalMatches.Where(match => ids.Contains(match.Id))] };
    }

    private static void AddBounded<T>(Dictionary<string, T> cache, string key, T value, int maximumCacheEntries)
    {
        while (cache.Count >= maximumCacheEntries)
        {
            cache.Remove(cache.Keys.First());
        }

        cache[key] = value;
    }

    private static string BuildQuery(RepositoryMemoryRetrievalRequest request, out bool bounded)
    {
        var current = request.CurrentInstruction.Trim();
        var task = request.TaskIntent?.Trim();
        var includeTask = !string.IsNullOrWhiteSpace(task) && !string.Equals(current, task, StringComparison.Ordinal);
        bounded = current.Length + (includeTask ? 1L + task?.Length : 0) > request.Options.MaximumQueryCharacters;
        var builder = new StringBuilder(Math.Min(current.Length, request.Options.MaximumQueryCharacters));
        builder.Append(current.AsSpan(0, Math.Min(current.Length, request.Options.MaximumQueryCharacters)));
        if (includeTask && task is not null && builder.Length < request.Options.MaximumQueryCharacters)
        {
            builder.Append('\n');
            builder.Append(task.AsSpan(0, Math.Min(task.Length, request.Options.MaximumQueryCharacters - builder.Length)));
        }

        var text = builder.ToString();
        var length = Math.Min(text.Length, request.Options.MaximumQueryCharacters);
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private static IReadOnlyList<string> Tokenize(string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        var token = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                token.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return ordered;

        void Flush()
        {
            if (token.Length > 0)
            {
                var term = token.ToString();
                if (terms.Add(term))
                {
                    ordered.Add(term);
                }

                token.Clear();
            }
        }
    }

    private static bool IsCompatible(RepositoryMemoryEntry entry, TextEmbeddingModelDescriptor model)
    {
        return string.Equals(entry.EmbeddingSpaceId, model.SpaceId, StringComparison.Ordinal)
            && entry.EmbeddingDimensions == model.Dimensions
            && entry.EmbeddingContentHash == entry.ContentHash && entry.EmbeddingRevision == entry.Revision
            && EmbeddingValidation.IsValid(model, new TextEmbeddingResult(entry.Embedding, 0, false));
    }

    private static double Cosine(ReadOnlySpan<float> query, ReadOnlySpan<float> memory)
    {
        var dot = 0d;
        var queryNorm = 0d;
        var memoryNorm = 0d;
        for (var index = 0; index < query.Length; index++)
        {
            dot += (double)query[index] * memory[index];
            queryNorm += (double)query[index] * query[index];
            memoryNorm += (double)memory[index] * memory[index];
        }

        // Roundoff must not admit a semantic match above the configured maximum of 1.
        return Math.Clamp(dot / Math.Sqrt(queryNorm * memoryNorm), -1, 1);
    }

    private static void AddRebuildDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count < 48)
        {
            diagnostics.Add(message);
        }
    }

    private async Task<bool> RebuildEmbeddingsAsync(
        RepositoryMemoryReadSnapshot snapshot,
        TextEmbeddingModelDescriptor model,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var rebuilt = false;
        foreach (var entry in snapshot.Entries.Where(entry => !IsCompatible(entry, model)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var embedding = await _generator.GenerateAsync(entry.Text, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (embedding.WasTruncated || embedding.InputTokenCount > model.MaxInputTokens
                    || !EmbeddingValidation.IsValid(model, embedding))
                {
                    AddRebuildDiagnostic(diagnostics, $"Memory {entry.Id.Value:D}: embedding cannot represent complete content; lexical retrieval remains available. Inspect and shorten this imported memory.");
                    continue;
                }

                var attached = await _store.AttachEmbeddingAsync(snapshot.RepositoryIdentity, entry.Id, entry.Revision, entry.ContentHash, model, embedding, cancellationToken);
                rebuilt |= attached;
                AddRebuildDiagnostic(diagnostics, $"Memory {entry.Id.Value:D}: embedding rebuild {(attached ? "completed" : "discarded because content changed")}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AddRebuildDiagnostic(diagnostics, $"Memory {entry.Id.Value:D}: embedding rebuild failed ({exception.GetType().Name}); lexical retrieval remains available.");
            }
        }

        return rebuilt;
    }
}
