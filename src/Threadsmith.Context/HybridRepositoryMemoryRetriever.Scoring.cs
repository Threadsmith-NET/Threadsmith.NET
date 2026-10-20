namespace Threadsmith.Context;

using Threadsmith.Core;

/// <summary>Normalizes complete scoring pools before final admission.</summary>
public sealed partial class HybridRepositoryMemoryRetriever
{
    /// <summary>Freezes the entire successful comparison pool before normalizing, independently per owner.</summary>
    internal static RepositoryMemoryRetrievalCandidate[] FuseWeightedScores(
        IReadOnlyList<RepositoryMemoryRetrievalCandidate> candidates,
        RepositoryMemoryScoringOptions options)
    {
        var fused = new List<RepositoryMemoryRetrievalCandidate>(candidates.Count);
        foreach (var pool in candidates.GroupBy(candidate => candidate.Entry.Ownership))
        {
            if (pool.Any(candidate => candidate.CrossEncoderScore is not { } ce || !double.IsFinite(ce)
                || (candidate.SemanticRank is not null && (candidate.CosineSimilarity is not { } semantic || !double.IsFinite(semantic)))
                || (candidate.LexicalRank is not null && (candidate.LexicalScore is not { } lexical || !double.IsFinite(lexical)))))
            {
                throw new InvalidOperationException("Weighted scoring requires complete, finite evidence for every returned signal.");
            }

            var semanticMax = pool.Max(candidate => candidate.SemanticRank is null ? 0 : Math.Max(0, candidate.CosineSimilarity.GetValueOrDefault()));

            // SQLite returns native ascending BM25: convert its sign exactly once here.
            var lexicalMax = pool.Max(candidate => candidate.LexicalRank is null ? 0 : Math.Max(0, -candidate.LexicalScore.GetValueOrDefault()));
            var ceMin = pool.Min(candidate => candidate.CrossEncoderScore.GetValueOrDefault());
            var ceMax = pool.Max(candidate => candidate.CrossEncoderScore.GetValueOrDefault());
            var ceRange = ceMax - ceMin;
            foreach (var candidate in pool)
            {
                var rawSemantic = candidate.SemanticRank is null ? null : candidate.CosineSimilarity;
                var rawLexical = candidate.LexicalRank is null ? null : candidate.LexicalScore;
                var rawCe = candidate.CrossEncoderScore.GetValueOrDefault();
                var semantic = semanticMax == 0 ? 0 : Math.Clamp(Math.Max(0, rawSemantic.GetValueOrDefault()) / semanticMax, 0, 1);
                var lexical = lexicalMax == 0 ? 0 : Math.Clamp(Math.Max(0, -rawLexical.GetValueOrDefault()) / lexicalMax, 0, 1);
                var ce = ceRange == 0 ? 0 : Math.Clamp(
                    double.IsFinite(ceRange)
                    ? (rawCe - ceMin) / ceRange
                    : ((rawCe / 2) - (ceMin / 2)) / ((ceMax / 2) - (ceMin / 2)),
                    0,
                    1);
                var score = Math.Clamp((options.SemanticWeight * semantic) + (options.LexicalWeight * lexical) + (options.CrossEncoderWeight * ce), 0, 1);
                fused.Add(candidate with
                {
                    Score = score,
                    ScoreEvidence = new(rawSemantic, rawLexical, rawCe, semantic, lexical, ce, score, score >= options.FinalThreshold),
                });
            }
        }

        return [.. fused.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Entry.Id.Value)];
    }
}
