namespace Threadsmith.ExecutionOrchestration.Tests;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Verifies Plan 37 orchestration, write-ahead, resume, and migration contracts.</summary>
public sealed partial class ExecutionOrchestratorTests
{
    /// <summary>Verifies an approved plan stages an exact diff and completes only after separate authorization and validation.</summary>
    [Fact]
    public async Task ApprovedPlan_StagesThenAppliesAndValidates_WithDurableCheckpoints()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var request = fixture.StartRequest;

        // Act
        var pending = await fixture.Orchestrator.StartAsync(request);
        var staged = await fixture.Orchestrator.HandleAsync(
            new GetExecutionMutationCommand(request.SessionId, request.RunId));
        var outcome = await fixture.Orchestrator.ContinueAsync(
            new ContinueExecutionRequest
            {
                SessionId = request.SessionId,
                RunId = request.RunId,
                Approval = new MutationApproval
                {
                    Level = MutationApprovalLevel.EntireSet,
                    ApprovalId = fixture.Staged.ApprovalId,
                },
                ApprovalProvenance = "test user",
            });

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, pending.Phase);
        Assert.NotNull(pending.DiffArtifact);
        Assert.NotNull(staged);
        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Equal(["src/Example.cs"], outcome.ChangedFiles);
        Assert.NotNull(outcome.Validation);
        Assert.Contains(observed, item => item is ExecutionSideEffectRecorded
        {
            State: ExecutionOperationState.Pending,
        });
        Assert.Contains(observed, item => item is ExecutionSideEffectRecorded
        {
            State: ExecutionOperationState.Completed,
        });
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Single(fixture.ValidationHandler.Commands);
    }

    /// <summary>Verifies multiple ordinary plans can complete one objective without terminalizing the shared run early.</summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task IncrementalPlans_PreserveCumulativeOutcome(
        bool failValidation,
        bool cancelAfterProposal,
        bool failCommit,
        bool failFinalValidation)
    {
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var secondStepId = StepId.New();
        var secondPlan = fixture.StartRequest.ApprovedPlan with
        {
            Revision = fixture.StartRequest.ApprovedPlan.Revision + 1,
            Summary = "Finish example",
            Steps =
            [
                fixture.StartRequest.ApprovedPlan.Steps[0] with
                {
                    StepId = secondStepId,
                    Title = "Finish example",
                    ExpectedOutcome = "Example behavior is complete.",
                },
            ],
            Risks = ["Second plan risk."],
        };
        var secondStaged = fixture.CorrectionStaged with { PlanStepIds = [secondStepId] };
        fixture.ProposalHandler.Results.Enqueue(new MutationProposalResult
        {
            StagedMutationSet = secondStaged,
            StepComplete = true,
            Rationale = secondStaged.MutationSet.Rationale,
        });
        fixture.CommitHandler.Enqueue(new MutationCommitResult(
            secondStaged.MutationSet.MutationSetId,
            secondStaged.MutationSet.Mutations.Select(mutation => mutation.MutationId).ToArray(),
            ["src/Example.cs"],
            "revision",
            false));
        var firstRequest = fixture.StartRequest with
        {
            ApprovedPlan = fixture.StartRequest.ApprovedPlan with { Risks = ["First plan risk."] },
            AllowPlanContinuation = true,
            InitialBudgetUsage = new BudgetDimensions(10, 1, TimeSpan.FromSeconds(1)),
            CorrectionBudget = 0,
            ValidationRequest = fixture.StartRequest.ValidationRequest with
            {
                AffectedPaths = ["src/First.cs"],
                Projects =
                [
                    new AffectedProject(
                        "First",
                        "src/First/First.csproj",
                        ["net10.0"],
                        SemanticConfidenceLevel.FullSemantic,
                        true),
                ],
            },
        };
        var terminalWait = fixture.Orchestrator.WaitForOutcomeAsync(firstRequest.RunId);

        _ = await fixture.Orchestrator.StartAsync(firstRequest);
        var firstProgress = await fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged));
        var firstBoundary = await fixture.Orchestrator.WaitForPlanCompletionAsync(
            firstRequest.RunId,
            afterPlanOrdinal: 0);

        Assert.Equal(ExecutionCheckpointPhase.PlanContinuationPending, firstProgress.Status);
        Assert.Null(firstProgress.FinalDiff);
        Assert.Equal(1, firstBoundary.PlanOrdinal);
        Assert.False(terminalWait.IsCompleted);
        fixture.ValidationHandler.Enqueue(fixture.ValidationHandler.LastResult with
        {
            Gate = new AcceptanceGateResult(
                failValidation ? AcceptanceGateStatus.Failed : AcceptanceGateStatus.Passed,
                failValidation ? ["Second tranche failed validation."] : []),
        });
        fixture.ValidationHandler.Enqueue(fixture.ValidationHandler.LastResult with
        {
            Gate = new AcceptanceGateResult(
                failFinalValidation ? AcceptanceGateStatus.Failed : AcceptanceGateStatus.Passed,
                failFinalValidation ? ["Cumulative validation found a regression."] : []),
        });

        var currentBaseline = fixture.WorkspaceResolver
            .GetWorkspace(firstRequest.Baseline.WorkspaceId)
            .Baseline;
        var secondRequest = firstRequest with
        {
            Baseline = currentBaseline,
            ApprovedPlan = secondPlan,
            ValidationRequest = firstRequest.ValidationRequest with
            {
                Baseline = currentBaseline,
                AffectedPaths = ["src/Second.cs"],
                Projects =
                [
                    new AffectedProject(
                        "Second",
                        "src/Second/Second.csproj",
                        ["net10.0"],
                        SemanticConfidenceLevel.FullSemantic,
                        true),
                ],
            },
            InitialBudgetUsage = new BudgetDimensions(15, 2, TimeSpan.FromSeconds(2)),
        };
        using var cancellation = new CancellationTokenSource();
        fixture.ProposalHandler.AfterProposal = cancelAfterProposal ? cancellation.Cancel : null;
        var secondPending = await fixture.Orchestrator.ContinueWithPlanAsync(secondRequest, cancellation.Token);
        if (cancelAfterProposal)
        {
            var resumed = await RecreateOrchestrator(fixture).ResumeAsync(firstRequest.SessionId, firstRequest.RunId);
            Assert.Equal(secondPending.MutationSetId, resumed.MutationSetId);
            Assert.Equal(2, fixture.ProposalHandler.Commands.Count);
        }

        ExecutionOutcomeProjection secondProgress;
        if (failCommit)
        {
            fixture.CommitHandler.ThrowOnNext = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.ContinueAsync(
                CreateContinuation(fixture, secondStaged)));
            secondProgress = await fixture.Checkpoints.GetOutcomeAsync(firstRequest.RunId)
                ?? throw new InvalidOperationException("The failed commit outcome was not recorded.");
        }
        else
        {
            secondProgress = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, secondStaged));
        }

        if (failValidation || failCommit)
        {
            Assert.Equal(ExecutionCheckpointPhase.Failed, secondProgress.Status);
            Assert.Equal([fixture.StepId], secondProgress.CompletedStepIds);
            Assert.Equal([secondStepId], secondProgress.UncompletedStepIds);
            Assert.All(firstProgress.BehaviorSummary, behavior => Assert.Contains(behavior, secondProgress.BehaviorSummary));
            Assert.All(firstProgress.ResidualRisks, risk => Assert.Contains(risk, secondProgress.ResidualRisks));
            Assert.Contains("First plan risk.", secondProgress.ResidualRisks);
            Assert.Same(secondProgress, await terminalWait);
            return;
        }

        var secondBoundary = await fixture.Orchestrator.WaitForPlanCompletionAsync(
            firstRequest.RunId,
            afterPlanOrdinal: 1);
        var boundaryCheckpoint = await fixture.Checkpoints.GetCheckpointAsync(firstRequest.RunId)
            ?? throw new InvalidOperationException("The second plan boundary checkpoint is missing.");
        var compactState = await fixture.Artifacts.ReadAsync(boundaryCheckpoint.StateArtifact!)
            ?? throw new InvalidOperationException("The second plan boundary state is missing.");
        Assert.DoesNotContain("\"AppliedDiffs\"", compactState, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PriorValidations\"", compactState, StringComparison.Ordinal);
        var outcome = await fixture.Orchestrator.CompleteObjectiveAsync(
            firstRequest.SessionId,
            firstRequest.RunId);

        if (failFinalValidation)
        {
            Assert.Equal(ExecutionCheckpointPhase.Failed, outcome.Status);
            Assert.Equal(
                ["Cumulative validation found a regression."],
                outcome.Validation?.Gate.Reasons);
            Assert.Same(outcome, await terminalWait);
            Assert.Equal(3, fixture.ValidationHandler.Commands.Count);
            return;
        }

        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, secondPending.Phase);
        Assert.Equal(2, secondPending.PlanOrdinal);
        Assert.Equal(ExecutionCheckpointPhase.PlanContinuationPending, secondProgress.Status);
        Assert.Equal(2, secondBoundary.PlanOrdinal);
        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Equal([fixture.StepId, secondStepId], outcome.CompletedStepIds);
        Assert.Equal(2, outcome.BehaviorSummary.Count);
        Assert.Equal(["src/Example.cs"], outcome.ChangedFiles);
        Assert.Same(outcome, await terminalWait);
        Assert.Equal(2, fixture.CommitHandler.Commands.Count);
        Assert.Equal(3, fixture.ValidationHandler.Commands.Count);
        var finalValidation = fixture.ValidationHandler.Commands[^1].Request;
        Assert.Contains("src/First.cs", finalValidation.AffectedPaths);
        Assert.Contains("src/Second.cs", finalValidation.AffectedPaths);
        Assert.Contains("src/Example.cs", finalValidation.AffectedPaths);
        Assert.Contains(finalValidation.Projects, project => project.Name == "First");
        Assert.Contains(finalValidation.Projects, project => project.Name == "Second");
        Assert.Equal(15, fixture.ProposalHandler.Commands[1].BudgetUsed?.Tokens);
        Assert.Equal(2, fixture.ProposalHandler.Commands[1].BudgetUsed?.Calls);
    }

    /// <summary>Verifies early mutation authorization waits for the review-ready checkpoint instead of restoring the start-request artifact as active state.</summary>
    [Fact]
    public async Task ApplyExecutionMutation_WhenReviewEventBeatsCheckpoint_WaitsForMutationApprovalCheckpoint()
    {
        var fixture = CreateFixture(blockFirstProposal: true);
        await using var events = fixture.Events;
        var startTask = fixture.Orchestrator.StartAsync(fixture.StartRequest);
        await fixture.ProposalHandler.FirstHandleEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var applyTask = fixture.Orchestrator.HandleAsync(
            new ApplyExecutionMutationCommand(CreateContinuation(fixture, fixture.Staged)));
        try
        {
            Assert.False(applyTask.IsCompleted);
        }
        finally
        {
            fixture.ProposalHandler.ReleaseFirstHandle();
        }

        var pending = await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        var applied = await applyTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, pending.Phase);
        Assert.Equal(ExecutionCheckpointPhase.MutationApplied, applied.Continuation.Phase);
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>Verifies apply-only continuation writes the mutation and defers validation to resume.</summary>
    [Fact]
    public async Task ApplyExecutionMutation_StopsBeforeValidationUntilResumed()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var continuation = CreateContinuation(fixture, fixture.Staged);

        // Act
        var applied = await fixture.Orchestrator.HandleAsync(
            new ApplyExecutionMutationCommand(continuation));

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.MutationApplied, applied.Continuation.Phase);
        Assert.Equal(["src/Example.cs"], applied.ChangedFiles);
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Empty(fixture.ValidationHandler.Commands);
        var baselineCommand = Assert.Single(fixture.BaselineHandler.Commands);
        Assert.Same(fixture.Staged.MutationSet, baselineCommand.MutationSet);

        var resumed = await fixture.Orchestrator.ResumeAsync(
            fixture.StartRequest.SessionId,
            fixture.StartRequest.RunId);
        Assert.Equal(ExecutionCheckpointPhase.Completed, resumed.Phase);
        Assert.Single(fixture.ValidationHandler.Commands);
    }

    /// <summary>Verifies a failed validation pre-capture save does not poison later apply in memory.</summary>
    [Fact]
    public async Task PrepareValidation_SaveFailure_DoesNotPoisonApplyBaselineCapture()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var pending = await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        fixture.Checkpoints.ThrowOnNextSave = true;

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.HandleAsync(new PrepareExecutionValidationCommand(
                fixture.StartRequest.SessionId,
                fixture.StartRequest.RunId)));
        Assert.Single(fixture.BaselineHandler.Commands);
        var afterFailure = await fixture.Checkpoints.GetCheckpointAsync(
                fixture.StartRequest.RunId)
            ?? throw new InvalidOperationException("The execution checkpoint was not saved.");
        Assert.Equal(pending.RecordedAt, afterFailure.RecordedAt);
        Assert.Null(afterFailure.BaselineArtifact);

        var applied = await fixture.Orchestrator.HandleAsync(
            new ApplyExecutionMutationCommand(CreateContinuation(fixture, fixture.Staged)));

        Assert.Equal(ExecutionCheckpointPhase.MutationApplied, applied.Continuation.Phase);
        Assert.Equal(2, fixture.BaselineHandler.Commands.Count);
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>Verifies duplicate terminal continuation is denied and never repeats a repository effect.</summary>
    [Fact]
    public async Task ContinueAfterTerminal_DoesNotRepeatCommit()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var continuation = new ContinueExecutionRequest
        {
            SessionId = fixture.StartRequest.SessionId,
            RunId = fixture.StartRequest.RunId,
            Approval = new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = fixture.Staged.ApprovalId,
            },
            ApprovalProvenance = "test user",
        };
        _ = await fixture.Orchestrator.ContinueAsync(continuation);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ContinueAsync(continuation));
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>Verifies a pending write-ahead side effect fails resume closed rather than being replayed.</summary>
    [Fact]
    public async Task ResumePendingSideEffect_FailsClosed()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var pending = await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        await fixture.Checkpoints.SaveCheckpointAsync(pending with
        {
            Phase = ExecutionCheckpointPhase.MutationApplyPending,
            Operation = new ExecutionOperationRecord
            {
                OperationId = Guid.NewGuid(),
                Kind = "mutation-commit",
                State = ExecutionOperationState.Pending,
                ExpectedPreState = pending.MutationBaselineIdentity,
            },
        });

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ResumeAsync(
                fixture.StartRequest.SessionId,
                fixture.StartRequest.RunId));
        Assert.Empty(fixture.CommitHandler.Commands);
    }

    /// <summary>Verifies a cancelled commit with proven compensation resumes at fresh mutation authorization.</summary>
    [Fact]
    public async Task ResumeCompensatedMutation_ReturnsToMutationAuthorization()
    {
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var pending = await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        await fixture.Checkpoints.SaveCheckpointAsync(pending with
        {
            Phase = ExecutionCheckpointPhase.Cancelled,
            Operation = new ExecutionOperationRecord
            {
                OperationId = Guid.NewGuid(),
                Kind = "mutation-commit",
                State = ExecutionOperationState.RolledBack,
                ExpectedPreState = pending.MutationBaselineIdentity,
                Reconciliation = "mutation:Compensated",
            },
        });

        var resumed = await fixture.Orchestrator.ResumeAsync(
            fixture.StartRequest.SessionId,
            fixture.StartRequest.RunId);

        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, resumed.Phase);
        Assert.Equal(ExecutionOperationState.RolledBack, resumed.Operation?.State);
        Assert.Contains("fresh mutation authorization", resumed.NextAction, StringComparison.Ordinal);
        Assert.Empty(fixture.CommitHandler.Commands);
    }

    /// <summary>Verifies interrupted baseline validation resumes at a fresh authorization boundary.</summary>
    [Fact]
    public async Task ResumeBaselineValidation_ReturnsToMutationAuthorization()
    {
        // Arrange
        var fixture = CreateFixture(includeBuildValidation: true);
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        fixture.BaselineHandler.ThrowOnNext = true;
        var continuation = CreateContinuation(fixture, fixture.Staged);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ContinueAsync(continuation));

        // Act
        var resumed = await fixture.Orchestrator.ResumeAsync(
            fixture.StartRequest.SessionId,
            fixture.StartRequest.RunId);
        var outcome = await fixture.Orchestrator.ContinueAsync(continuation);

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, resumed.Phase);
        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>Verifies an applied mutation resumes by replaying validation without replaying the commit.</summary>
    [Fact]
    public async Task ResumeMutationApplied_ReplaysValidationOnly()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        fixture.ValidationHandler.ThrowOnNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged)));

        // Act
        var resumed = await fixture.Orchestrator.ResumeAsync(
            fixture.StartRequest.SessionId,
            fixture.StartRequest.RunId);

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.Completed, resumed.Phase);
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Equal(2, fixture.ValidationHandler.Commands.Count);
    }

    /// <summary>Verifies successful partial proposals complete only their explicitly correlated plan steps.</summary>
    [Fact]
    public async Task PartialProposal_CompletesOnlyCorrelatedPlanSteps()
    {
        // Arrange
        var fixture = CreateFixture(includeSecondPlanStep: true);
        fixture.ProposalHandler.Results.Enqueue(new MutationProposalResult
        {
            StagedMutationSet = fixture.CorrectionStaged,
            StepComplete = true,
            Rationale = "Implement next step.",
        });
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);

        // Act
        var outcome = await fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged));

        // Assert
        Assert.Equal([fixture.StepId], outcome.CompletedStepIds);
        Assert.Equal([fixture.SecondStepId], outcome.UncompletedStepIds);
        Assert.DoesNotContain("Untouched behavior changes.", outcome.BehaviorSummary);
        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, outcome.Status);
        Assert.Null(outcome.FinalDiff);
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(fixture.StartRequest.RunId));
    }

    /// <summary>Verifies a selectively committed subset cannot complete every step claimed by its proposal.</summary>
    [Fact]
    public async Task SelectiveCommit_DoesNotCompleteClaimedPlanSteps()
    {
        // Arrange
        var fixture = CreateFixture(includeRejectedLifecycleMutation: true);
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var committedMutationId = fixture.Staged.MutationSet.Mutations[0].MutationId;
        var continuation = CreateContinuation(fixture, fixture.Staged) with
        {
            Approval = new MutationApproval
            {
                Level = MutationApprovalLevel.SelectedMutations,
                ApprovalId = fixture.Staged.ApprovalId,
                SelectedMutations = [committedMutationId],
            },
        };

        // Act
        var outcome = await fixture.Orchestrator.ContinueAsync(continuation);

        // Assert
        Assert.Empty(outcome.CompletedStepIds);
        Assert.Equal([fixture.StepId], outcome.UncompletedStepIds);
        Assert.Equal(ExecutionCheckpointPhase.ContinuationPending, outcome.Status);
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(fixture.StartRequest.RunId));
        Assert.Single(fixture.ProposalHandler.Commands);
        Assert.Null(await fixture.Orchestrator.HandleAsync(new GetExecutionMutationCommand(
            fixture.StartRequest.SessionId, fixture.StartRequest.RunId)));
    }

    /// <summary>Verifies lifecycle projections exclude lifecycle mutations omitted by selective approval.</summary>
    [Fact]
    public async Task SelectiveCommit_ReportsOnlyCommittedLifecycleChanges()
    {
        // Arrange
        var fixture = CreateFixture(includeRejectedLifecycleMutation: true);
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var committedMutationId = fixture.Staged.MutationSet.Mutations[0].MutationId;
        var continuation = CreateContinuation(fixture, fixture.Staged) with
        {
            Approval = new MutationApproval
            {
                Level = MutationApprovalLevel.SelectedMutations,
                ApprovalId = fixture.Staged.ApprovalId,
                SelectedMutations = [committedMutationId],
            },
        };

        // Act
        var outcome = await fixture.Orchestrator.ContinueAsync(
            continuation);

        // Assert
        Assert.Empty(outcome.LifecycleChanges);
    }

    /// <summary>Verifies failed commit reconciliation records and signals a terminal failed outcome.</summary>
    [Fact]
    public async Task FailedCommit_TerminalizesOutcomeAndUnblocksWaiter()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        fixture.CommitHandler.ThrowOnNext = true;
        var waiter = fixture.Orchestrator.WaitForOutcomeAsync(
            fixture.StartRequest.RunId,
            TestContext.Current.CancellationToken);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged)));
        var outcome = await waiter.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome.Status);
        Assert.Empty(outcome.CompletedStepIds);
        Assert.Equal([fixture.StepId], outcome.UncompletedStepIds);
        Assert.Same(
            outcome,
            await fixture.Checkpoints.GetOutcomeAsync(
                fixture.StartRequest.RunId,
                TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies a failed correction commit retains the already-applied diff and validation evidence.</summary>
    [Fact]
    public async Task FailedCorrectionCommit_PreservesCumulativeEvidence()
    {
        // Arrange
        var fixture = CreateFixture(includeCorrection: true);
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        _ = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        fixture.CommitHandler.ThrowOnNext = true;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.CorrectionStaged)));
        var outcome = await fixture.Orchestrator.WaitForOutcomeAsync(
            fixture.StartRequest.RunId,
            TestContext.Current.CancellationToken);
        var finalDiff = outcome.FinalDiff is null
            ? null
            : await fixture.Artifacts.ReadAsync(outcome.FinalDiff);

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome.Status);
        Assert.NotNull(finalDiff);
        Assert.Contains("-old", finalDiff, StringComparison.Ordinal);
        Assert.Contains("+new", finalDiff, StringComparison.Ordinal);
        Assert.NotNull(outcome.Validation);
        Assert.Equal(AcceptanceGateStatus.Failed, outcome.Validation.Gate.Status);
        Assert.True(outcome.RollbackAvailable);
        Assert.Contains("Plan risk.", outcome.ResidualRisks);
        Assert.Contains("Correction required.", outcome.ResidualRisks);
    }

    /// <summary>Oversized cumulative diffs are omitted instead of publishing partial or unbounded evidence.</summary>
    [Fact]
    public async Task FinalDiff_ExceedingConfiguredOutputLimit_IsOmitted()
    {
        var fixture = CreateFixture(workspaceLimits: new WorkspaceResourceLimits
        {
            MaximumFinalDiffCharacters = 32,
        });
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);

        var outcome = await fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged));

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Null(outcome.FinalDiff);
    }

    /// <summary>Verifies a proven compensated failure does not advertise another rollback.</summary>
    [Fact]
    public async Task CompensatedCommitFailure_DoesNotAdvertiseRollback()
    {
        // Arrange
        var fixture = CreateFixture(includeRejectedLifecycleMutation: true);
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        fixture.WorkspaceResolver.Reconciliations =
        [
            .. fixture.Staged.MutationSet.Mutations.Select(mutation =>
                new FileLifecycleReconciliation(
                    mutation.MutationId,
                    FileLifecycleReconciliationState.Compensated,
                    mutation.RelativePath,
                    null,
                    "Mutation endpoint restored.")),
        ];
        fixture.CommitHandler.ThrowOnNext = true;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged)));
        var outcome = await fixture.Orchestrator.WaitForOutcomeAsync(
            fixture.StartRequest.RunId,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.False(outcome.RollbackAvailable);
        Assert.NotEmpty(outcome.LifecycleReconciliations);
        Assert.All(
            outcome.LifecycleReconciliations,
            item => Assert.Equal(FileLifecycleReconciliationState.Compensated, item.State));
    }

    /// <summary>Verifies concurrent continuations consume one pending authorization serially.</summary>
    [Fact]
    public async Task ConcurrentContinue_OnlyOneConsumesPendingAuthorization()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        fixture.CommitHandler.BlockOnNext = true;
        var continuation = CreateContinuation(fixture, fixture.Staged);

        // Act
        var first = fixture.Orchestrator.ContinueAsync(continuation);
        await fixture.CommitHandler.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var second = AssertContinuationRejectedAsync(fixture.Orchestrator, continuation);
        fixture.CommitHandler.Release();
        var outcome = await first;

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        await second;
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>Resume request restoration cannot race an initial proposal already in flight.</summary>
    [Fact]
    public async Task ConcurrentResumeRequest_WaitsForInitialProposalGate()
    {
        var fixture = CreateFixture(blockFirstProposal: true);
        await using var events = fixture.Events;
        var start = fixture.Orchestrator.StartAsync(fixture.StartRequest);
        await fixture.ProposalHandler.FirstHandleEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var resumeRequest = fixture.Orchestrator.GetResumeRequestAsync(
            fixture.StartRequest.SessionId,
            fixture.StartRequest.RunId);
        try
        {
            Assert.False(resumeRequest.IsCompleted);
        }
        finally
        {
            fixture.ProposalHandler.ReleaseFirstHandle();
        }

        var pending = await start.WaitAsync(TimeSpan.FromSeconds(5));
        var restoredRequest = await resumeRequest.WaitAsync(TimeSpan.FromSeconds(5));
        var resumed = await fixture.Orchestrator.ResumeAsync(
            fixture.StartRequest.SessionId,
            fixture.StartRequest.RunId);

        Assert.Equal(fixture.StartRequest.RunId, restoredRequest.RunId);
        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, resumed.Phase);
        Assert.Equal(pending.MutationSetId, resumed.MutationSetId);
        Assert.Single(fixture.ProposalHandler.Commands);
    }

    /// <summary>Verifies resume waits for an in-flight continuation on the same run.</summary>
    [Fact]
    public async Task ConcurrentResume_WaitsForContinuationGate()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        fixture.CommitHandler.BlockOnNext = true;

        // Act
        var continuation = fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged));
        await fixture.CommitHandler.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var resume = AssertResumeRejectedAsync(fixture);

        // Assert
        try
        {
            Assert.False(resume.IsCompleted);
        }
        finally
        {
            fixture.CommitHandler.Release();
        }

        _ = await continuation;
        await resume;
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>Verifies lifecycle-only reconciliation cannot prove a mixed transaction was fully restored.</summary>
    [Fact]
    public async Task FailedMixedCommit_WithOnlyLifecycleCompensation_RequiresRecovery()
    {
        // Arrange
        var fixture = CreateFixture(includeRejectedLifecycleMutation: true);
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var lifecycleMutation = fixture.Staged.MutationSet.Mutations[1];
        fixture.WorkspaceResolver.Reconciliations =
        [
            new FileLifecycleReconciliation(
                lifecycleMutation.MutationId,
                FileLifecycleReconciliationState.Compensated,
                lifecycleMutation.RelativePath,
                null,
                "Lifecycle endpoint restored."),
        ];
        fixture.CommitHandler.ThrowOnNext = true;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged)));
        var checkpoint = await fixture.Checkpoints.GetCheckpointAsync(
            fixture.StartRequest.RunId,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(checkpoint);
        Assert.Equal(ExecutionOperationState.RecoveryRequired, checkpoint.Operation?.State);
    }

    /// <summary>Verifies corrected outcomes retain both the initial and correction diff evidence.</summary>
    [Fact]
    public async Task CorrectedExecution_PreservesCumulativeFinalDiff()
    {
        // Arrange
        var fixture = CreateFixture(includeCorrection: true);
        await using var events = fixture.Events;
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var correctionPending = await fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged));

        // Act
        var outcome = await fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.CorrectionStaged));
        var finalDiff = outcome.FinalDiff is null
            ? null
            : await fixture.Artifacts.ReadAsync(outcome.FinalDiff);

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.CorrectionPending, correctionPending.Status);
        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.NotNull(finalDiff);
        Assert.Contains("-old", finalDiff, StringComparison.Ordinal);
        Assert.Contains("+fixed", finalDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("+new", finalDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("-new", finalDiff, StringComparison.Ordinal);
        Assert.Equal(2, fixture.CommitHandler.Commands.Count);
        var checkpoint = await fixture.Checkpoints.GetCheckpointAsync(fixture.StartRequest.RunId);
        Assert.Equal(0, checkpoint!.CorrectionAttempts);
        Assert.Equal(1, outcome.CorrectionAttempts);
        var correctionCommand = Assert.Single(
            fixture.ProposalHandler.Commands,
            command => command.Correction is not null);
        Assert.Equal(ModelCorrectionCategory.PostApplyValidation, correctionCommand.Correction?.Category);
        Assert.Contains("Correction required", correctionCommand.Correction?.SafeReason, StringComparison.Ordinal);
        var correctionEvent = Assert.Single(observed.OfType<ModelCorrectionAttempted>());
        Assert.Equal(ModelCorrectionCategory.PostApplyValidation, correctionEvent.Category);
    }

    /// <summary>Verifies execution startup failure terminalizes the approved run and unblocks waiters.</summary>
    [Fact]
    public async Task ApprovedExecutionStartupFailure_TerminalizesRun()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var observed = new List<IDomainEvent>();
        var planProposed = new TaskCompletionSource<PlanProposed>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            if (item is PlanProposed proposed)
            {
                planProposed.TrySetResult(proposed);
            }

            return Task.CompletedTask;
        });
        var application = new SessionApplication(
            events,
            new PlanModelProvider(fixture.StartRequest.ApprovedPlan),
            new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            executionOrchestrator: new FailingStartOrchestrator(),
            executionRequestFactory: (sessionId, runId, task, plan, _) =>
            {
                var validationRequest = fixture.StartRequest.ValidationRequest with
                {
                    SessionId = sessionId,
                    RunId = runId,
                };
                return Task.FromResult<ExecutionStartRequest?>(fixture.StartRequest with
                {
                    SessionId = sessionId,
                    RunId = runId,
                    Task = task,
                    ApprovedPlan = plan,
                    ValidationRequest = validationRequest,
                });
            },
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("startup failure"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "change example"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var proposed = await planProposed.Task.WaitAsync(timeout.Token);
        Assert.Equal(runId, proposed.RunId);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(new ApprovePlanCommand(sessionId, runId), timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(new WaitForRunCommand(runId), timeout.Token));
        Assert.Contains(
            observed,
            item => item is RunCompleted completed
                && completed.RunId == runId
                && !completed.Succeeded);
    }

    /// <summary>Verifies policy-auto-approved plans enter implementation through a legal phase transition.</summary>
    [Fact]
    public async Task AutoApprovedPlan_StartsExecutionFromAwaitingApproval()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var orchestrator = new CompletingStartOrchestrator();
        var application = new SessionApplication(
            events,
            new PlanModelProvider(fixture.StartRequest.ApprovedPlan),
            new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            executionOrchestrator: orchestrator,
            executionRequestFactory: CreateStartRequestFactory(fixture),
            planSanityChecker: new PlanSanityChecker(TestPromptLoader.Instance),
            planApprovalPolicy: new AlwaysAutoPlanApprovalPolicy(),
            planSanityRequestFactory: CreateSanityRequestFactory(fixture),
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("auto startup"));

        // Act
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "change example"));
        var succeeded = await dispatcher.DispatchAsync(new WaitForRunCommand(runId));

        // Assert
        Assert.True(succeeded);
        Assert.Equal(1, orchestrator.StartCount);
        Assert.Contains(observed, item => item is RunTransitioned transitioned
            && transitioned.RunId == runId
            && transitioned.Source == RunPhase.ChangePlanning
            && transitioned.Destination == RunPhase.AwaitingPlanApproval);
        Assert.Contains(observed, item => item is RunTransitioned transitioned
            && transitioned.RunId == runId
            && transitioned.Source == RunPhase.AwaitingPlanApproval
            && transitioned.Destination == RunPhase.ImplementationPreparing);
        Assert.DoesNotContain(observed, item => item is RunTransitionFailed failed && failed.RunId == runId);
    }

    /// <summary>Planning usage from a scoped production-style budget is carried into governed execution.</summary>
    [Fact]
    public async Task ApprovedPlan_CarriesAccumulatedPlanningUsageIntoExecution()
    {
        var fixture = CreateFixture();
        await using var events = new DomainEventStream();
        var orchestrator = new CompletingStartOrchestrator();
        var budget = new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1), 10));
        var application = new SessionApplication(
            events,
            new PlanModelProvider(
                fixture.StartRequest.ApprovedPlan,
                new ModelUsage(12, 8, EstimatedCost: 0.25m)),
            budget,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            executionOrchestrator: orchestrator,
            executionRequestFactory: CreateStartRequestFactory(fixture),
            planSanityChecker: new PlanSanityChecker(TestPromptLoader.Instance),
            planApprovalPolicy: new AlwaysAutoPlanApprovalPolicy(),
            planSanityRequestFactory: CreateSanityRequestFactory(fixture),
            budgetFactory: budget.CreateScope,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("budget propagation"));

        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "change example"));
        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        var usage = Assert.IsType<ExecutionStartRequest>(orchestrator.LastStartRequest).InitialBudgetUsage;
        Assert.Equal(20, usage.Tokens);
        Assert.Equal(1, usage.Calls);
        Assert.Equal(0.25m, usage.Cost);
    }

    /// <summary>Verifies auto-approved execution startup failure is terminalized only by the startup path.</summary>
    [Fact]
    public async Task AutoApprovedExecutionStartupFailure_DoesNotDoubleTerminalizeRun()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var application = new SessionApplication(
            events,
            new PlanModelProvider(fixture.StartRequest.ApprovedPlan),
            new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            executionOrchestrator: new FailingStartOrchestrator(),
            executionRequestFactory: CreateStartRequestFactory(fixture),
            planSanityChecker: new PlanSanityChecker(TestPromptLoader.Instance),
            planApprovalPolicy: new AlwaysAutoPlanApprovalPolicy(),
            planSanityRequestFactory: CreateSanityRequestFactory(fixture),
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("auto startup failure"));

        // Act
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "change example"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(new WaitForRunCommand(runId), timeout.Token));

        // Assert
        Assert.Single(observed.OfType<RunCompleted>(), item => item.RunId == runId);
        Assert.DoesNotContain(observed, item => item is RunTransitionFailed failed && failed.RunId == runId);
    }

    /// <summary>Verifies the current ordered migrations retain the Plan-37 execution checkpoint table.</summary>
    [Fact]
    public async Task CurrentMigrations_RetainExecutionRunsTable()
    {
        // Arrange
        var databasePath = Path.Combine(Path.GetTempPath(), $"threadsmith-m11-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            var runner = new MigrationRunner(connectionString, DefaultMigrations.All);

            // Act
            var version = await runner.RunAsync();

            // Assert
            Assert.Equal(11, version);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='execution_runs';";
            var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1, count);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>Checkpoint writes cannot change the independently versioned terminal outcome payload.</summary>
    [Fact]
    public async Task ExecutionCheckpointStore_CheckpointWritePreservesReadableOutcomeSchema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"threadsmith-execution-store-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var sessionId = SessionId.New();
        var runId = RunId.New();
        try
        {
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var store = new ExecutionCheckpointStore(connectionString);
            var outcome = new ExecutionOutcomeProjection
            {
                Key = new ProjectionKey("execution-outcome", runId.Value.ToString("D")),
                SessionId = sessionId,
                RunId = runId,
                Status = ExecutionCheckpointPhase.Completed,
                ApprovalProvenance = "test",
            };
            var checkpoint = new ExecutionContinuation
            {
                SchemaVersion = 2,
                SessionId = sessionId,
                RunId = runId,
                WorkspaceId = WorkspaceId.New(),
                PlanRevision = 1,
                PlanHash = "plan",
                Phase = ExecutionCheckpointPhase.Completed,
                DiagnosticBaselineIdentity = "diagnostic",
                MutationBaselineIdentity = "mutation",
                NextAction = "inspect outcome",
                RecordedAt = DateTimeOffset.UtcNow,
            };

            await store.SaveOutcomeAsync(outcome);
            await store.SaveCheckpointAsync(checkpoint);

            var reopened = new ExecutionCheckpointStore(connectionString);
            var restored = await reopened.GetOutcomeAsync(runId);
            Assert.NotNull(restored);
            Assert.Equal(outcome.SchemaVersion, restored.SchemaVersion);
            Assert.Equal(outcome.SessionId, restored.SessionId);
            Assert.Equal(outcome.RunId, restored.RunId);
            Assert.Equal(outcome.Status, restored.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static ExecutionFixture CreateFixture(
        bool includeSecondPlanStep = false,
        bool includeCorrection = false,
        bool includeRejectedLifecycleMutation = false,
        bool includeBuildValidation = false,
        bool blockFirstProposal = false,
        bool applyLifecycleMutation = false,
        WorkspaceResourceLimits? workspaceLimits = null)
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var workspaceId = WorkspaceId.New();
        var stepId = StepId.New();
        var secondStepId = StepId.New();
        var mutationSetId = MutationSetId.New();
        var mutationId = MutationId.New();
        var approvalId = ApprovalId.New();
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var baseline = new WorkspaceBaseline(
            workspaceId,
            "C:\\repo",
            capturedAt,
            [new WorkspaceFileHash("src/Example.cs", "abc", 10)],
            GitRevision: "revision",
            SelectedSolutionPath: "Example.sln",
            TrustLevel: RepositoryTrustLevel.TrustedMutation);
        List<ImplementationPlanStep> steps =
        [
            new ImplementationPlanStep
            {
                StepId = stepId,
                Title = "Edit example",
                Description = "Change observable behavior.",
                FileIntents =
                [
                    new PlanFileIntent
                    {
                        Kind = PlanFileChangeKind.Modify,
                        Path = "src/Example.cs",
                    },
                ],
                ExpectedOutcome = "Example behavior changes.",
                Validation = ["Build and test."],
            },
        ];
        if (includeSecondPlanStep)
        {
            steps.Add(new ImplementationPlanStep
            {
                StepId = secondStepId,
                Title = "Edit untouched component",
                Description = "Change another observable behavior.",
                FileIntents =
                [
                    new PlanFileIntent
                    {
                        Kind = PlanFileChangeKind.Modify,
                        Path = "src/Untouched.cs",
                    },
                ],
                ExpectedOutcome = "Untouched behavior changes.",
                Validation = ["Build and test."],
            });
        }

        var plan = new ImplementationPlan
        {
            Summary = "Change example",
            Steps = steps,
            Risks = ["Plan risk."],
        };
        var mutations = new List<Mutation>
        {
            new()
            {
                MutationId = mutationId,
                Type = MutationType.ReplaceText,
                RelativePath = "src/Example.cs",
                BaselineSha256 = "abc",
                ExpectedText = "old",
                ReplacementText = "new",
                Length = 3,
            },
        };
        var lifecycleMutationId = MutationId.New();
        if (includeRejectedLifecycleMutation)
        {
            mutations.Add(new Mutation
            {
                MutationId = lifecycleMutationId,
                Type = MutationType.CreateFile,
                RelativePath = "src/Rejected.cs",
                Content = new FileContentDescriptor { Text = "rejected" },
            });
        }

        var mutationSet = new MutationSet
        {
            MutationSetId = mutationSetId,
            SessionId = sessionId,
            RunId = runId,
            WorkspaceId = workspaceId,
            BaselineCapturedAt = capturedAt,
            BaselineRevision = "revision",
            Mutations = mutations,
            Rationale = "Implement approved step.",
            IsWithinApprovedPlan = true,
        };
        var preview = new MutationPreview(
            mutationSetId,
            "--- a/src/Example.cs\n+++ b/src/Example.cs\n-old\n+new\n",
            [new MutationDiff(mutationId, "src/Example.cs", "-old\n+new", true)],
            1,
            1)
        {
            LifecycleChanges = includeRejectedLifecycleMutation
                ?
                [
                    new FileLifecycleChange(
                        lifecycleMutationId,
                        MutationType.CreateFile,
                        "src/Rejected.cs",
                        null,
                        false,
                        FileLifecycleRisk.Additive),
                ]
                : [],
        };
        var staged = new StagedMutationSet(
            mutationSet,
            preview,
            new ConflictReport(mutationSetId, []),
            approvalId)
        {
            PlanStepIds = [stepId],
        };
        var correctionSetId = MutationSetId.New();
        var correctionMutationId = MutationId.New();
        var correctionMutationSet = mutationSet with
        {
            MutationSetId = correctionSetId,
            Mutations =
            [
                mutationSet.Mutations[0] with
                {
                    MutationId = correctionMutationId,
                    ExpectedText = "new",
                    ReplacementText = "fixed",
                },
            ],
            Rationale = "Correct validation failure.",
        };
        var correctionStaged = new StagedMutationSet(
            correctionMutationSet,
            new MutationPreview(
                correctionSetId,
                "--- a/src/Example.cs\n+++ b/src/Example.cs\n-new\n+fixed\n",
                [new MutationDiff(correctionMutationId, "src/Example.cs", "-new\n+fixed", true)],
                1,
                1),
            new ConflictReport(correctionSetId, []),
            ApprovalId.New())
        {
            PlanStepIds = [stepId],
        };
        var events = new DomainEventStream();
        var proposalHandler = new ProposalHandler(
            includeCorrection ? [staged, correctionStaged] : [staged],
            blockFirstProposal);
        var firstCommit = new MutationCommitResult(
            mutationSetId,
            applyLifecycleMutation ? [mutationId, lifecycleMutationId] : [mutationId],
            applyLifecycleMutation ? ["src/Example.cs", "src/Rejected.cs"] : ["src/Example.cs"],
            "revision",
            false)
        {
            LifecycleReconciliations = applyLifecycleMutation
                ?
                [
                    new FileLifecycleReconciliation(
                        lifecycleMutationId,
                        FileLifecycleReconciliationState.Applied,
                        "src/Rejected.cs",
                        null,
                        "Created file is present."),
                ]
                : [],
        };
        var correctionCommit = new MutationCommitResult(
            correctionSetId,
            [correctionMutationId],
            ["src/Example.cs"],
            "revision",
            false);
        var commitHandler = new CommitHandler(includeCorrection
            ? [firstCommit, correctionCommit]
            : [firstCommit]);
        var baselineHandler = new BaselineHandler(new BaselineCapture(
            workspaceId,
            capturedAt,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            []));
        var validation = new MutationValidationResult(
            new BuildValidationResult(true, [], ["Example.sln"], TimeSpan.Zero),
            [],
            new TestValidationResult
            {
                Selection = new TestSelection { Rationale = ["Affected tests selected."] },
                Completed = true,
            },
            new AcceptanceGateResult(AcceptanceGateStatus.Passed, []));
        var failedValidation = validation with
        {
            Gate = new AcceptanceGateResult(AcceptanceGateStatus.Failed, ["Correction required."]),
        };
        var validationHandler = new ValidationHandler(includeCorrection
            ? [failedValidation, validation]
            : [validation]);
        var checkpoints = new MemoryCheckpointStore();
        var artifacts = new MemoryArtifactPublisher();
        var workspaceResolver = new WorkspaceResolver(baseline, () => commitHandler.CommittedCount);
        var orchestrator = new ExecutionOrchestrator(
            proposalHandler,
            commitHandler,
            baselineHandler,
            validationHandler,
            workspaceResolver,
            checkpoints,
            artifacts,
            events,
            new SecretOutputSanitizer(),
            NullLogger<ExecutionOrchestrator>.Instance,
            new CorrectiveMessageFactory(TestPromptLoader.Instance),
            workspaceLimits: workspaceLimits);
        var start = new ExecutionStartRequest
        {
            SessionId = sessionId,
            RunId = runId,
            Baseline = baseline,
            Task = new TaskSpecification("Change example", []),
            ApprovedPlan = plan,
            ValidationRequest = new BuildValidationRequest
            {
                SessionId = sessionId,
                RunId = runId,
                Baseline = baseline,
                Confidence = SemanticConfidenceLevel.FullSemantic,
                Stages = includeBuildValidation
                    ?
                    [
                        MutationValidationStage.Compile,
                        MutationValidationStage.Diagnostics,
                    ]
                    : [MutationValidationStage.Semantic],
            },
        };
        return new ExecutionFixture(
            orchestrator,
            events,
            checkpoints,
            artifacts,
            proposalHandler,
            baselineHandler,
            commitHandler,
            validationHandler,
            workspaceResolver,
            start,
            staged,
            correctionStaged,
            stepId,
            secondStepId,
            workspaceLimits ?? new WorkspaceResourceLimits());
    }

    private static string SerializePlanProposal(ImplementationPlan plan)
    {
        return JsonSerializer.Serialize(new
        {
            plan.Summary,
            steps = plan.Steps.Select(step => new
            {
                step.Title,
                step.Description,
                fileIntents = step.FileIntents.Select(intent => new
                {
                    kind = intent.Kind.ToString(),
                    intent.Path,
                    intent.DestinationPath,
                }),
                step.ExpectedOutcome,
                step.Validation,
            }),
            plan.Risks,
            plan.OutstandingQuestions,
        });
    }

    private static async Task AssertContinuationRejectedAsync(
        ExecutionOrchestrator orchestrator,
        ContinueExecutionRequest continuation)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ContinueAsync(continuation));
        Assert.Contains("cannot be consumed", exception.Message, StringComparison.Ordinal);
    }

    private static async Task AssertResumeRejectedAsync(ExecutionFixture fixture)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.ResumeAsync(
                fixture.StartRequest.SessionId,
                fixture.StartRequest.RunId));
        Assert.Contains("terminal execution", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ContinueExecutionRequest CreateContinuation(
        ExecutionFixture fixture,
        StagedMutationSet staged)
    {
        return new ContinueExecutionRequest
        {
            SessionId = fixture.StartRequest.SessionId,
            RunId = fixture.StartRequest.RunId,
            Approval = new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            },
            ApprovalProvenance = "test user",
        };
    }

    private sealed record ExecutionFixture(
        ExecutionOrchestrator Orchestrator,
        DomainEventStream Events,
        MemoryCheckpointStore Checkpoints,
        MemoryArtifactPublisher Artifacts,
        ProposalHandler ProposalHandler,
        BaselineHandler BaselineHandler,
        CommitHandler CommitHandler,
        ValidationHandler ValidationHandler,
        WorkspaceResolver WorkspaceResolver,
        ExecutionStartRequest StartRequest,
        StagedMutationSet Staged,
        StagedMutationSet CorrectionStaged,
        StepId StepId,
        StepId SecondStepId,
        WorkspaceResourceLimits WorkspaceLimits);

    private sealed class ProposalHandler : ICommandHandler<ProposeMutationSetCommand, StagedMutationSet>, IIncrementalMutationProposalProvider
    {
        private readonly TaskCompletionSource _firstHandleEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseFirstHandle = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly bool _blockFirstProposal;
        private bool _firstHandled;

        public ProposalHandler(IEnumerable<StagedMutationSet> staged, bool blockFirstProposal = false)
        {
            Results = new Queue<MutationProposalResult>(staged.Select(item => new MutationProposalResult
            {
                StagedMutationSet = item,
                StepComplete = item.StepComplete ?? true,
                Rationale = item.MutationSet.Rationale,
            }));
            _blockFirstProposal = blockFirstProposal;
            if (!blockFirstProposal)
            {
                _firstHandleEntered.TrySetResult();
                _releaseFirstHandle.TrySetResult();
            }
        }

        public List<ProposeMutationSetCommand> Commands { get; } = [];

        public Queue<MutationProposalResult> Results { get; }

        public int? InterruptCall { get; set; }

        public Action? AfterProposal { get; set; }

        public Task FirstHandleEntered => _firstHandleEntered.Task;

        public void ReleaseFirstHandle()
        {
            _releaseFirstHandle.TrySetResult();
        }

        public async Task<StagedMutationSet> HandleAsync(
            ProposeMutationSetCommand command,
            CancellationToken cancellationToken = default)
        {
            var result = await ProposeAsync(command, cancellationToken);
            return result.StagedMutationSet ?? throw new InvalidOperationException("Completion-only result.");
        }

        public async Task<MutationProposalResult> ProposeAsync(
            ProposeMutationSetCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (InterruptCall == Commands.Count)
            {
                throw new OperationCanceledException("Simulated interrupted model turn.");
            }

            if (_blockFirstProposal && !_firstHandled)
            {
                _firstHandled = true;
                _firstHandleEntered.TrySetResult();
                await _releaseFirstHandle.Task.WaitAsync(cancellationToken);
            }

            var result = Results.Dequeue();
            AfterProposal?.Invoke();
            return result;
        }
    }

    private static Func<SessionId, ImplementationPlan, CancellationToken, Task<PlanSanityCheckRequest?>>
        CreateSanityRequestFactory(ExecutionFixture fixture)
    {
        return (_, plan, _) => Task.FromResult<PlanSanityCheckRequest?>(new PlanSanityCheckRequest
        {
            Plan = plan,
            RepositoryRoot = fixture.StartRequest.Baseline.RepositoryPath,
            Baseline = fixture.StartRequest.Baseline,
            TrustLevel = fixture.StartRequest.Baseline.TrustLevel,
            ProhibitedPaths = fixture.StartRequest.Baseline.ProhibitedPaths ?? [],
        });
    }

    private static Func<SessionId, RunId, TaskSpecification, ImplementationPlan, CancellationToken, Task<ExecutionStartRequest?>>
        CreateStartRequestFactory(ExecutionFixture fixture)
    {
        return (sessionId, runId, task, plan, _) =>
        {
            var validationRequest = fixture.StartRequest.ValidationRequest with
            {
                SessionId = sessionId,
                RunId = runId,
            };
            return Task.FromResult<ExecutionStartRequest?>(fixture.StartRequest with
            {
                SessionId = sessionId,
                RunId = runId,
                Task = task,
                ApprovedPlan = plan,
                ValidationRequest = validationRequest,
            });
        };
    }

    private sealed class AlwaysAutoPlanApprovalPolicy : IPlanApprovalPolicy
    {
        public PlanApprovalPolicy CurrentPolicy => PlanApprovalPolicy.ReviewRisky;

        public Task BindRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public PlanApprovalDecision Decide(PlanSanityCheckResult result, RepositoryTrustLevel trustLevel)
        {
            ArgumentNullException.ThrowIfNull(result);
            return new PlanApprovalDecision
            {
                Kind = PlanApprovalDecisionKind.AutoApproved,
                Policy = CurrentPolicy,
                Risk = result.Risk,
                Reason = "test auto approval",
            };
        }

        public Task SetPolicyAsync(PlanApprovalPolicy policy, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(policy))
            {
                throw new ArgumentOutOfRangeException(nameof(policy));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CompletingStartOrchestrator : IExecutionOrchestrator
    {
        public int StartCount { get; private set; }

        public ExecutionStartRequest? LastStartRequest { get; private set; }

        public Task<ExecutionContinuation> StartAsync(
            ExecutionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            LastStartRequest = request;
            StepId? currentStepId = request.ApprovedPlan.Steps.Count == 0
                ? null
                : request.ApprovedPlan.Steps[0].StepId;
            return Task.FromResult(new ExecutionContinuation
            {
                SessionId = request.SessionId,
                RunId = request.RunId,
                WorkspaceId = request.Baseline.WorkspaceId,
                PlanRevision = request.ApprovedPlan.Revision,
                PlanHash = "test-plan-hash",
                Phase = ExecutionCheckpointPhase.MutationApprovalPending,
                CurrentPlanStepId = currentStepId,
                DiagnosticBaselineIdentity = request.Baseline.CapturedAt.ToUnixTimeMilliseconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                MutationBaselineIdentity = request.Baseline.CapturedAt.ToUnixTimeMilliseconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                RecordedAt = DateTimeOffset.UtcNow,
                NextAction = "test-complete",
            });
        }

        public Task<ExecutionOutcomeProjection> ContinueAsync(
            ContinueExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionContinuation> ResumeAsync(
            SessionId sessionId,
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionOutcomeProjection> WaitForOutcomeAsync(
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ExecutionOutcomeProjection
            {
                Key = new ProjectionKey("execution-outcome", runId.Value.ToString("D")),
                SessionId = SessionId.New(),
                RunId = runId,
                Status = ExecutionCheckpointPhase.Completed,
                ApprovalProvenance = "test",
            });
        }
    }

    private sealed class PlanModelProvider : IModelProvider
    {
        private readonly ImplementationPlan _plan;
        private readonly ModelUsage? _usage;

        public PlanModelProvider(ImplementationPlan plan, ModelUsage? usage = null)
        {
            _plan = plan;
            _usage = usage;
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ModelChunk
            {
                Output = new ToolRequestModelOutput(
                    "propose_plan",
                    SerializePlanProposal(_plan)),
                Usage = _usage,
                FinishReason = ModelFinishReason.ToolCalls,
            };
        }
    }

    private sealed class FailingStartOrchestrator : IExecutionOrchestrator
    {
        public Task<ExecutionContinuation> StartAsync(
            ExecutionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ExecutionContinuation>(
                new InvalidOperationException("Simulated execution startup failure."));
        }

        public Task<ExecutionOutcomeProjection> ContinueAsync(
            ContinueExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionContinuation> ResumeAsync(
            SessionId sessionId,
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionOutcomeProjection> WaitForOutcomeAsync(
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CommitHandler : ICommandHandler<CommitMutationSetCommand, MutationCommitResult>
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Queue<MutationCommitResult> _results;

        public CommitHandler(IEnumerable<MutationCommitResult> results)
        {
            _results = new Queue<MutationCommitResult>(results);
        }

        public List<CommitMutationSetCommand> Commands { get; } = [];

        public int CommittedCount { get; private set; }

        public bool BlockOnNext { get; set; }

        public Task Entered => _entered.Task;

        public bool ThrowOnNext { get; set; }

        public void Enqueue(MutationCommitResult result)
        {
            _results.Enqueue(result);
        }

        public Task<MutationCommitResult> HandleAsync(
            CommitMutationSetCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (ThrowOnNext)
            {
                ThrowOnNext = false;
                throw new InvalidOperationException("Simulated commit failure.");
            }

            if (BlockOnNext)
            {
                BlockOnNext = false;
                _entered.TrySetResult();
                return CompleteAfterReleaseAsync(cancellationToken);
            }

            CommittedCount++;
            return Task.FromResult(_results.Dequeue());
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        private async Task<MutationCommitResult> CompleteAfterReleaseAsync(
            CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            CommittedCount++;
            return _results.Dequeue();
        }
    }

    private sealed class BaselineHandler : ICommandHandler<CaptureBaselineBuildCommand, BaselineCapture>
    {
        private readonly BaselineCapture _result;

        public BaselineHandler(BaselineCapture result)
        {
            _result = result;
        }

        public List<CaptureBaselineBuildCommand> Commands { get; } = [];

        public bool ThrowOnNext { get; set; }

        public Task<BaselineCapture> HandleAsync(
            CaptureBaselineBuildCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (ThrowOnNext)
            {
                ThrowOnNext = false;
                throw new InvalidOperationException("Simulated baseline interruption.");
            }

            return Task.FromResult(_result with { BaselineCapturedAt = command.Request.Baseline.CapturedAt });
        }
    }

    private sealed class ValidationHandler : ICommandHandler<ValidateMutationCommand, MutationValidationResult>
    {
        private readonly Queue<MutationValidationResult> _results;

        public ValidationHandler(IEnumerable<MutationValidationResult> results)
        {
            _results = new Queue<MutationValidationResult>(results);
        }

        public List<ValidateMutationCommand> Commands { get; } = [];

        public bool ThrowOnNext { get; set; }

        public MutationValidationResult LastResult { get; private set; } = null!;

        public void Enqueue(MutationValidationResult result)
        {
            _results.Enqueue(result);
        }

        public void PassAll()
        {
            var results = _results.Select(result => result with
            {
                Gate = new AcceptanceGateResult(AcceptanceGateStatus.Passed, []),
            }).ToArray();
            _results.Clear();
            foreach (var result in results)
            {
                _results.Enqueue(result);
            }
        }

        public Task<MutationValidationResult> HandleAsync(
            ValidateMutationCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (ThrowOnNext)
            {
                ThrowOnNext = false;
                throw new InvalidOperationException("Simulated validation interruption.");
            }

            LastResult = _results.Dequeue();
            return Task.FromResult(LastResult);
        }
    }

    private sealed class WorkspaceResolver : ITransactionalWorkspaceResolver
    {
        private readonly Func<int> _committedCount;
        private WorkspaceBaseline _baseline;

        public WorkspaceResolver(WorkspaceBaseline baseline, Func<int> committedCount)
        {
            _baseline = baseline;
            _committedCount = committedCount;
        }

        public IReadOnlyList<FileLifecycleReconciliation> Reconciliations { get; set; } = [];

        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId)
        {
            return new Workspace(_baseline, Reconciliations, _committedCount() switch
            {
                0 => "old",
                1 => "new",
                _ => "fixed",
            });
        }

        public Task<WorkspaceBaseline> PromoteBaselineAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            _baseline = _baseline with { CapturedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(_baseline);
        }

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class Workspace : ITransactionalWorkspace
    {
        private readonly IReadOnlyList<FileLifecycleReconciliation> _reconciliations;
        private readonly string _content;

        public Workspace(
            WorkspaceBaseline baseline,
            IReadOnlyList<FileLifecycleReconciliation> reconciliations,
            string content)
        {
            Baseline = baseline;
            Isolation = new WorkspaceIsolation(WorkspaceIsolationMode.TrackedInPlace, baseline.RepositoryPath);
            _reconciliations = reconciliations;
            _content = content;
        }

        public WorkspaceBaseline Baseline { get; }

        public WorkspaceIsolation Isolation { get; }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<MutationCommitResult> CommitAsync(
            MutationSetId mutationSetId,
            MutationApproval approval,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ReadBaselineTextAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(relativePath == "src/Example.cs" ? _content : null);
        }

        public Task<string?> ReadStagedTextAsync(
            MutationSetId mutationSetId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<FileLifecycleReconciliation>> ReconcileLifecycleAsync(
            MutationSetId mutationSetId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_reconciliations);
        }

        public Task<MutationRollbackResult> RollbackAsync(
            MutationSetId mutationSetId,
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

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class MemoryCheckpointStore : IExecutionCheckpointStore
    {
        private readonly Dictionary<RunId, ExecutionContinuation> _checkpoints = [];
        private readonly Dictionary<RunId, ExecutionOutcomeProjection> _outcomes = [];

        public bool ThrowOnNextSave { get; set; }

        public Task<ExecutionContinuation?> GetCheckpointAsync(
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            _checkpoints.TryGetValue(runId, out var checkpoint);
            return Task.FromResult(checkpoint);
        }

        public Task<ExecutionOutcomeProjection?> GetOutcomeAsync(
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            _outcomes.TryGetValue(runId, out var outcome);
            return Task.FromResult(outcome);
        }

        public Task SaveCheckpointAsync(
            ExecutionContinuation checkpoint,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnNextSave)
            {
                ThrowOnNextSave = false;
                throw new InvalidOperationException("Simulated checkpoint persistence failure.");
            }

            _checkpoints[checkpoint.RunId] = checkpoint;
            return Task.CompletedTask;
        }

        public Task SaveOutcomeAsync(
            ExecutionOutcomeProjection outcome,
            CancellationToken cancellationToken = default)
        {
            _outcomes[outcome.RunId] = outcome;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryArtifactPublisher : IExecutionArtifactPublisher
    {
        private readonly Dictionary<string, string> _content = new(StringComparer.Ordinal);

        public Task<ExecutionArtifactReference> PublishAsync(
            SessionId sessionId,
            string kind,
            string content,
            CancellationToken cancellationToken = default)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            _content[hash] = content;
            return Task.FromResult(new ExecutionArtifactReference(
                hash,
                kind,
                Encoding.UTF8.GetByteCount(content)));
        }

        public Task<string?> ReadAsync(
            ExecutionArtifactReference reference,
            CancellationToken cancellationToken = default)
        {
            _content.TryGetValue(reference.ContentHash, out var content);
            return Task.FromResult(content);
        }
    }
}
