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

    private async Task<(RepositoryMemoryRetrievalCandidate[] Selected, RepositoryMemorySearchBranchStatus Status, bool PairTruncated)> RerankAsync(
        RepositoryMemoryRetrievalCandidate[] ranked,
        string query,
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

            return (fallback, options.RerankerEnabled ? RepositoryMemorySearchBranchStatus.NotRequired : RepositoryMemorySearchBranchStatus.Disabled, false);
        }

        var crossEncoder = _crossEncoder;
        if (crossEncoder is null || model is null)
        {
            diagnostics.Add("Memory reranking is enabled but no cross-encoder is available; using qualified hybrid ranking.");
            return (fallback, RepositoryMemorySearchBranchStatus.Unavailable, false);
        }

        var timer = Stopwatch.StartNew();
        try
        {
            if (model.MaxBatchSize <= 0 || model.MaxInputTokens <= 0 || string.IsNullOrWhiteSpace(model.ModelId))
            {
                throw new InvalidOperationException("The cross-encoder descriptor is invalid.");
            }

            var candidates = ranked.Where(candidate => candidate.LexicalRank is not null || candidate.SemanticRank is not null)
                .Take(options.RerankerCandidateLimit)
                .Concat(ranked.Where(candidate => candidate.LexicalRank is null && candidate.SemanticRank is null)
                    .Take(options.ConceptRecallEnabled ? options.ConceptCandidateLimit : 0)).ToArray();
            var scores = new List<TextCrossEncoderScore>(candidates.Length);
            foreach (var batch in candidates.Chunk(model.MaxBatchSize))
            {
                var batchScores = await crossEncoder.ScoreAsync(query, batch.Select(candidate => candidate.Entry.Text).ToArray(), cancellationToken);
                if (batchScores.Count != batch.Length)
                {
                    diagnostics.Add("The cross-encoder returned an incomplete batch; using qualified hybrid ranking.");
                    return (fallback, RepositoryMemorySearchBranchStatus.Incomplete, false);
                }

                scores.AddRange(batchScores);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (scores.Count != candidates.Length || scores.Any(score => score is null || !double.IsFinite(score.Score) || score.InputTokenCount < 0))
            {
                diagnostics.Add("The cross-encoder returned invalid or incomplete scores; using qualified hybrid ranking.");
                return (fallback, RepositoryMemorySearchBranchStatus.Incomplete, false);
            }

            if (scores.Any(score => score.WasTruncated || score.InputTokenCount > model.MaxInputTokens))
            {
                diagnostics.Add("Memory reranking could not score every complete query-memory pair within its token limit; using qualified hybrid ranking.");
                return (fallback, RepositoryMemorySearchBranchStatus.Incomplete, true);
            }

            var qualified = candidates.Select((candidate, index) => candidate with { CrossEncoderScore = scores[index].Score })
                .OrderByDescending(candidate => candidate.CrossEncoderScore)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Entry.Id.Value).ToArray();
            diagnostics.Add($"Memory reranking used {model.ModelId}; candidates={candidates.Length}, selected={Math.Min(qualified.Length, options.MaximumResults)}, elapsed={timer.Elapsed.TotalMilliseconds:F1}ms.");
            return (qualified, RepositoryMemorySearchBranchStatus.Completed, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Memory reranking is unavailable ({exception.GetType().Name}); using qualified hybrid ranking.");
            return (fallback, RepositoryMemorySearchBranchStatus.Unavailable, false);
        }
    }
}
