namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Agents;
using Threadsmith.Models;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using Xunit;

public static partial class AgentWorkspaceTests
{
    /// <summary>Stopped views capture final request metadata without waiting for a refresh tick.</summary>
    [Theory]
    [InlineData(false, AgentRunStatus.Completed)]
    [InlineData(true, AgentRunStatus.Completed)]
    [InlineData(false, AgentRunStatus.Failed)]
    [InlineData(false, AgentRunStatus.Cancelled)]
    [InlineData(false, AgentRunStatus.Discarded)]
    public static async Task TerminalChildRetainsFinalMetadataWithoutRefresh(bool initialRefresh, AgentRunStatus status)
    {
        // Arrange: stop only the periodic pump; explicit operations remain serialized and cancellable.
        var ct = TestContext.Current.CancellationToken;
        using var pumpStop = new CancellationTokenSource();
        await pumpStop.CancelAsync();
        var session = SessionId.New();
        var target = Target(session);
        var profile = new ModelProfile
        {
            Id = new ModelProfileId(Guid.NewGuid()),
            Name = "Final child model",
            Provider = "openai-compatible",
            ProviderName = "Child inference",
            Endpoint = new Uri("https://models.example/v1/chat/completions"),
            ModelId = "child",
            ContextWindow = 10000,
            MaximumOutputTokens = 1000,
            Capabilities = new ModelCapabilitySet { Streaming = true },
            Cost = new ModelCostMetadata(),
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 1, Delay = TimeSpan.Zero },
        };
        var usage = new SessionUsageProjection();
        var views = new AgentViews(_ => CellStyle.Default);
        var sink = new RecordingSurface { Views = views };
        await using var projection = new AgentWorkspaceProjection(sink, null, usage, new ConfiguredModelCatalog([profile]), new AgentNameCatalog(), false, pumpStop.Token);
        await projection.AttachAsync(session, ct);
        await projection.ObserveAsync(new DelegationCheckpointWritten(session, DateTimeOffset.UtcNow, target.DelegationId, RunId.New(), DelegationCheckpointPhase.Accepted, 1, "run"), ct);
        var lifecycle = new AgentRunLifecycleObserved(session, DateTimeOffset.UtcNow, target.DelegationId, target.AssignmentId, target.RunId, AgentRole.Explorer, AgentRunStatus.Running, 1, string.Empty);
        await projection.ObserveAsync(lifecycle, ct);
        if (initialRefresh)
        {
            usage.ObserveRequest(session, target.RunId, new AgentRequestStatus(profile.Id, ReasoningLevel.None, 500, 10000, 1));
            usage.Observe(session, new ModelRequestUsageId(target.RunId, "child", 0, Guid.NewGuid()), new ModelUsage(500, 20));
            await projection.RefreshAsync(ct);
        }

        usage.ObserveRequest(session, target.RunId, new AgentRequestStatus(profile.Id, ReasoningLevel.High, 2500, 10000, 2));
        usage.Observe(session, new ModelRequestUsageId(target.RunId, "child", 1, Guid.NewGuid()), new ModelUsage(1200, 80));
        var expectedUsage = usage.GetOwnerSnapshot(session, target.RunId);

        // Act: complete without a refresh after the final observations.
        await projection.ObserveAsync(lifecycle with { Status = status, Revision = 2 }, ct);

        // Assert at the awaited projection ownership boundary.
        var retained = views.Ordered.Single(view => view.Snapshot?.Target == target).Snapshot!;
        Assert.Equal(status, retained.State);
        Assert.Equal(profile.Name, retained.Model);
        Assert.Equal(profile.ProviderName, retained.ProviderName);
        Assert.Equal(ReasoningLevel.High, retained.Reasoning);
        Assert.Equal(2500, retained.ContextTokens);
        Assert.Equal(10000, retained.ContextLimit);
        Assert.Equal(expectedUsage, retained.Usage);
        Assert.True(retained.Usage!.HasObservation);
        Assert.NotNull(retained.Usage.LatestRequest);
        Assert.Equal(initialRefresh ? 1700 : 1200, retained.Usage.InputTokens);
        Assert.Equal(initialRefresh ? 100 : 80, retained.Usage.OutputTokens);
        Assert.Null(usage.GetRequestStatus(session, target.RunId));

        // An authoritative correction must not erase metadata after request status was retired.
        await projection.ObserveAsync(lifecycle with { Status = AgentRunStatus.Failed, Revision = 3 }, ct);
        var corrected = views.Ordered.Single(view => view.Snapshot?.Target == target).Snapshot!;
        Assert.Equal(retained with { State = AgentRunStatus.Failed, Revision = 3 }, corrected);
    }
}
