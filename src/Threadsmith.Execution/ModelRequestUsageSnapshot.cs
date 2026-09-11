namespace Threadsmith.Execution;

using Threadsmith.Models;

/// <summary>One observed request's usage, retaining missing counters and provider input semantics.</summary>
public sealed record ModelRequestUsageSnapshot(ModelRequestUsageId RequestId, ModelUsage? Usage)
{
    /// <summary>Gets cache reads as a percentage of total request input, only when both are known.</summary>
    public double? CacheHitPercentage
    {
        get
        {
            if (Usage is not { IsEstimate: false, Cache.Availability: CacheUsageAvailability.Reported } usage
                || usage.Cache.CacheReadTokens is not { } reads)
            {
                return null;
            }

            var input = usage.Cache.ReadInputSemantics switch
            {
                CacheReadInputSemantics.IncludedInInput => (double)usage.InputTokens,
                CacheReadInputSemantics.AdditionalToInput => (double)usage.InputTokens + reads,
                _ => 0,
            };
            return input > 0 && reads <= input ? 100.0 * reads / input : null;
        }
    }
}
