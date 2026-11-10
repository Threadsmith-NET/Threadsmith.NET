namespace Threadsmith.Models.OpenAiCompatible.OpenRouter;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Models;

/// <summary>Maps Responses usage without double billing reasoning or inventing cache counters.</summary>
internal static class OpenRouterResponsesUsage
{
    /// <summary>Normalizes one cumulative attempt snapshot.</summary>
    internal static ModelUsage Read(JsonObject report, ModelStreamRequest request, ModelProfile profile, long characters)
    {
        var input = Count(report, "input_tokens");
        var output = Count(report, "output_tokens");
        var inputDetails = report["input_tokens_details"] as JsonObject;
        var outputDetails = report["output_tokens_details"] as JsonObject;
        var reads = Count(inputDetails, "cached_tokens");
        var writes = Count(inputDetails, "cache_write_tokens");
        var reasoning = ReasoningTokenUsage.Read(JsonSerializer.SerializeToElement(outputDetails), "reasoning_tokens", output ?? long.MaxValue);
        var inputCount = input ?? Math.Max(EstimateInput(request, profile), checked((reads ?? 0) + (writes ?? 0)));
        var outputCount = output ?? Math.Max((characters + 3) / 4, reasoning ?? 0);
        if ((reads ?? 0) > inputCount || (writes ?? 0) > inputCount - (reads ?? 0))
        {
            throw new MalformedModelOutputException("OpenRouter cache counters exceed total input usage.");
        }

        var cache = reads is null && writes is null ? null : new ModelCacheUsage
        {
            Availability = CacheUsageAvailability.Reported,
            CacheReadTokens = reads,
            CacheWriteTokens = writes,
            ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
            Provenance = "openrouter:input_tokens_details",
        };
        var usage = new ModelUsage(inputCount, outputCount, IsEstimate: input is null || output is null, Cache: cache) { ReasoningTokens = reasoning };
        var conservative = profile.Cost.Calculate(Math.Max(inputCount, EstimateInput(request, profile)), Math.Max(outputCount, (characters + 3) / 4));
        usage = usage with { EstimatedCost = Math.Max(conservative, profile.Cost.CalculateUsage(usage)) };
        if (report["cost"] is { } charge)
        {
            if (charge is not JsonValue value || !value.TryGetValue<decimal>(out var cost) || cost < 0)
            {
                throw new MalformedModelOutputException("OpenRouter returned invalid charge metadata.");
            }

            usage = usage with { ReportedCost = cost, CostProvenance = "openrouter:usage.cost" };
        }

        return usage;
    }

    /// <summary>Marks missing provider totals as estimates rather than reported zeroes.</summary>
    internal static ModelUsage Estimate(ModelStreamRequest request, ModelProfile profile, long characters)
        => Read([], request, profile, characters);

    private static long EstimateInput(ModelStreamRequest request, ModelProfile profile)
        => request.WireEstimate?.WireInputTokens ?? ProviderResponseReplay.Estimate(request, profile).WireInputTokens;

    private static long? Count(JsonObject? node, string name)
    {
        if (node?[name] is null)
        {
            return null;
        }

        return node[name] is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0 ? count
            : throw new MalformedModelOutputException("OpenRouter returned invalid token usage.");
    }
}
