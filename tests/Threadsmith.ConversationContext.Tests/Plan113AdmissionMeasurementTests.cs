namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Deterministic plan 113.1 request-occupancy and cumulative-usage baselines.</summary>
public static class Plan113AdmissionMeasurementTests
{
    /// <summary>Four fixed workloads keep request occupancy, replay, schema, cache, and missing usage distinct.</summary>
    [Fact]
    public static void Synthetic_baselines_separate_request_size_from_cumulative_usage()
    {
        const string projectedValidation = "{\"status\":\"passed\",\"tests\":120}";
        var opaqueProcessResult = new string('p', 2_000);
        var tool = new ModelToolDefinition
        {
            Name = "run_process",
            Description = "Runs one bounded validation command.",
            ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}}}",
        };
        var projectedMessage = CreateToolResult("native-validation", projectedValidation);
        var opaqueMessage = CreateToolResult("opaque-process", opaqueProcessResult);
        var overlapping = Estimate(
            [projectedMessage, opaqueMessage, CreateToolResult("overlap", opaqueProcessResult)],
            tool);
        var unique = Estimate(
            [projectedMessage, opaqueMessage, CreateToolResult("unique", new string('u', 6_000))],
            tool);
        var repeatedPrefixes = Enumerable.Range(1, 3)
            .Select(count => Estimate(
                Enumerable.Range(1, count)
                    .Select(index => CreateToolResult($"opaque-{index}", opaqueProcessResult))
                    .ToArray(),
                tool))
            .ToArray();
        var retry = Estimate([projectedMessage, opaqueMessage], tool);
        var rows = new[]
        {
            new BaselineRow("overlapping-exploration", overlapping.WireInputTokens, overlapping.WireInputTokens, 1),
            new BaselineRow("mostly-unique-evidence", unique.WireInputTokens, unique.WireInputTokens, 1),
            new BaselineRow(
                "moderate-context-replay",
                repeatedPrefixes[^1].WireInputTokens,
                repeatedPrefixes.Sum(item => (long)item.WireInputTokens),
                repeatedPrefixes.Length),
            new BaselineRow("failed-summary-retry", retry.WireInputTokens, retry.WireInputTokens * 2L, 2),
        };

        Assert.Equal(4, rows.Length);
        Assert.True(unique.WireInputTokens > overlapping.WireInputTokens);
        Assert.True(rows[2].CumulativeInputTokens > rows[2].LatestRequestInputTokens);
        Assert.Equal(rows[3].LatestRequestInputTokens * 2, rows[3].CumulativeInputTokens);
        Assert.All(
            [overlapping, unique, .. repeatedPrefixes, retry],
            estimate =>
            {
                Assert.True(estimate.NativeToolTokens > 0);
                Assert.True(estimate.FramingTokens > 0);
                Assert.Equal(128, estimate.OutputReserveTokens);
            });
        Assert.Equal(projectedValidation, projectedMessage.GetModelVisibleContent());
        Assert.Equal(opaqueProcessResult, opaqueMessage.GetModelVisibleContent());
        Assert.Equal(
            [
                "overlapping-exploration",
                "mostly-unique-evidence",
                "moderate-context-replay",
                "failed-summary-retry",
            ],
            rows.Select(row => row.Name));
    }

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

    private sealed record BaselineRow(
        string Name,
        int LatestRequestInputTokens,
        long CumulativeInputTokens,
        int Calls);
}
