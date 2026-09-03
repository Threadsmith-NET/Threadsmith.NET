namespace Threadsmith.ParallelAgents.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies Plan 96 idempotent safe-boundary steering coordination.</summary>
public static class Plan96RunSteeringTests
{
    /// <summary>Repeated Enter requests reuse one pause and one submitted message.</summary>
    [Fact]
    public static async Task RequestPause_RepeatedRequest_IsIdempotentAcrossChildBarrier()
    {
        var coordinator = new RunSteeringCoordinator();
        var sessionId = SessionId.New();
        var parentRunId = RunId.New();
        var firstChildRunId = RunId.New();
        var secondChildRunId = RunId.New();
        var delegationId = DelegationId.New();
        coordinator.RegisterRun(sessionId, parentRunId);
        coordinator.RegisterDelegation(
            sessionId,
            parentRunId,
            delegationId,
            [firstChildRunId, secondChildRunId]);

        var firstRequest = coordinator.RequestPause(sessionId, parentRunId);
        var repeatedRequest = coordinator.RequestPause(sessionId, parentRunId);
        var firstChildBoundary = coordinator.PauseChildAtBoundaryAsync(
            sessionId,
            parentRunId,
            firstChildRunId,
            CancellationToken.None);
        coordinator.CompleteChild(sessionId, parentRunId, secondChildRunId);
        var ready = await coordinator.WaitForPauseAsync(
            sessionId,
            parentRunId,
            firstRequest.PauseId);

        Assert.Equal(RunSteeringPauseRequestStatus.Accepted, firstRequest.Status);
        Assert.Equal(RunSteeringPauseRequestStatus.AlreadyPending, repeatedRequest.Status);
        Assert.Equal(firstRequest.PauseId, repeatedRequest.PauseId);
        Assert.Equal(RunSteeringPauseWaitStatus.Ready, ready.Status);

        var submitted = coordinator.Submit(
            sessionId,
            parentRunId,
            firstRequest.PauseId,
            "inspect the parser boundary");
        var delivered = await firstChildBoundary;
        var summary = coordinator.GetDelegationSummary(sessionId, parentRunId, delegationId);

        Assert.Equal(RunSteeringSubmissionStatus.Accepted, submitted.Status);
        Assert.Equal(1, submitted.Sequence);
        Assert.Single(delivered);
        Assert.Equal("inspect the parser boundary", delivered[0].Text);
        Assert.Equal(new DelegationSteeringSummary(1, 1, 1), summary);

        coordinator.CompleteDelegation(sessionId, parentRunId, delegationId);
        coordinator.CompleteRun(sessionId, parentRunId);
    }

    /// <summary>An empty steering composer dismisses the same pause without adding context.</summary>
    [Fact]
    public static async Task Submit_EmptyText_ReleasesParentWithoutMessage()
    {
        var coordinator = new RunSteeringCoordinator();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        coordinator.RegisterRun(sessionId, runId);
        var request = coordinator.RequestPause(sessionId, runId);
        var boundary = coordinator.PauseParentAtBoundaryAsync(
            sessionId,
            runId,
            CancellationToken.None);

        var ready = await coordinator.WaitForPauseAsync(
            sessionId,
            runId,
            request.PauseId);
        var dismissed = coordinator.Submit(sessionId, runId, request.PauseId, string.Empty);
        var messages = await boundary;

        Assert.Equal(RunSteeringPauseWaitStatus.Ready, ready.Status);
        Assert.Equal(RunSteeringSubmissionStatus.Dismissed, dismissed.Status);
        Assert.Null(dismissed.Sequence);
        Assert.Empty(messages);

        coordinator.CompleteRun(sessionId, runId);
    }
}
