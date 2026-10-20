namespace Threadsmith.Context;

using System.Diagnostics;
using Threadsmith.Core;

/// <summary>Scores only qualified hybrid candidates and preserves recall when reranking is unavailable.</summary>
public sealed partial class HybridRepositoryMemoryRetriever
{
    private TextCrossEncoderModelDescriptor? GetCrossEncoderModel(List<string> diagnostics)
    {
        try
        {
            return _crossEncoder?.Model;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.Add($"The memory cross-encoder descriptor is unavailable ({exception.GetType().Name}); using qualified hybrid ranking.");
            return null;
        }
    }

    private async Task<(RepositoryMemoryRetrievalCandidate[] Selected, RepositoryMemorySearchBranchStatus Status, bool PairTruncated, string? InputFailure)> RerankAsync(
        RepositoryMemoryRetrievalCandidate[] ranked,
        string query,
        TextCrossEncoderRecallQuery? recallQuery,
        RepositoryMemorySearchOptions options,
        TextCrossEncoderModelDescriptor? model,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var fallback = ranked.Where(candidate => candidate.LexicalRank is not null || candidate.SemanticRank is not null).ToArray();
        if (!options.RerankerEnabled || ranked.Length == 0)
        {
            if (!options.RerankerEnabled && ranked.Any(candidate => candidate.ConceptMatches.Count > 0 && candidate.LexicalRank is null && candidate.SemanticRank is null))
            {
                diagnostics.Add("Concept-only candidates deferred because memory reranking is disabled.");
            }

            return (fallback, options.RerankerEnabled ? RepositoryMemorySearchBranchStatus.NotRequired : RepositoryMemorySearchBranchStatus.Disabled, false, null);
        }

        var crossEncoder = _crossEncoder;
        if (crossEncoder is null || model is null)
        {
            diagnostics.Add("Memory reranking is enabled but no cross-encoder is available; using qualified hybrid ranking.");
            return (fallback, RepositoryMemorySearchBranchStatus.Unavailable, false, null);
        }

        var timer = Stopwatch.StartNew();
        try
        {
            if (model.MaxBatchSize <= 0 || model.MaxInputTokens <= 0 || string.IsNullOrWhiteSpace(model.ModelId))
            {
                throw new InvalidOperationException("The cross-encoder descriptor is invalid.");
            }

            var candidates = options.Scoring.Mode == RepositoryMemoryScoringMode.Weighted ? ranked : [.. ranked.Where(candidate => candidate.LexicalRank is not null || candidate.SemanticRank is not null)
                .Take(options.RerankerCandidateLimit)
                .Concat(ranked.Where(candidate => candidate.LexicalRank is null && candidate.SemanticRank is null)
                    .Take(options.ConceptRecallEnabled ? options.ConceptCandidateLimit : 0))];
            var scores = new List<TextCrossEncoderScore>(candidates.Length);
            foreach (var batch in candidates.Chunk(model.MaxBatchSize))
            {
                var documents = batch.Select(candidate => candidate.Entry.Text).ToArray();
                var batchScores = recallQuery is not null
                    ? await crossEncoder.ScoreRecallAsync(recallQuery, documents, cancellationToken)
                    : await crossEncoder.ScoreAsync(query, documents, cancellationToken);
                if (batchScores.Count != batch.Length)
                {
                    diagnostics.Add("The cross-encoder returned an incomplete batch; using qualified hybrid ranking.");
                    return (fallback, RepositoryMemorySearchBranchStatus.Incomplete, false, null);
                }

                scores.AddRange(batchScores);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (scores.Count != candidates.Length || scores.Any(score => score is null || !double.IsFinite(score.Score) || score.InputTokenCount < 0))
            {
                diagnostics.Add("The cross-encoder returned invalid or incomplete scores; using qualified hybrid ranking.");
                return (fallback, RepositoryMemorySearchBranchStatus.Incomplete, false, null);
            }

            if (scores.Any(score => score.InputTokenCount > model.MaxInputTokens && !score.WasTruncated))
            {
                diagnostics.Add("Memory reranking returned an overlength pair without truncation accounting; using qualified hybrid ranking.");
                return (fallback, RepositoryMemorySearchBranchStatus.Incomplete, false, null);
            }

            var pairTruncated = scores.Any(score => score.WasTruncated
                || (recallQuery is null && score.UsedQueryTokenCount < score.QueryTokenCount));
            if (pairTruncated)
            {
                diagnostics.Add("Memory reranking omitted pair content; using qualified hybrid ranking without accepting incomplete scores.");
                return (fallback, RepositoryMemorySearchBranchStatus.Incomplete, true, null);
            }

            if (scores.FirstOrDefault() is { QueryTokenCount: { } original, UsedQueryTokenCount: { } used } && used < original)
            {
                diagnostics.Add($"Recall reranker question shortened from {original} to {used} tokens; complete candidate memories preserved.");
            }

            var qualified = candidates.Select((candidate, index) => candidate with
            {
                CrossEncoderScore = scores[index].Score,
                RerankerQueryTokens = scores[index].QueryTokenCount,
                RerankerUsedQueryTokens = scores[index].UsedQueryTokenCount ?? scores[index].QueryTokenCount,
                RerankerDocumentTokens = scores[index].DocumentTokenCount,
            })
                .OrderByDescending(candidate => candidate.CrossEncoderScore)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Entry.Id.Value).ToArray();
            diagnostics.Add($"Memory reranking used {model.ModelId}; candidates={candidates.Length}, selected={Math.Min(qualified.Length, options.MaximumResults)}, elapsed={timer.Elapsed.TotalMilliseconds:F1}ms.");
            return (qualified, RepositoryMemorySearchBranchStatus.Completed, pairTruncated, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TextCrossEncoderInputException exception)
        {
            diagnostics.Add($"{exception.Message} Recall is using qualified hybrid ranking.");
            return (fallback, RepositoryMemorySearchBranchStatus.Incomplete, true, exception.Message);
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Memory reranking is unavailable ({exception.GetType().Name}); using qualified hybrid ranking.");
            return (fallback, RepositoryMemorySearchBranchStatus.Unavailable, false, null);
        }
    }
}
