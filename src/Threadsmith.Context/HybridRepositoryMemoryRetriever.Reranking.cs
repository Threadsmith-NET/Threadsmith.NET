namespace Threadsmith.Context;

using System.Diagnostics;
using System.Globalization;
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

    private async Task<(RepositoryMemoryRetrievalCandidate[] Selected, bool Degraded)> RerankAsync(
        RepositoryMemoryRetrievalCandidate[] ranked,
        string query,
        RepositoryMemoryOptions options,
        TextCrossEncoderModelDescriptor? model,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var fallback = ranked.Take(options.EffectiveContextMaximum).ToArray();
        if (!options.RerankerEnabled || ranked.Length == 0)
        {
            return (fallback, false);
        }

        var crossEncoder = _crossEncoder;
        if (crossEncoder is null || model is null)
        {
            diagnostics.Add("Memory reranking is enabled but no cross-encoder is available; using qualified hybrid ranking.");
            return (fallback, true);
        }

        var timer = Stopwatch.StartNew();
        try
        {
            if (model.MaxBatchSize <= 0 || model.MaxInputTokens <= 0 || string.IsNullOrWhiteSpace(model.ModelId))
            {
                throw new InvalidOperationException("The cross-encoder descriptor is invalid.");
            }

            var candidates = ranked.Take(Math.Min(options.RerankerCandidateLimit, model.MaxBatchSize)).ToArray();
            var scores = await crossEncoder.ScoreAsync(query, candidates.Select(candidate => candidate.Entry.Text).ToArray(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (scores.Count != candidates.Length || scores.Any(score => score is null || !double.IsFinite(score.Score) || score.InputTokenCount < 0))
            {
                throw new InvalidOperationException("The cross-encoder returned invalid or incomplete scores.");
            }

            if (scores.Any(score => score.WasTruncated || score.InputTokenCount > model.MaxInputTokens))
            {
                diagnostics.Add("Memory reranking could not score every complete query-memory pair within its token limit; using qualified hybrid ranking.");
                return (fallback, true);
            }

            var selected = candidates.Select((candidate, index) => candidate with { CrossEncoderScore = scores[index].Score })
                .Where(candidate => options.RerankerMinimumScore is not { } minimum || candidate.CrossEncoderScore > minimum)
                .OrderByDescending(candidate => candidate.CrossEncoderScore)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Entry.Id.Value)
                .Take(options.EffectiveContextMaximum).ToArray();
            var cutoff = options.RerankerMinimumScore?.ToString("R", CultureInfo.InvariantCulture) ?? "none";
            diagnostics.Add($"Memory reranking used {model.ModelId}; candidates={candidates.Length}, minimumScore={cutoff}, selected={selected.Length}, elapsed={timer.Elapsed.TotalMilliseconds:F1}ms.");
            return (selected, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Memory reranking is unavailable ({exception.GetType().Name}); using qualified hybrid ranking.");
            return (fallback, true);
        }
    }
}
