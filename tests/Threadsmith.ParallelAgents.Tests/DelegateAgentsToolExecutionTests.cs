namespace Threadsmith.ParallelAgents.Tests;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies the Plan 91 public tool definition and durable fork/join behavior.</summary>
public sealed class DelegateAgentsToolExecutionTests
{
    /// <summary>Verifies the model-facing contract exposes only bounded v1 arguments and exact result shape.</summary>
    [Fact]
    public async Task Definition_UsesStrictBoundedWorkflowContract()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new CompletedFindingRunner()));

        // Act
        var definition = fixture.Tool.Definition;
        using var inputSchema = JsonDocument.Parse(definition.InputSchema.JsonSchema);
        using var outputSchema = JsonDocument.Parse(definition.OutputSchema.JsonSchema);
        var inputProperties = inputSchema.RootElement.GetProperty("properties");
        var agentProperties = inputProperties.GetProperty("agents")
            .GetProperty("items")
            .GetProperty("properties");
        var childProperties = outputSchema.RootElement.GetProperty("properties")
            .GetProperty("children")
            .GetProperty("items")
            .GetProperty("properties");

        // Assert
        Assert.Equal(DelegateAgentsContract.ToolId, definition.Id);
        Assert.Equal(ToolCategory.Workflow, definition.Category);
        Assert.Equal(ToolIdempotency.NonIdempotent, definition.Idempotency);
        Assert.Equal(ToolConcurrencyMode.ExclusiveSession, definition.Scheduling.ConcurrencyMode);
        Assert.True(definition.SupportsCancellation);
        Assert.True(definition.ConversationAvailable);
        Assert.True(definition.RequiresWorkspace);
        Assert.True(definition.PreferStrictArguments);
        Assert.True(inputSchema.RootElement.GetProperty("additionalProperties").ValueKind
            == JsonValueKind.False);
        Assert.Equal(["agents"], inputProperties.EnumerateObject().Select(item => item.Name));
        Assert.Equal(
            ["context", "task", "toolAccess"],
            agentProperties.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.False(agentProperties.TryGetProperty("model", out _));
        Assert.False(agentProperties.TryGetProperty("budget", out _));
        Assert.False(agentProperties.TryGetProperty("allowedToolIds", out _));
        Assert.True(outputSchema.RootElement.GetProperty("additionalProperties").ValueKind
            == JsonValueKind.False);
        Assert.True(outputSchema.RootElement.GetProperty("properties")
            .TryGetProperty("disagreements", out _));
        Assert.True(childProperties.GetProperty("usage")
            .GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
    }

    /// <summary>Verifies two requested children overlap, checkpoint progress, join, and remain inspectable.</summary>
    [Fact]
    public async Task ExecuteAsync_TwoChildren_ForksJoinsAndPersistsInspectableProgress()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new ConcurrentFindingRunner(TimeSpan.FromMilliseconds(100));
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));
        var input = CreateInput(2);

        // Act
        var execution = await fixture.Tool.ExecuteAsync(input, fixture.Context);
        var result = execution.Value;
        var inspected = await coordinator.GetAsync(new DelegationId(Guid.Parse(result.DelegationId)));

        // Assert
        Assert.Equal(2, runner.MaximumActive);
        Assert.Equal(DelegateAgentsStatus.Completed, result.Status);
        Assert.Equal(2, result.Children.Count);
        Assert.All(result.Children, child => Assert.Equal("Completed", child.Status));
        Assert.Equal(result.DelegationId, execution.Sources.Single().Identifier);
        Assert.Contains(result.DelegationId, execution.ModelResultContent, StringComparison.Ordinal);
        Assert.DoesNotContain("raw child transcript sentinel", execution.ModelResultContent, StringComparison.Ordinal);
        var firstChild = result.Children[0];
        var firstFinding = Assert.Single(firstChild.Findings);
        Assert.Contains(
            PromptAssetRenderer.RenderWithPlatformLineEndings(
                TestPromptLoader.Instance,
                PromptFileNames.ToolDelegateAgentsFinding,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["AssignmentId"] = $"{firstChild.AssignmentId}",
                    ["Title"] = firstFinding.Title,
                    ["FilePathBlock"] = firstFinding.FilePath is null
                        ? string.Empty
                        : $" [{firstFinding.FilePath}]",
                    ["SymbolBlock"] = firstFinding.Symbol is null
                        ? string.Empty
                        : $" symbol={firstFinding.Symbol}",
                    ["Evidence"] = firstFinding.Evidence,
                    ["Confidence"] = $"{firstFinding.Confidence}",
                    ["UncertaintyBlock"] = string.Empty,
                }),
            execution.ModelResultContent,
            StringComparison.Ordinal);
        Assert.NotNull(inspected);
        Assert.Equal(DelegationCheckpointPhase.ResearchJoined, inspected.Phase);
        Assert.Contains(checkpoints.History, checkpoint =>
            checkpoint.Phase == DelegationCheckpointPhase.Accepted);
        Assert.Contains(checkpoints.History, checkpoint =>
            checkpoint.Phase == DelegationCheckpointPhase.ChildrenQueued
            && checkpoint.ChildOutcomes.All(outcome => outcome.Status == AgentRunStatus.Queued));
        Assert.Contains(checkpoints.History, checkpoint =>
            checkpoint.Phase == DelegationCheckpointPhase.ChildrenRunning
            && checkpoint.ChildOutcomes.Any(outcome => outcome.Status == AgentRunStatus.Running));
        Assert.All(
            checkpoints.History.Zip(checkpoints.History.Skip(1)),
            pair => Assert.True(pair.First.Revision < pair.Second.Revision));
        Assert.All(
            observed.OfType<DelegationCheckpointWritten>(),
            domainEvent => Assert.True(domainEvent.Revision > 0));
        Assert.All(
            observed.OfType<AgentRunLifecycleObserved>(),
            domainEvent => Assert.True(domainEvent.Revision > 0));
    }

    /// <summary>Verifies finding uncertainty uses the exact conditional prompt block.</summary>
    [Fact]
    public async Task ExecuteAsync_FindingUncertainty_RendersExactConditionalPromptBlock()
    {
        // Arrange
        const string uncertainty = "bounded uncertainty";
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(
            coordinator,
            new FixedRunnerFactory(new UncertainFindingRunner(uncertainty)));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context);

        // Assert
        var expected = PromptAssetRenderer.RenderWithPlatformLineEndings(
            TestPromptLoader.Instance,
            PromptFileNames.ToolDelegateAgentsFindingUncertainty,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Uncertainty"] = uncertainty,
            });
        Assert.Contains(expected, execution.ModelResultContent, StringComparison.Ordinal);
    }

    /// <summary>Verifies an active delegation can be inspected while its child is still running.</summary>
    [Fact]
    public async Task ExecuteAsync_RunningChild_ExposesDurableRunningCheckpoint()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new ReleasableFindingRunner();
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));

        // Act
        var executionTask = fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context);
        await runner.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await checkpoints.ChildrenRunning.WaitAsync(TimeSpan.FromSeconds(5));
        var inspected = await coordinator.GetAsync(running.DelegationId);
        runner.Release();
        var execution = await executionTask;

        // Assert
        Assert.NotNull(inspected);
        Assert.Equal(DelegationCheckpointPhase.ChildrenRunning, inspected.Phase);
        Assert.Equal(AgentRunStatus.Running, Assert.Single(inspected.ChildOutcomes).Status);
        Assert.Equal(DelegateAgentsStatus.Completed, execution.Value.Status);
    }

    /// <summary>Verifies caller cancellation reaches every active child and produces a terminal joined result.</summary>
    [Fact]
    public async Task ExecuteAsync_CallerCancellation_CancelsChildrenAndReturnsTerminalStatus()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new BlockingRunner(expectedActive: 2);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));
        using var cancellation = new CancellationTokenSource();

        // Act
        var executionTask = fixture.Tool.ExecuteAsync(
            CreateInput(2),
            fixture.Context,
            cancellation.Token);
        await runner.AllEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        var execution = await executionTask;

        // Assert
        Assert.Equal(DelegateAgentsStatus.Cancelled, execution.Value.Status);
        Assert.All(execution.Value.Children, child => Assert.Equal("Cancelled", child.Status));
        Assert.Equal(0, runner.Active);
        Assert.Equal(
            DelegationCheckpointPhase.Cancelled,
            Assert.Single(
                checkpoints.History,
                checkpoint => checkpoint.Phase == DelegationCheckpointPhase.Cancelled).Phase);
    }

    /// <summary>Verifies parent cancellation remains authoritative when one sibling already completed.</summary>
    [Fact]
    public async Task ExecuteAsync_MixedCompletionAndParentCancellation_ReturnsCancelledWithOutcomes()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new MixedCancellationJoinRunner();
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));
        using var cancellation = new CancellationTokenSource();

        // Act
        var executionTask = fixture.Tool.ExecuteAsync(
            CreateInput(2),
            fixture.Context,
            cancellation.Token);
        await runner.BlockingChildEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        var execution = await executionTask;

        // Assert
        Assert.Equal(DelegateAgentsStatus.Cancelled, execution.Value.Status);
        Assert.Equal(["Completed", "Cancelled"], execution.Value.Children.Select(child => child.Status));
        Assert.Equal(0, runner.JoinCalls);
        var cancelled = checkpoints.History.Last();
        Assert.Equal(DelegationCheckpointPhase.Cancelled, cancelled.Phase);
        Assert.Equal(2, cancelled.ChildOutcomes.Count);
    }

    /// <summary>Verifies cancellation that arrives during join wins even when the joiner returns late.</summary>
    [Fact]
    public async Task ExecuteAsync_ParentCancellationDuringNonCooperativeJoin_ReturnsCancelled()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new NonCooperativeJoinRunner();
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));
        using var cancellation = new CancellationTokenSource();

        // Act
        var executionTask = fixture.Tool.ExecuteAsync(
            CreateInput(1),
            fixture.Context,
            cancellation.Token);
        await runner.JoinEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        runner.ReleaseJoin();
        var execution = await executionTask;

        // Assert
        Assert.Equal(DelegateAgentsStatus.Cancelled, execution.Value.Status);
        Assert.True(runner.JoinCancellationToken.CanBeCanceled);
        Assert.True(runner.JoinCancellationToken.IsCancellationRequested);
        Assert.Equal(DelegationCheckpointPhase.Cancelled, checkpoints.History.Last().Phase);
    }

    /// <summary>Verifies cancellation wins when a non-cooperative joiner throws another exception.</summary>
    [Fact]
    public async Task ExecuteAsync_ParentCancellationDuringThrowingJoin_ReturnsCancelled()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new ThrowingAfterCancellationJoinRunner();
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));
        using var cancellation = new CancellationTokenSource();

        // Act
        var executionTask = fixture.Tool.ExecuteAsync(
            CreateInput(1),
            fixture.Context,
            cancellation.Token);
        await runner.JoinEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        runner.ReleaseJoin();
        var execution = await executionTask;

        // Assert
        Assert.Equal(DelegateAgentsStatus.Cancelled, execution.Value.Status);
        Assert.Equal(DelegationCheckpointPhase.Cancelled, checkpoints.History.Last().Phase);
    }

    /// <summary>Verifies cancellation cannot race disposal of the active token source.</summary>
    [Fact]
    public async Task CancelAsync_InFlightCancellationDuringCompletion_DoesNotUseDisposedSource()
    {
        // Arrange
        await using var events = new DomainEventStream();
        using var scheduler = new CancellationDisposalRaceScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new CompletedFindingRunner();
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));
        var plan = fixture.Plans.Create(CreateInput(1), fixture.Context);
        var executionTask = coordinator.StartAsync(plan, runner);
        await scheduler.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        var cancellationTask = Task.Run(() => coordinator.CancelAsync(plan.DelegationId));
        await scheduler.CancellationEntered.WaitAsync(TimeSpan.FromSeconds(5));
        DelegationCheckpoint checkpoint;
        try
        {
            scheduler.Complete();
            checkpoint = await executionTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            scheduler.ReleaseCancellation();
        }

        var cancelled = await cancellationTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(cancelled);
        Assert.Equal(DelegationCheckpointPhase.Cancelled, checkpoint.Phase);
        Assert.Equal(AgentRunStatus.Completed, Assert.Single(checkpoint.ChildOutcomes).Status);
    }

    /// <summary>Verifies pre-cancelled runs retain every child identity with a terminal status.</summary>
    [Fact]
    public async Task StartAsync_PreCancelledInvocation_RetainsCancelledChildIdentities()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new CompletedFindingRunner()));
        var plan = fixture.Plans.Create(CreateInput(2), fixture.Context);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var checkpoint = await coordinator.StartAsync(
            plan,
            new CompletedFindingRunner(),
            cancellation.Token);

        // Assert
        Assert.Equal(DelegationCheckpointPhase.Cancelled, checkpoint.Phase);
        Assert.Equal(2, checkpoint.ChildOutcomes.Count);
        Assert.Equal(
            plan.Assignments.Select(item => item.AssignmentId),
            checkpoint.ChildOutcomes.Select(item => item.AssignmentId));
        Assert.All(checkpoint.ChildOutcomes, outcome =>
            Assert.Equal(AgentRunStatus.Cancelled, outcome.Status));
    }

    /// <summary>Verifies an uncommitted delivery claim cannot make child findings authoritative.</summary>
    [Fact]
    public async Task ExecuteAsync_JoinFailure_ReturnsFailedResultWithPreservedChildren()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new FailingJoinRunner();
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(2), fixture.Context);

        // Assert
        Assert.Equal(DelegateAgentsStatus.Failed, execution.Value.Status);
        Assert.Equal(2, execution.Value.Children.Count);
        Assert.All(execution.Value.Children, child =>
        {
            Assert.Equal("Failed", child.Status);
            Assert.Empty(child.Findings);
            Assert.Contains("join failed", child.Summary, StringComparison.Ordinal);
        });
        Assert.Equal(1, runner.JoinCalls);
        Assert.Equal(DelegationCheckpointPhase.Failed, checkpoints.History.Last().Phase);
    }

    /// <summary>Verifies a committed evidence join survives failure in a later event subscriber.</summary>
    [Fact]
    public async Task ExecuteAsync_PostCommitSubscriberFailure_RetainsJoinedResultAndEvidence()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var firstSubscription = events.Subscribe((_, _) => Task.CompletedTask);
        await using var failingSubscription = events.Subscribe((domainEvent, _) =>
            domainEvent is EvidenceAdded
                ? Task.FromException(new InvalidOperationException("evidence observer failed"))
                : Task.CompletedTask);
        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var runner = new EvidenceJoiningRunner(evidence);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(runner));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context);

        // Assert
        Assert.Equal(DelegateAgentsStatus.Completed, execution.Value.Status);
        Assert.Single(Assert.Single(execution.Value.Children).Findings);
        Assert.Single(evidence.Snapshot(fixture.Context.SessionId));
        Assert.Equal(DelegationCheckpointPhase.ResearchJoined, checkpoints.History.Last().Phase);
    }

    /// <summary>Verifies usable sibling evidence joins as partial instead of becoming a failed checkpoint.</summary>
    [Fact]
    public async Task ExecuteAsync_MixedOutcomes_PersistsJoinedPartialResult()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new MixedOutcomeRunner()));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(2), fixture.Context);

        // Assert
        Assert.Equal(DelegateAgentsStatus.Partial, execution.Value.Status);
        Assert.Equal(["Completed", "Failed"], execution.Value.Children.Select(child => child.Status));
        Assert.Equal(DelegationCheckpointPhase.ResearchJoined, checkpoints.History.Last().Phase);
    }

    /// <summary>Verifies an Explorer with an empty finding set is not reported as completed.</summary>
    [Fact]
    public async Task ExecuteAsync_EmptyFindingSet_IsFailedAtCheckpointAndResultBoundaries()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new EmptyFindingRunner()));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context);

        // Assert
        Assert.Equal(DelegateAgentsStatus.Failed, execution.Value.Status);
        Assert.Equal("Failed", Assert.Single(execution.Value.Children).Status);
        Assert.Equal(DelegationCheckpointPhase.Failed, checkpoints.History.Last().Phase);
        Assert.Equal(AgentRunStatus.Failed, Assert.Single(checkpoints.History.Last().ChildOutcomes).Status);
    }

    /// <summary>Verifies approved-plan preflight may complete with coverage and no findings.</summary>
    [Fact]
    public async Task StartAsync_ApprovedPlanPreflightWithCoverageOnly_RemainsCompleted()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new EmptyFindingRunner()));
        var basePlan = fixture.Plans.Create(CreateInput(1), fixture.Context);
        var plan = basePlan with
        {
            Provenance = basePlan.Provenance with
            {
                ApprovedPlanIdentity = Guid.NewGuid().ToString("D"),
                ApprovedPlanRevision = 1,
            },
        };

        // Act
        var checkpoint = await coordinator.StartAsync(plan, new ApprovedPlanAssignmentRunner());

        // Assert
        Assert.Equal(DelegationCheckpointPhase.ResearchJoined, checkpoint.Phase);
        var outcome = Assert.Single(checkpoint.ChildOutcomes);
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Empty(Assert.IsType<AgentFindingSet>(outcome.Findings).Findings);
        Assert.NotEmpty(outcome.Findings.CoverageNotes);
    }

    /// <summary>Verifies approved-plan identity and revision provenance cannot be supplied independently.</summary>
    [Fact]
    public async Task Validate_ApprovedPlanProvenanceRequiresIdentityRevisionPair()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var coordinator = new DelegationCoordinator(
            scheduler,
            new RecordingCheckpointStore(),
            events);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new CompletedFindingRunner()));
        var plan = fixture.Plans.Create(CreateInput(1), fixture.Context);

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => DelegationPlanValidator.Validate(plan with
        {
            Provenance = plan.Provenance with { ApprovedPlanIdentity = "plan" },
        }));
        Assert.Throws<InvalidDataException>(() => DelegationPlanValidator.Validate(plan with
        {
            Provenance = plan.Provenance with { ApprovedPlanRevision = 1 },
        }));
    }

    /// <summary>Verifies bounded opposing conclusions over one subject are called out explicitly.</summary>
    [Fact]
    public async Task ExecuteAsync_OpposingConclusions_ReportsDisagreement()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new OpposingFindingRunner()));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(2), fixture.Context);

        // Assert
        var disagreement = Assert.Single(execution.Value.Disagreements);
        Assert.Contains("Shared.Symbol", disagreement, StringComparison.Ordinal);
        Assert.Contains("Disagreement:", execution.ModelResultContent, StringComparison.Ordinal);
    }

    /// <summary>Verifies a negated safety statement is classified as a concern.</summary>
    [Fact]
    public async Task ExecuteAsync_NegatedSafetyAndSafeConclusion_ReportsDisagreement()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new NegatedConcernRunner()));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(2), fixture.Context);

        // Assert
        Assert.Single(execution.Value.Disagreements);
    }

    /// <summary>Verifies maximum configured child payloads remain below the tool envelope byte limit.</summary>
    [Fact]
    public async Task ExecuteAsync_MaximumPayload_RetainsStatusesAndBoundsStructuredResult()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var options = new DelegateAgentsOptions
        {
            MaximumAgents = 8,
            MaximumSummaryCharacters = 4_096,
        };
        var fixture = CreateTool(
            coordinator,
            new FixedRunnerFactory(new LargeFindingRunner()),
            options);

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(8), fixture.Context);
        var structuredBytes = JsonSerializer.SerializeToUtf8Bytes(execution.Value).Length;

        // Assert
        Assert.True(structuredBytes <= DelegateAgentsContract.MaximumStructuredResultBytes);
        Assert.Equal(8, execution.Value.Children.Count);
        Assert.All(execution.Value.Children, child =>
        {
            Assert.Equal("Completed", child.Status);
            Assert.Contains(child.AssignmentId, execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains(child.Omissions, omission => omission.Contains("retained", StringComparison.Ordinal));
        });
        Assert.True(execution.IsTruncated);
    }

    /// <summary>Verifies clipping a retained finding field is reflected in truncation metadata.</summary>
    [Fact]
    public async Task ExecuteAsync_OversizedFindingField_ReportsStructuredTruncation()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(
            coordinator,
            new FixedRunnerFactory(new OversizedFindingFieldRunner()));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context);

        // Assert
        var child = Assert.Single(execution.Value.Children);
        var finding = Assert.Single(child.Findings);
        Assert.True(execution.IsTruncated);
        Assert.True(finding.Title.Length <= 1_024);
        Assert.Contains(child.Omissions, omission => omission.Contains(
            "finding fields were truncated",
            StringComparison.Ordinal));
    }

    /// <summary>Verifies projecting one location and symbol reports additional values as truncation.</summary>
    [Fact]
    public async Task ExecuteAsync_MultipleLocationsAndSymbols_ReportsStructuredTruncation()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var fixture = CreateTool(
            coordinator,
            new FixedRunnerFactory(new MultiLocationFindingRunner()));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context);

        // Assert
        var child = Assert.Single(execution.Value.Children);
        var finding = Assert.Single(child.Findings);
        Assert.True(execution.IsTruncated);
        Assert.Equal("src/first.cs", finding.FilePath);
        Assert.Equal("First.Symbol", finding.Symbol);
        Assert.Contains(child.Omissions, omission => omission.Contains(
            "finding fields were truncated",
            StringComparison.Ordinal));
    }

    /// <summary>Verifies model projection truncation reserves room for a complete footer.</summary>
    [Fact]
    public async Task ExecuteAsync_AsciiHeavyProjection_PreservesStatusesAndTruncationFooter()
    {
        // Arrange
        const int maximumModelProjectionCharacters = 48 * 1024;
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var options = new DelegateAgentsOptions
        {
            MaximumAgents = 8,
            MaximumSummaryCharacters = 4_096,
        };
        var fixture = CreateTool(
            coordinator,
            new FixedRunnerFactory(new LargeFindingRunner('x')),
            options);

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(8), fixture.Context);

        // Assert
        var modelContent = Assert.IsType<string>(execution.ModelResultContent);
        Assert.True(execution.IsTruncated);
        Assert.True(modelContent.Length <= maximumModelProjectionCharacters);
        Assert.All(execution.Value.Children, child => Assert.Contains(
            child.AssignmentId,
            modelContent,
            StringComparison.Ordinal));
        Assert.Contains(
            "complete detail block(s) omitted by the model projection bound",
            modelContent,
            StringComparison.Ordinal);
    }

    /// <summary>Enlarged editable blocks cannot bypass the model projection bound or hide truncation.</summary>
    [Fact]
    public async Task ExecuteAsync_EnlargedPromptBlocks_RemainBoundedAndCountOmissions()
    {
        // Arrange
        const int maximumModelProjectionCharacters = 48 * 1024;
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var prompts = TestPromptLoader.Instance
            .WithPrompt(
                PromptFileNames.ToolDelegateAgentsResultHeader,
                new string('h', 100_000) + "{{DelegationId}}{{Status}}")
            .WithPrompt(
                PromptFileNames.ToolDelegateAgentsChildStatus,
                new string('c', 100_000) + "{{AssignmentId}}{{Role}}{{ToolAccess}}{{Status}}");
        var fixture = CreateTool(
            coordinator,
            new FixedRunnerFactory(new CompletedFindingRunner()),
            prompts: prompts);

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context);

        // Assert
        var modelContent = Assert.IsType<string>(execution.ModelResultContent);
        Assert.True(execution.IsTruncated);
        Assert.True(modelContent.Length <= maximumModelProjectionCharacters);
        Assert.Contains(
            "2 complete detail block(s) omitted by the model projection bound.",
            modelContent,
            StringComparison.Ordinal);
    }

    /// <summary>An editable truncation footer that cannot fit fails without returning partial prose.</summary>
    [Fact]
    public async Task ExecuteAsync_EnlargedTruncationPrompt_FailsWithoutPartialProjection()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var prompts = TestPromptLoader.Instance.WithPrompt(
            PromptFileNames.ToolDelegateAgentsTruncation,
            "{{OmittedBlockCount}}" + new string('t', 100_000));
        var fixture = CreateTool(
            coordinator,
            new FixedRunnerFactory(new CompletedFindingRunner()),
            prompts: prompts);

        // Act and assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context));
        Assert.Contains("truncation prompt exceeds", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies a non-cooperative progress store cannot indefinitely block child completion.</summary>
    [Fact]
    public async Task ExecuteAsync_NonCooperativeProgressCheckpoint_CompletesWithinBound()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new BlockingProgressCheckpointStore();
        var coordinator = new DelegationCoordinator(
            scheduler,
            checkpoints,
            events,
            TimeSpan.FromMilliseconds(50));
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new CompletedFindingRunner()));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context)
            .WaitAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(DelegateAgentsStatus.Completed, execution.Value.Status);
        Assert.Equal(
            DelegationCheckpointPhase.ResearchJoined,
            Assert.IsType<DelegationCheckpoint>(checkpoints.Latest).Phase);
    }

    /// <summary>Verifies a late progress write cannot replace a newer terminal checkpoint.</summary>
    [Fact]
    public async Task ExecuteAsync_LateProgressSaveAfterJoin_DoesNotOverwriteTerminalCheckpoint()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        await using var scheduler = CreateScheduler();
        var checkpoints = new DelayedProgressCheckpointStore();
        var coordinator = new DelegationCoordinator(
            scheduler,
            checkpoints,
            events,
            TimeSpan.FromMilliseconds(50));
        var fixture = CreateTool(coordinator, new FixedRunnerFactory(new CompletedFindingRunner()));

        // Act
        var execution = await fixture.Tool.ExecuteAsync(CreateInput(1), fixture.Context)
            .WaitAsync(TimeSpan.FromSeconds(2));
        var eventCountAtTerminal = observed.Count;
        checkpoints.ReleaseProgress();
        await checkpoints.LateSaveCompleted.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.Equal(DelegateAgentsStatus.Completed, execution.Value.Status);
        Assert.Equal(
            DelegationCheckpointPhase.ResearchJoined,
            Assert.IsType<DelegationCheckpoint>(checkpoints.Latest).Phase);
        Assert.Equal(eventCountAtTerminal, observed.Count);
    }

    private static AgentRunScheduler CreateScheduler()
    {
        return new AgentRunScheduler(new AgentSchedulerOptions
        {
            QueueCapacity = 8,
            MaximumActiveChildren = 2,
            MaximumActiveChildrenPerParent = 2,
            MaximumActiveImplementers = 1,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
    }

    private static ToolFixture CreateTool(
        IDelegationCoordinator coordinator,
        IExplorerAssignmentRunnerFactory runners,
        DelegateAgentsOptions? configuredOptions = null,
        IPromptLoader? prompts = null)
    {
        var workspaceId = WorkspaceId.New();
        var baseline = new WorkspaceBaseline(
            workspaceId,
            Environment.CurrentDirectory,
            DateTimeOffset.UtcNow,
            []);
        var snapshots = new ConversationToolSnapshotStore();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var snapshotId = snapshots.Capture(sessionId, runId, []);
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            sessionId,
            runId,
            new ToolInvocationContext
            {
                WorkspaceId = workspaceId,
                RepositoryPath = Environment.CurrentDirectory,
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                ModelVisibleToolSnapshotId = snapshotId,
                RequestedBy = "model",
            })
        {
            Phase = RunPhase.EvidenceCollection,
        };
        var options = configuredOptions ?? new DelegateAgentsOptions();
        var plans = new DelegateAgentsPlanFactory(
            new StubWorkspaceResolver(baseline),
            new SessionModelPreferences(),
            snapshots,
            TestPromptLoader.Instance,
            options);
        return new ToolFixture(
            new DelegateAgentsTool(
                plans,
                runners,
                coordinator,
                options,
                prompts ?? TestPromptLoader.Instance),
            context,
            plans);
    }

    private static DelegateAgentsInput CreateInput(int count)
    {
        return new DelegateAgentsInput
        {
            Agents = Enumerable.Range(1, count).Select(index => new DelegateAgentRequest
            {
                Task = $"Inspect bounded area {index}.",
                Context = $"Use only evidence for area {index}.",
                ToolAccess = DelegateAgentToolAccess.ReadOnly,
            }).ToArray(),
        };
    }

    private static AgentRunOutcome CreateCompletedOutcome(
        DelegationPlan plan,
        AgentAssignment assignment)
    {
        return new AgentRunOutcome
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Role = assignment.Role,
            Generation = plan.Provenance.Generation,
            Status = AgentRunStatus.Completed,
            Usage = new AgentResourceUsage { ModelTokens = 20 },
            Reason = "raw child transcript sentinel",
            Findings = new AgentFindingSet
            {
                AssignmentId = assignment.AssignmentId,
                ChildRunId = assignment.ChildRunId,
                Generation = plan.Provenance.Generation,
                Summary = "Bounded child summary.",
                Findings =
                [
                    new AgentFinding
                    {
                        FindingId = Guid.NewGuid(),
                        Category = "behavior",
                        Summary = "The assigned area has bounded evidence.",
                        EvidenceIds = [EvidenceId.New()],
                        Confidence = 0.9,
                    },
                ],
                CoverageNotes = ["Only the assigned area was inspected."],
            },
        };
    }

    private sealed record ToolFixture(
        DelegateAgentsTool Tool,
        ToolExecutionContext Context,
        DelegateAgentsPlanFactory Plans);

    private sealed class FixedRunnerFactory(IAgentAssignmentRunner runner)
        : IExplorerAssignmentRunnerFactory
    {
        public IAgentAssignmentRunner Create(ToolExecutionContext parentContext)
        {
            return runner;
        }
    }

    private sealed class CompletedFindingRunner : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateCompletedOutcome(plan, assignment));
        }
    }

    private sealed class UncertainFindingRunner(string uncertainty) : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = CreateCompletedOutcome(plan, assignment);
            var findings = Assert.IsType<AgentFindingSet>(completed.Findings);
            var finding = Assert.Single(findings.Findings) with { Uncertainty = uncertainty };
            return Task.FromResult(completed with
            {
                Findings = findings with { Findings = [finding] },
            });
        }
    }

    private sealed class CancellationDisposalRaceScheduler : IAgentRunScheduler, IDisposable
    {
        private readonly TaskCompletionSource _cancellationEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _complete = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ManualResetEventSlim _releaseCancellation = new(initialState: false);
        private CancellationTokenRegistration? _registration;

        public Task CancellationEntered => _cancellationEntered.Task;

        public Task Entered => _entered.Task;

        public void Complete()
        {
            _complete.TrySetResult();
        }

        public void Dispose()
        {
            _releaseCancellation.Set();
            _registration?.Dispose();
            _releaseCancellation.Dispose();
        }

        public void ReleaseCancellation()
        {
            _releaseCancellation.Set();
        }

        public Task<bool> CancelAssignmentAsync(
            DelegationId delegationId,
            AgentAssignmentId assignmentId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public async Task<IReadOnlyList<AgentRunOutcome>> RunAsync(
            DelegationPlan plan,
            IAgentAssignmentRunner runner,
            CancellationToken cancellationToken = default)
        {
            _registration = cancellationToken.Register(() =>
            {
                _cancellationEntered.TrySetResult();
                _releaseCancellation.Wait();
            });
            _entered.TrySetResult();
#pragma warning disable VSTHRD003 // The fake completes independently while cancellation is in flight.
            await _complete.Task;
#pragma warning restore VSTHRD003
            return [CreateCompletedOutcome(plan, Assert.Single(plan.Assignments))];
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseCancellation();
            Complete();
            return Task.CompletedTask;
        }
    }

    private sealed class MixedCancellationJoinRunner : IAgentAssignmentRunner, IAgentOutcomeJoiner
    {
        private readonly TaskCompletionSource _blockingChildEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _joinCalls;

        public Task BlockingChildEntered => _blockingChildEntered.Task;

        public int JoinCalls => Volatile.Read(ref _joinCalls);

        public Task<bool> JoinAsync(
            DelegationPlan plan,
            IReadOnlyList<AgentRunOutcome> outcomes,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _joinCalls);
            return Task.FromResult(tryCommit());
        }

        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            if (assignment.AssignmentId == plan.Assignments[0].AssignmentId)
            {
                return CreateCompletedOutcome(plan, assignment);
            }

            _blockingChildEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("A cancelled child returned unexpectedly.");
        }
    }

    private sealed class NonCooperativeJoinRunner : IAgentAssignmentRunner, IAgentOutcomeJoiner
    {
        private readonly TaskCompletionSource _joinEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseJoin = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task JoinEntered => _joinEntered.Task;

        public CancellationToken JoinCancellationToken { get; private set; }

        public async Task<bool> JoinAsync(
            DelegationPlan plan,
            IReadOnlyList<AgentRunOutcome> outcomes,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            JoinCancellationToken = cancellationToken;
            _joinEntered.TrySetResult();
#pragma warning disable VSTHRD003 // This fake intentionally ignores join cancellation until released.
            await _releaseJoin.Task;
#pragma warning restore VSTHRD003
            return tryCommit();
        }

        public void ReleaseJoin()
        {
            _releaseJoin.TrySetResult();
        }

        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateCompletedOutcome(plan, assignment));
        }
    }

    private sealed class ThrowingAfterCancellationJoinRunner : IAgentAssignmentRunner, IAgentOutcomeJoiner
    {
        private readonly TaskCompletionSource _joinEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseJoin = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task JoinEntered => _joinEntered.Task;

        public async Task<bool> JoinAsync(
            DelegationPlan plan,
            IReadOnlyList<AgentRunOutcome> outcomes,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            _joinEntered.TrySetResult();
#pragma warning disable VSTHRD003 // This fake intentionally throws after non-cooperative join work.
            await _releaseJoin.Task;
#pragma warning restore VSTHRD003
            throw new InvalidOperationException("join failed after parent cancellation");
        }

        public void ReleaseJoin()
        {
            _releaseJoin.TrySetResult();
        }

        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateCompletedOutcome(plan, assignment));
        }
    }

    private sealed class FailingJoinRunner : IAgentAssignmentRunner, IAgentOutcomeJoiner
    {
        private int _joinCalls;

        public int JoinCalls => Volatile.Read(ref _joinCalls);

        public Task<bool> JoinAsync(
            DelegationPlan plan,
            IReadOnlyList<AgentRunOutcome> outcomes,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _joinCalls);
            throw new CommittedDomainEventDeliveryException(
                "A faulty joiner claimed committed delivery without admitting evidence.");
        }

        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateCompletedOutcome(plan, assignment));
        }
    }

    private sealed class EvidenceJoiningRunner(EvidenceStore evidence)
        : IAgentAssignmentRunner, IAgentOutcomeJoiner
    {
        public async Task<bool> JoinAsync(
            DelegationPlan plan,
            IReadOnlyList<AgentRunOutcome> outcomes,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            return await evidence.TryAddBatchAsync(
                [
                    new Evidence
                    {
                        EvidenceId = EvidenceId.New(),
                        SessionId = plan.Provenance.SessionId,
                        RunId = plan.Provenance.ParentRunId,
                        Kind = EvidenceKind.ToolResult,
                        Content = "joined child evidence",
                        Provenance = new EvidenceProvenance { Source = "delegate_agents test join" },
                        CollectedAt = DateTimeOffset.UtcNow,
                        Relevance = 1,
                        EstimatedTokens = 3,
                    },
                ],
                tryCommit,
                cancellationToken);
        }

        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateCompletedOutcome(plan, assignment));
        }
    }

    private sealed class MixedOutcomeRunner : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assignment.AssignmentId == plan.Assignments[0].AssignmentId)
            {
                return Task.FromResult(CreateCompletedOutcome(plan, assignment));
            }

            return Task.FromResult(new AgentRunOutcome
            {
                AssignmentId = assignment.AssignmentId,
                ChildRunId = assignment.ChildRunId,
                Role = assignment.Role,
                Generation = plan.Provenance.Generation,
                Status = AgentRunStatus.Failed,
                Usage = new AgentResourceUsage(),
                Reason = "bounded child failure",
            });
        }
    }

    private sealed class EmptyFindingRunner : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = CreateCompletedOutcome(plan, assignment);
            var findings = Assert.IsType<AgentFindingSet>(completed.Findings);
            return Task.FromResult(completed with
            {
                Findings = findings with { Findings = [] },
            });
        }
    }

    private sealed class OpposingFindingRunner : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concern = assignment.AssignmentId == plan.Assignments[0].AssignmentId;
            var completed = CreateCompletedOutcome(plan, assignment);
            var findings = Assert.IsType<AgentFindingSet>(completed.Findings);
            var finding = Assert.Single(findings.Findings) with
            {
                Summary = concern
                    ? "A bug makes Shared.Symbol unsafe."
                    : "No issue exists; Shared.Symbol is safe.",
                Symbols = ["Shared.Symbol"],
                Risk = concern ? "Unsafe behavior can fail." : "No risk found.",
            };
            return Task.FromResult(completed with
            {
                Findings = findings with { Findings = [finding] },
            });
        }
    }

    private sealed class NegatedConcernRunner : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var concern = assignment.AssignmentId == plan.Assignments[0].AssignmentId;
            var completed = CreateCompletedOutcome(plan, assignment);
            var findings = Assert.IsType<AgentFindingSet>(completed.Findings);
            var finding = Assert.Single(findings.Findings) with
            {
                Summary = concern
                    ? "Shared.Symbol is not safe."
                    : "No issue exists; Shared.Symbol is safe.",
                Symbols = ["Shared.Symbol"],
            };
            return Task.FromResult(completed with
            {
                Findings = findings with { Findings = [finding] },
            });
        }
    }

    private sealed class LargeFindingRunner(char character = '\u0800') : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = new string(character, 4_096);
            AgentFinding[] findings =
            [
                .. Enumerable.Range(0, 64).Select(index => new AgentFinding
                {
                    FindingId = Guid.NewGuid(),
                    Category = "behavior",
                    Summary = $"{index:D2}:{text}",
                    EvidenceIds = Enumerable.Range(0, 32).Select(_ => EvidenceId.New()).ToArray(),
                    Locations = [$"src/{text}"],
                    Symbols = [$"Symbol.{text}"],
                    Confidence = 0.9,
                    Uncertainty = text,
                }),
            ];
            return Task.FromResult(new AgentRunOutcome
            {
                AssignmentId = assignment.AssignmentId,
                ChildRunId = assignment.ChildRunId,
                Role = assignment.Role,
                Generation = plan.Provenance.Generation,
                Status = AgentRunStatus.Completed,
                Usage = new AgentResourceUsage { ModelTokens = 16_000, ToolCalls = 12 },
                Reason = "bounded large result",
                Findings = new AgentFindingSet
                {
                    AssignmentId = assignment.AssignmentId,
                    ChildRunId = assignment.ChildRunId,
                    Generation = plan.Provenance.Generation,
                    Summary = text,
                    Findings = findings,
                    UnresolvedQuestions = Enumerable.Repeat(text, 32).ToArray(),
                    CoverageNotes = Enumerable.Repeat(text, 32).ToArray(),
                },
            });
        }
    }

    private sealed class OversizedFindingFieldRunner : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = CreateCompletedOutcome(plan, assignment);
            var findings = Assert.IsType<AgentFindingSet>(completed.Findings);
            var finding = Assert.Single(findings.Findings) with
            {
                Summary = new string('x', 2_048),
            };
            return Task.FromResult(completed with
            {
                Findings = findings with { Findings = [finding] },
            });
        }
    }

    private sealed class MultiLocationFindingRunner : IAgentAssignmentRunner
    {
        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = CreateCompletedOutcome(plan, assignment);
            var findings = Assert.IsType<AgentFindingSet>(completed.Findings);
            var finding = Assert.Single(findings.Findings) with
            {
                Locations = ["src/first.cs", "src/second.cs"],
                Symbols = ["First.Symbol", "Second.Symbol"],
            };
            return Task.FromResult(completed with
            {
                Findings = findings with { Findings = [finding] },
            });
        }
    }

    private sealed class ConcurrentFindingRunner(TimeSpan delay) : IAgentAssignmentRunner
    {
        private int _active;
        private int _maximumActive;

        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(delay, cancellationToken);
                return CreateCompletedOutcome(plan, assignment);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActive);
                if (current >= active
                    || Interlocked.CompareExchange(ref _maximumActive, active, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ReleasableFindingRunner : IAgentAssignmentRunner
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release()
        {
            _release.TrySetResult();
        }

        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CreateCompletedOutcome(plan, assignment);
        }
    }

    private sealed class BlockingRunner(int expectedActive) : IAgentAssignmentRunner
    {
        private readonly TaskCompletionSource _allEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _active;

        public int Active => Volatile.Read(ref _active);

        public Task AllEntered => _allEntered.Task;

        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _active) == expectedActive)
            {
                _allEntered.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("A blocked child completed without cancellation.");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class RecordingCheckpointStore : IDelegationCheckpointStore
    {
        private readonly TaskCompletionSource<DelegationCheckpoint> _childrenRunning = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Lock _gate = new();
        private readonly List<DelegationCheckpoint> _history = [];
        private readonly Dictionary<DelegationId, DelegationCheckpoint> _latest = [];

        public Task<DelegationCheckpoint> ChildrenRunning => _childrenRunning.Task;

        public IReadOnlyList<DelegationCheckpoint> History
        {
            get
            {
                lock (_gate)
                {
                    return _history.ToArray();
                }
            }
        }

        public Task<bool> SaveAsync(
            DelegationCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applied = false;
            lock (_gate)
            {
                if (!_latest.TryGetValue(checkpoint.DelegationId, out var current)
                    || current.Revision < checkpoint.Revision)
                {
                    _latest[checkpoint.DelegationId] = checkpoint;
                    _history.Add(checkpoint);
                    applied = true;
                }
            }

            if (applied && checkpoint.Phase == DelegationCheckpointPhase.ChildrenRunning)
            {
                _childrenRunning.TrySetResult(checkpoint);
            }

            return Task.FromResult(applied);
        }

        public Task<DelegationCheckpoint?> GetAsync(
            DelegationId delegationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_latest.GetValueOrDefault(delegationId));
            }
        }
    }

    private sealed class DelayedProgressCheckpointStore : IDelegationCheckpointStore
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _lateSaveCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private DelegationCheckpoint? _latest;
        private int _lateSaveCount;

        public Task LateSaveCompleted => _lateSaveCompleted.Task;

        public DelegationCheckpoint? Latest
        {
            get
            {
                lock (_gate)
                {
                    return _latest;
                }
            }
        }

        public void ReleaseProgress()
        {
            _release.TrySetResult();
        }

        public async Task<bool> SaveAsync(
            DelegationCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            if (checkpoint.Phase == DelegationCheckpointPhase.ChildrenRunning)
            {
#pragma warning disable VSTHRD003 // The fake intentionally outlives the caller's progress timeout.
                await _release.Task;
#pragma warning restore VSTHRD003
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var applied = false;
            lock (_gate)
            {
                if (_latest is null || _latest.Revision < checkpoint.Revision)
                {
                    _latest = checkpoint;
                    applied = true;
                }
            }

            if (checkpoint.Phase == DelegationCheckpointPhase.ChildrenRunning)
            {
                if (Interlocked.Increment(ref _lateSaveCount) == 2)
                {
                    _lateSaveCompleted.TrySetResult();
                }
            }

            return applied;
        }

        public Task<DelegationCheckpoint?> GetAsync(
            DelegationId delegationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_latest?.DelegationId == delegationId ? _latest : null);
            }
        }
    }

    private sealed class BlockingProgressCheckpointStore : IDelegationCheckpointStore
    {
        private readonly TaskCompletionSource<bool> _never = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public DelegationCheckpoint? Latest { get; private set; }

        public Task<bool> SaveAsync(
            DelegationCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            if (checkpoint.Phase == DelegationCheckpointPhase.ChildrenRunning)
            {
#pragma warning disable VSTHRD003 // The fake intentionally returns a never-completing external store task.
                return _never.Task;
#pragma warning restore VSTHRD003
            }

            cancellationToken.ThrowIfCancellationRequested();
            Latest = checkpoint;
            return Task.FromResult(true);
        }

        public Task<DelegationCheckpoint?> GetAsync(
            DelegationId delegationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Latest?.DelegationId == delegationId ? Latest : null);
        }
    }

    private sealed class StubWorkspaceResolver : ITransactionalWorkspaceResolver
    {
        private readonly ITransactionalWorkspace _workspace;

        public StubWorkspaceResolver(WorkspaceBaseline baseline)
        {
            _workspace = new StubWorkspace(baseline);
        }

        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId)
        {
            return _workspace.Baseline.WorkspaceId == workspaceId
                ? _workspace
                : throw new KeyNotFoundException();
        }

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<WorkspaceBaseline> PromoteBaselineAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubWorkspace : ITransactionalWorkspace
    {
        public StubWorkspace(WorkspaceBaseline baseline)
        {
            Baseline = baseline;
            Isolation = new WorkspaceIsolation(
                WorkspaceIsolationMode.TrackedInPlace,
                baseline.RepositoryPath,
                baseline.GitRevision);
        }

        public WorkspaceBaseline Baseline { get; }

        public WorkspaceIsolation Isolation { get; }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<string?> ReadBaselineTextAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ReadStagedTextAsync(
            MutationSetId mutationSetId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MutationPreview> SetPreviewEnabledAsync(
            MutationSetId mutationSetId,
            MutationId mutationId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MutationCommitResult> CommitAsync(
            MutationSetId mutationSetId,
            MutationApproval approval,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<FileLifecycleReconciliation>> ReconcileLifecycleAsync(
            MutationSetId mutationSetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MutationRollbackResult> RollbackAsync(
            MutationSetId mutationSetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<WorkspaceBaseline> PromoteBaselineAsync(
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
