namespace Threadsmith.Models.Anthropic;

using System.Text.Json;
using Threadsmith.Models;

/// <summary>Accumulates cumulative stream counters once and prices their normalized categories.</summary>
internal sealed class AnthropicUsageAccumulator
{
    private readonly AnthropicModelCompatibility _compatibility;
    private readonly int _estimatedInput;
    private long? _input;
    private long? _output;
    private long? _reasoning;
    private long? _writes;
    private long? _reads;
    private bool _hasFinalOutput;

    /// <summary>Initializes a new instance of the <see cref="AnthropicUsageAccumulator"/> class.</summary>
    internal AnthropicUsageAccumulator(ModelProfile profile, AnthropicModelCompatibility compatibility, int estimatedInput)
    {
        _compatibility = compatibility;
        _estimatedInput = estimatedInput;
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal bool HasReportedUsage => _input.HasValue || _output.HasValue || _writes.HasValue || _reads.HasValue;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal long? ReportedOutputTokens => _output;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal void Update(JsonElement usage, bool finalOutput = false)
    {
        if (usage.ValueKind != JsonValueKind.Object)
        {
            throw new MalformedModelOutputException("Anthropic usage must be a JSON object.");
        }

        UpdateCounter(usage, "input_tokens", ref _input);
        UpdateCounter(usage, "output_tokens", ref _output);
        UpdateCounter(usage, "cache_creation_input_tokens", ref _writes);
        UpdateCounter(usage, "cache_read_input_tokens", ref _reads);
        if (finalOutput && usage.TryGetProperty("output_tokens", out var output) && output.ValueKind == JsonValueKind.Number)
        {
            _hasFinalOutput = true;
            usage.TryGetProperty("output_tokens_details", out var details);
            _reasoning = ReasoningTokenUsage.Read(details, "thinking_tokens", _output.GetValueOrDefault());
        }
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal ModelUsage Snapshot(long estimatedOutput)
    {
        var input = _input ?? _estimatedInput;
        if ((_writes ?? 0) > long.MaxValue - input || (_reads ?? 0) > long.MaxValue - input - (_writes ?? 0))
        {
            throw new ModelProviderException("Anthropic usage counters overflowed the host accounting limit.");
        }

        input += (_writes ?? 0) + (_reads ?? 0);
        var output = _hasFinalOutput ? _output ?? estimatedOutput : Math.Max(_output ?? 0, estimatedOutput);
        var cache = new ModelCacheUsage
        {
            Availability = _writes.HasValue || _reads.HasValue ? CacheUsageAvailability.Reported : CacheUsageAvailability.Unavailable,
            CacheWriteTokens = _writes,
            CacheReadTokens = _reads,
            ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
            Provenance = "anthropic.messages.usage",
        };
        var usage = new ModelUsage(input, output, IsEstimate: !_input.HasValue || !_hasFinalOutput, Cache: cache)
        {
            ReasoningTokens = _hasFinalOutput ? _reasoning : null,
        };
        var prices = _compatibility.Prices;
        if (!prices.IsComplete)
        {
            throw new ModelProviderException("Anthropic usage cannot be priced without reviewed model rates.");
        }

        var uncachedRate = prices.InputPerMillionTokens.GetValueOrDefault();
        var outputRate = prices.OutputPerMillionTokens.GetValueOrDefault();
        var writeRate = prices.CacheWritePerMillionTokens.GetValueOrDefault();
        var readRate = prices.CacheReadPerMillionTokens.GetValueOrDefault();
        var cost = _writes is { } writes && _reads is { } reads && _input is { } uncached
            ? ((uncached * uncachedRate) + (writes * writeRate) + (reads * readRate) + (output * outputRate)) / 1000000m
            : ((Math.Max(input, _estimatedInput) * Math.Max(uncachedRate, _compatibility.PromptCachingEnabled ? writeRate : uncachedRate))
                + (output * outputRate)) / 1000000m;
        return usage with { EstimatedCost = cost };
    }

    /// <summary>Combines distinct attempts into the cumulative snapshot expected by request accounting.</summary>
    internal static ModelUsage CombineAttempts(ModelUsage? previous, ModelUsage current)
    {
        if (previous is null)
        {
            return current;
        }

        checked
        {
            var reads = AddReported(previous.Cache?.CacheReadTokens, current.Cache?.CacheReadTokens);
            var writes = AddReported(previous.Cache?.CacheWriteTokens, current.Cache?.CacheWriteTokens);
            return new ModelUsage(
                previous.InputTokens + current.InputTokens,
                previous.OutputTokens + current.OutputTokens,
                previous.EstimatedCost + current.EstimatedCost,
                previous.IsEstimate || current.IsEstimate,
                new ModelCacheUsage
                {
                    Availability = reads.HasValue || writes.HasValue ? CacheUsageAvailability.Reported : CacheUsageAvailability.Unavailable,
                    CacheReadTokens = reads,
                    CacheWriteTokens = writes,
                    ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
                    Provenance = "anthropic.messages.usage",
                })
            {
                ReasoningTokens = previous.ReasoningTokens + current.ReasoningTokens,
                MaximumAttemptOutputTokens = Math.Max(
                    previous.MaximumAttemptOutputTokens ?? previous.OutputTokens,
                    current.MaximumAttemptOutputTokens ?? current.OutputTokens),
            };
        }
    }

    private static long? AddReported(long? previous, long? current)
    {
        return previous.HasValue || current.HasValue ? checked(previous.GetValueOrDefault() + current.GetValueOrDefault()) : null;
    }

    private static void UpdateCounter(JsonElement usage, string name, ref long? current)
    {
        if (!usage.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0 || count < current)
        {
            throw new MalformedModelOutputException("Anthropic usage counters must be nonnegative and cumulative.");
        }

        current = count;
    }
}
