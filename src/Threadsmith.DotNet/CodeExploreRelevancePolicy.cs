namespace Threadsmith.DotNet;

/// <summary>Owns evidence admission and score normalization for natural-language code exploration.</summary>
internal static class CodeExploreRelevancePolicy
{
    /// <summary>Maximum number of lexical entry points expanded by the graph.</summary>
    internal const int MaximumGraphEntryPoints = 3;

    /// <summary>Minimum useful candidate count retained when enough evidence exists.</summary>
    internal const int MinimumBackfillCandidateCount = 3;

    /// <summary>Production evidence required before unrequested tests can be removed.</summary>
    internal const int MinimumProductionCandidatesForTestExclusion = 2;

    /// <summary>Fraction of the strongest score used as the evidence floor.</summary>
    internal const double RelativeScoreFloorRatio = 0.20;

    /// <summary>Maximum floor so one compound-name hit cannot suppress useful single-term mechanisms.</summary>
    internal const int MaximumRelativeScoreFloor = 200;

    /// <summary>Ranking multiplier for unrequested test evidence.</summary>
    internal const double TestScoreMultiplier = 0.50;

    /// <summary>Ranking multiplier for unrequested generated evidence.</summary>
    internal const double GeneratedScoreMultiplier = 0.30;

    /// <summary>Relative lexical strength of an inflectional stem match.</summary>
    internal const double StemMatchMultiplier = 0.80;

    /// <summary>Relative lexical strength of an explicit semantic alias match.</summary>
    internal const double AliasMatchMultiplier = 0.45;

    /// <summary>Base score for one literal prefix retrieval match.</summary>
    internal const int PrefixMatchScore = 80;

    /// <summary>Relative term-coverage strength of prefix-only evidence.</summary>
    internal const double PrefixCoverageMultiplier = 0.50;

    /// <summary>Damping for an uncorroborated ordinary concept match.</summary>
    internal const double SingleConceptScoreMultiplier = 0.60;

    /// <summary>Damping for a common-word exact match without corroboration.</summary>
    internal const double CommonExactScoreMultiplier = 0.30;

    /// <summary>Weight for a weak member with no compiler-known usage edge.</summary>
    internal const double IsolatedWeakKindScoreMultiplier = 0.08;

    /// <summary>Minimum relative graph mass for ordinary graph-only evidence.</summary>
    internal const double MinimumGraphMassRatio = 0.06;

    /// <summary>Graph-mass differences below this fraction defer to lexical evidence.</summary>
    internal const double GraphMassTieRatio = 0.01;

    /// <summary>Maximum graph-central files eligible for literal-term corroboration.</summary>
    internal const int MaximumCentralGraphFiles = 2;

    /// <summary>Restart probability for bounded graph diffusion.</summary>
    internal const double GraphRestartProbability = 0.25;

    /// <summary>Fixed deterministic graph-diffusion iteration count.</summary>
    internal const int GraphWalkIterations = 25;

    /// <summary>Maps morphology strength to one deterministic prefix-channel score.</summary>
    public static int CalculatePrefixMatchScore(CodeExploreTermVariantKind kind)
    {
        var multiplier = kind switch
        {
            CodeExploreTermVariantKind.Literal => 1.0,
            CodeExploreTermVariantKind.Stem => StemMatchMultiplier,
            CodeExploreTermVariantKind.Alias => AliasMatchMultiplier,
            _ => 0,
        };
        return Math.Max(
            1,
            (int)Math.Round(PrefixMatchScore * multiplier, MidpointRounding.AwayFromZero));
    }

    /// <summary>Determines whether morphology evidence is strong enough to corroborate a query concept.</summary>
    public static bool IsStrongConceptMatch(double strength)
    {
        return strength >= StemMatchMultiplier;
    }

    /// <summary>Applies source-classification penalties while retaining explicit evidence for later gates.</summary>
    public static int ApplyClassificationMultiplier(
        int score,
        bool isTest,
        bool isGenerated,
        bool hasTestFocus,
        bool hasGeneratedFocus)
    {
        if (score <= 0)
        {
            return score;
        }

        var multiplier = 1.0;
        if (isTest && !hasTestFocus)
        {
            multiplier *= TestScoreMultiplier;
        }

        if (isGenerated && !hasGeneratedFocus)
        {
            multiplier *= GeneratedScoreMultiplier;
        }

        return Math.Max(1, (int)Math.Round(score * multiplier, MidpointRounding.AwayFromZero));
    }

    /// <summary>Applies the same source-classification policy to graph mass before normalization.</summary>
    public static double ApplyGraphClassificationMultiplier(
        double graphMass,
        bool isTest,
        bool isGenerated,
        bool hasTestFocus,
        bool hasGeneratedFocus)
    {
        var multiplier = 1.0;
        if (isTest && !hasTestFocus)
        {
            multiplier *= TestScoreMultiplier;
        }

        if (isGenerated && !hasGeneratedFocus)
        {
            multiplier *= GeneratedScoreMultiplier;
        }

        return graphMass * multiplier;
    }

    /// <summary>Computes the relative evidence floor from the strongest admitted score.</summary>
    public static int CalculateRelativeScoreFloor(int strongestScore)
    {
        return Math.Max(
            1,
            Math.Min(
                MaximumRelativeScoreFloor,
                (int)Math.Ceiling(Math.Max(0, strongestScore) * RelativeScoreFloorRatio)));
    }

    /// <summary>Determines whether a candidate survives the score gate.</summary>
    public static bool ShouldRetainCandidate(
        int zeroBasedRank,
        int score,
        int relativeScoreFloor,
        bool hasExactEvidence)
    {
        return zeroBasedRank < MinimumBackfillCandidateCount
            || hasExactEvidence
            || score >= relativeScoreFloor;
    }

    /// <summary>Converts bounded graph evidence to a deterministic ranking boost.</summary>
    public static int CalculateGraphBoost(
        double graphMass,
        double maximumGraphMass,
        int maximumBoost)
    {
        if (graphMass <= 0 || maximumGraphMass <= 0 || maximumBoost <= 0)
        {
            return 0;
        }

        var normalizedMass = Math.Clamp(graphMass / maximumGraphMass, 0, 1);
        return Math.Max(
            1,
            (int)Math.Round(maximumBoost * normalizedMass, MidpointRounding.AwayFromZero));
    }

    /// <summary>Applies multiplicative weight for distinct name-level query concepts.</summary>
    public static int ApplyMultiTermNameCorroboration(int score, double corroboratedConceptStrength)
    {
        if (score <= 0 || corroboratedConceptStrength <= 0)
        {
            return score;
        }

        var multiplier = 1 + (corroboratedConceptStrength * 0.5);
        return (int)Math.Min(
            int.MaxValue,
            Math.Round(score * multiplier, MidpointRounding.AwayFromZero));
    }

    /// <summary>Demotes an uncorroborated query concept before result truncation.</summary>
    public static int ApplySingleConceptDamping(int score, bool isCommonExactMatch)
    {
        if (score <= 0)
        {
            return score;
        }

        var multiplier = isCommonExactMatch
            ? CommonExactScoreMultiplier
            : SingleConceptScoreMultiplier;
        return Math.Max(
            1,
            (int)Math.Round(score * multiplier, MidpointRounding.AwayFromZero));
    }

    /// <summary>Demotes a weak declaration only after graph probing found no usage edge.</summary>
    public static int ApplyIsolatedWeakKindPenalty(int score)
    {
        return score <= 0
            ? score
            : Math.Max(
                1,
                (int)Math.Round(score * IsolatedWeakKindScoreMultiplier, MidpointRounding.AwayFromZero));
    }
}
