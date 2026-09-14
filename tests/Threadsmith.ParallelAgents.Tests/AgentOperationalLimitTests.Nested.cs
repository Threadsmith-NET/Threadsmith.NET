namespace Threadsmith.ParallelAgents.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

public sealed partial class AgentOperationalLimitTests
{
    /// <summary>Waiting ancestors yield capacity and resume under the same active-child ceiling.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Scheduler_NestedForkJoinYieldsCapacityAndReacquiresBeforeResuming(int maximumActive)
    {
        await using var scheduler = new AgentRunScheduler(new AgentSchedulerOptions
        {
            MaximumActiveChildren = maximumActive,
            MaximumActiveChildrenPerParent = maximumActive,
        });
        var activity = new NestedActivity();
        var runner = new NestedRunner(scheduler, activity, depth: 2);

        var outcomes = await scheduler.RunAsync(CreateResponsePlan(2), runner)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(outcomes, outcome => Assert.Equal(AgentRunStatus.Completed, outcome.Status));
        Assert.Equal(14, activity.Completed);
        Assert.InRange(activity.Maximum, 1, maximumActive);
        Assert.Equal(0, activity.Active);
    }

    /// <summary>Nested cancellation and elapsed deadlines leave permits available to subsequent work.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduler_NestedCancellationAndDeadlineReleaseCapacity(bool deadline)
    {
        await using var scheduler = new AgentRunScheduler(new AgentSchedulerOptions
        {
            MaximumActiveChildren = 1,
            MaximumActiveChildrenPerParent = 1,
        });
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new NestedWaitingRunner(scheduler, entered);
        var plan = CreateResponsePlan(1, deadline ? TimeSpan.FromSeconds(2) : TimeSpan.Zero);
        var running = scheduler.RunAsync(plan, runner, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!deadline)
        {
            await cancellation.CancelAsync();
        }

        var outcome = Assert.Single(await running.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(deadline ? AgentRunStatus.Failed : AgentRunStatus.Cancelled, outcome.Status);
        Assert.Equal(deadline ? "child timed out: assignment deadline elapsed" : "child cancellation observed", outcome.Reason);
        var retry = await scheduler.RunAsync(CreateResponsePlan(1), new NestedRunner(scheduler, new NestedActivity(), 0))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentRunStatus.Completed, Assert.Single(retry).Status);
    }

    private static DelegationPlan CreateResponsePlan(int count, TimeSpan? timeout = null)
    {
        var plan = CreatePlan(count, timeout);
        return plan with
        {
            Assignments = [.. plan.Assignments.Select(assignment => assignment with { OutputSchema = DelegateAgentsContract.ResponseSchema })],
        };
    }

    private static DelegationPlan CreateNestedPlan(DelegationPlan parent, AgentAssignment assignment, int count)
    {
        var nested = CreateResponsePlan(count);
        return nested with
        {
            Provenance = parent.Provenance with { ParentRunId = assignment.ChildRunId },
        };
    }

    private static AgentRunOutcome CompleteNestedAssignment(DelegationPlan plan, AgentAssignment assignment)
    {
        return new AgentRunOutcome
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Role = assignment.Role,
            Generation = plan.Provenance.Generation,
            Status = AgentRunStatus.Completed,
            Reason = "done",
            Response = "Inspected assigned source.",
            Usage = new AgentResourceUsage(),
        };
    }

    private sealed class NestedActivity
    {
        private readonly Lock _gate = new();

        public int Active { get; private set; }

        public int Maximum { get; private set; }

        public int Completed { get; private set; }

        public void Enter()
        {
            lock (_gate)
            {
                Active++;
                Maximum = Math.Max(Maximum, Active);
            }
        }

        public void Leave(bool completed = false)
        {
            lock (_gate)
            {
                Active--;
                if (completed)
                {
                    Completed++;
                }
            }
        }
    }

    private sealed class NestedRunner : IAgentAssignmentRunner
    {
        private readonly AgentRunScheduler _scheduler;
        private readonly NestedActivity _activity;
        private readonly int _depth;

        public NestedRunner(AgentRunScheduler scheduler, NestedActivity activity, int depth)
        {
            _scheduler = scheduler;
            _activity = activity;
            _depth = depth;
        }

        public async Task<AgentRunOutcome> RunAsync(DelegationPlan plan, AgentAssignment assignment, CancellationToken cancellationToken = default)
        {
            _activity.Enter();
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            if (_depth > 0)
            {
                _activity.Leave();
                var children = await _scheduler.RunAsync(
                    CreateNestedPlan(plan, assignment, 2),
                    new NestedRunner(_scheduler, _activity, _depth - 1),
                    cancellationToken);
                _activity.Enter();
                Assert.All(children, child => Assert.Equal(AgentRunStatus.Completed, child.Status));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            _activity.Leave(completed: true);
            return CompleteNestedAssignment(plan, assignment);
        }
    }

    private sealed class NestedWaitingRunner : IAgentAssignmentRunner
    {
        private readonly AgentRunScheduler _scheduler;
        private readonly TaskCompletionSource _entered;

        public NestedWaitingRunner(AgentRunScheduler scheduler, TaskCompletionSource entered)
        {
            _scheduler = scheduler;
            _entered = entered;
        }

        public async Task<AgentRunOutcome> RunAsync(DelegationPlan plan, AgentAssignment assignment, CancellationToken cancellationToken = default)
        {
            await _scheduler.RunAsync(CreateNestedPlan(plan, assignment, 1), new WaitingLeaf(_entered), cancellationToken);
            return CompleteNestedAssignment(plan, assignment);
        }
    }

    private sealed class WaitingLeaf : IAgentAssignmentRunner
    {
        private readonly TaskCompletionSource _entered;

        public WaitingLeaf(TaskCompletionSource entered)
        {
            _entered = entered;
        }

        public async Task<AgentRunOutcome> RunAsync(DelegationPlan plan, AgentAssignment assignment, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CompleteNestedAssignment(plan, assignment);
        }
    }
}
