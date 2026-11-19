namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Hosting;
using TUIKit.Modals;
using TUIKit.Terminal;
using Xunit;

/// <summary>Checks external-refresh notification routing, terminal behavior, and native toast rendering.</summary>
[Collection("TUIKit terminal")]
public static class SemanticRefreshNotificationTests
{
    /// <summary>Session restoration observes prior refresh events before later terminal events.</summary>
    [Fact]
    public static async Task RefreshRestorationIsOrderedWithLifecycleDelivery()
    {
        var dispatcher = new InteractionEventDispatcher();
        var activities = new InteractionOperationActivities(TimeProvider.System, true);
        var started = new SemanticRefreshStarted(SessionId.New(), DateTimeOffset.UtcNow, SemanticRefreshId.New(), WorkspaceId.New(), SemanticRefreshReason.ExternalChange, SemanticRefreshMode.Full, 1, 1);
        await dispatcher.QueueAsync(started, TestContext.Current.CancellationToken);
        await dispatcher.QueueWorkAsync(
            _ =>
        {
            Assert.Single(activities.Activities);
            return Task.CompletedTask;
        },
            TestContext.Current.CancellationToken);
        await dispatcher.QueueAsync(new SemanticRefreshCompleted(SessionId.New(), DateTimeOffset.UtcNow, started.RefreshId, started.WorkspaceId, started.Reason, started.Mode, 1, 1, 1, SemanticConfidenceLevel.PartialCompilation, 240), TestContext.Current.CancellationToken);
        dispatcher.Complete();
        await dispatcher.DrainAsync(
            (batch, _) =>
        {
            foreach (var item in batch)
            {
                activities.Observe(item);
            }

            return Task.CompletedTask;
        },
            TestContext.Current.CancellationToken);
        Assert.Empty(activities.Activities);
    }

    /// <summary>A workspace refresh persists independently of conversation output and run lifecycles.</summary>
    [Fact]
    public static void RefreshActivityPersistsUntilMatchingWorkspaceCompletion()
    {
        var started = new SemanticRefreshStarted(SessionId.New(), DateTimeOffset.UtcNow, SemanticRefreshId.New(), WorkspaceId.New(), SemanticRefreshReason.ExternalChange, SemanticRefreshMode.Full, 1, 1);
        var activities = new InteractionOperationActivities(TimeProvider.System, false);
        Assert.True(activities.Observe(started));
        var activity = Assert.Single(activities.Activities);
        Assert.Equal(started.WorkspaceId, activity.SemanticWorkspaceId);
        Assert.Contains("blocked", activity.Label, StringComparison.Ordinal);
        Assert.False(activities.Observe(started));
        Assert.False(activities.Observe(new ModelOutputObserved(SessionId.New(), DateTimeOffset.UtcNow, "Other session output")));
        Assert.Same(activity, Assert.Single(activities.Activities));
        Assert.True(activities.Observe(new SemanticRefreshCompleted(SessionId.New(), DateTimeOffset.UtcNow, started.RefreshId, started.WorkspaceId, started.Reason, started.Mode, 1, 1, 1, SemanticConfidenceLevel.PartialCompilation, 240)));
        Assert.Empty(activities.Activities);
    }

    /// <summary>Only external changes and recovery produce transient echoes, regardless of transcript wording.</summary>
    [Theory]
    [InlineData(SemanticRefreshReason.ExternalChange, true)]
    [InlineData(SemanticRefreshReason.Recovery, true)]
    [InlineData(SemanticRefreshReason.HostMutation, false)]
    [InlineData(SemanticRefreshReason.Manual, false)]
    [InlineData(SemanticRefreshReason.UserAdmission, false)]
    public static void RefreshAttributionControlsNotifications(SemanticRefreshReason reason, bool visible)
    {
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        var refresh = SemanticRefreshId.New();
        var now = DateTimeOffset.UtcNow;
        IDomainEvent[] events =
        [
            new SemanticRefreshStarted(session, now, refresh, workspace, reason, SemanticRefreshMode.Incremental, 1, 2),
            new SemanticRefreshCompleted(session, now, refresh, workspace, reason, SemanticRefreshMode.Incremental, 1, 2, 2, SemanticConfidenceLevel.FullSemantic, 240),
            new SemanticRefreshFailed(session, now, refresh, workspace, reason, SemanticRefreshMode.Full, 1, 2, 1, SemanticRefreshFailureKind.Infrastructure, "diagnostic details", 240),
        ];

        var notifications = events.Select(InteractionEventSegments.CreateNotification).ToArray();

        if (visible)
        {
            Assert.Equal(
                [PresentationTextRole.Status, PresentationTextRole.Success, PresentationTextRole.Error],
                notifications.Select(notification => Assert.IsType<PresentationNotification>(notification).Role));
            Assert.All(notifications, notification => Assert.InRange(notification!.Text.Length, 1, 38));
        }
        else
        {
            Assert.All(notifications, Assert.Null);
        }

        Assert.Null(InteractionEventSegments.CreateNotification(new ModelOutputObserved(session, now, "External changes; refreshing semantics")));
    }
}
