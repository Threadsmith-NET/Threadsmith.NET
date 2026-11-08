namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>The effective initial request is observed before the provider can produce output.</summary>
    [Fact]
    public async Task InitialInstructionsMatchPreparedRequestBeforeProviderOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var registry = new ToolRegistry([]);
        var provider = new InstructionInspectingProvider(observed);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []) with
        {
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero),
            InitialContext = "shared context\n\nchild context",
        };
        var plan = CreatePlan(assignment);
        var runner = CreateRunner(provider, CreatePipeline(registry, events, sanitizer), evidence, sanitizer, profile, CreateParentContext(plan, []), []);
        await using var scheduler = new AgentRunScheduler();
        var coordinator = new DelegationCoordinator(scheduler, new RoleCheckpointStore(), events, progressCheckpointTimeout: TimeSpan.Zero, sanitizer: sanitizer);

        var checkpoint = await coordinator.StartAsync(plan, runner, ct);

        var instructions = Assert.Single(observed.OfType<AgentRunInstructionsObserved>());
        Assert.Equal(AgentInstructionSource.PreparedRequest, instructions.Source);
        Assert.Equal(assignment.ChildRunId, instructions.ChildRunId);
        Assert.Equal(assignment.AssignmentId, instructions.AssignmentId);
        Assert.Equal(plan.DelegationId, instructions.DelegationId);
        Assert.Equal(plan.Provenance.Generation, instructions.Generation);
        Assert.Equal(4, instructions.Sections.Count);
        Assert.Equal(provider.Request!.Messages.Take(4).Select(message => sanitizer.Sanitize(message.GetModelVisibleContent())), instructions.Sections.Select(section => section.Text));
        Assert.Contains("shared context\n\nchild context", instructions.Sections[3].Text, StringComparison.Ordinal);
        Assert.Equal(AgentRunStatus.Completed, Assert.Single(checkpoint.ChildOutcomes).Status);
        var roundTrip = Assert.IsType<AgentRunInstructionsObserved>(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(instructions), instructions.SchemaVersion, DomainEventJson.Serialize(instructions)));
        Assert.Equal(instructions.Sections, roundTrip.Sections);
        Assert.Equal(instructions.ChildRunId, roundTrip.ChildRunId);
    }

    /// <summary>Terminal children without a request retain their sanitized frozen assignment.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalBeforePreparationShowsFrozenAssignment(bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var assignment = CreateAssignment(CreateProfile().Id, []) with
        {
            InitialContext = "shared context\n\nchild context\npassword=fixture-secret",
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero),
            Scope = new AgentAssignmentScope { Files = ["src/Target.cs"] },
        };
        var plan = CreatePlan(assignment);
        await using var scheduler = new AgentRunScheduler();
        var coordinator = new DelegationCoordinator(scheduler, new RoleCheckpointStore(), events, progressCheckpointTimeout: TimeSpan.Zero);
        var runner = new UnpreparedRunner(cancel);

        var checkpoint = await coordinator.StartAsync(plan, runner, ct);

        var entries = observed.ToArray();
        var instructions = Assert.Single(entries.OfType<AgentRunInstructionsObserved>());
        Assert.Equal(AgentInstructionSource.AssignmentOnly, instructions.Source);
        var text = Assert.Single(instructions.Sections).Text;
        Assert.Contains(assignment.Objective, text, StringComparison.Ordinal);
        Assert.Contains(assignment.Tasks[0], text, StringComparison.Ordinal);
        Assert.Contains("shared context\n\nchild context", text, StringComparison.Ordinal);
        Assert.Contains("src/Target.cs", text, StringComparison.Ordinal);
        Assert.Contains(assignment.OutputSchema, text, StringComparison.Ordinal);
        Assert.Contains(assignment.StoppingCondition, text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", text, StringComparison.Ordinal);
        var instructionIndex = Array.IndexOf(entries, instructions);
        Assert.All(entries.OfType<AgentRunLifecycleObserved>().Where(item => item.Status is AgentRunStatus.Failed or AgentRunStatus.Cancelled), terminal => Assert.True(Array.IndexOf(entries, terminal) > instructionIndex));
        Assert.Equal(cancel ? AgentRunStatus.Cancelled : AgentRunStatus.Failed, Assert.Single(checkpoint.ChildOutcomes).Status);
    }

    /// <summary>A stalled instruction subscriber cannot retain a terminal child's scheduler slot.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledFallbackInstructionsReleaseSchedulerSlot(bool cancelChild)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clock = new ObservedTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe(async (item, token) =>
        {
            if (item is AgentRunInstructionsObserved)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            observed.Enqueue(item);
        });
        var assignment = CreateAssignment(CreateProfile().Id, []) with
        {
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero),
        };
        var plan = CreatePlan(assignment);
        await using var scheduler = new AgentRunScheduler(
            new AgentSchedulerOptions { MaximumActiveChildren = 1 }, timeProvider: clock);
        var abandoned = new ConcurrentQueue<Task>();
        var coordinator = new DelegationCoordinator(
            scheduler,
            new RoleCheckpointStore(),
            events,
            progressCheckpointTimeout: cancelChild ? TimeSpan.Zero : TimeSpan.FromMilliseconds(50),
            timeProvider: clock)
        {
            AbandonedProgressSaveObserver = abandoned.Enqueue,
        };
        var running = coordinator.StartAsync(plan, new UnpreparedRunner(cancelChild), ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            // This second delegation can finish only after the first releases the sole global lease.
            var probe = CreatePlan(assignment with
            {
                AssignmentId = AgentAssignmentId.New(),
                ChildRunId = RunId.New(),
            });
            var queued = scheduler.RunAsync(probe, new UnpreparedRunner(false), ct);
            Assert.False(queued.IsCompleted);

            // Act: the publication is already entered, so its progress timer is registered.
            if (cancelChild)
            {
                Assert.True(await scheduler.CancelAssignmentAsync(plan.DelegationId, assignment.AssignmentId, ct));
            }
            else
            {
                clock.Advance(TimeSpan.FromMilliseconds(50));
            }

            var probeOutcomes = await queued.WaitAsync(ct);

            // Assert before allowing fallback delivery to complete.
            Assert.Equal(AgentRunStatus.Failed, Assert.Single(probeOutcomes).Status);
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var checkpoint = await running.WaitAsync(ct);
        await Task.WhenAll(abandoned).WaitAsync(ct);
        Assert.NotEmpty(abandoned);
        Assert.Equal(cancelChild ? AgentRunStatus.Cancelled : AgentRunStatus.Failed, Assert.Single(checkpoint.ChildOutcomes).Status);
        var entries = observed.ToArray();
        var instructions = Assert.Single(entries.OfType<AgentRunInstructionsObserved>());
        Assert.Equal(AgentInstructionSource.AssignmentOnly, instructions.Source);
        Assert.Equal(assignment.AssignmentId, instructions.AssignmentId);
        Assert.All(
            entries.OfType<AgentRunLifecycleObserved>().Where(item => item.Status is AgentRunStatus.Failed or AgentRunStatus.Cancelled),
            terminal => Assert.True(Array.IndexOf(entries, instructions) < Array.IndexOf(entries, terminal)));
    }

    /// <summary>Display sanitization and repeated observations leave model input unchanged.</summary>
    [Fact]
    public async Task PreparedProviderInstructionsAreSanitizedWithoutChangingRequestOrDuplicatingObservation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<AgentRunInstructionsObserved>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is AgentRunInstructionsObserved instructions)
            {
                observed.Enqueue(instructions);
            }

            return Task.CompletedTask;
        });
        var assignment = CreateAssignment(CreateProfile().Id, []);
        var plan = CreatePlan(assignment);
        var request = new ModelStreamRequest
        {
            RunId = assignment.ChildRunId,
            Input = assignment.Objective,
            Messages = [new ModelMessage { Role = ModelMessageRole.User, SectionId = "child-assignment", Content = [new ModelContentPart { Content = "task\npassword=fixture-secret" }] }],
            ProviderInstructions = new ModelProviderInstructions { SectionId = "provider", Content = "provider\npassword=provider-secret" },
        };
        using var observation = new ChildAgentInstructionObservation(plan, events, new SecretOutputSanitizer());
        await ChildAgentInstructionObservation.ObservePreparedAsync(plan, assignment, request, ct);
        await ChildAgentInstructionObservation.ObservePreparedAsync(plan, assignment, request, ct);
        await observation.ObserveAssignmentAsync(assignment.AssignmentId, ct);
        var sections = Assert.Single(observed).Sections;
        Assert.Equal(["Assignment", "Provider instructions"], sections.Select(section => section.Label));
        Assert.All(sections, section => Assert.Contains("[REDACTED]", section.Text, StringComparison.Ordinal));
        Assert.Contains("fixture-secret", request.Messages[0].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Contains("provider-secret", request.ProviderInstructions.Content, StringComparison.Ordinal);
    }

    /// <summary>Queued cancellation and expired deadlines still publish assignments before terminal lifecycle.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildrenThatNeverEnterRunnerStillPublishAssignment(bool cancelAtAcceptance)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        var acceptedAt = DateTimeOffset.UtcNow;
        var assignment = CreateAssignment(CreateProfile().Id, []) with
        {
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero),
            Deadline = acceptedAt.AddHours(1),
        };
        var plan = CreatePlan(assignment) with { AcceptedAt = acceptedAt };
        // The controllable scheduler clock starts past the assignment deadline; no runner-speed limit is involved.
        await using var scheduler = new AgentRunScheduler(timeProvider: new FakeTimeProvider(acceptedAt.AddHours(2)));
        var coordinator = new DelegationCoordinator(scheduler, new RoleCheckpointStore(), events, progressCheckpointTimeout: TimeSpan.Zero);
        await using var subscription = events.Subscribe(async (item, token) =>
        {
            observed.Enqueue(item);
            if (cancelAtAcceptance && item is DelegationCheckpointWritten { Phase: DelegationCheckpointPhase.Accepted })
            {
                Assert.True(await coordinator.CancelAsync(plan.DelegationId, token));
            }
        });

        await coordinator.StartAsync(plan, new UnpreparedRunner(false), ct);

        var entries = observed.ToArray();
        var instruction = Assert.Single(entries.OfType<AgentRunInstructionsObserved>());
        Assert.Equal(AgentInstructionSource.AssignmentOnly, instruction.Source);
        var queued = Assert.Single(entries.OfType<AgentRunLifecycleObserved>(), item => item.Status == AgentRunStatus.Queued);
        Assert.True(Array.IndexOf(entries, queued) < Array.IndexOf(entries, instruction));
        Assert.DoesNotContain(entries.OfType<AgentRunLifecycleObserved>(), item => item.Status == AgentRunStatus.Running);
        Assert.All(entries.OfType<AgentRunLifecycleObserved>().Where(item => item.Status != AgentRunStatus.Queued), item => Assert.True(Array.IndexOf(entries, instruction) < Array.IndexOf(entries, item)));
    }

    /// <summary>Real XML-escaped prompt preparation cannot bypass credential redaction in durable instructions.</summary>
    [Theory]
    [InlineData("password=\"quoted-fixture-secret\"")]
    [InlineData("{\"password\":\"quoted-fixture-secret\"}")]
    [InlineData("password='quoted-fixture-secret'")]
    public async Task PreparedEscapedCredentialsAreRedactedWithoutChangingModelInput(string context)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var registry = new ToolRegistry([]);
        var provider = new InstructionInspectingProvider(observed);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []) with
        {
            InitialContext = context,
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero),
        };
        var plan = CreatePlan(assignment);
        var runner = CreateRunner(provider, CreatePipeline(registry, events, sanitizer), evidence, sanitizer, profile, CreateParentContext(plan, []), []);
        await using var scheduler = new AgentRunScheduler();
        var coordinator = new DelegationCoordinator(scheduler, new RoleCheckpointStore(), events, progressCheckpointTimeout: TimeSpan.Zero, sanitizer: sanitizer);

        await coordinator.StartAsync(plan, runner, ct);

        var instructions = Assert.Single(observed.OfType<AgentRunInstructionsObserved>());
        var serialized = DomainEventJson.Serialize(instructions);
        Assert.DoesNotContain("quoted-fixture-secret", serialized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", instructions.Sections[3].Text, StringComparison.Ordinal);
        var original = Assert.Single(provider.Request!.Messages, message => message.SectionId == "child-assignment").GetModelVisibleContent();
        Assert.Contains("quoted-fixture-secret", original, StringComparison.Ordinal);
        Assert.Contains("&", original, StringComparison.Ordinal);
        Assert.Equal(context, assignment.InitialContext);
    }

    private sealed class InstructionInspectingProvider : IModelProvider
    {
        private readonly ConcurrentQueue<IDomainEvent> _observed;

        public InstructionInspectingProvider(ConcurrentQueue<IDomainEvent> observed)
        {
            _observed = observed;
        }

        public ModelStreamRequest? Request { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.Single(_observed.OfType<AgentRunInstructionsObserved>());
            Request = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new ModelChunk { Text = "done" };
        }
    }

    private sealed class UnpreparedRunner : IAgentAssignmentRunner
    {
        private readonly bool _cancel;

        public UnpreparedRunner(bool cancel)
        {
            _cancel = cancel;
        }

        public Task<AgentRunOutcome> RunAsync(DelegationPlan plan, AgentAssignment assignment, CancellationToken cancellationToken = default)
        {
            return Task.FromException<AgentRunOutcome>(_cancel ? new OperationCanceledException(cancellationToken) : new InvalidOperationException("preparation failed"));
        }
    }
}
