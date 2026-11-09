namespace Threadsmith.ParallelAgents.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

public sealed partial class DelegateAgentsToolExecutionTests
{
    /// <summary>Caller cancellation interrupts queued publication before children start and preserves terminal cleanup.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_CancellationDuringQueuedPublication_PersistsCancelledChildren(bool blockLifecycle)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCancellation = new ManualResetEventSlim();
        CancellationTokenRegistration cancellationBarrier = default;
        await using var events = new DomainEventStream();
        await using var subscription = events.Subscribe((domainEvent, deliveryToken) =>
        {
            var shouldBlock = blockLifecycle
                ? domainEvent is AgentRunLifecycleObserved { Status: AgentRunStatus.Queued }
                : domainEvent is DelegationCheckpointWritten { Phase: DelegationCheckpointPhase.ChildrenQueued };
            if (!shouldBlock)
            {
                return Task.CompletedTask;
            }

            // Cancellation callbacks run in reverse registration order. Hold the coordinator's
            // earlier callback back while the publication's later WaitAsync observes cancellation.
            cancellationBarrier = deliveryToken.Register(() =>
            {
                cancellationEntered.TrySetResult();
                releaseCancellation.Wait(ct);
            });
            entered.TrySetResult(deliveryToken);
            return release.Task.WaitAsync(deliveryToken);
        });
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var runner = new QueuedPublicationCancellationRunner();
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));
        var plan = fixture.Plans.Create(CreateInput(2), fixture.Context);

        // Act
        var running = coordinator.StartAsync(plan, runner, cancellation.Token);
        var cancelling = Task.CompletedTask;
        try
        {
            var deliveryToken = await entered.Task.WaitAsync(ct);
            // Fail deterministically for the regression before waiting on cancellation.
            Assert.Equal(cancellation.Token, deliveryToken);
            cancelling = cancellation.CancelAsync();
            await cancellationEntered.Task.WaitAsync(ct);
            var terminal = await running.WaitAsync(ct);

            // Assert
            Assert.False(release.Task.IsCompleted);
            Assert.Equal(0, runner.Calls);
            Assert.Equal(DelegationCheckpointPhase.Cancelled, terminal.Phase);
            Assert.Equal(plan.Assignments.Select(assignment => assignment.AssignmentId), terminal.ChildOutcomes.Select(outcome => outcome.AssignmentId));
            Assert.All(terminal.ChildOutcomes, outcome => Assert.Equal(AgentRunStatus.Cancelled, outcome.Status));
            Assert.DoesNotContain(checkpoints.History, checkpoint => checkpoint.Phase == DelegationCheckpointPhase.ChildrenRunning);
            Assert.Equal(DelegationCheckpointPhase.Cancelled, checkpoints.History[^1].Phase);
            Assert.False(await coordinator.CancelAsync(plan.DelegationId, ct));
        }
        finally
        {
            // Always release and join, including when the token assertion exposes the regression.
            releaseCancellation.Set();
            await cancelling;
            await cancellation.CancelAsync();
            await cancellationBarrier.DisposeAsync();
            release.TrySetResult();
            await running.WaitAsync(ct);
        }
    }

    private sealed class QueuedPublicationCancellationRunner : IAgentAssignmentRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<AgentRunOutcome> RunAsync(DelegationPlan plan, AgentAssignment assignment, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("Children must not start while queued publication is blocked.");
        }
    }
}
