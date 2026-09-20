namespace Threadsmith.ExecutionOrchestration.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

public sealed partial class ExecutionOrchestratorTests
{
    /// <summary>Interrupted planning remains resumable without completion, even with history disabled.</summary>
    [Fact]
    public async Task Replan_ConversationPausePreservesReceiptAndWithholdsCompletion()
    {
        var fixture = CreateFixture();
        var request = fixture.StartRequest with { AllowPlanContinuation = true };
        SetProposals(fixture, Replan());
        await fixture.Orchestrator.StartAsync(request);
        var restored = fixture with { StartRequest = request, Orchestrator = RecreateOrchestrator(fixture) };
        await using var scenario = await ConversationScenario.CreateRestoredAsync(restored);
        await scenario.Store.SetModeAsync(request.SessionId, ConversationContextMode.Stateless);

        await scenario.Dispatcher.DispatchAsync(new ResumeRunCommand(request.SessionId, request.RunId));
        Assert.False(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(request.RunId)));

        var modelRequest = scenario.Model.LastRequest!;
        Assert.DoesNotContain(modelRequest.Tools, tool => tool.Name == "complete_objective");
        Assert.Contains(modelRequest.Tools, tool => tool.Name == "propose_plan");
        Assert.Contains(modelRequest.Messages, message => message.GetModelVisibleContent().Contains("PlanReplanningPending", StringComparison.Ordinal));
        Assert.Contains(modelRequest.Messages, message => message.GetModelVisibleContent().Contains("Inspect the missing dependency", StringComparison.Ordinal));
        Assert.Contains(modelRequest.Messages, message => message.GetModelVisibleContent().Contains("Change observable behavior", StringComparison.Ordinal));
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(request.RunId));
        Assert.Single(fixture.ProposalHandler.Commands);
        Assert.Empty(fixture.CommitHandler.Commands);
    }

    /// <summary>Replacement approval and exact-diff authorization use the ordinary conversation entry points.</summary>
    [Fact]
    public async Task Replan_ConversationApprovesReplacementAndCompletesSameRun()
    {
        var fixture = CreateFixture();
        var request = fixture.StartRequest with { AllowPlanContinuation = true };
        SetProposals(fixture, Replan(), Proposal(fixture.Staged, true));
        await fixture.Orchestrator.StartAsync(request);
        var restored = fixture with { StartRequest = request, Orchestrator = RecreateOrchestrator(fixture) };
        await using var scenario = await ConversationScenario.CreateRestoredAsync(restored, planProposalCount: 1);
        var awaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = fixture.Events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is RunTransitioned { Destination: RunPhase.AwaitingPlanApproval })
            {
                awaiting.TrySetResult();
            }

            return Task.CompletedTask;
        });

        await scenario.Dispatcher.DispatchAsync(new ResumeRunCommand(request.SessionId, request.RunId));
        await awaiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(fixture.ProposalHandler.Commands);
        Assert.Empty(fixture.CommitHandler.Commands);
        Assert.True(await scenario.Dispatcher.DispatchAsync(new ApprovePlanCommand(request.SessionId, request.RunId)));
        Assert.Equal(2, fixture.ProposalHandler.Commands.Count);
        Assert.Empty(fixture.CommitHandler.Commands);
        fixture.ValidationHandler.Enqueue(PassingValidation());
        await restored.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));

        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(request.RunId)));
        Assert.Contains(scenario.Model.LastRequest!.Tools, tool => tool.Name == "complete_objective");
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Equal(ExecutionCheckpointPhase.Completed, (await fixture.Checkpoints.GetOutcomeAsync(request.RunId))!.Status);
    }

    /// <summary>An initial no-mutation replan survives restart and carries forward execution budget.</summary>
    [Fact]
    public async Task Replan_BeforeFirstMutationRestoresAndRequiresReplacementApproval()
    {
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var request = fixture.StartRequest with
        {
            AllowPlanContinuation = true,
            InitialBudgetUsage = new BudgetDimensions(10, 1, TimeSpan.Zero),
        };
        SetProposals(fixture, Replan() with { BudgetUsed = new BudgetDimensions(100, 2, TimeSpan.Zero) }, Proposal(fixture.Staged, true));

        var paused = await fixture.Orchestrator.StartAsync(request);
        var restored = RecreateOrchestrator(fixture);
        var resumed = await restored.ResumeAsync(request.SessionId, request.RunId);
        var boundary = await restored.WaitForPlanCompletionAsync(request.RunId, 0);

        Assert.Equal(ExecutionCheckpointPhase.PlanReplanningPending, paused.Phase);
        Assert.Equal(paused.Phase, resumed.Phase);
        Assert.NotNull(boundary.PlanUnderRevision);
        Assert.Empty(boundary.Progress.CompletedStepIds);
        Assert.Equal([fixture.StepId], boundary.Progress.UncompletedStepIds);
        Assert.Empty(fixture.CommitHandler.Commands);
        Assert.Empty(fixture.BaselineHandler.Commands);
        Assert.Null(await restored.HandleAsync(new GetExecutionMutationCommand(request.SessionId, request.RunId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.CompleteObjectiveAsync(request.SessionId, request.RunId));

        var replacement = ReplacementRequest(fixture, request) with { InitialBudgetUsage = new BudgetDimensions(20, 2, TimeSpan.Zero) };
        var pending = await restored.ContinueWithPlanAsync(replacement);
        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, pending.Phase);
        Assert.Equal(2, pending.PlanOrdinal);
        Assert.Equal(110, fixture.ProposalHandler.Commands[^1].BudgetUsed!.Tokens);
        Assert.Equal(3, fixture.ProposalHandler.Commands[^1].BudgetUsed!.Calls);
        Assert.Empty(fixture.CommitHandler.Commands);

        await restored.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        fixture.ValidationHandler.Enqueue(fixture.ValidationHandler.LastResult);
        var outcome = await restored.CompleteObjectiveAsync(request.SessionId, request.RunId);
        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Equal([replacement.ApprovedPlan.Steps[0].StepId], outcome.CompletedStepIds);
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>Boundary assessment usage remains durable across orchestrator reconstruction.</summary>
    [Fact]
    public async Task PlanBoundary_PlanningUsageSurvivesResume()
    {
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var initialUsage = new BudgetDimensions(10, 1, TimeSpan.FromSeconds(1), 0.1m);
        var assessedUsage = new BudgetDimensions(30, 2, TimeSpan.FromSeconds(3), 0.3m);
        var request = fixture.StartRequest with
        {
            AllowPlanContinuation = true,
            InitialBudgetUsage = initialUsage,
        };
        SetProposals(fixture, Replan());
        await fixture.Orchestrator.StartAsync(request);

        await fixture.Orchestrator.RecordPlanningUsageAsync(request.SessionId, request.RunId, assessedUsage);
        var restored = RecreateOrchestrator(fixture);
        var resumedRequest = await restored.GetResumeRequestAsync(request.SessionId, request.RunId);

        Assert.Equal(assessedUsage, resumedRequest.InitialBudgetUsage);
    }

    /// <summary>Planning after a completed tranche is charged against cumulative execution usage.</summary>
    [Fact]
    public async Task PlanBoundary_CumulativeUsageSeedsReplacementPlan()
    {
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var request = fixture.StartRequest with
        {
            AllowPlanContinuation = true,
            InitialBudgetUsage = new BudgetDimensions(10, 1, TimeSpan.Zero),
        };
        SetProposals(
            fixture,
            Proposal(fixture.Staged, true) with
            {
                BudgetUsed = new BudgetDimensions(25, 2, TimeSpan.Zero),
            });
        await fixture.Orchestrator.StartAsync(request);
        var progress = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        var cumulativeUsage = new BudgetDimensions(30, 3, TimeSpan.Zero);
        await fixture.Orchestrator.RecordPlanningUsageAsync(request.SessionId, request.RunId, cumulativeUsage);
        var replacement = ReplacementRequest(fixture, request) with { InitialBudgetUsage = cumulativeUsage };
        var replacementStepId = replacement.ApprovedPlan.Steps[0].StepId;
        fixture.ProposalHandler.Results.Enqueue(Proposal(
            fixture.CorrectionStaged with { PlanStepIds = [replacementStepId] },
            true));

        await fixture.Orchestrator.ContinueWithPlanAsync(replacement);

        Assert.Equal(ExecutionCheckpointPhase.PlanContinuationPending, progress.Status);
        Assert.Equal(25, progress.BudgetUsed!.Tokens);
        Assert.Equal(cumulativeUsage, fixture.ProposalHandler.Commands[^1].BudgetUsed);
    }

    /// <summary>Usage emitted before a failed boundary assessment remains durable for the next resume.</summary>
    [Fact]
    public async Task PlanBoundary_FailedAssessmentPersistsReportedUsage()
    {
        var fixture = CreateFixture();
        var request = fixture.StartRequest with { AllowPlanContinuation = true };
        SetProposals(fixture, Replan());
        await fixture.Orchestrator.StartAsync(request);
        var restored = fixture with { StartRequest = request, Orchestrator = RecreateOrchestrator(fixture) };
        await using var scenario = await ConversationScenario.CreateRestoredAsync(restored);
        scenario.Model.Usage = new ModelUsage(12, 8, EstimatedCost: 0.25m);
        scenario.Model.ThrowAfterUsage = true;

        await scenario.Dispatcher.DispatchAsync(new ResumeRunCommand(request.SessionId, request.RunId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(request.RunId)));
        var resumeRequest = await RecreateOrchestrator(fixture).GetResumeRequestAsync(
            request.SessionId,
            request.RunId);

        Assert.True(resumeRequest.InitialBudgetUsage.Tokens >= 20);
        Assert.True(resumeRequest.InitialBudgetUsage.Calls >= 1);
        Assert.True(resumeRequest.InitialBudgetUsage.Cost >= 0.25m);
    }

    /// <summary>A pending replacement plan is restored without repeating its planning turn.</summary>
    [Fact]
    public async Task Replan_PublishedReplacementRestoresForExistingApproval()
    {
        var fixture = CreateFixture();
        var request = fixture.StartRequest with { AllowPlanContinuation = true };
        SetProposals(fixture, Replan(), Proposal(fixture.Staged, true));
        await fixture.Orchestrator.StartAsync(request);
        var replacement = ReplacementRequest(fixture, request).ApprovedPlan;
        var approvalId = ApprovalId.New();
        var otherRunId = RunId.New();
        var otherApprovalId = ApprovalId.New();
        var otherPlan = replacement with { Revision = replacement.Revision + 1 };
        var projection = new SessionProjection
        {
            Key = new ProjectionKey("session", request.SessionId.Value.ToString("D")),
            SessionId = request.SessionId,
            Name = "restored replacement",
            Phase = RunPhase.AwaitingPlanApproval,
            Plan = new PlanProjection(otherRunId, otherApprovalId, otherPlan, PlanReviewStatus.Pending),
            PendingPlans =
            [
                new PlanProjection(request.RunId, approvalId, replacement, PlanReviewStatus.Pending),
                new PlanProjection(otherRunId, otherApprovalId, otherPlan, PlanReviewStatus.Pending),
            ],
            PendingApprovals =
            [
                new ApprovalProjection(approvalId, "Approve replacement plan"),
                new ApprovalProjection(otherApprovalId, "Approve another plan"),
            ],
        };
        var restored = fixture with { StartRequest = request, Orchestrator = RecreateOrchestrator(fixture) };
        await using var scenario = await ConversationScenario.CreateRestoredAsync(
            restored,
            sessionProjection: projection);

        var checkpoint = await scenario.Dispatcher.DispatchAsync(
            new ResumeRunCommand(request.SessionId, request.RunId));

        Assert.Equal(ExecutionCheckpointPhase.PlanReplanningPending, checkpoint.Phase);
        Assert.Equal(0, scenario.Model.RequestCount);
        Assert.True(await scenario.Dispatcher.DispatchAsync(new ApprovePlanCommand(request.SessionId, request.RunId)));
        Assert.Equal(replacement, fixture.ProposalHandler.Commands[^1].ApprovedPlan);
        fixture.ValidationHandler.PassAll();
        fixture.ValidationHandler.Enqueue(PassingValidation());
        await restored.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(request.RunId)));
        Assert.Equal(1, scenario.Model.RequestCount);
    }

    /// <summary>Projection recovery retains and resolves pending plans independently for concurrent runs.</summary>
    [Fact]
    public async Task PlanProjection_TracksPendingPlansPerRun()
    {
        var fixture = CreateFixture();
        var sessionId = fixture.StartRequest.SessionId;
        var firstRunId = fixture.StartRequest.RunId;
        var secondRunId = RunId.New();
        var firstApprovalId = ApprovalId.New();
        var secondApprovalId = ApprovalId.New();
        var firstPlan = fixture.StartRequest.ApprovedPlan;
        var secondPlan = firstPlan with { Revision = firstPlan.Revision + 1 };
        var projections = new InMemoryProjectionStore();
        await projections.ApplyAsync(new SessionCreated(sessionId, DateTimeOffset.UtcNow, "pending plans"));
        await projections.ApplyAsync(new PlanProposed(
            sessionId,
            DateTimeOffset.UtcNow,
            firstPlan.Summary,
            firstRunId,
            firstPlan,
            firstApprovalId));
        await projections.ApplyAsync(new ApprovalRequested(
            sessionId,
            DateTimeOffset.UtcNow,
            firstApprovalId,
            "Approve first plan",
            ApprovalRequestKind.Plan));
        await projections.ApplyAsync(new PlanProposed(
            sessionId,
            DateTimeOffset.UtcNow,
            secondPlan.Summary,
            secondRunId,
            secondPlan,
            secondApprovalId));
        await projections.ApplyAsync(new ApprovalRequested(
            sessionId,
            DateTimeOffset.UtcNow,
            secondApprovalId,
            "Approve second plan",
            ApprovalRequestKind.Plan));

        await projections.ApplyAsync(new ApprovalGranted(sessionId, DateTimeOffset.UtcNow, secondApprovalId));
        var afterApproval = await projections.GetAsync<SessionProjection>(
            new ProjectionKey("session", sessionId.Value.ToString("D")));
        var pending = Assert.Single(afterApproval!.PendingPlans);
        Assert.Equal(firstRunId, pending.RunId);

        await projections.ApplyAsync(new PlanRevisionRequested(
            sessionId,
            DateTimeOffset.UtcNow,
            firstRunId,
            "Revise the first plan"));
        var afterRevision = await projections.GetAsync<SessionProjection>(
            new ProjectionKey("session", sessionId.Value.ToString("D")));
        Assert.Empty(afterRevision!.PendingPlans);
        Assert.Empty(afterRevision.PendingApprovals);
    }

    /// <summary>Applied work and introduced failures cannot be forgotten by replacing a plan.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Replan_AfterAppliedWorkPreservesProgressBaselineAndCumulativeValidation(bool completeFirstStep, bool validationFails)
    {
        var fixture = CreateFixture(includeSecondPlanStep: completeFirstStep, includeCorrection: true);
        await using var events = fixture.Events;
        if (!validationFails)
        {
            fixture.ValidationHandler.PassAll();
        }

        var request = fixture.StartRequest with { AllowPlanContinuation = true };
        SetProposals(fixture, Proposal(fixture.Staged, completeFirstStep), Replan(), Replan(), Proposal(fixture.CorrectionStaged, true));
        await fixture.Orchestrator.StartAsync(request);
        var progress = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        var paused = await fixture.Checkpoints.GetCheckpointAsync(request.RunId);
        var originalCapture = fixture.ValidationHandler.Commands[0].BaselineCapture;
        var restored = RecreateOrchestrator(fixture);
        await restored.ResumeAsync(request.SessionId, request.RunId);
        var boundary = await restored.WaitForPlanCompletionAsync(request.RunId, 0);

        Assert.Equal(ExecutionCheckpointPhase.PlanReplanningPending, progress.Status);
        Assert.Equal(["src/Example.cs"], progress.ChangedFiles);
        Assert.Equal(completeFirstStep ? [fixture.StepId] : Array.Empty<StepId>(), progress.CompletedStepIds);
        Assert.Equal(validationFails ? AcceptanceGateStatus.Failed : AcceptanceGateStatus.Passed, boundary.Progress.Validation!.Gate.Status);
        Assert.Contains("Inspect", boundary.Progress.ReplanReason, StringComparison.Ordinal);
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(request.RunId));

        var replacement = ReplacementRequest(fixture, request);
        var replanned = await restored.ContinueWithPlanAsync(replacement);
        Assert.Equal(ExecutionCheckpointPhase.PlanReplanningPending, replanned.Phase);
        Assert.Equal(paused!.ValidationArtifact, replanned.ValidationArtifact);
        var replannedState = await fixture.Artifacts.ReadAsync(replanned.StateArtifact!);
        Assert.NotNull(replannedState);
        Assert.DoesNotContain(
            fixture.Staged.MutationSet.MutationSetId.Value.ToString("D"),
            replannedState,
            StringComparison.OrdinalIgnoreCase);
        restored = RecreateOrchestrator(fixture);
        await restored.ResumeAsync(request.SessionId, request.RunId);
        var repeatedBoundary = await restored.WaitForPlanCompletionAsync(request.RunId, 1);
        Assert.Equal(boundary.Progress.Validation.Gate.Status, repeatedBoundary.Progress.Validation!.Gate.Status);
        Assert.Equal(boundary.Progress.CompletedStepIds, repeatedBoundary.Progress.CompletedStepIds);
        replacement = ReplacementRequest(fixture, replacement);
        var pending = await restored.ContinueWithPlanAsync(replacement);
        Assert.Equal(paused.BaselineArtifact, pending.BaselineArtifact);
        Assert.Equal(paused.CorrectionAttempts, pending.CorrectionAttempts);
        Assert.Single(fixture.BaselineHandler.Commands);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restored.ContinueAsync(CreateContinuation(fixture, fixture.Staged)));
        await restored.ContinueAsync(CreateContinuation(fixture, fixture.CorrectionStaged));
        fixture.ValidationHandler.Enqueue(fixture.ValidationHandler.LastResult);
        var outcome = await restored.CompleteObjectiveAsync(request.SessionId, request.RunId);

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.DoesNotContain(fixture.SecondStepId, outcome.CompletedStepIds);
        Assert.Equal(completeFirstStep ? 2 : 1, outcome.CompletedStepIds.Count);
        Assert.Single(fixture.BaselineHandler.Commands);
        Assert.All(fixture.ValidationHandler.Commands, command =>
        {
            Assert.Equal(originalCapture.WorkspaceId, command.BaselineCapture.WorkspaceId);
            Assert.Equal(originalCapture.BaselineCapturedAt, command.BaselineCapture.BaselineCapturedAt);
            Assert.Equal(originalCapture.CapturedAt, command.BaselineCapture.CapturedAt);
            Assert.Equal(originalCapture.Confidence, command.BaselineCapture.Confidence);
            Assert.Equal(originalCapture.Diagnostics, command.BaselineCapture.Diagnostics);
            Assert.Equal(originalCapture.BaselineCapturedAt, command.Request.Baseline.CapturedAt);
        });
        Assert.Contains("src/Additional.cs", fixture.ValidationHandler.Commands[^1].Request.AffectedPaths);
        Assert.Contains("src/Example.cs", fixture.ValidationHandler.Commands[^1].Request.AffectedPaths);
        var diff = await fixture.Artifacts.ReadAsync(outcome.FinalDiff!);
        Assert.Contains("-old", diff, StringComparison.Ordinal);
        Assert.Contains("+fixed", diff, StringComparison.Ordinal);
        Assert.Equal(2, fixture.CommitHandler.Commands.Count);
    }

    /// <summary>Replacement plans have no count ceiling and can still be restored without replay.</summary>
    [Fact]
    public async Task Replan_ReplacementsContinueBeyondFormerPlanCapAndRemainResumable()
    {
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var orchestrator = fixture.Orchestrator;
        var request = fixture.StartRequest with { AllowPlanContinuation = true };
        SetProposals(fixture, [.. Enumerable.Repeat(Replan(), 13)]);
        await orchestrator.StartAsync(request);
        for (var ordinal = 2; ordinal <= 13; ordinal++)
        {
            request = ReplacementRequest(fixture, request);
            await orchestrator.ContinueWithPlanAsync(request);
        }

        orchestrator = RecreateOrchestrator(fixture);
        var resumed = await orchestrator.ResumeAsync(request.SessionId, request.RunId);
        Assert.Equal(ExecutionCheckpointPhase.PlanReplanningPending, resumed.Phase);
        Assert.Equal(13, resumed.PlanOrdinal);
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(request.RunId));
        Assert.Empty(fixture.CommitHandler.Commands);
    }

    private static MutationProposalResult Replan() => new()
    {
        ReplanRequested = true,
        Rationale = "Inspect the missing dependency and replace unfinished work.",
    };

    private static MutationValidationResult PassingValidation() => new(
        new BuildValidationResult(true, [], [], TimeSpan.Zero),
        [],
        new TestValidationResult { Completed = true, Selection = new TestSelection() },
        new AcceptanceGateResult(AcceptanceGateStatus.Passed, []));

    private static ExecutionStartRequest ReplacementRequest(ExecutionFixture fixture, ExecutionStartRequest previous)
    {
        var baseline = fixture.WorkspaceResolver.GetWorkspace(previous.Baseline.WorkspaceId).Baseline;
        return previous with
        {
            Baseline = baseline,
            ApprovedPlan = previous.ApprovedPlan with
            {
                Revision = previous.ApprovedPlan.Revision + 1,
                Steps = [previous.ApprovedPlan.Steps[0] with { StepId = StepId.New(), Title = "Finish remaining work" }],
            },
            ValidationRequest = previous.ValidationRequest with { Baseline = baseline, AffectedPaths = ["src/Additional.cs"] },
        };
    }
}
