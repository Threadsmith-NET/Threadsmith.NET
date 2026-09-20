namespace Threadsmith.ExecutionOrchestration.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class ExecutionOrchestratorTests
{
    /// <summary>Repository path identity follows the backing filesystem rather than the host OS default.</summary>
    [Fact]
    public static void RepositoryPathComparer_ReflectsBackingFileSystemCaseSensitivity()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"threadsmith-path-comparer-{Guid.NewGuid():N}");
        var repository = Path.Combine(parent, "Repository");
        Directory.CreateDirectory(repository);
        try
        {
            var alternateCase = Path.Combine(parent, "repository");
            var expectedCaseSensitive = !Directory.Exists(alternateCase);

            var comparer = RepositoryPathPolicy.GetPathComparer(repository);

            Assert.Equal(expectedCaseSensitive, !comparer.Equals("Case.cs", "case.cs"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>A later step cannot borrow a completed step's validation.</summary>
    [Fact]
    public async Task CompletionOnly_CannotReuseAnotherStepsValidation()
    {
        var fixture = CreateFixture(includeSecondPlanStep: true);
        await using var events = fixture.Events;
        fixture.ProposalHandler.Results.Enqueue(CompletionOnly());
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged)));

        var checkpoint = await fixture.Checkpoints.GetCheckpointAsync(fixture.StartRequest.RunId);
        Assert.Equal([fixture.StepId], checkpoint!.CompletedStepIds);
        Assert.Equal(fixture.SecondStepId, checkpoint.CurrentPlanStepId);
        Assert.False(fixture.ProposalHandler.Commands[^1].ExecutionScope!.CanCompleteWithoutChanges);
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(fixture.StartRequest.RunId));
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>An unspecified hint can be confirmed against the same validated batch.</summary>
    [Fact]
    public async Task MissingHint_CanConfirmTheSameValidatedStepWithoutAnotherMutation()
    {
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        SetProposals(fixture, Proposal(fixture.Staged, null), CompletionOnly());
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);

        var outcome = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Equal([fixture.StepId], outcome.CompletedStepIds);
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Single(fixture.ValidationHandler.Commands);
        Assert.True(fixture.ProposalHandler.Commands[^1].ExecutionScope!.CanCompleteWithoutChanges);
    }

    /// <summary>Repeated edits retain the original comparison basis.</summary>
    [Fact]
    public async Task OneStep_CanApplyTwoBatchesAndReportANetDiff()
    {
        var fixture = CreateFixture(includeCorrection: true);
        await using var events = fixture.Events;
        fixture.ValidationHandler.PassAll();
        SetProposals(fixture, Proposal(fixture.Staged, false), Proposal(fixture.CorrectionStaged, true));
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);

        var progress = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, progress.Status);
        Assert.Empty(progress.CompletedStepIds);
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(fixture.StartRequest.RunId));
        var outcome = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.CorrectionStaged));

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Equal([fixture.StepId], outcome.CompletedStepIds);
        Assert.Equal(2, fixture.CommitHandler.Commands.Count);
        Assert.Single(fixture.BaselineHandler.Commands);
        var diff = await fixture.Artifacts.ReadAsync(outcome.FinalDiff!);
        Assert.Contains("-old", diff, StringComparison.Ordinal);
        Assert.Contains("+fixed", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("new", diff, StringComparison.Ordinal);
    }

    /// <summary>A later step cannot inherit lifecycle paths activated by an earlier step.</summary>
    [Fact]
    public async Task LaterStepContinuation_UsesOnlyItsOwnActivatedLifecyclePaths()
    {
        var fixture = CreateFixture(
            includeSecondPlanStep: true,
            includeCorrection: true,
            includeRejectedLifecycleMutation: true,
            applyLifecycleMutation: true);
        await using var events = fixture.Events;
        fixture.ValidationHandler.PassAll();
        var secondStep = fixture.CorrectionStaged with
        {
            PlanStepIds = [fixture.SecondStepId],
        };
        SetProposals(
            fixture,
            Proposal(fixture.Staged, true),
            Proposal(secondStep, false),
            CompletionOnly());
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        _ = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        var restored = RecreateOrchestrator(fixture);

        var outcome = await restored.ContinueAsync(CreateContinuation(fixture, secondStep));

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Empty(fixture.ProposalHandler.Commands[^1].ExecutionScope!.ActivatedPaths);
        Assert.Contains(
            outcome.LifecycleReconciliations,
            item => item.SourcePath == "src/Rejected.cs"
                && item.State == FileLifecycleReconciliationState.Applied);
    }

    /// <summary>Correction hints cannot overwrite the original implementation intent.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, true)]
    public async Task Correction_RetainsOriginalCompletionIntent(bool? originalHint, bool correctionHint)
    {
        var fixture = CreateFixture(includeCorrection: true);
        await using var events = fixture.Events;
        SetProposals(fixture, Proposal(fixture.Staged, originalHint), Proposal(fixture.CorrectionStaged, correctionHint));
        if (originalHint != true)
        {
            fixture.ProposalHandler.Results.Enqueue(CompletionOnly());
        }

        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        var checkpoint = await fixture.Checkpoints.GetCheckpointAsync(fixture.StartRequest.RunId);
        Assert.Equal(originalHint, checkpoint!.PendingStepComplete);
        Assert.Equal(MutationBatchPurpose.Correction, checkpoint.BatchPurpose);

        var outcome = await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.CorrectionStaged));

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        Assert.Equal(originalHint == true ? 2 : 3, fixture.ProposalHandler.Commands.Count);
    }

    /// <summary>Restart resumes proposal generation without replaying accepted mutations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_InterruptedNextStepPreservesProgressAndDoesNotRepeatCommit(bool cancelled)
    {
        var fixture = CreateFixture(includeSecondPlanStep: true, includeCorrection: true);
        await using var events = fixture.Events;
        fixture.ValidationHandler.PassAll();
        fixture.ProposalHandler.InterruptCall = 2;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged)));
        var interrupted = await fixture.Checkpoints.GetCheckpointAsync(fixture.StartRequest.RunId);
        Assert.Equal(ExecutionCheckpointPhase.ImplementationModelTurn, interrupted!.Phase);
        if (cancelled)
        {
            await fixture.Checkpoints.SaveCheckpointAsync(interrupted with { Phase = ExecutionCheckpointPhase.Cancelled });
        }

        var restartedHost = RecreateOrchestrator(fixture);
        var resumed = await restartedHost.ResumeAsync(fixture.StartRequest.SessionId, fixture.StartRequest.RunId);

        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, resumed.Phase);
        Assert.Equal(2, resumed.BatchOrdinal);
        Assert.Equal([fixture.StepId], resumed.CompletedStepIds);
        Assert.Equal(fixture.SecondStepId, resumed.CurrentPlanStepId);
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Single(fixture.ValidationHandler.Commands);
        Assert.Equal(interrupted.MutationBaselineIdentity, resumed.MutationBaselineIdentity);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restartedHost.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged)));
    }

    /// <summary>An admitted later candidate is durable even when cancellation arrives with its model result.</summary>
    [Fact]
    public async Task LaterCandidate_CancellationAfterProposalPersistsCandidateAndBudget()
    {
        var fixture = CreateFixture(includeSecondPlanStep: true, includeCorrection: true);
        await using var events = fixture.Events;
        fixture.ValidationHandler.PassAll();
        var secondProposal = Proposal(
            fixture.CorrectionStaged with { PlanStepIds = [fixture.SecondStepId] },
            true) with
        {
            BudgetUsed = new BudgetDimensions(25, 2, TimeSpan.FromSeconds(1)),
        };
        SetProposals(
            fixture,
            Proposal(fixture.Staged, true),
            secondProposal);
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        using var cancellation = new CancellationTokenSource();
        fixture.ProposalHandler.AfterProposal = cancellation.Cancel;

        var progress = await fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged),
            cancellation.Token);
        var resumed = await RecreateOrchestrator(fixture).ResumeAsync(
            fixture.StartRequest.SessionId,
            fixture.StartRequest.RunId);
        var state = JsonNode.Parse((await fixture.Artifacts.ReadAsync(resumed.StateArtifact!))!)!.AsObject();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, progress.Status);
        Assert.Equal(fixture.CorrectionStaged.MutationSet.MutationSetId, resumed.MutationSetId);
        Assert.Equal(25, state["BudgetUsed"]!["Tokens"]!.GetValue<long>());
        Assert.Equal(2, state["BudgetUsed"]!["Calls"]!.GetValue<int>());
        Assert.Equal(2, fixture.ProposalHandler.Commands.Count);
    }

    /// <summary>A failed later step retains earlier completed steps from the same plan.</summary>
    [Fact]
    public async Task FailedLaterStep_PreservesEarlierCurrentPlanProgress()
    {
        var fixture = CreateFixture(includeSecondPlanStep: true);
        await using var events = fixture.Events;
        var secondStaged = fixture.CorrectionStaged with
        {
            PlanStepIds = [fixture.SecondStepId],
            StepComplete = true,
        };
        SetProposals(fixture, Proposal(fixture.Staged, true), Proposal(secondStaged, true));
        fixture.CommitHandler.Enqueue(new MutationCommitResult(
            secondStaged.MutationSet.MutationSetId,
            secondStaged.MutationSet.Mutations.Select(mutation => mutation.MutationId).ToArray(),
            ["src/Example.cs"],
            "revision",
            false));
        var request = fixture.StartRequest with { CorrectionBudget = 0 };
        await fixture.Orchestrator.StartAsync(request);
        var pending = await fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, fixture.Staged));
        fixture.ValidationHandler.Enqueue(fixture.ValidationHandler.LastResult with
        {
            Gate = new AcceptanceGateResult(AcceptanceGateStatus.Failed, ["Second step failed."]),
        });

        var outcome = await fixture.Orchestrator.ContinueAsync(
            CreateContinuation(fixture, secondStaged));

        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, pending.Status);
        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome.Status);
        Assert.Equal([fixture.StepId], outcome.CompletedStepIds);
        Assert.Equal([fixture.SecondStepId], outcome.UncompletedStepIds);
        Assert.Contains("Example behavior changes.", outcome.BehaviorSummary);
        Assert.DoesNotContain("Untouched behavior changes.", outcome.BehaviorSummary);
    }

    /// <summary>Partial consent stops automatic generation until an explicit resume.</summary>
    [Fact]
    public async Task PartialApproval_PausesUntilExplicitResumeAndRequiresFreshAuthorization()
    {
        var fixture = CreateFixture(includeRejectedLifecycleMutation: true);
        await using var events = fixture.Events;
        fixture.ProposalHandler.Results.Enqueue(Proposal(fixture.CorrectionStaged, true));
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var request = CreateContinuation(fixture, fixture.Staged) with
        {
            Approval = new MutationApproval
            {
                Level = MutationApprovalLevel.SelectedMutations,
                ApprovalId = fixture.Staged.ApprovalId,
                SelectedMutations = [fixture.Staged.MutationSet.Mutations[0].MutationId],
            },
        };

        var progress = await fixture.Orchestrator.ContinueAsync(request);

        Assert.Equal(ExecutionCheckpointPhase.ContinuationPending, progress.Status);
        Assert.Single(fixture.ProposalHandler.Commands);
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(fixture.StartRequest.RunId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.ContinueAsync(request));
        var resumed = await RecreateOrchestrator(fixture).ResumeAsync(fixture.StartRequest.SessionId, fixture.StartRequest.RunId);
        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, resumed.Phase);
        Assert.Empty(resumed.CompletedStepIds);
        Assert.Equal(fixture.CorrectionStaged.MutationSet.MutationSetId, resumed.MutationSetId);
        Assert.False(fixture.ProposalHandler.Commands[^1].ExecutionScope!.CanCompleteWithoutChanges);
        Assert.Single(fixture.CommitHandler.Commands);
    }

    /// <summary>Migrated progress is saved once and can be restored by a fresh host.</summary>
    [Fact]
    public async Task Resume_LegacyStateIsPersistedInCurrentSchemaBeforeFurtherWork()
    {
        var fixture = CreateFixture();
        await using var events = fixture.Events;
        var checkpoint = await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var state = JsonNode.Parse((await fixture.Artifacts.ReadAsync(checkpoint.StateArtifact!))!)!.AsObject();
        foreach (var field in new[] { "CurrentStepId", "PendingStepComplete", "BatchOrdinal", "BudgetUsed", "CurrentBatchFullyApplied" })
        {
            state.Remove(field);
        }

        var artifact = await fixture.Artifacts.PublishAsync(fixture.StartRequest.SessionId, "executionContinuationState", state.ToJsonString());
        await fixture.Checkpoints.SaveCheckpointAsync(checkpoint with { SchemaVersion = 1, StateArtifact = artifact });

        var resumed = await RecreateOrchestrator(fixture).ResumeAsync(fixture.StartRequest.SessionId, fixture.StartRequest.RunId);

        Assert.Equal(2, resumed.SchemaVersion);
        Assert.Equal(fixture.StepId, resumed.CurrentPlanStepId);
        Assert.Equal(1, resumed.BatchOrdinal);
        var nextResume = await RecreateOrchestrator(fixture).ResumeAsync(fixture.StartRequest.SessionId, fixture.StartRequest.RunId);
        Assert.Equal(resumed.StateArtifact, nextResume.StateArtifact);
        Assert.Empty(fixture.CommitHandler.Commands);
        Assert.Single(fixture.ProposalHandler.Commands);
    }

    /// <summary>Legacy optimistic completion is revoked until resumed validation passes.</summary>
    [Fact]
    public async Task Resume_LegacyAppliedStepWithoutPassingValidation_RemainsCurrentCorrectionStep()
    {
        var fixture = CreateFixture(includeCorrection: true);
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        var applied = await fixture.Orchestrator.ApplyAsync(CreateContinuation(fixture, fixture.Staged));
        var state = JsonNode.Parse((await fixture.Artifacts.ReadAsync(applied.Continuation.StateArtifact!))!)!.AsObject();
        state["AppliedPlanStepIds"] = JsonSerializer.SerializeToNode(new[] { fixture.StepId });
        var artifact = await fixture.Artifacts.PublishAsync(
            fixture.StartRequest.SessionId,
            "executionContinuationState",
            state.ToJsonString());
        await fixture.Checkpoints.SaveCheckpointAsync(applied.Continuation with
        {
            SchemaVersion = 1,
            CompletedStepIds = [fixture.StepId],
            StateArtifact = artifact,
        });

        var resumed = await RecreateOrchestrator(fixture).ResumeAsync(
            fixture.StartRequest.SessionId,
            fixture.StartRequest.RunId);

        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, resumed.Phase);
        Assert.Empty(resumed.CompletedStepIds);
        Assert.Equal(fixture.StepId, resumed.CurrentPlanStepId);
        var correction = fixture.ProposalHandler.Commands[^1];
        Assert.NotNull(correction.Correction);
        Assert.Equal(fixture.StepId, correction.ExecutionScope!.ActiveStep.StepId);
    }

    /// <summary>Legacy runs cannot manufacture original bytes from already-mutated content.</summary>
    [Fact]
    public async Task LegacyAppliedWork_WithoutOriginalArtifactsDoesNotFabricateANetDiff()
    {
        var fixture = CreateFixture(includeCorrection: true);
        await using var events = fixture.Events;
        await fixture.Orchestrator.StartAsync(fixture.StartRequest);
        await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        var checkpoint = await fixture.Checkpoints.GetCheckpointAsync(fixture.StartRequest.RunId);
        var state = JsonNode.Parse((await fixture.Artifacts.ReadAsync(checkpoint!.StateArtifact!))!)!.AsObject();
        state.Remove("OriginalFiles");
        var artifact = await fixture.Artifacts.PublishAsync(fixture.StartRequest.SessionId, "executionContinuationState", state.ToJsonString());
        await fixture.Checkpoints.SaveCheckpointAsync(checkpoint with { SchemaVersion = 1, StateArtifact = artifact });
        var restored = RecreateOrchestrator(fixture);
        await restored.ResumeAsync(fixture.StartRequest.SessionId, fixture.StartRequest.RunId);

        var outcome = await restored.ContinueAsync(CreateContinuation(fixture, fixture.CorrectionStaged));

        Assert.Null(outcome.FinalDiff);
        Assert.Equal(["src/Example.cs"], outcome.ChangedFiles);
    }

    /// <summary>Net diffs use actual promoted workspace bytes, including dirty starting content and file lifecycle changes.</summary>
    [Theory]
    [InlineData("edit")]
    [InlineData("create-edit")]
    [InlineData("create-delete")]
    [InlineData("move-edit")]
    public async Task NetDiff_UsesOriginalBytesAcrossRealWorkspaceTransactions(string scenario)
    {
        const string original = "dirty value";
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-net-diff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Example.cs"), original);
            var fixture = CreateFixture(includeCorrection: true);
            await using var events = fixture.Events;
            fixture.ValidationHandler.PassAll();
            await using var workspaces = new TransactionalWorkspaceCoordinator(events);
            var baseline = fixture.StartRequest.Baseline with
            {
                RepositoryPath = root,
                ApprovedRoots = ["src"],
                Files = [new WorkspaceFileHash("src/Example.cs", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original))), Encoding.UTF8.GetByteCount(original))],
            };
            await workspaces.RegisterBaselineAsync(baseline);
            var path = scenario.StartsWith("create", StringComparison.Ordinal) ? "src/Created.txt"
                : scenario == "move-edit" ? "src/Moved.cs" : "src/Example.cs";
            var first = new Mutation
            {
                MutationId = MutationId.New(),
                Type = scenario.StartsWith("create", StringComparison.Ordinal) ? MutationType.CreateFile
                    : scenario == "move-edit" ? MutationType.MoveFile : MutationType.ReplaceText,
                RelativePath = scenario == "move-edit" ? "src/Example.cs" : path,
                DestinationRelativePath = scenario == "move-edit" ? path : null,
                Content = scenario.StartsWith("create", StringComparison.Ordinal) ? new FileContentDescriptor { Text = "middle" } : null,
                ExpectedText = scenario == "edit" ? original : null,
                ReplacementText = scenario == "move-edit" ? string.Empty : "middle",
                Length = scenario == "edit" ? original.Length : 0,
            };
            var expected = scenario == "move-edit" ? original : "middle";
            var second = new Mutation
            {
                MutationId = MutationId.New(),
                Type = scenario == "create-delete" ? MutationType.DeleteFile : MutationType.ReplaceText,
                RelativePath = path,
                ExpectedText = scenario == "create-delete" ? null : expected,
                ReplacementText = scenario == "create-delete" ? string.Empty : "final",
                Length = scenario == "create-delete" ? 0 : expected.Length,
            };
            var proposals = new WorkspaceBatchProposals(workspaces, [first, second]);
            var orchestrator = new ExecutionOrchestrator(
                proposals,
                workspaces,
                fixture.BaselineHandler,
                fixture.ValidationHandler,
                workspaces,
                fixture.Checkpoints,
                fixture.Artifacts,
                events,
                new SecretOutputSanitizer(),
                NullLogger<ExecutionOrchestrator>.Instance,
                new CorrectiveMessageFactory(TestPromptLoader.Instance));
            var request = fixture.StartRequest with
            {
                Baseline = baseline,
                ValidationRequest = fixture.StartRequest.ValidationRequest with { Baseline = baseline },
            };

            await orchestrator.StartAsync(request);
            ExecutionOutcomeProjection? outcome = null;
            for (var batch = 0; batch < 2; batch++)
            {
                var staged = await orchestrator.HandleAsync(new GetExecutionMutationCommand(request.SessionId, request.RunId));
                outcome = await orchestrator.ContinueAsync(CreateContinuation(fixture, staged!));
            }

            Assert.Equal(ExecutionCheckpointPhase.Completed, outcome!.Status);
            if (scenario == "create-delete")
            {
                Assert.Null(outcome.FinalDiff);
                Assert.False(File.Exists(Path.Combine(root, path)));
            }
            else
            {
                var diff = await fixture.Artifacts.ReadAsync(outcome.FinalDiff!);
                Assert.DoesNotContain("middle", diff, StringComparison.Ordinal);
                Assert.Contains("+final", diff, StringComparison.Ordinal);
                Assert.Equal("final", await File.ReadAllTextAsync(Path.Combine(root, path)));
                Assert.Contains(scenario == "create-edit" ? "--- /dev/null" : "-dirty value", diff, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class WorkspaceBatchProposals : ICommandHandler<ProposeMutationSetCommand, StagedMutationSet>
    {
        private readonly ITransactionalWorkspaceResolver _workspaces;
        private readonly Queue<Mutation> _mutations;

        public WorkspaceBatchProposals(ITransactionalWorkspaceResolver workspaces, IEnumerable<Mutation> mutations)
        {
            _workspaces = workspaces;
            _mutations = new Queue<Mutation>(mutations);
        }

        public async Task<StagedMutationSet> HandleAsync(ProposeMutationSetCommand command, CancellationToken cancellationToken = default)
        {
            var baseline = _workspaces.GetWorkspace(command.WorkspaceId).Baseline;
            var staged = await _workspaces.StageAsync(
                new MutationSet
                {
                    MutationSetId = MutationSetId.New(),
                    SessionId = command.SessionId,
                    RunId = command.RunId,
                    WorkspaceId = command.WorkspaceId,
                    BaselineCapturedAt = baseline.CapturedAt,
                    BaselineRevision = baseline.GitRevision,
                    Mutations = [_mutations.Dequeue()],
                    Rationale = "Apply the next fixture change.",
                    IsWithinApprovedPlan = true,
                },
                cancellationToken);
            return staged with { StepComplete = _mutations.Count == 0 };
        }
    }

    private static MutationProposalResult Proposal(StagedMutationSet staged, bool? hint) => new()
    {
        StagedMutationSet = staged,
        StepComplete = hint,
        Rationale = staged.MutationSet.Rationale,
    };

    private static MutationProposalResult CompletionOnly() => new()
    {
        StepComplete = true,
        Rationale = "The selected step is complete.",
    };

    private static void SetProposals(ExecutionFixture fixture, params MutationProposalResult[] proposals)
    {
        fixture.ProposalHandler.Results.Clear();
        foreach (var proposal in proposals)
        {
            fixture.ProposalHandler.Results.Enqueue(proposal);
        }
    }

    private static ExecutionOrchestrator RecreateOrchestrator(ExecutionFixture fixture) => new(
        fixture.ProposalHandler,
        fixture.CommitHandler,
        fixture.BaselineHandler,
        fixture.ValidationHandler,
        fixture.WorkspaceResolver,
        fixture.Checkpoints,
        fixture.Artifacts,
        fixture.Events,
        new SecretOutputSanitizer(),
        NullLogger<ExecutionOrchestrator>.Instance,
        new CorrectiveMessageFactory(TestPromptLoader.Instance),
        workspaceLimits: fixture.WorkspaceLimits);
}
