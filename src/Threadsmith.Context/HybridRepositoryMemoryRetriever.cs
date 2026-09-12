namespace Threadsmith.Context;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Small exact hybrid search with snapshot-consistent candidates and bounded turn-query caches.</summary>
public sealed partial class HybridRepositoryMemoryRetriever : IHybridRepositoryMemoryRetriever, IDisposable
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
    public async Task<RepositoryMemoryRetrievalResult> RetrieveAsync(
        RepositoryMemoryRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryIdentity);
        request.Options.Validate();
        var semanticMinimum = request.Options.SemanticMinimum;
        cancellationToken.ThrowIfCancellationRequested();
        var query = BuildQuery(request, out var bounded);
        IReadOnlyList<RepositoryMemoryEntry> standingPreferences = [];
        long? memorySetRevision = null;
        var timer = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var terms = Tokenize(query).Where(term => !StopWords.Contains(term)).Take(request.Options.MaximumQueryTerms).ToArray();
            var snapshot = await _store.GetSnapshotAsync(request.RepositoryIdentity, terms, cancellationToken);
            memorySetRevision = snapshot.Revision;
            standingPreferences = SelectStandingPreferences(snapshot);
            snapshot = SelectSituationalSnapshot(snapshot);
            if (request.Options.EffectiveContextMaximum == 0 || string.IsNullOrWhiteSpace(query) || snapshot.Entries.Count == 0)
            {
                IReadOnlyList<string> earlyDiagnostics = request.Options.EffectiveContextMaximum == 0
                    ? [.. snapshot.Warnings, "Automatic situational-memory retrieval is disabled by the context limit."]
                    : string.IsNullOrWhiteSpace(query)
                        ? [.. snapshot.Warnings, "The current situational-memory query is empty."]
                        : snapshot.Warnings;
                return new RepositoryMemoryRetrievalResult([], earlyDiagnostics, snapshot.Revision)
                {
                    StandingPreferences = standingPreferences,
                };
            }

            var model = _generator.Model;
            var queryKey = model.SpaceId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)));

            // Retrieval settings affect selections, while the embedding cache remains model/query-only.
            var minimumKey = string.Join(':', semanticMinimum.ToString("R", CultureInfo.InvariantCulture), request.Options.MaximumQueryTerms, request.Options.MaximumDiagnostics);
            var diagnostics = new List<string>(snapshot.Warnings.Take(request.Options.MaximumDiagnostics));
            var crossEncoderModel = request.Options.RerankerEnabled ? GetCrossEncoderModel(diagnostics) : null;
            var rerankerKey = request.Options.RerankerEnabled
                ? string.Join(':', crossEncoderModel?.ModelId ?? "unavailable", crossEncoderModel?.MaxInputTokens, crossEncoderModel?.MaxBatchSize, request.Options.RerankerCandidateLimit, request.Options.RerankerMinimumScore?.ToString("R", CultureInfo.InvariantCulture) ?? "none")
                : "disabled";
            var rankKey = string.Join(':', request.RepositoryIdentity, snapshot.Revision, queryKey, request.Options.MaxNumberOfRepoMemories, request.Options.EffectiveContextMaximum, minimumKey, rerankerKey);
            var degradedKey = request.UserTurnId is { } turn ? rankKey + ":degraded:" + turn.Value.ToString("D") : null;
            if (_rankingCache.TryGetValue(rankKey, out var cached)
                || (degradedKey is not null && _rankingCache.TryGetValue(degradedKey, out cached)))
            {
                return cached with { QueryEmbeddingCacheHit = _queryCache.ContainsKey(queryKey), RankingCacheHit = true };
            }

            var queryCacheHit = _queryCache.TryGetValue(queryKey, out var embedding);
            if (!queryCacheHit)
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
                    snapshot = await _store.GetSnapshotAsync(request.RepositoryIdentity, terms, cancellationToken);
                    memorySetRevision = snapshot.Revision;
                    standingPreferences = SelectStandingPreferences(snapshot);
                    snapshot = SelectSituationalSnapshot(snapshot);
                    rankKey = string.Join(':', request.RepositoryIdentity, snapshot.Revision, queryKey, request.Options.MaxNumberOfRepoMemories, request.Options.EffectiveContextMaximum, minimumKey, rerankerKey);
                }
            }

            var lexical = snapshot.LexicalMatches.OrderBy(match => match.Bm25).ThenBy(match => match.Id.Value)
                .Select((match, index) => (match.Id, Rank: index + 1)).ToDictionary(match => match.Id, match => match.Rank);
            var semantic = embedding is null
                ? []
                : snapshot.Entries.Where(entry => IsCompatible(entry, model))
                    .Select(entry => (entry.Id, Similarity: Cosine(embedding.Vector.Span, entry.Embedding.Span)))
                    .Where(match => match.Similarity > semanticMinimum)
                    .OrderByDescending(match => match.Similarity).ThenBy(match => match.Id.Value)
                    .Select((match, index) => (match.Id, match.Similarity, Rank: index + 1))
                    .ToDictionary(match => match.Id, match => (match.Similarity, match.Rank));
            var ranked = snapshot.Entries.Where(entry => lexical.ContainsKey(entry.Id) || semantic.ContainsKey(entry.Id))
                .Select(entry =>
                {
                    int? lexicalRank = lexical.TryGetValue(entry.Id, out var rank) ? rank : null;
                    var hasSemantic = semantic.TryGetValue(entry.Id, out var vectorMatch);
                    int? semanticRank = hasSemantic ? vectorMatch.Rank : null;
                    var score = (lexicalRank is { } left ? 1d / (FusionConstant + left) : 0)
                        + (semanticRank is { } right ? 1d / (FusionConstant + right) : 0);
                    return new RepositoryMemoryRetrievalCandidate(entry, score, lexicalRank, semanticRank, hasSemantic ? vectorMatch.Similarity : null);
                })
                .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Entry.Id.Value)
                .Take(Math.Max(request.Options.EffectiveContextMaximum, request.Options.RerankerEnabled ? request.Options.RerankerCandidateLimit : 0)).ToArray();
            var (selected, rerankerDegraded) = await RerankAsync(
                ranked, query, request.Options, crossEncoderModel, diagnostics, cancellationToken);
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
            var result = new RepositoryMemoryRetrievalResult(selected, boundedDiagnostics, snapshot.Revision, truncated, queryCacheHit, false)
            {
                StandingPreferences = standingPreferences,
            };
            var degraded = embedding is null || rerankerDegraded || snapshot.Entries.Any(entry => !IsCompatible(entry, model));
            if (!degraded)
            {
                AddBounded(_rankingCache, rankKey, result, request.Options.MaximumCacheEntries);
            }
            else if (request.UserTurnId is { } userTurn)
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

            return new RepositoryMemoryRetrievalResult([], [$"Repository memory search failed ({exception.GetType().Name}); situational matches were omitted."], memorySetRevision)
            {
                StandingPreferences = standingPreferences,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static IReadOnlyList<RepositoryMemoryEntry> SelectStandingPreferences(RepositoryMemoryReadSnapshot snapshot)
        => [.. snapshot.Entries.Where(entry => entry.MemoryType == RepositoryMemoryType.StandingPreference).OrderBy(entry => entry.Id.Value)];

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
