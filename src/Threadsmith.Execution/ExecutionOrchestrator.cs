namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Serial host-owned approved-plan execution state machine over existing mutation and validation facades.</summary>
public sealed class ExecutionOrchestrator :
    IExecutionOrchestrator,
    ICommandHandler<ResumeRunCommand, ExecutionContinuation>,
    ICommandHandler<ContinueExecutionCommand, ExecutionOutcomeProjection>,
    ICommandHandler<PrepareExecutionValidationCommand, ExecutionContinuation>,
    ICommandHandler<ApplyExecutionMutationCommand, ExecutionApplyResult>,
    ICommandHandler<GetExecutionMutationCommand, StagedMutationSet?>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IExecutionArtifactPublisher _artifacts;
    private readonly IExecutionCheckpointStore _checkpoints;
    private readonly ICommandHandler<CommitMutationSetCommand, MutationCommitResult> _commits;
    private readonly IDomainEventStream _events;
    private readonly ILogger<ExecutionOrchestrator> _logger;
    private readonly ICommandHandler<ProposeMutationSetCommand, StagedMutationSet> _proposals;
    private readonly ConcurrentDictionary<RunId, ActiveExecution> _runs = new();
    private readonly ConcurrentDictionary<RunId, RunContinuationGate> _runContinuationGates = new();
    private readonly ConcurrentDictionary<RunId, TaskCompletionSource<ExecutionOutcomeProjection>> _terminalOutcomes = new();
    private readonly ICommandHandler<CaptureBaselineBuildCommand, BaselineCapture> _baselineValidation;
    private readonly ICommandHandler<ValidateMutationCommand, MutationValidationResult> _mutationValidation;
    private readonly ITransactionalWorkspaceResolver _workspaces;

    /// <summary>Initializes a new instance of the <see cref="ExecutionOrchestrator"/> class.</summary>
    public ExecutionOrchestrator(
        ICommandHandler<ProposeMutationSetCommand, StagedMutationSet> proposals,
        ICommandHandler<CommitMutationSetCommand, MutationCommitResult> commits,
        ICommandHandler<CaptureBaselineBuildCommand, BaselineCapture> baselineValidation,
        ICommandHandler<ValidateMutationCommand, MutationValidationResult> mutationValidation,
        ITransactionalWorkspaceResolver workspaces,
        IExecutionCheckpointStore checkpoints,
        IExecutionArtifactPublisher artifacts,
        IDomainEventStream events,
        ILogger<ExecutionOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(baselineValidation);
        ArgumentNullException.ThrowIfNull(mutationValidation);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        _proposals = proposals;
        _commits = commits;
        _baselineValidation = baselineValidation;
        _mutationValidation = mutationValidation;
        _workspaces = workspaces;
        _checkpoints = checkpoints;
        _artifacts = artifacts;
        _events = events;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExecutionContinuation> StartAsync(
        ExecutionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateStartRequest(request);
        if (await _checkpoints.GetOutcomeAsync(request.RunId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("A terminal execution cannot be started again.");
        }

        var diagnosticIdentity = GetBaselineIdentity(request.Baseline);
        var planHash = GetHash(JsonSerializer.Serialize(request.ApprovedPlan, JsonOptions));
        var preparing = CreateCheckpoint(
            request,
            planHash,
            diagnosticIdentity,
            ExecutionCheckpointPhase.ImplementationPreparing,
            nextAction: "start implementation model turn");
        var requestState = await _artifacts.PublishAsync(
            request.SessionId,
            "executionStartRequest",
            JsonSerializer.Serialize(request, JsonOptions),
            cancellationToken);
        preparing = preparing with { StateArtifact = requestState };
        await SaveCheckpointAsync(preparing, cancellationToken);

        var latestSafe = preparing;
        try
        {
            var modelTurn = preparing with
            {
                Phase = ExecutionCheckpointPhase.ImplementationModelTurn,
                NextAction = "admit one propose_mutations call",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveCheckpointAsync(modelTurn, cancellationToken);
            latestSafe = modelTurn;
            var staged = await _proposals.HandleAsync(
                new ProposeMutationSetCommand(
                    request.SessionId,
                    request.RunId,
                    request.Baseline.WorkspaceId,
                    request.Task,
                    request.ApprovedPlan,
                    RunPhase.ImplementationModelTurn),
                cancellationToken);
            var diff = await _artifacts.PublishAsync(
                request.SessionId,
                "executionDiff",
                staged.Preview.UnifiedDiff,
                CancellationToken.None);
            var active = new ActiveExecution(request, staged, null, null, [], [], [], []);
            var state = await PublishStateAsync(active, CancellationToken.None);
            var pendingApproval = modelTurn with
            {
                Phase = ExecutionCheckpointPhase.MutationApprovalPending,
                CurrentPlanStepId = ResolveCurrentStep(request.ApprovedPlan, staged.MutationSet),
                MutationSetId = staged.MutationSet.MutationSetId,
                StateArtifact = state,
                DiffArtifact = diff,
                NextAction = "obtain separate mutation authorization",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            _runs[request.RunId] = active;
            await SaveCheckpointAsync(pendingApproval, CancellationToken.None);
            return pendingApproval;
        }
        catch (OperationCanceledException)
        {
            var cancelled = latestSafe with
            {
                Phase = ExecutionCheckpointPhase.Cancelled,
                NextAction = "explicit resume after repository revalidation",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveCheckpointAsync(cancelled, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await SaveCheckpointAsync(
                preparing with
                {
                    Phase = ExecutionCheckpointPhase.Failed,
                    NextAction = "inspect failure and submit a fresh request",
                    RecordedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None);
            _logger.LogError(
                exception,
                "Execution preparation failed for run {RunId}.",
                request.RunId.Value);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ExecutionOutcomeProjection> ContinueAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateContinueRequest(request);
        using var gate = await EnterRunContinuationGateAsync(
            request.RunId,
            cancellationToken);
        var applied = await ApplyCoreAsync(request, cancellationToken);
        var active = await ResolveActiveAsync(
            request.SessionId,
            request.RunId,
            cancellationToken);
        var baseline = active.BaselineCapture
            ?? throw new InvalidDataException("Applied execution state has no immutable diagnostic baseline.");
        return await ValidateAndCompleteAsync(
            request,
            active,
            applied.Continuation,
            baseline,
            applied.Continuation.BaselineArtifact,
            Bound(request.ApprovalProvenance, 256),
            cancellationToken);
    }

    /// <summary>Pre-captures immutable validation baseline evidence while mutation review is pending.</summary>
    public async Task<ExecutionContinuation> PrepareValidationAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        using var gate = await EnterRunContinuationGateAsync(
            runId,
            cancellationToken);
        var active = await ResolveActiveAsync(
            sessionId,
            runId,
            cancellationToken);
        var checkpoint = await RequireCheckpointAsync(runId, cancellationToken);
        if (checkpoint.Phase != ExecutionCheckpointPhase.MutationApprovalPending)
        {
            return checkpoint;
        }

        if (active.BaselineCapture is not null && checkpoint.BaselineArtifact is not null)
        {
            return checkpoint;
        }

        var baseline = await _baselineValidation.HandleAsync(
            new CaptureBaselineBuildCommand(active.Request.ValidationRequest)
            {
                MutationSet = active.Staged.MutationSet,
            },
            cancellationToken);
        ValidateBaselineCapture(active.Request, baseline);
        var baselineArtifact = await _artifacts.PublishAsync(
            sessionId,
            "executionBaselineCapture",
            JsonSerializer.Serialize(baseline, JsonOptions),
            cancellationToken);
        var preparedActive = active with { BaselineCapture = baseline };
        var state = await PublishStateAsync(preparedActive, cancellationToken);
        var prepared = checkpoint with
        {
            BaselineArtifact = baselineArtifact,
            StateArtifact = state,
            NextAction = "obtain separate mutation authorization",
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveCheckpointAsync(prepared, cancellationToken);
        _runs[runId] = preparedActive;
        return prepared;
    }

    /// <summary>Applies an authorized execution mutation and stops at the durable applied boundary.</summary>
    public async Task<ExecutionApplyResult> ApplyAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateContinueRequest(request);
        using var gate = await EnterRunContinuationGateAsync(
            request.RunId,
            cancellationToken);
        return await ApplyCoreAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExecutionContinuation> ResumeAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        using var gate = await EnterRunContinuationGateAsync(
            runId,
            cancellationToken);
        var checkpoint = await RequireCheckpointAsync(runId, cancellationToken);
        if (checkpoint.SessionId != sessionId)
        {
            throw new UnauthorizedAccessException("The execution does not belong to the requesting session.");
        }

        if (checkpoint.Phase is ExecutionCheckpointPhase.Completed
            or ExecutionCheckpointPhase.Failed
            or ExecutionCheckpointPhase.RolledBack)
        {
            await PublishResumeAsync(sessionId, runId, false, "terminal executions cannot resume", cancellationToken);
            throw new InvalidOperationException("A terminal execution cannot be resumed.");
        }

        if (checkpoint.Operation?.State == ExecutionOperationState.Pending)
        {
            await PublishResumeAsync(
                sessionId,
                runId,
                false,
                "pending side effect requires explicit reconciliation",
                cancellationToken);
            throw new InvalidOperationException(
                "The pending side effect cannot be safely replayed; explicit recovery is required.");
        }

        if (checkpoint.Phase == ExecutionCheckpointPhase.Cancelled
            && checkpoint.MutationSetId is null)
        {
            var restart = await RestoreStartRequestAsync(checkpoint, cancellationToken);
            ValidateLiveWorkspace(checkpoint, restart.Baseline);
            var restarted = await StartAsync(restart, cancellationToken);
            await PublishResumeAsync(
                sessionId,
                runId,
                true,
                "cancelled implementation restarted from durable host request state",
                cancellationToken);
            return restarted;
        }

        var active = await RestoreStateAsync(checkpoint, cancellationToken);
        ValidateResumeState(checkpoint, active);
        ValidateLiveWorkspace(checkpoint, active.Request.Baseline);
        _runs[runId] = active;
        ExecutionContinuation resumed;
        if (checkpoint.Phase == ExecutionCheckpointPhase.BaselineValidation)
        {
            resumed = checkpoint with
            {
                Phase = ExecutionCheckpointPhase.MutationApprovalPending,
                NextAction = "obtain fresh mutation authorization after interrupted baseline validation",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveCheckpointAsync(resumed, cancellationToken);
        }
        else if (checkpoint.Phase is ExecutionCheckpointPhase.MutationApplied
            or ExecutionCheckpointPhase.BuildValidation
            or ExecutionCheckpointPhase.TestValidation
            or ExecutionCheckpointPhase.CompletionPending)
        {
            var baseline = active.BaselineCapture
                ?? throw new InvalidDataException("Applied execution state has no immutable diagnostic baseline.");
            var provenance = checkpoint.PolicyIdentity ?? "resumed execution authorization";
            _ = await ValidateAndCompleteAsync(
                new ContinueExecutionRequest
                {
                    SessionId = sessionId,
                    RunId = runId,
                    Approval = new MutationApproval
                    {
                        Level = MutationApprovalLevel.EntireSet,
                        ApprovalId = active.Staged.ApprovalId,
                    },
                    ApprovalProvenance = provenance,
                },
                active,
                checkpoint,
                baseline,
                checkpoint.BaselineArtifact,
                provenance,
                cancellationToken);
            resumed = await RequireCheckpointAsync(runId, cancellationToken);
        }
        else
        {
            var compensatedMutation = checkpoint.Phase == ExecutionCheckpointPhase.Cancelled
                && checkpoint.MutationSetId is not null
                && checkpoint.Operation?.State == ExecutionOperationState.RolledBack;
            resumed = checkpoint with
            {
                Phase = compensatedMutation
                    ? ExecutionCheckpointPhase.MutationApprovalPending
                    : checkpoint.Phase,
                NextAction = compensatedMutation
                    ? "obtain fresh mutation authorization after proven compensation"
                    : checkpoint.Phase == ExecutionCheckpointPhase.Cancelled
                        ? "restart implementation from the last safe phase boundary"
                        : checkpoint.NextAction,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveCheckpointAsync(resumed, cancellationToken);
        }

        await PublishResumeAsync(
            sessionId,
            runId,
            true,
            "checkpoint and artifacts revalidated and advanced from the durable boundary",
            cancellationToken);
        return resumed;
    }

    /// <inheritdoc />
    public Task<ExecutionContinuation> HandleAsync(
        ResumeRunCommand command,
        CancellationToken cancellationToken = default)
    {
        return ResumeAsync(command.SessionId, command.RunId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionOutcomeProjection> HandleAsync(
        ContinueExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ContinueAsync(command.Request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionContinuation> HandleAsync(
        PrepareExecutionValidationCommand command,
        CancellationToken cancellationToken = default)
    {
        return PrepareValidationAsync(command.SessionId, command.RunId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionApplyResult> HandleAsync(
        ApplyExecutionMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyAsync(command.Request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExecutionOutcomeProjection> WaitForOutcomeAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _checkpoints.GetOutcomeAsync(runId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var completion = _terminalOutcomes.GetOrAdd(
            runId,
            static _ => new TaskCompletionSource<ExecutionOutcomeProjection>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        return await completion.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StagedMutationSet?> HandleAsync(
        GetExecutionMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await _checkpoints.GetCheckpointAsync(
            command.RunId,
            cancellationToken);
        if (checkpoint is null || checkpoint.SessionId != command.SessionId)
        {
            return null;
        }

        var active = await ResolveActiveAsync(
            command.SessionId,
            command.RunId,
            cancellationToken);
        return active.Staged;
    }

    private static void ValidateContinueRequest(ContinueExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Approval);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApprovalProvenance);
    }

    private async Task<ExecutionApplyResult> ApplyCoreAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var checkpoint = await WaitForMutationApprovalCheckpointAsync(
            request.RunId,
            cancellationToken);
        var active = await ResolveActiveAsync(
            request.SessionId,
            request.RunId,
            cancellationToken);

        if (request.Approval.ApprovalId != active.Staged.ApprovalId)
        {
            throw new UnauthorizedAccessException("Mutation authorization does not match the staged exact diff.");
        }

        var provenance = Bound(request.ApprovalProvenance, 256);
        var baselinePending = checkpoint with
        {
            Phase = ExecutionCheckpointPhase.BaselineValidation,
            PolicyIdentity = provenance,
            NextAction = "capture exact pre-mutation diagnostic baseline",
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveCheckpointAsync(baselinePending, cancellationToken);
        var baselineArtifact = checkpoint.BaselineArtifact;
        if (active.BaselineCapture is null || baselineArtifact is null)
        {
            var capturedBaseline = await _baselineValidation.HandleAsync(
                new CaptureBaselineBuildCommand(active.Request.ValidationRequest)
                {
                    MutationSet = active.Staged.MutationSet,
                },
                cancellationToken);
            ValidateBaselineCapture(active.Request, capturedBaseline);

            baselineArtifact = await _artifacts.PublishAsync(
                request.SessionId,
                "executionBaselineCapture",
                JsonSerializer.Serialize(capturedBaseline, JsonOptions),
                cancellationToken);
            active = active with { BaselineCapture = capturedBaseline };
        }

        var operationId = GetStableOperationId(request.RunId, active.Staged.MutationSet.MutationSetId);
        var intent = new ExecutionOperationRecord
        {
            OperationId = operationId,
            Kind = "mutation-commit",
            State = ExecutionOperationState.Pending,
            ExpectedPreState = checkpoint.MutationBaselineIdentity,
            ExpectedResult = active.Staged.MutationSet.MutationSetId.Value.ToString("D"),
        };
        var stateBeforeCommit = await PublishStateAsync(active, cancellationToken);
        var applyPending = baselinePending with
        {
            Phase = ExecutionCheckpointPhase.MutationApplyPending,
            BaselineArtifact = baselineArtifact,
            StateArtifact = stateBeforeCommit,
            Operation = intent,
            NextAction = "reconcile or apply mutation transaction",
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveCheckpointAsync(applyPending, cancellationToken);
        await PublishOperationAsync(
            request.SessionId,
            request.RunId,
            intent,
            cancellationToken);

        MutationCommitResult committed;
        try
        {
            committed = await _commits.HandleAsync(
                new CommitMutationSetCommand(
                    request.SessionId,
                    active.Staged.MutationSet.MutationSetId,
                    request.Approval),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var reconciliation = await ReconcileFailedCommitAsync(
                active,
                applyPending,
                wasCancelled: true);
            if (!reconciliation.Succeeded)
            {
                await MarkRecoveryRequiredAsync(applyPending, "commit cancellation requires explicit reconciliation");
            }

            await RecordFailedCommitOutcomeIfTerminalAsync(
                request,
                active,
                provenance,
                reconciliation.Results);
            throw;
        }
        catch (Exception)
        {
            var reconciliation = await ReconcileFailedCommitAsync(
                active,
                applyPending,
                wasCancelled: false);
            if (!reconciliation.Succeeded)
            {
                await MarkRecoveryRequiredAsync(applyPending, "commit result could not be proven");
            }

            await RecordFailedCommitOutcomeIfTerminalAsync(
                request,
                active,
                provenance,
                reconciliation.Results);
            throw;
        }

        var completedOperation = intent with
        {
            State = ExecutionOperationState.Completed,
            ExpectedResult = GetHash(JsonSerializer.Serialize(committed, JsonOptions)),
            Reconciliation = "transaction returned its authoritative committed result",
        };
        var promotedBaseline = await _workspaces.PromoteBaselineAsync(
            active.Request.Baseline.WorkspaceId,
            committed.ChangedFiles,
            cancellationToken);
        var appliedDiff = CreateAppliedDiff(active.Staged, committed);
        active = active with
        {
            Commit = committed,
            AppliedFiles = active.AppliedFiles
                .Concat(committed.ChangedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AppliedPlanStepIds = IsEntireStagedSetApplied(active.Staged, committed)
                ? active.AppliedPlanStepIds
                    .Concat(active.Staged.PlanStepIds)
                    .Distinct()
                    .ToArray()
                : active.AppliedPlanStepIds,
            AppliedDiffs = string.IsNullOrWhiteSpace(appliedDiff)
                ? active.AppliedDiffs
                : [.. active.AppliedDiffs, appliedDiff],
            AppliedLifecycleChanges = active.AppliedLifecycleChanges
                .Concat(CreateAppliedLifecycleChanges(active.Staged, committed))
                .ToArray(),
            AppliedLifecycleReconciliations = active.AppliedLifecycleReconciliations
                .Concat(committed.LifecycleReconciliations)
                .ToArray(),
        };
        _runs[request.RunId] = active;
        var appliedState = await PublishStateAsync(active, cancellationToken);
        var applied = applyPending with
        {
            Phase = ExecutionCheckpointPhase.MutationApplied,
            StateArtifact = appliedState,
            Operation = completedOperation,
            MutationBaselineGeneration = applyPending.MutationBaselineGeneration + 1,
            MutationBaselineIdentity = GetBaselineIdentity(promotedBaseline),
            NextAction = "validate applied mutation",
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await PublishOperationAsync(
            request.SessionId,
            request.RunId,
            completedOperation,
            cancellationToken);
        await SaveCheckpointAsync(applied, cancellationToken);
        return new ExecutionApplyResult
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            MutationSetId = active.Staged.MutationSet.MutationSetId,
            ChangedFiles = committed.ChangedFiles.ToArray(),
            LifecycleReconciliations = committed.LifecycleReconciliations.ToArray(),
            Continuation = applied,
        };
    }

    /// <summary>Advances authoritative validation and outcome assembly from an applied checkpoint.</summary>
    private async Task<ExecutionOutcomeProjection> ValidateAndCompleteAsync(
        ContinueExecutionRequest request,
        ActiveExecution active,
        ExecutionContinuation applied,
        BaselineCapture baseline,
        ExecutionArtifactReference? baselineArtifact,
        string provenance,
        CancellationToken cancellationToken)
    {
        var committed = active.Commit
            ?? throw new InvalidDataException("Applied execution state has no authoritative commit result.");
        var validation = await _mutationValidation.HandleAsync(
            new ValidateMutationCommand
            {
                Request = active.Request.ValidationRequest,
                BaselineCapture = baseline,
                MutationSet = active.Staged.MutationSet,
                RequiredApprovalsPresent = committed.AppliedMutations.Count > 0,
                FinalDiffAvailable = active.AppliedDiffs.Count > 0,
                ResidualRisks = active.Request.ApprovedPlan.Risks,
            },
            cancellationToken);
        var validationArtifact = await _artifacts.PublishAsync(
            request.SessionId,
            "executionValidation",
            JsonSerializer.Serialize(validation, JsonOptions),
            cancellationToken);
        active = active with
        {
            Validation = validation,
            PriorValidations = [.. active.PriorValidations, validation],
        };
        _runs[request.RunId] = active;

        var succeeded = validation.Gate.Status == AcceptanceGateStatus.Passed;
        if (!succeeded && applied.CorrectionAttempts < applied.CorrectionBudget)
        {
            var correctionEvidence = CreateCorrectionEvidence(validation);
            var correction = await _proposals.HandleAsync(
                new ProposeMutationSetCommand(
                    request.SessionId,
                    request.RunId,
                    active.Request.Baseline.WorkspaceId,
                    active.Request.Task,
                    active.Request.ApprovedPlan,
                    RunPhase.CorrectionModelTurn,
                    correctionEvidence),
                cancellationToken);
            var correctionDiff = await _artifacts.PublishAsync(
                request.SessionId,
                "executionCorrectionDiff",
                correction.Preview.UnifiedDiff,
                cancellationToken);
            active = active with { Staged = correction };
            _runs[request.RunId] = active;
            var correctionState = await PublishStateAsync(active, cancellationToken);
            await SaveCheckpointAsync(
                applied with
                {
                    Phase = ExecutionCheckpointPhase.MutationApprovalPending,
                    CurrentPlanStepId = ResolveCurrentStep(active.Request.ApprovedPlan, correction.MutationSet),
                    MutationSetId = correction.MutationSet.MutationSetId,
                    StateArtifact = correctionState,
                    DiffArtifact = correctionDiff,
                    BaselineArtifact = baselineArtifact,
                    ValidationArtifact = validationArtifact,
                    Operation = null,
                    CorrectionAttempts = applied.CorrectionAttempts + 1,
                    NextAction = "review correction exact diff and obtain separate mutation authorization",
                    RecordedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken);
            return CreateInterimCorrectionProjection(request, active, validation, provenance);
        }

        var status = succeeded
            ? ExecutionCheckpointPhase.Completed
            : ExecutionCheckpointPhase.Failed;
        StepId[] allSteps = [.. active.Request.ApprovedPlan.Steps.Select(step => step.StepId)];
        HashSet<StepId> appliedStepIds = [.. active.AppliedPlanStepIds];
        StepId[] completedSteps = [.. allSteps.Where(appliedStepIds.Contains)];
        StepId[] uncompletedSteps = [.. allSteps.Where(stepId => !appliedStepIds.Contains(stepId))];
        var finalDiff = active.AppliedDiffs.Count == 0
            ? null
            : await _artifacts.PublishAsync(
                request.SessionId,
                "executionFinalDiff",
                string.Join(Environment.NewLine, active.AppliedDiffs),
                cancellationToken);
        var outcome = new ExecutionOutcomeProjection
        {
            Key = new ProjectionKey("executionOutcome", request.RunId.Value.ToString("D")),
            SessionId = request.SessionId,
            RunId = request.RunId,
            Status = status,
            CompletedStepIds = succeeded ? completedSteps : [],
            UncompletedStepIds = succeeded ? uncompletedSteps : allSteps,
            ChangedFiles = active.AppliedFiles.ToArray(),
            LifecycleChanges = active.AppliedLifecycleChanges.ToArray(),
            LifecycleReconciliations = active.AppliedLifecycleReconciliations.ToArray(),
            BehaviorSummary = active.Request.ApprovedPlan.Steps
                .Where(step => succeeded && appliedStepIds.Contains(step.StepId))
                .Select(step => step.ExpectedOutcome)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Bound(value, 512))
                .ToArray(),
            FinalDiff = finalDiff,
            Validation = validation,
            ApprovalProvenance = provenance,
            CorrectionAttempts = applied.CorrectionAttempts,
            RollbackAvailable = committed.AppliedMutations.Count > 0,
            ResidualRisks = succeeded
                ? active.Request.ApprovedPlan.Risks.ToArray()
                : [.. active.Request.ApprovedPlan.Risks, .. validation.Gate.Reasons],
        };
        await _checkpoints.SaveOutcomeAsync(outcome, cancellationToken);
        _terminalOutcomes.GetOrAdd(
            request.RunId,
            static _ => new TaskCompletionSource<ExecutionOutcomeProjection>(
                TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(outcome);
        await _events.PublishAsync(
            new ExecutionOutcomeRecorded(
                request.SessionId,
                DateTimeOffset.UtcNow,
                request.RunId,
                status),
            cancellationToken);
        await SaveCheckpointAsync(
            applied with
            {
                Phase = status,
                DiffArtifact = finalDiff,
                ValidationArtifact = validationArtifact,
                StateArtifact = await PublishStateAsync(active, cancellationToken),
                NextAction = "terminal outcome recorded",
                RecordedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);
        return outcome;
    }

    private async Task RecordFailedCommitOutcomeIfTerminalAsync(
        ContinueExecutionRequest request,
        ActiveExecution active,
        string provenance,
        IReadOnlyList<FileLifecycleReconciliation> currentReconciliations)
    {
        var checkpoint = await RequireCheckpointAsync(request.RunId, CancellationToken.None);
        if (checkpoint.Phase != ExecutionCheckpointPhase.Failed)
        {
            return;
        }

        StepId[] allSteps = [.. active.Request.ApprovedPlan.Steps.Select(step => step.StepId)];
        var finalDiff = active.AppliedDiffs.Count == 0
            ? null
            : await _artifacts.PublishAsync(
                request.SessionId,
                "executionFinalDiff",
                string.Join(Environment.NewLine, active.AppliedDiffs),
                CancellationToken.None);
        var outcome = new ExecutionOutcomeProjection
        {
            Key = new ProjectionKey("executionOutcome", request.RunId.Value.ToString("D")),
            SessionId = request.SessionId,
            RunId = request.RunId,
            Status = ExecutionCheckpointPhase.Failed,
            UncompletedStepIds = allSteps,
            ChangedFiles = active.AppliedFiles.ToArray(),
            LifecycleChanges = active.AppliedLifecycleChanges.ToArray(),
            LifecycleReconciliations = active.AppliedLifecycleReconciliations
                .Concat(currentReconciliations)
                .ToArray(),
            BehaviorSummary = ["Mutation commit failed before authoritative completion."],
            FinalDiff = finalDiff,
            Validation = active.Validation,
            ApprovalProvenance = provenance,
            CorrectionAttempts = checkpoint.CorrectionAttempts,
            RollbackAvailable = active.AppliedFiles.Count > 0,
            ResidualRisks =
            [
                .. active.Request.ApprovedPlan.Risks,
                .. active.Validation?.Gate.Reasons ?? [],
                "The mutation transaction did not produce an authoritative committed result.",
            ],
        };
        await _checkpoints.SaveOutcomeAsync(outcome, CancellationToken.None);
        _terminalOutcomes.GetOrAdd(
            request.RunId,
            static _ => new TaskCompletionSource<ExecutionOutcomeProjection>(
                TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(outcome);
        await _events.PublishAsync(
            new ExecutionOutcomeRecorded(
                request.SessionId,
                DateTimeOffset.UtcNow,
                request.RunId,
                ExecutionCheckpointPhase.Failed),
            CancellationToken.None);
    }

    private async Task<ExecutionContinuation> WaitForMutationApprovalCheckpointAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var checkpoint = await RequireCheckpointAsync(runId, cancellationToken);
            if (checkpoint.Phase == ExecutionCheckpointPhase.MutationApprovalPending)
            {
                return checkpoint;
            }

            if (checkpoint.Phase is ExecutionCheckpointPhase.PlanApproved
                or ExecutionCheckpointPhase.ImplementationPreparing
                or ExecutionCheckpointPhase.ImplementationModelTurn
                or ExecutionCheckpointPhase.MutationProposed
                or ExecutionCheckpointPhase.MutationStaged)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                continue;
            }

            throw new InvalidOperationException(
                $"Execution is at {checkpoint.Phase}; mutation authorization cannot be consumed.");
        }
    }

    private async Task<ActiveExecution> ResolveActiveAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken)
    {
        if (_runs.TryGetValue(runId, out var active))
        {
            if (active.Request.SessionId != sessionId)
            {
                throw new UnauthorizedAccessException("The execution does not belong to the requesting session.");
            }

            return active;
        }

        var checkpoint = await RequireCheckpointAsync(runId, cancellationToken);
        active = await RestoreStateAsync(checkpoint, cancellationToken);
        ValidateResumeState(checkpoint, active);
        _runs[runId] = active;
        return active;
    }

    private async Task<ActiveExecution> RestoreStateAsync(
        ExecutionContinuation checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.StateArtifact is null)
        {
            throw new InvalidDataException("The checkpoint has no resumable continuation-state artifact.");
        }

        var json = await _artifacts.ReadAsync(checkpoint.StateArtifact, cancellationToken)
            ?? throw new InvalidDataException("The continuation-state artifact is missing or corrupt.");

        var active = JsonSerializer.Deserialize<ActiveExecution>(json, JsonOptions)
            ?? throw new InvalidDataException("The continuation-state artifact is invalid.");
        ValidateActiveExecutionShape(active);
        return active;
    }

    private async Task<ExecutionStartRequest> RestoreStartRequestAsync(
        ExecutionContinuation checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.StateArtifact is null)
        {
            throw new InvalidDataException("The checkpoint has no resumable start-request artifact.");
        }

        var json = await _artifacts.ReadAsync(checkpoint.StateArtifact, cancellationToken)
            ?? throw new InvalidDataException("The start-request artifact is missing or corrupt.");
        return JsonSerializer.Deserialize<ExecutionStartRequest>(json, JsonOptions)
            ?? throw new InvalidDataException("The start-request artifact is invalid.");
    }

    private void ValidateLiveWorkspace(
        ExecutionContinuation checkpoint,
        WorkspaceBaseline persistedBaseline)
    {
        var liveBaseline = _workspaces.GetWorkspace(checkpoint.WorkspaceId).Baseline;
        var expectedIdentity = checkpoint.MutationBaselineIdentity;
        if (!string.Equals(GetBaselineIdentity(liveBaseline), expectedIdentity, StringComparison.Ordinal)
            || liveBaseline.TrustLevel != persistedBaseline.TrustLevel
            || !string.Equals(
                liveBaseline.SelectedSolutionPath,
                persistedBaseline.SelectedSolutionPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The live repository bytes, trust, or selected solution changed after the execution checkpoint.");
        }
    }

    private async Task<ExecutionArtifactReference> PublishStateAsync(
        ActiveExecution active,
        CancellationToken cancellationToken)
    {
        return await _artifacts.PublishAsync(
            active.Request.SessionId,
            "executionContinuationState",
            JsonSerializer.Serialize(active, JsonOptions),
            cancellationToken);
    }

    private async Task SaveCheckpointAsync(
        ExecutionContinuation checkpoint,
        CancellationToken cancellationToken)
    {
        await _checkpoints.SaveCheckpointAsync(checkpoint, cancellationToken);
        await _events.PublishAsync(
            new ExecutionCheckpointWritten(
                checkpoint.SessionId,
                DateTimeOffset.UtcNow,
                checkpoint.RunId,
                checkpoint.Phase,
                checkpoint.NextAction),
            cancellationToken);
    }

    private async Task<ExecutionContinuation> RequireCheckpointAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        return await _checkpoints.GetCheckpointAsync(runId, cancellationToken)
            ?? throw new KeyNotFoundException($"Run '{runId}' has no execution checkpoint.");
    }

    private Task PublishOperationAsync(
        SessionId sessionId,
        RunId runId,
        ExecutionOperationRecord operation,
        CancellationToken cancellationToken)
    {
        return _events.PublishAsync(
            new ExecutionSideEffectRecorded(
                sessionId,
                DateTimeOffset.UtcNow,
                runId,
                operation.OperationId,
                operation.Kind,
                operation.State),
            cancellationToken);
    }

    private Task PublishResumeAsync(
        SessionId sessionId,
        RunId runId,
        bool succeeded,
        string reason,
        CancellationToken cancellationToken)
    {
        return _events.PublishAsync(
            new ExecutionResumeRecorded(
                sessionId,
                DateTimeOffset.UtcNow,
                runId,
                succeeded,
                reason),
            cancellationToken);
    }

    private async Task<FailedCommitReconciliation> ReconcileFailedCommitAsync(
        ActiveExecution active,
        ExecutionContinuation checkpoint,
        bool wasCancelled)
    {
        var reconciliations = await _workspaces
            .GetWorkspace(active.Request.Baseline.WorkspaceId)
            .ReconcileLifecycleAsync(
                active.Staged.MutationSet.MutationSetId,
                CancellationToken.None);
        if (reconciliations.Count == 0)
        {
            return new FailedCommitReconciliation(false, []);
        }

        var operation = checkpoint.Operation
            ?? throw new InvalidOperationException("The apply checkpoint has no pending operation.");
        var summary = string.Join(
            "; ",
            reconciliations.Select(item => $"{item.MutationId.Value:D}:{item.State}"));
        HashSet<MutationId> reconciledMutationIds =
        [
            .. reconciliations.Select(item => item.MutationId),
        ];
        var everyMutationReconciled = active.Staged.MutationSet.Mutations.All(mutation =>
            reconciledMutationIds.Contains(mutation.MutationId));
        var safelyCompensated = everyMutationReconciled
            && reconciliations.All(item =>
                item.State is FileLifecycleReconciliationState.NotStarted
                    or FileLifecycleReconciliationState.Compensated);
        var reconciledOperation = operation with
        {
            State = safelyCompensated
                ? ExecutionOperationState.RolledBack
                : ExecutionOperationState.RecoveryRequired,
            Reconciliation = Bound(summary, 1024),
        };
        await SaveCheckpointAsync(
            checkpoint with
            {
                Operation = reconciledOperation,
                Phase = safelyCompensated && wasCancelled
                    ? ExecutionCheckpointPhase.Cancelled
                    : ExecutionCheckpointPhase.Failed,
                NextAction = safelyCompensated
                    ? wasCancelled
                        ? "resume from fresh mutation authorization after proven compensation"
                        : "inspect the failed commit; exact baseline identities were restored"
                    : "explicit repository recovery required for reconciled lifecycle effects",
                RecordedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        await PublishOperationAsync(
            active.Request.SessionId,
            active.Request.RunId,
            reconciledOperation,
            CancellationToken.None);
        return new FailedCommitReconciliation(true, reconciliations);
    }

    private async Task MarkRecoveryRequiredAsync(
        ExecutionContinuation checkpoint,
        string reason)
    {
        var operation = checkpoint.Operation
            ?? throw new InvalidOperationException("The apply checkpoint has no pending operation.");
        var recovery = operation with
        {
            State = ExecutionOperationState.RecoveryRequired,
            Reconciliation = reason,
        };
        await SaveCheckpointAsync(
            checkpoint with
            {
                Operation = recovery,
                Phase = ExecutionCheckpointPhase.Failed,
                NextAction = "explicit repository recovery required",
                RecordedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
    }

    private static ExecutionContinuation CreateCheckpoint(
        ExecutionStartRequest request,
        string planHash,
        string baselineIdentity,
        ExecutionCheckpointPhase phase,
        string nextAction)
    {
        return new ExecutionContinuation
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = request.Baseline.WorkspaceId,
            PlanRevision = request.ApprovedPlan.Revision,
            PlanHash = planHash,
            Phase = phase,
            DiagnosticBaselineIdentity = baselineIdentity,
            MutationBaselineIdentity = baselineIdentity,
            CorrectionBudget = request.CorrectionBudget,
            NextAction = nextAction,
            RecordedAt = DateTimeOffset.UtcNow,
        };
    }

    private static void ValidateStartRequest(ExecutionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        ArgumentNullException.ThrowIfNull(request.Task);
        ArgumentNullException.ThrowIfNull(request.ApprovedPlan);
        ArgumentNullException.ThrowIfNull(request.ValidationRequest);
        if (request.SessionId == default
            || request.RunId == default
            || request.Baseline.WorkspaceId == default
            || request.ApprovedPlan.Revision < 1
            || request.CorrectionBudget < 0)
        {
            throw new ArgumentException("Execution start state contains invalid identity, plan, or budget data.", nameof(request));
        }

        if (request.ValidationRequest.SessionId != request.SessionId
            || request.ValidationRequest.RunId != request.RunId
            || request.ValidationRequest.Baseline.WorkspaceId != request.Baseline.WorkspaceId
            || request.ValidationRequest.Baseline.CapturedAt != request.Baseline.CapturedAt)
        {
            throw new ArgumentException(
                "Execution validation and mutation baselines must share exact host-owned identity.",
                nameof(request));
        }
    }

    private static void ValidateBaselineCapture(
        ExecutionStartRequest request,
        BaselineCapture capture)
    {
        if (capture.WorkspaceId != request.Baseline.WorkspaceId
            || capture.BaselineCapturedAt != request.Baseline.CapturedAt
            || capture.CapturedAt == default)
        {
            throw new InvalidOperationException(
                "The diagnostic baseline capture is missing, stale, or belongs to another workspace generation.");
        }
    }

    private static void ValidateResumeState(
        ExecutionContinuation checkpoint,
        ActiveExecution active)
    {
        ValidateActiveExecutionShape(active);
        var baselineIdentity = GetBaselineIdentity(active.Request.Baseline);
        var planHash = GetHash(JsonSerializer.Serialize(active.Request.ApprovedPlan, JsonOptions));
        if (active.Request.SessionId != checkpoint.SessionId
            || active.Request.RunId != checkpoint.RunId
            || active.Request.Baseline.WorkspaceId != checkpoint.WorkspaceId
            || active.Request.ApprovedPlan.Revision != checkpoint.PlanRevision
            || !string.Equals(planHash, checkpoint.PlanHash, StringComparison.Ordinal)
            || !string.Equals(
                baselineIdentity,
                checkpoint.DiagnosticBaselineIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The restored repository, plan, or diagnostic baseline does not match the checkpoint.");
        }
    }

    private static void ValidateActiveExecutionShape(ActiveExecution active)
    {
        if (active.Request is null
            || active.Request.Baseline is null
            || active.Request.ApprovedPlan is null
            || active.Staged is null
            || active.Staged.MutationSet is null
            || active.Staged.Preview is null
            || active.Staged.Conflicts is null
            || active.PriorValidations is null
            || active.AppliedFiles is null
            || active.AppliedPlanStepIds is null
            || active.AppliedDiffs is null
            || active.AppliedLifecycleChanges is null
            || active.AppliedLifecycleReconciliations is null)
        {
            throw new InvalidDataException("The continuation-state artifact does not contain active execution state.");
        }
    }

    private static StepId? ResolveCurrentStep(
        ImplementationPlan plan,
        MutationSet mutationSet)
    {
        var files = mutationSet.Mutations
            .Select(mutation => mutation.RelativePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.Steps.FirstOrDefault(step => step.GetAffectedPaths()
            .Select(path => path.Replace('\\', '/'))
            .Any(files.Contains))?.StepId;
    }

    private static string GetBaselineIdentity(WorkspaceBaseline baseline)
    {
        return GetHash(string.Join(
            "|",
            baseline.WorkspaceId.Value.ToString("D"),
            baseline.CapturedAt.ToString("O"),
            baseline.GitRevision ?? string.Empty,
            baseline.SelectedSolutionPath ?? string.Empty,
            baseline.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => $"{file.RelativePath}:{file.Sha256}")));
    }

    private static string CreateCorrectionEvidence(MutationValidationResult validation)
    {
        var diagnostic = validation.Diagnostics.FirstOrDefault(item =>
            item.Severity == DiagnosticSeverity.Error && !item.IsBaselineDiagnostic);
        if (diagnostic is not null)
        {
            return $"Compiler {diagnostic.Code} in {diagnostic.File ?? diagnostic.Project}: {diagnostic.Message}";
        }

        var failedTest = validation.Tests.Results.FirstOrDefault(item =>
            item.Outcome == TestOutcome.Failed);
        if (failedTest is not null)
        {
            return $"Selected test project {failedTest.Project.Name} failed with {failedTest.Failed} failing tests.";
        }

        return $"Validation gate requires correction: {string.Join("; ", validation.Gate.Reasons.Take(3))}";
    }

    private async Task<RunContinuationGateLease> EnterRunContinuationGateAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        RunContinuationGate? gate = null;
        while (gate is null)
        {
            if (!_runContinuationGates.TryGetValue(runId, out var current))
            {
                var candidate = new RunContinuationGate();
                if (!_runContinuationGates.TryAdd(runId, candidate))
                {
                    candidate.Dispose();
                    continue;
                }

                current = candidate;
            }

            if (current.TryAddReference())
            {
                gate = current;
            }
        }

        try
        {
            await gate.WaitAsync(cancellationToken);
            return new RunContinuationGateLease(this, runId, gate);
        }
        catch
        {
            ReleaseRunContinuationGateReference(runId, gate);
            throw;
        }
    }

    private void ReleaseRunContinuationGateReference(RunId runId, RunContinuationGate gate)
    {
        if (!gate.ReleaseReference())
        {
            return;
        }

        var removed = _runContinuationGates.TryRemove(runId, out var registered);
        if (!removed || !ReferenceEquals(gate, registered))
        {
            throw new InvalidOperationException("The run continuation gate registry became inconsistent.");
        }

        gate.Dispose();
    }

    private static bool IsEntireStagedSetApplied(
        StagedMutationSet staged,
        MutationCommitResult committed)
    {
        HashSet<MutationId> appliedMutationIds = [.. committed.AppliedMutations];
        return appliedMutationIds.Count == staged.MutationSet.Mutations.Count
            && staged.MutationSet.Mutations.All(mutation => appliedMutationIds.Contains(mutation.MutationId));
    }

    private static string CreateAppliedDiff(
        StagedMutationSet staged,
        MutationCommitResult committed)
    {
        HashSet<MutationId> appliedMutationIds = [.. committed.AppliedMutations];
        if (IsEntireStagedSetApplied(staged, committed))
        {
            return staged.Preview.UnifiedDiff;
        }

        return string.Join(
            Environment.NewLine,
            staged.Preview.Changes
                .Where(change => appliedMutationIds.Contains(change.MutationId))
                .Select(change => change.UnifiedDiff));
    }

    private static IReadOnlyList<FileLifecycleChange> CreateAppliedLifecycleChanges(
        StagedMutationSet staged,
        MutationCommitResult committed)
    {
        HashSet<MutationId> appliedMutationIds = [.. committed.AppliedMutations];
        return staged.Preview.LifecycleChanges
            .Where(change => appliedMutationIds.Contains(change.MutationId))
            .ToArray();
    }

    private static ExecutionOutcomeProjection CreateInterimCorrectionProjection(
        ContinueExecutionRequest request,
        ActiveExecution active,
        MutationValidationResult validation,
        string provenance)
    {
        return new ExecutionOutcomeProjection
        {
            Key = new ProjectionKey("executionOutcome", request.RunId.Value.ToString("D")),
            SessionId = request.SessionId,
            RunId = request.RunId,
            Status = ExecutionCheckpointPhase.CorrectionPending,
            UncompletedStepIds = active.Request.ApprovedPlan.Steps
                .Select(step => step.StepId)
                .ToArray(),
            ChangedFiles = active.AppliedFiles.ToArray(),
            LifecycleChanges = active.AppliedLifecycleChanges.ToArray(),
            LifecycleReconciliations = active.AppliedLifecycleReconciliations.ToArray(),
            BehaviorSummary = ["Validation failed; a bounded correction is staged for separate review."],
            Validation = validation,
            ApprovalProvenance = provenance,
            CorrectionAttempts = active.PriorValidations.Count,
            RollbackAvailable = active.AppliedFiles.Count > 0,
            ResidualRisks = validation.Gate.Reasons.ToArray(),
        };
    }

    private static Guid GetStableOperationId(RunId runId, MutationSetId mutationSetId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{runId.Value:D}|{mutationSetId.Value:D}|mutation-commit"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string GetHash(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
    }

    private static string Bound(string value, int maximumCharacters)
    {
        return value.Length <= maximumCharacters ? value : value[..maximumCharacters];
    }

    private sealed record FailedCommitReconciliation(
        bool Succeeded,
        IReadOnlyList<FileLifecycleReconciliation> Results);

    private sealed class RunContinuationGate : IDisposable
    {
        private readonly Lock _sync = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _references;
        private bool _retired;

        public void Dispose()
        {
            _semaphore.Dispose();
        }

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return false;
                }

                _references++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_sync)
            {
                _references--;
                if (_references != 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            return _semaphore.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _semaphore.Release();
        }
    }

    private sealed class RunContinuationGateLease : IDisposable
    {
        private readonly ExecutionOrchestrator _owner;
        private readonly RunContinuationGate _gate;
        private readonly RunId _runId;
        private bool _disposed;

        public RunContinuationGateLease(
            ExecutionOrchestrator owner,
            RunId runId,
            RunContinuationGate gate)
        {
            _owner = owner;
            _runId = runId;
            _gate = gate;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _gate.Release();
            _owner.ReleaseRunContinuationGateReference(_runId, _gate);
        }
    }

    private sealed record ActiveExecution(
        ExecutionStartRequest Request,
        StagedMutationSet Staged,
        BaselineCapture? BaselineCapture,
        MutationCommitResult? Commit,
        IReadOnlyList<MutationValidationResult> PriorValidations,
        IReadOnlyList<string> AppliedFiles,
        IReadOnlyList<StepId> AppliedPlanStepIds,
        IReadOnlyList<string> AppliedDiffs)
    {
        public IReadOnlyList<FileLifecycleChange> AppliedLifecycleChanges { get; init; } = [];

        public IReadOnlyList<FileLifecycleReconciliation> AppliedLifecycleReconciliations { get; init; } = [];

        public MutationValidationResult? Validation { get; init; }
    }
}
