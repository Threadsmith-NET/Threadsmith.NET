namespace Threadsmith.Core;

/// <summary>Discretionary child-result bounds, independent of semantic validation and path authority.</summary>
public sealed record AgentResultLimits
{
    /// <summary>Whether positive result bounds are enforced; false disables every discretionary bound.</summary>
    public bool EnforceLimits { get; init; } = true;

    /// <summary>Maximum characters per finding detail; zero disables this bound.</summary>
    public int MaximumDetailCharacters { get; init; } = 4_096;

    /// <summary>Maximum items per metadata array; zero disables this bound.</summary>
    public int MaximumMetadataItems { get; init; } = 32;

    /// <summary>Maximum common findings for non-Explorer roles; zero disables this bound.</summary>
    public int MaximumFindings { get; init; } = 32;

    /// <summary>Maximum characters per repository-relative path; zero disables length limits, not path authorization.</summary>
    public int MaximumPathCharacters { get; init; } = 1_024;

    /// <summary>Maximum characters per model-authored category; zero disables this bound.</summary>
    public int MaximumCategoryCharacters { get; init; } = 128;

    /// <summary>Maximum JSON nesting depth; zero disables this bound.</summary>
    public int MaximumJsonDepth { get; init; } = 16;

    /// <summary>Rejects negative bounds, including when enforcement is disabled.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumDetailCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumMetadataItems);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumFindings);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPathCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumCategoryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumJsonDepth);
    }

    /// <summary>Resolves a configured bound to a positive maximum or null when it is disabled.</summary>
    public int? GetMaximum(int configuredMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(configuredMaximum);
        return EnforceLimits && configuredMaximum > 0 ? configuredMaximum : null;
    }

    /// <summary>Checks a measured size or count without imposing a bound when it is disabled.</summary>
    public bool Exceeds(long value, int configuredMaximum)
    {
        return GetMaximum(configuredMaximum) is { } maximum && value > maximum;
    }
}
