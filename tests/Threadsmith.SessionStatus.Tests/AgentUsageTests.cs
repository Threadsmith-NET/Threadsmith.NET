namespace Threadsmith.SessionStatus.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

/// <summary>Regression checks for agent usage tests.</summary>
public static class AgentUsageTests
{
    /// <summary>Latest per-request counters are isolated by owner and missing reports never reuse earlier cache observations.</summary>
    [Fact]
    public static void LatestRequestRetainsIdentityAndMissingCounters()
    {
        var projection = new SessionUsageProjection();
        var session = SessionId.New();
        var root = RunId.New();
        var child = RunId.New();
        projection.RegisterChild(session, child);
        var rootId = new ModelRequestUsageId(root, "conversation", 0, Guid.NewGuid());
        var childId = new ModelRequestUsageId(child, "delegate-agent", 0, Guid.NewGuid());
        var reported = new ModelUsage(1000, 20, Cache: new ModelCacheUsage
        {
            Availability = CacheUsageAvailability.Reported, CacheReadTokens = 800, CacheWriteTokens = 200, ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
        });
        projection.Observe(session, rootId, reported);
        projection.Observe(session, childId, reported with { InputTokens = 2000 });
        Assert.Equal(80, projection.GetOwnerSnapshot(session).LatestRequest?.CacheHitPercentage);
        Assert.Equal(40, projection.GetOwnerSnapshot(session, child).LatestRequest?.CacheHitPercentage);
        Assert.Equal(rootId, projection.GetOwnerSnapshot(session).LatestRequest?.RequestId);
        var nextId = rootId with { Round = 1, InvocationId = Guid.NewGuid() };
        projection.Observe(session, nextId, new ModelUsage(1200, 5));
        Assert.Null(projection.GetOwnerSnapshot(session).LatestRequest?.CacheHitPercentage);
        Assert.Equal(800, projection.GetOwnerSnapshot(session).CachedInputTokens);
        projection.ObserveMissing(session, nextId);
        Assert.Null(projection.GetOwnerSnapshot(session).LatestRequest?.Usage);
        projection.Observe(session, nextId, reported);
        projection.Observe(session, nextId, reported);
        Assert.Equal(2000, projection.GetOwnerSnapshot(session).InputTokens);
        Assert.Equal(1600, projection.GetOwnerSnapshot(session).CachedInputTokens);
        projection.Restore(session, projection.GetDurableSnapshot(session));
        Assert.Null(projection.GetOwnerSnapshot(session).LatestRequest);
    }

    /// <summary>Percentage uses the provider's declared input semantics, without double-counting cache writes or guessing missing reads.</summary>
    [Theory]
    [InlineData(1000L, 800L, CacheReadInputSemantics.IncludedInInput, false, 80.0)]
    [InlineData(200L, 800L, CacheReadInputSemantics.AdditionalToInput, false, 80.0)]
    [InlineData(1000L, 0L, CacheReadInputSemantics.IncludedInInput, false, 0.0)]
    [InlineData(0L, 0L, CacheReadInputSemantics.IncludedInInput, false, null)]
    [InlineData(1000L, null, CacheReadInputSemantics.IncludedInInput, false, null)]
    [InlineData(1000L, 800L, CacheReadInputSemantics.Unknown, false, null)]
    [InlineData(1000L, 800L, CacheReadInputSemantics.IncludedInInput, true, null)]
    [InlineData(100L, 800L, CacheReadInputSemantics.IncludedInInput, false, null)]
    public static void RequestCachePercentagePreservesSemantics(long input, long? reads, CacheReadInputSemantics semantics, bool estimate, double? expected)
    {
        var request = new ModelRequestUsageSnapshot(new(RunId.New(), "test", 0, Guid.NewGuid()), new ModelUsage(input, 1, IsEstimate: estimate, Cache: new ModelCacheUsage
        {
            Availability = CacheUsageAvailability.Reported, CacheReadTokens = reads, CacheWriteTokens = 100, ReadInputSemantics = semantics,
        }));
        Assert.Equal(expected, request.CacheHitPercentage);
    }

    /// <summary>Verifies root and children reuse deduplicated usage without changing session totals.</summary>
    [Fact]
    public static void RootAndChildrenReuseDeduplicatedUsageWithoutChangingSessionTotals()
    {
        var usage = new SessionUsageProjection();
        var session = SessionId.New();
        var root = RunId.New();
        var child = RunId.New();
        usage.RegisterChild(session, child);
        var childRequest = new ModelRequestUsageId(child, "delegate-agent", 0, Guid.NewGuid());
        usage.Observe(session, new(root, "mutation", 0, Guid.NewGuid()), new ModelUsage(10, 2));
        usage.Observe(session, childRequest, new ModelUsage(5, 1));
        usage.Observe(session, childRequest, new ModelUsage(7, 3));
        usage.ObserveMissing(session, new(child, "compaction", 0, Guid.NewGuid()));
        Assert.Equal(10, usage.GetOwnerSnapshot(session).InputTokens);
        Assert.Equal(7, usage.GetOwnerSnapshot(session, child).InputTokens);
        Assert.True(usage.GetOwnerSnapshot(session, child).HasUnknownUsage);
        Assert.Equal(17, usage.GetSnapshot(session).InputTokens);
        Assert.Equal(5, usage.GetSnapshot(session).OutputTokens);
        usage.RetireRequestStatus(session, child);
        Assert.Equal(17, usage.GetSnapshot(session).InputTokens);
        Assert.Equal(10, usage.GetOwnerSnapshot(session).InputTokens);
    }

    /// <summary>Verifies context reflects latest request and explicit owner.</summary>
    [Fact]
    public static void ContextReflectsLatestRequestAndExplicitOwner()
    {
        var usage = new SessionUsageProjection();
        var session = SessionId.New();
        var root = RunId.New();
        var child = RunId.New();
        usage.RegisterChild(session, child);
        usage.ObserveRequest(session, root, new(null, ReasoningLevel.Low, 1000, 8000, 1));
        usage.ObserveRequest(session, child, new(null, ReasoningLevel.High, 7000, 16000, 2));
        usage.ObserveRequest(session, root, new(null, ReasoningLevel.Medium, 200, 8000, 3));
        Assert.Equal(200, usage.GetRequestStatus(session)!.ContextTokens);
        Assert.Equal(7000, usage.GetRequestStatus(session, child)!.ContextTokens);
        Assert.False(usage.GetSnapshot(session).HasObservation);
    }
}
