namespace Threadsmith.Models.Anthropic;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Trusted configuration for one direct Anthropic API-key provider instance.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnthropicProviderConfiguration : ModelProviderConfiguration
{
    /// <summary>Configurable streaming, discovery, and cache resource limits.</summary>
    public AnthropicResourceLimits ResourceLimits { get; init; } = new();

    /// <summary>Exact-model trusted policy overrides applied during discovery hydration.</summary>
    public IReadOnlyList<AnthropicModelOverrideConfiguration> ModelOverrides { get; init; } = [];

    /// <summary>Trusted host request defaults applied after metadata eligibility is established.</summary>
    public AnthropicModelDefaults Defaults { get; init; } = new();

    /// <summary>Secret-free startup acquisition state; never configurable or persisted.</summary>
    [JsonIgnore]
    public AnthropicCatalogSnapshot? CatalogSnapshot { get; init; }
}

/// <summary>Trusted override for one exact Anthropic model identifier.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnthropicModelOverrideConfiguration
{
    /// <summary>Exact case-sensitive provider model identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>Optional display name used after metadata has been detached and validated.</summary>
    public string? Name { get; init; }

    /// <summary>Whether this discovered model remains selectable.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Optional reviewed compatibility policy. Omission requires a compiled exact-ID policy record.</summary>
    public AnthropicModelCompatibility? Compatibility { get; init; }

    /// <summary>Optional exact-model request defaults replacing the provider defaults.</summary>
    public AnthropicModelDefaults? Defaults { get; init; }
}

/// <summary>Fully hydrated Anthropic model metadata used only after trusted discovery completes.</summary>
public sealed record AnthropicModelConfiguration : ModelConfiguration
{
    /// <summary>Exact case-preserved Anthropic model identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>Compiled compatibility and price policy for this exact model identifier.</summary>
    public required AnthropicModelCompatibility Compatibility { get; init; }
}

/// <summary>Compiled thinking mode for an exact Anthropic model.</summary>
public enum AnthropicThinkingMode
{
    /// <summary>The model has no Anthropic thinking mode compatible with this adapter.</summary>
    Disabled,

    /// <summary>The model supports adaptive thinking and effort selection.</summary>
    Adaptive,

    /// <summary>The model uses reviewed manual thinking budgets.</summary>
    Manual,
}

/// <summary>Reviewed relationship between model input and output limits.</summary>
public enum AnthropicCombinedWindowRule
{
    /// <summary>Only the advertised input limit is authoritative for safe host admission.</summary>
    InputOnlyConservative,

    /// <summary>A reviewed model-specific combined context rule permits the configured window.</summary>
    Combined,
}

/// <summary>Exact per-million-token rates for a direct Anthropic API model.</summary>
public sealed record AnthropicModelPrices
{
    /// <summary>Gets the standard input price, when reviewed and available.</summary>
    public decimal? InputPerMillionTokens { get; init; }

    /// <summary>Gets the five-minute cache-write price, when reviewed and available.</summary>
    public decimal? CacheWritePerMillionTokens { get; init; }

    /// <summary>Gets the cache-read price, when reviewed and available.</summary>
    public decimal? CacheReadPerMillionTokens { get; init; }

    /// <summary>Gets the output price, when reviewed and available.</summary>
    public decimal? OutputPerMillionTokens { get; init; }

    /// <summary>Returns whether every direct-message price required for admission is known.</summary>
    public bool IsComplete => InputPerMillionTokens is >= 0
        && CacheWritePerMillionTokens is >= 0
        && CacheReadPerMillionTokens is >= 0
        && OutputPerMillionTokens is >= 0;
}

/// <summary>Adapter-owned compatibility facts for one exact Anthropic model identifier.</summary>
public sealed record AnthropicModelCompatibility
{
    /// <summary>Exact case-sensitive Anthropic model identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>ISO date of the exact-model capability and pricing review.</summary>
    public string SourceDate { get; init; } = string.Empty;

    /// <summary>Review source for exact-model capability and pricing facts.</summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>Reviewed input limit used only when the API omits the field.</summary>
    public int? MaximumInputTokens { get; init; }

    /// <summary>Reviewed output limit used only when the API omits the field.</summary>
    public int? MaximumOutputTokens { get; init; }

    /// <summary>Whether this exact model supports streamed text.</summary>
    public bool SupportsStreaming { get; init; }

    /// <summary>Whether this exact model supports native client tools.</summary>
    public bool SupportsToolCalls { get; init; }

    /// <summary>Reviewed permitted adaptive effort levels.</summary>
    public IReadOnlyList<ReasoningLevel> AdaptiveEffortLevels { get; init; } = [];

    /// <summary>Whether the reviewed strict-schema subset is supported.</summary>
    public bool SupportsStrictSchemas { get; init; }

    /// <summary>Whether explicit five-minute prompt caching is supported.</summary>
    public bool PromptCachingEnabled { get; init; }

    /// <summary>Minimum prompt length that may receive a cache breakpoint.</summary>
    public int? MinimumCacheableTokens { get; init; }

    /// <summary>Reviewed thinking protocol for this model.</summary>
    public AnthropicThinkingMode ThinkingMode { get; init; }

    /// <summary>Reviewed manual thinking budgets keyed by selected host effort.</summary>
    public IReadOnlyDictionary<ReasoningLevel, int> ManualThinkingBudgets { get; init; }
        = new Dictionary<ReasoningLevel, int>();

    /// <summary>Whether an explicit reasoning-off request is supported.</summary>
    public bool SupportsReasoningOff { get; init; }

    /// <summary>Reviewed input/output context-window rule.</summary>
    public AnthropicCombinedWindowRule CombinedWindowRule { get; init; }

    /// <summary>Direct API pricing captured from a dated reviewed source.</summary>
    public AnthropicModelPrices Prices { get; init; } = new();
}

/// <summary>Versioned deterministic identifiers for hydrated Anthropic profiles.</summary>
public static class AnthropicProfileIdentifiers
{
    /// <summary>Current derivation namespace marker.</summary>
    public const string Namespace = "threadsmith.anthropic.profile.v1";

    /// <summary>Derives a deterministic profile id from exact provider and model identities.</summary>
    public static ModelProfileId Create(string providerId, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var canonical = Namespace + "\n" + providerId.Trim().ToLowerInvariant() + "\n" + modelId;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, bytes.Length).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new ModelProfileId(new Guid(bytes, bigEndian: true));
    }
}

/// <summary>Trusted request policy for hydrated models, independently of hard API capabilities.</summary>
public sealed record AnthropicModelDefaults
{
    /// <summary>Per-request output reserve, validated rather than clamped.</summary>
    public int RequestOutputTokenReserve { get; init; } = 8192;

    /// <summary>Requested default effort; omission selects reviewed high when available.</summary>
    public ReasoningLevel? DefaultReasoningLevel { get; init; }

    /// <summary>Request deadline in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Maximum streamed bytes per request.</summary>
    public long MaximumStreamedBytes { get; init; } = ModelProfile.DefaultMaximumStreamedBytes;

    /// <summary>Maximum tool calls per response.</summary>
    public int MaximumToolCalls { get; init; } = ModelProfile.DefaultMaximumToolCalls;

    /// <summary>Maximum total attempts.</summary>
    public int RetryMaxAttempts { get; init; } = 3;

    /// <summary>Retry delay in milliseconds.</summary>
    public int RetryDelayMilliseconds { get; init; } = 250;

    /// <summary>Whether classified sensitive input is permitted.</summary>
    public ModelSensitiveDataPolicy SensitiveDataPolicy { get; init; } = ModelSensitiveDataPolicy.Allowed;

    /// <summary>Optional eligible workload restriction.</summary>
    public IReadOnlyList<WorkloadClass> IntendedWorkloadClasses { get; init; } = [];
}
