namespace Threadsmith.Models.Anthropic;

using System.Collections.ObjectModel;
using Threadsmith.Models;

/// <summary>Dated exact-ID policy; discovery alone decides which models are available.</summary>
public static class AnthropicReviewedModels
{
    /// <summary>Date on which the linked direct API documentation was reviewed.</summary>
    public const string SourceDate = "2026-09-10";

    /// <summary>Reviewed records, keyed by exact case-sensitive model identifier.</summary>
    public static IReadOnlyDictionary<string, AnthropicModelCompatibility> All { get; } = Create();

    private static IReadOnlyDictionary<string, AnthropicModelCompatibility> Create()
    {
        // Official source pages are deliberately recorded beside the facts they support.
        // https://platform.claude.com/docs/en/build-with-claude/effort
        // https://platform.claude.com/docs/en/build-with-claude/prompt-caching
        // https://platform.claude.com/docs/en/build-with-claude/structured-outputs
        // https://platform.claude.com/docs/en/about-claude/pricing
        AnthropicModelCompatibility[] models =
        [
            Adaptive("claude-opus-5", 5m, 25m, 0.5m, 512, true, ["low", "medium", "high", "xhigh", "max"]),
            Adaptive("claude-sonnet-5", 2m, 10m, 0.2m, 1024, true, ["low", "medium", "high", "xhigh", "max"]),
            Adaptive("claude-fable-5-1", 10m, 50m, 0.25m, 512, false, ["low", "medium", "high", "xhigh", "max"]),
            Adaptive("claude-opus-4-6", 5m, 25m, 0.5m, 4096, true, ["low", "medium", "high", "max"]),
            Adaptive("claude-sonnet-4-6", 3m, 15m, 0.3m, 1024, true, ["low", "medium", "high", "max"]),
            new AnthropicModelCompatibility
            {
                ModelId = "claude-haiku-4-5-20251001",
                SourceDate = SourceDate,
                SourceUrl = "https://platform.claude.com/docs/en/models/haiku-4-5/overview",
                MaximumInputTokens = 200000,
                MaximumOutputTokens = 64000,
                SupportsStreaming = true,
                SupportsToolCalls = true,
                SupportsStrictSchemas = true,
                PromptCachingEnabled = true,
                MinimumCacheableTokens = 4096,
                ThinkingMode = AnthropicThinkingMode.Manual,
                SupportsReasoningOff = true,
                ManualThinkingBudgets = new ReadOnlyDictionary<ReasoningLevel, int>(new Dictionary<ReasoningLevel, int>
                {
                    [ReasoningLevel.Low] = 1024,
                    [ReasoningLevel.Medium] = 2048,
                    [ReasoningLevel.High] = 4096,
                }),
                Prices = new AnthropicModelPrices
                {
                    InputPerMillionTokens = 1m,
                    OutputPerMillionTokens = 5m,
                    CacheWritePerMillionTokens = 1.25m,
                    CacheReadPerMillionTokens = 0.1m,
                },
            },
        ];
        return new ReadOnlyDictionary<string, AnthropicModelCompatibility>(models.ToDictionary(model => model.ModelId, StringComparer.Ordinal));
    }

    private static AnthropicModelCompatibility Adaptive(
        string id, decimal input, decimal output, decimal read, int threshold, bool supportsOff, string[] levels)
    {
        return new AnthropicModelCompatibility
        {
            ModelId = id,
            SourceDate = SourceDate,
            SourceUrl = "https://platform.claude.com/docs/en/models/" + id[7..] + "/overview",
            MaximumInputTokens = 1000000,
            MaximumOutputTokens = 128000,
            SupportsStreaming = true,
            SupportsToolCalls = true,
            SupportsStrictSchemas = true,
            PromptCachingEnabled = true,
            MinimumCacheableTokens = threshold,
            ThinkingMode = AnthropicThinkingMode.Adaptive,
            SupportsReasoningOff = supportsOff,
            AdaptiveEffortLevels = Array.AsReadOnly(levels.Select(level => new ReasoningLevel(level)).ToArray()),
            Prices = new AnthropicModelPrices
            {
                InputPerMillionTokens = input,
                OutputPerMillionTokens = output,
                CacheWritePerMillionTokens = input * 1.25m,
                CacheReadPerMillionTokens = read,
            },
        };
    }
}
