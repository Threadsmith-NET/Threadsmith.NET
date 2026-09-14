namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Models;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies visible ownership before manual skill workflows reach a provider.</summary>
public static partial class Milestone1Tests
{
    /// <summary>Use, continue, and resume show one live block until success or failure without duplicating a starting receipt.</summary>
    [Theory]
    [InlineData("use", false)]
    [InlineData("use", true)]
    [InlineData("continue", false)]
    [InlineData("continue", true)]
    [InlineData("resume", false)]
    [InlineData("resume", true)]
    public static async Task SkillInvocationProgress_ShowsOneLiveBlockUntilOutcome(string operation, bool fail)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var id = SkillInvocationId.New();
        var command = operation switch
        {
            "use" => "/skills use Maintained:review@1.0.0 {}",
            "continue" => $"/skills continue {id.Value:D} {{}}",
            _ => $"/skills resume {id.Value:D}",
        };
        var surface = new SkillProgressSurface([command, "/quit"]);
        var handler = new PendingSkillHandler
        {
            Fail = fail,
            OnDispatch = invocation => id = invocation,
        };
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [handler]);
        handler.Events = harness.EventStream;
        handler.Session = () => harness.Events.OfType<SessionCreated>().Single().SessionId;
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        var run = coordinator.RunAsync(cancellationToken: timeout.Token);
        await handler.Started.Task.WaitAsync(timeout.Token);
        await surface.Started.Task.WaitAsync(timeout.Token);

        Assert.False(run.IsCompleted);
        Assert.True(surface.Active);
        Assert.True(surface.Activity!.ShowDuration);
        Assert.StartsWith("SKILLS: ", surface.Activity.Label, StringComparison.Ordinal);
        Assert.Equal($"Invocation: {id.Value:D}", surface.Activity.ToolDetail);
        Assert.DoesNotContain("SKILLS:", surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Completed;", surface.Text, StringComparison.Ordinal);
        handler.Release.SetResult();
        await run.WaitAsync(timeout.Token);

        Assert.False(surface.Active);
        Assert.Contains(fail ? "Skill command failed: acquisition failed" : "Completed;", surface.Text, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(" - starting", surface.Text, StringComparison.Ordinal);
        Assert.Equal(1, surface.Text.Split("SKILLS:", StringSplitOptions.None).Length - 1);
    }

    /// <summary>The legacy frontend remains free to show host prompts during a running skill.</summary>
    [Fact]
    public static async Task SkillInvocationProgress_LegacyFrontendDoesNotHoldActivityAcrossHostInput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var surface = new LegacySkillProgressSurface(["/skills use Maintained:review@1.0.0 {}", "/quit"]);
        var handler = new PendingSkillHandler
        {
            DuringWork = token => surface.PresentAsync(new PresentationBatch([new PresentationTextItem([new("Host input requested", PresentationTextRole.Status)])]), token),
        };
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [handler]);
        handler.Events = harness.EventStream;
        handler.Session = () => harness.Events.OfType<SessionCreated>().Single().SessionId;
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        var run = coordinator.RunAsync(cancellationToken: timeout.Token);
        await handler.Started.Task.WaitAsync(timeout.Token);
        handler.Release.SetResult();
        await run.WaitAsync(timeout.Token);

        var output = string.Concat(surface.Batches.SelectMany(batch => batch.Items).OfType<PresentationTextItem>()
            .SelectMany(item => item.Segments).Select(segment => segment.Text));
        Assert.DoesNotContain(" - starting", output, StringComparison.Ordinal);
        Assert.Contains("Host input requested", output, StringComparison.Ordinal);
        Assert.Contains("SKILLS: review@1.0.0 - completed", output, StringComparison.Ordinal);
    }

    /// <summary>Stopping the display cannot abandon the invocation's cancellation checkpoint cleanup.</summary>
    [Fact]
    public static async Task SkillInvocationProgress_CancellationJoinsWorkflowCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        var surface = new SkillProgressSurface(["/skills use Maintained:review@1.0.0 {}", "/quit"]);
        var handler = new PendingSkillHandler { HoldCancellationCleanup = true };
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [handler]);
        handler.Events = harness.EventStream;
        handler.Session = () => harness.Events.OfType<SessionCreated>().Single().SessionId;
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        var run = coordinator.RunAsync(cancellationToken: cancelled.Token);
        await surface.Started.Task.WaitAsync(timeout.Token);
        await handler.Started.Task.WaitAsync(timeout.Token);
        await cancelled.CancelAsync();
        await handler.CleanupStarted.Task.WaitAsync(timeout.Token);
        Assert.False(run.IsCompleted);
        handler.CleanupRelease.SetResult();
        try
        {
            await run.WaitAsync(timeout.Token);
            Assert.Fail("Expected session cancellation.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested && !timeout.IsCancellationRequested)
        {
            Assert.True(handler.CleanupFinished);
        }
    }

    private sealed class LegacySkillProgressSurface : RecordingInteractionSurface, IInteractionSurface
    {
        internal LegacySkillProgressSurface(IEnumerable<string> inputs)
            : base(inputs)
        {
        }

        public new Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default) =>
            operation.WaitAsync(cancellationToken);
    }

    private sealed class SkillProgressSurface : RecordingInteractionSurface, IInteractionSurface, IInteractionToolActivitySurface
    {
        internal SkillProgressSurface(IEnumerable<string> inputs)
            : base(inputs)
        {
        }

        public new InteractionSurfaceCapabilities Capabilities { get; } = new(SupportsRetainedActivity: true);

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Active { get; private set; }

        internal InteractionActivity? Activity { get; private set; }

        internal string Text => string.Concat(Batches.SelectMany(batch => batch.Items).OfType<PresentationTextItem>()
            .SelectMany(item => item.Segments).Select(segment => segment.Text));

        public Task PresentToolActivitiesAsync(IReadOnlyList<InteractionActivity> activities, CancellationToken cancellationToken = default)
        {
            Activity = activities.SingleOrDefault(item => item.Label.StartsWith("SKILLS:", StringComparison.Ordinal));
            Active = Activity is not null;
            if (Active)
            {
                Started.TrySetResult();
            }
            else
            {
                Stopped.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class PendingSkillHandler :
        ICommandHandler<InvokeSkillCommand, SkillInvocationResult>,
        ICommandHandler<ContinueSkillCommand, SkillInvocationResult>,
        ICommandHandler<ResumeSkillCommand, SkillInvocationResult>
    {
        internal IDomainEventStream Events { get; set; } = null!;

        internal Func<SessionId> Session { get; set; } = null!;

        internal bool Fail { get; init; }

        internal bool HoldCancellationCleanup { get; init; }

        internal bool CleanupFinished { get; private set; }

        internal TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Action<SkillInvocationId>? OnDispatch { get; init; }

        internal Func<CancellationToken, Task>? DuringWork { get; init; }

        internal int Calls { get; private set; }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SkillInvocationResult> HandleAsync(InvokeSkillCommand command, CancellationToken cancellationToken = default) =>
            CompleteAsync(command.Request.InvocationId, cancellationToken);

        public Task<SkillInvocationResult> HandleAsync(ContinueSkillCommand command, CancellationToken cancellationToken = default) =>
            CompleteAsync(command.InvocationId, cancellationToken);

        public Task<SkillInvocationResult> HandleAsync(ResumeSkillCommand command, CancellationToken cancellationToken = default) =>
            CompleteAsync(command.InvocationId, cancellationToken);

        private async Task<SkillInvocationResult> CompleteAsync(SkillInvocationId id, CancellationToken cancellationToken)
        {
            Calls++;
            OnDispatch?.Invoke(id);
            var started = new SkillWorkflowCheckpointWritten(Session(), DateTimeOffset.UtcNow, id, SkillWorkflowId.New(), new SkillId("review"), "1.0.0", new string('a', 64), SkillInvocationStatus.Running, 0, "Execute") { RunId = RunId.New() };
            await Events.PublishAsync(started, cancellationToken);
            await Events.PublishAsync(new SkillInvocationProgressObserved(started.SessionId, DateTimeOffset.UtcNow, id, 0, "Fetching repository (Git)"), cancellationToken);
            Started.SetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && HoldCancellationCleanup)
            {
                CleanupStarted.SetResult();
                await CleanupRelease.Task.WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
                CleanupFinished = true;
                throw;
            }

            if (DuringWork is not null)
            {
                await DuringWork(cancellationToken);
            }

            await Events.PublishAsync(
                started with
            {
                OccurredAt = DateTimeOffset.UtcNow,
                Status = Fail ? SkillInvocationStatus.Failed : SkillInvocationStatus.Completed,
            },
                cancellationToken);
            if (Fail)
            {
                throw new InvalidOperationException("acquisition failed");
            }

            var package = new SkillPackageIdentity(new SkillId("review"), "review", "1.0.0", new SkillDigest("sha256", new string('a', 64)), "test");
            return new SkillInvocationResult
            {
                InvocationId = id,
                Package = package,
                Status = SkillInvocationStatus.Completed,
                Reason = "Finished",
                Checkpoint = new SkillWorkflowCheckpoint
                {
                    WorkflowId = SkillWorkflowId.New(), InvocationId = id, SessionId = SessionId.New(), RunId = RunId.New(),
                    Package = package, InputJson = "{}", EffectiveBudget = new SkillBudget(),
                    Status = SkillInvocationStatus.Completed, NextAction = "None", RecordedAt = DateTimeOffset.UtcNow,
                },
            };
        }
    }
}
