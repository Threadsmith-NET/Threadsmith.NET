namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Deterministic plan 113.1 request-occupancy and cumulative-usage baselines.</summary>
public static class Plan113AdmissionMeasurementTests
{
    /// <summary>Reported cache data and absent usage remain distinguishable in the same baseline corpus.</summary>
    [Fact]
    public static void Synthetic_baseline_preserves_cache_semantics_and_missing_usage()
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var usage = new SessionUsageProjection();
        usage.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "baseline", 0, Guid.NewGuid()),
            new ModelUsage(
                1_000,
                100,
                Cache: new ModelCacheUsage
                {
                    Availability = CacheUsageAvailability.Reported,
                    CacheReadTokens = 400,
                    CacheWriteTokens = 100,
                    ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
                }));
        usage.ObserveMissing(
            sessionId,
            new ModelRequestUsageId(runId, "baseline", 1, Guid.NewGuid()));

        var snapshot = usage.GetSnapshot(sessionId);

        Assert.Equal(1_000, snapshot.InputTokens);
        Assert.Equal(100, snapshot.OutputTokens);
        Assert.Equal(400, snapshot.CachedInputTokens);
        Assert.Equal(100, snapshot.CacheWriteTokens);
        Assert.True(snapshot.HasCacheObservation);
        Assert.True(snapshot.HasUnknownUsage);
    }

    private static ModelWireEstimate Estimate(
        IReadOnlyList<ModelMessage> messages,
        ModelToolDefinition tool)
    {
        return ModelWireEstimator.Estimate(
            messages,
            [tool],
            ToolTransportMode.Native,
            stablePrefixMessageCount: 0,
            outputReserveTokens: 128);
    }

    private static ModelMessage CreateToolResult(string id, string content)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = $"baseline:{id}",
            ToolCallId = id,
            ToolName = "run_process",
            Content = [new ModelContentPart { Content = content }],
        };
    }
}
