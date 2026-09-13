namespace Threadsmith.Models.Anthropic;

/// <summary>Application-owned Anthropic streaming and discovery limits, independent of model capabilities.</summary>
public sealed record AnthropicResourceLimits
{
    /// <summary>Maximum streamed tool-call identifier characters.</summary>
    public int MaximumToolCallIdCharacters { get; init; } = 256;

    /// <summary>Maximum decompressed bytes in one SSE frame.</summary>
    public int MaximumSseFrameBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum content blocks in a streamed response.</summary>
    public int MaximumContentBlocks { get; init; } = 4096;

    /// <summary>Maximum retained bytes in a tool-turn replay chain.</summary>
    public int MaximumReplayBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Maximum discovery and cached metadata bytes.</summary>
    public int MaximumMetadataBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum models and exact-model overrides.</summary>
    public int MaximumDiscoveredModels { get; init; } = 128;

    /// <summary>Maximum model discovery pages.</summary>
    public int MaximumDiscoveryPages { get; init; } = 10;

    /// <summary>Total model-discovery deadline in milliseconds.</summary>
    public int DiscoveryTimeoutMilliseconds { get; init; } = 15_000;

    /// <summary>Metadata cache freshness interval in seconds.</summary>
    public int MetadataCacheLifetimeSeconds { get; init; } = 86_400;

    /// <summary>Rejects invalid resource limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumToolCallIdCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSseFrameBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumContentBlocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumReplayBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMetadataBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiscoveredModels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiscoveryPages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DiscoveryTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MetadataCacheLifetimeSeconds);
    }
}
