namespace Threadsmith.Core;

using System.Text.Json.Serialization;

/// <summary>Rollout choice for final memory admission.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RepositoryMemoryScoringMode>))]
public enum RepositoryMemoryScoringMode
{
    /// <summary>Existing hybrid discovery with optional cross-encoder ordering.</summary>
    Prior,

    /// <summary>Pool-normalized three-signal fusion followed by an explicit threshold.</summary>
    Weighted,
}

/// <summary>Independent, immutable scoring and discovery settings for one route.</summary>
public sealed record RepositoryMemoryScoringOptions
{
    /// <summary>Prior behavior remains the rollout default.</summary>
    public RepositoryMemoryScoringMode Mode { get; init; }

    /// <summary>Experimental semantic contribution, not a calibrated default.</summary>
    public double SemanticWeight { get; init; } = 0.25;

    /// <summary>Experimental lexical contribution, not a calibrated default.</summary>
    public double LexicalWeight { get; init; } = 0.50;

    /// <summary>Experimental cross-encoder contribution, not a calibrated default.</summary>
    public double CrossEncoderWeight { get; init; } = 0.25;

    /// <summary>Required explicit weighted admission cutoff; zero is useful only for score inspection.</summary>
    public double? FinalThreshold { get; init; }

    /// <summary>Semantic TopK per ownership partition, bounded to 128.</summary>
    public int SemanticCandidateLimit { get; init; } = 20;

    /// <summary>Lexical TopK per ownership partition, bounded to 128.</summary>
    public int LexicalCandidateLimit { get; init; } = 20;

    /// <summary>Concept TopK per ownership partition when concepts are enabled, bounded to 128.</summary>
    public int ConceptCandidateLimit { get; init; } = 4;

    /// <summary>Optional strict cosine discovery floor; null disables the floor in weighted mode.</summary>
    public double? SemanticDiscoveryMinimum { get; init; }

    /// <summary>Rejects invalid settings; weight sums allow absolute rounding error up to 1e-9.</summary>
    public void Validate(bool rerankerEnabled)
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentException("Memory Scoring:Mode must be Prior or Weighted.");
        }

        if (!double.IsFinite(SemanticWeight) || SemanticWeight < 0
            || !double.IsFinite(LexicalWeight) || LexicalWeight < 0
            || !double.IsFinite(CrossEncoderWeight) || CrossEncoderWeight < 0
            || Math.Abs(SemanticWeight + LexicalWeight + CrossEncoderWeight - 1) > 1e-9)
        {
            throw new ArgumentException("Memory scoring weights must be finite, nonnegative and sum to one within 1e-9.");
        }

        if (FinalThreshold is { } threshold && (!double.IsFinite(threshold) || threshold is < 0 or > 1))
        {
            throw new ArgumentException("Memory Scoring:FinalThreshold must be finite and between zero and one.");
        }

        if (Mode == RepositoryMemoryScoringMode.Weighted && (!rerankerEnabled || FinalThreshold is null))
        {
            throw new ArgumentException("Weighted memory scoring requires RerankerEnabled=true and an explicit Scoring:FinalThreshold.");
        }

        if (SemanticDiscoveryMinimum is { } floor && (!double.IsFinite(floor) || floor is < -1 or > 1))
        {
            throw new ArgumentException("Memory Scoring:SemanticDiscoveryMinimum must be a finite cosine in [-1,1], or null.");
        }

        if (SemanticCandidateLimit is < 1 or > 128 || LexicalCandidateLimit is < 1 or > 128 || ConceptCandidateLimit is < 1 or > 128)
        {
            throw new ArgumentException("Memory scoring branch candidate limits must each be between 1 and 128.");
        }
    }
}

/// <summary>Auditable admission evidence; absent raw branch values mean that branch did not return this candidate.</summary>
public sealed record RepositoryMemoryScoreEvidence(
    double? RawSemantic,
    double? RawLexical,
    double RawCrossEncoder,
    double Semantic,
    double Lexical,
    double CrossEncoder,
    double FinalScore,
    bool Accepted);
