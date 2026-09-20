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
    private readonly IOutputSanitizer _sanitizer;
    private readonly IIncrementalMutationProposalProvider _proposals;
    private readonly MutationBatchingOptions _batching;
    private readonly PlanResourceLimits _planLimits;
    private readonly WorkspaceResourceLimits _workspaceLimits;
    private readonly ConcurrentDictionary<RunId, ActiveExecution> _runs = new();
    private readonly ConcurrentDictionary<RunId, RunContinuationGate> _runContinuationGates = new();
    private readonly ConcurrentDictionary<(RunId RunId, int PlanOrdinal), TaskCompletionSource<ExecutionPlanBoundary>>
        _planCompletions = new();

    private readonly ConcurrentDictionary<RunId, TaskCompletionSource<ExecutionOutcomeProjection>> _terminalOutcomes = new();
    private readonly ICommandHandler<CaptureBaselineBuildCommand, BaselineCapture> _baselineValidation;
    private readonly ICommandHandler<ValidateMutationCommand, MutationValidationResult> _mutationValidation;
    private readonly ITransactionalWorkspaceResolver _workspaces;
    private readonly CorrectiveMessageFactory _correctiveMessages;

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
        IOutputSanitizer sanitizer,
        ILogger<ExecutionOrchestrator> logger,
        CorrectiveMessageFactory correctiveMessages,
        ExecutionLimits? limits = null,
        WorkspaceResourceLimits? workspaceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(baselineValidation);
        ArgumentNullException.ThrowIfNull(mutationValidation);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(correctiveMessages);
        var executionLimits = limits ?? ExecutionLimits.Default;
        executionLimits.Validate();
        _workspaceLimits = workspaceLimits ?? new WorkspaceResourceLimits();
        _workspaceLimits.Validate();
        _batching = executionLimits.MutationBatching;
        _planLimits = executionLimits.Plan;
        _proposals = proposals as IIncrementalMutationProposalProvider
            ?? new LegacyMutationProposalProvider(proposals);
        _commits = commits;
        _baselineValidation = baselineValidation;
        _mutationValidation = mutationValidation;
        _workspaces = workspaces;
        _checkpoints = checkpoints;
        _artifacts = artifacts;
        _events = events;
        _sanitizer = sanitizer;
        _logger = logger;
        _correctiveMessages = correctiveMessages;
    }

    /// <inheritdoc />
    public async Task<ExecutionContinuation> StartAsync(
        ExecutionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        using var gate = await EnterRunContinuationGateAsync(request.RunId, cancellationToken);
        return await StartCoreAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExecutionContinuation> ContinueWithPlanAsync(
        ExecutionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateStartRequest(request);
        if (!request.AllowPlanContinuation)
        {
            throw new InvalidOperationException("The execution request does not allow incremental plan continuation.");
        }

        using var gate = await EnterRunContinuationGateAsync(request.RunId, cancellationToken);
        var checkpoint = await RequireCheckpointAsync(request.RunId, cancellationToken);
        if (checkpoint.SessionId != request.SessionId
            || checkpoint.Phase is not (ExecutionCheckpointPhase.PlanContinuationPending or ExecutionCheckpointPhase.PlanReplanningPending))
        {
            throw new InvalidOperationException(
                "A new plan can start only at a completed-plan or replanning boundary owned by the same session.");
        }

        var active = await ResolveActiveAsync(request.SessionId, request.RunId, cancellationToken);
        var replanning = checkpoint.Phase == ExecutionCheckpointPhase.PlanReplanningPending;
        if ((!replanning && active.LastCompletedPlanOrdinal != active.PlanOrdinal)
            || (replanning && string.IsNullOrWhiteSpace(active.ReplanReason))
            || active.Request.Baseline.WorkspaceId != request.Baseline.WorkspaceId
            || !string.Equals(active.Request.Task.Intent, request.Task.Intent, StringComparison.Ordinal)
            || request.ApprovedPlan.Revision <= active.Request.ApprovedPlan.Revision)
        {
            throw new InvalidOperationException(
                "The next plan must preserve the objective and workspace and advance the plan revision.");
        }

        request = EnsurePlanValidationScope(request);

        var baseline = await _workspaces.PromoteBaselineAsync(
            request.Baseline.WorkspaceId,
            request.ApprovedPlan.Steps.SelectMany(step => step.GetAffectedPaths()).ToArray(),
            cancellationToken);
        var planningBudgetUsed = request.InitialBudgetUsage;
        var cumulativeBudgetUsed = AddBudgetDelta(
            active.BudgetUsed ?? active.Request.InitialBudgetUsage,
            active.PlanningBudgetUsed,
            planningBudgetUsed);
        var preserveDiagnosticBaseline = active.PreserveDiagnosticBaseline
            || (replanning && active.BaselineCapture is not null);
        var diagnosticBaseline = preserveDiagnosticBaseline ? active.Request.Baseline : baseline;
        request = request with
        {
            Baseline = diagnosticBaseline,
            ValidationRequest = MergeValidationScope(
                active.Request.ValidationRequest,
                request.ValidationRequest,
                diagnosticBaseline),
            InitialBudgetUsage = cumulativeBudgetUsed,
        };
        var firstStep = request.ApprovedPlan.Steps[0];
        var nextPlanOrdinal = checked(active.PlanOrdinal + 1);
        var seed = ArchiveCompletedSteps(active) with
        {
            Request = request,
            CurrentStepId = firstStep.StepId,
            PendingStepComplete = null,
            CurrentBatchFullyApplied = false,
            BaselineCapture = preserveDiagnosticBaseline ? active.BaselineCapture : null,
            Staged = null,
            Commit = null,
            AppliedPlanStepIds = [],
            Validation = replanning ? active.Validation : null,
            BudgetUsed = cumulativeBudgetUsed,
            PlanningBudgetUsed = planningBudgetUsed,
            PlanOrdinal = nextPlanOrdinal,
            PendingPlanInitialization = true,
            PreserveDiagnosticBaseline = preserveDiagnosticBaseline,
            ReplanReason = null,
        };
        _runs[request.RunId] = seed;

        var preparing = CreateCheckpoint(
            request,
            GetHash(JsonSerializer.Serialize(request.ApprovedPlan, JsonOptions)),
            GetBaselineIdentity(diagnosticBaseline),
            ExecutionCheckpointPhase.ImplementationPreparing,
            "start next plan implementation model turn",
            nextPlanOrdinal) with
        {
            BatchOrdinal = seed.BatchOrdinal,
            CompletedStepIds = seed.CompletedPlanStepIds,
            MutationBaselineIdentity = GetBaselineIdentity(baseline),
            MutationBaselineGeneration = checkpoint.MutationBaselineGeneration,
            BaselineArtifact = preserveDiagnosticBaseline ? checkpoint.BaselineArtifact : null,
            ValidationArtifact = replanning ? checkpoint.ValidationArtifact : null,
            CorrectionAttempts = replanning ? checkpoint.CorrectionAttempts : 0,
            StateArtifact = await PublishStateAsync(seed, cancellationToken),
        };
        await SaveCheckpointAsync(preparing, cancellationToken);

        try
        {
            return await PrepareInitialPlanProposalAsync(request, preparing, seed, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await SaveCheckpointAsync(
                preparing with
                {
                    Phase = ExecutionCheckpointPhase.Cancelled,
                    NextAction = "explicit resume from the next plan boundary",
                    RecordedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None);
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
                "Incremental plan preparation failed for run {RunId}.",
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
                MutationSet = active.RequiredStaged.MutationSet,
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

        if (checkpoint.Phase is ExecutionCheckpointPhase.Cancelled
                or ExecutionCheckpointPhase.ImplementationPreparing
                or ExecutionCheckpointPhase.ImplementationModelTurn
            && checkpoint.StateArtifact?.Kind == "executionStartRequest")
        {
            var restart = await RestoreStartRequestAsync(checkpoint, cancellationToken);
            ValidateLiveWorkspace(checkpoint, restart.Baseline);
            var restarted = await StartCoreAsync(restart, cancellationToken);
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
        checkpoint = await RequireCheckpointAsync(runId, cancellationToken);
        _runs[runId] = active;
        ExecutionContinuation resumed;
        if (active.PendingPlanInitialization
            && checkpoint.Phase is ExecutionCheckpointPhase.ImplementationPreparing
                or ExecutionCheckpointPhase.ImplementationModelTurn
                or ExecutionCheckpointPhase.Cancelled)
        {
            resumed = await PrepareInitialPlanProposalAsync(active.Request, checkpoint, active, cancellationToken);
        }
        else if (checkpoint.Phase is ExecutionCheckpointPhase.ImplementationModelTurn
            or ExecutionCheckpointPhase.ContinuationPending
            || (checkpoint.Phase == ExecutionCheckpointPhase.Cancelled && checkpoint.MutationSetId is null))
        {
            var validation = active.Validation
                ?? throw new InvalidDataException("The next batch has no prior validation result.");
            var validationArtifact = checkpoint.ValidationArtifact
                ?? throw new InvalidDataException("The next batch has no validation artifact.");
            var next = await PrepareNextProposalAsync(
                active, checkpoint, checkpoint.BaselineArtifact, validationArtifact, cancellationToken);
            if (next is null)
            {
                active = _runs[runId];
                _ = await CompleteAsync(
                    active,
                    checkpoint,
                    validation,
                    validationArtifact,
                    checkpoint.PolicyIdentity ?? "resumed execution authorization",
                    cancellationToken);
            }

            resumed = await RequireCheckpointAsync(runId, cancellationToken);
        }
        else if (checkpoint.Phase == ExecutionCheckpointPhase.BaselineValidation)
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
                        ApprovalId = active.RequiredStaged.ApprovalId,
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
    public async Task<ExecutionStartRequest> GetResumeRequestAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        using var gate = await EnterRunContinuationGateAsync(runId, cancellationToken);
        var checkpoint = await RequireCheckpointAsync(runId, cancellationToken);
        if (checkpoint.SessionId != sessionId)
        {
            throw new UnauthorizedAccessException("The execution does not belong to the requesting session.");
        }

        if (string.Equals(checkpoint.StateArtifact?.Kind, "executionStartRequest", StringComparison.Ordinal))
        {
            return await RestoreStartRequestAsync(checkpoint, cancellationToken);
        }

        var active = await ResolveActiveAsync(sessionId, runId, cancellationToken);
        return active.Request with { InitialBudgetUsage = active.PlanningBudgetUsed };
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
    public async Task<ExecutionPlanBoundary> WaitForPlanCompletionAsync(
        RunId runId,
        int afterPlanOrdinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterPlanOrdinal);
        var checkpoint = await RequireCheckpointAsync(runId, cancellationToken);
        if (checkpoint.Phase is ExecutionCheckpointPhase.PlanContinuationPending or ExecutionCheckpointPhase.PlanReplanningPending
            && checkpoint.PlanOrdinal > afterPlanOrdinal)
        {
            var active = await ResolveActiveAsync(checkpoint.SessionId, runId, cancellationToken);
            return new ExecutionPlanBoundary
            {
                PlanOrdinal = checkpoint.PlanOrdinal,
                PlanUnderRevision = checkpoint.Phase == ExecutionCheckpointPhase.PlanReplanningPending
                    ? active.Request.ApprovedPlan : null,
                Progress = CreateOutcomeProjection(
                    active,
                    active.Validation,
                    active.ApprovalProvenance,
                    checkpoint.DiffArtifact,
                    checkpoint.Phase),
            };
        }

        var nextOrdinal = checked(afterPlanOrdinal + 1);
        var completion = _planCompletions.GetOrAdd(
            (runId, nextOrdinal),
            static _ => new TaskCompletionSource<ExecutionPlanBoundary>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        return await completion.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExecutionOutcomeProjection> CompleteObjectiveAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        using var gate = await EnterRunContinuationGateAsync(runId, cancellationToken);
        var checkpoint = await RequireCheckpointAsync(runId, cancellationToken);
        if (checkpoint.SessionId != sessionId
            || checkpoint.Phase != ExecutionCheckpointPhase.PlanContinuationPending)
        {
            throw new InvalidOperationException(
                "The objective can complete only at a validated plan-continuation boundary owned by the same session.");
        }

        var active = await ResolveActiveAsync(sessionId, runId, cancellationToken);
        if (active.LastCompletedPlanOrdinal != active.PlanOrdinal)
        {
            throw new InvalidDataException("The objective boundary does not contain a completed current plan.");
        }

        var baseline = active.BaselineCapture
            ?? throw new InvalidDataException("The completed objective has no validation baseline.");
        var validated = await ValidateExecutionAsync(
            active,
            baseline,
            active.AccumulatedResidualRisks
                .Concat(active.Request.ApprovedPlan.Risks)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            cancellationToken);
        active = validated.Active;
        var validation = validated.Validation;
        var status = validation.Gate.Status == AcceptanceGateStatus.Passed
            ? ExecutionCheckpointPhase.Completed
            : ExecutionCheckpointPhase.Failed;
        var finalDiff = await PublishFinalDiffAsync(active, cancellationToken);
        var outcome = CreateOutcomeProjection(
            active, validation, active.ApprovalProvenance, finalDiff, status);
        await RecordOutcomeAsync(outcome, cancellationToken);
        await SaveCheckpointAsync(
            checkpoint with
            {
                Phase = status,
                CompletedStepIds = active.CompletedPlanStepIds,
                DiffArtifact = finalDiff,
                ValidationArtifact = validated.Artifact,
                StateArtifact = await PublishStateAsync(active, cancellationToken),
                NextAction = status == ExecutionCheckpointPhase.Completed
                    ? "terminal objective outcome recorded after cumulative validation"
                    : "inspect cumulative validation failure and submit a fresh request",
                RecordedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);
        return outcome;
    }

    /// <inheritdoc />
    public async Task<StagedMutationSet?> HandleAsync(
        GetExecutionMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await _checkpoints.GetCheckpointAsync(
            command.RunId,
            cancellationToken);
        if (checkpoint is null || checkpoint.SessionId != command.SessionId
            || checkpoint.Phase != ExecutionCheckpointPhase.MutationApprovalPending)
        {
            return null;
        }

        var active = await ResolveActiveAsync(
            command.SessionId,
            command.RunId,
            cancellationToken);
        return active.Staged;
    }

    private async Task<ExecutionContinuation> StartCoreAsync(
        ExecutionStartRequest request,
        CancellationToken cancellationToken)
    {
        ValidateStartRequest(request);
        if (await _checkpoints.GetOutcomeAsync(request.RunId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("A terminal execution cannot be started again.");
        }

        request = EnsurePlanValidationScope(request);

        // A new execution may follow an external edit or manual rollback, even
        // after /new. Freeze current approved endpoints before asking for edits;
        // retries and approval keep this generation so later drift still conflicts.
        var baseline = await _workspaces.PromoteBaselineAsync(
            request.Baseline.WorkspaceId,
            request.ApprovedPlan.Steps.SelectMany(step => step.GetAffectedPaths()).ToArray(),
            cancellationToken);
        request = request with
        {
            Baseline = baseline,
            ValidationRequest = request.ValidationRequest with { Baseline = baseline },
        };
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

        try
        {
            return await PrepareInitialPlanProposalAsync(request, preparing, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var cancelled = preparing with
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

        if (request.Approval.ApprovalId != active.RequiredStaged.ApprovalId)
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
                    MutationSet = active.RequiredStaged.MutationSet,
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

        active = await CaptureOriginalFilesAsync(active, cancellationToken);
        var operationId = GetStableOperationId(request.RunId, active.RequiredStaged.MutationSet.MutationSetId);
        var intent = new ExecutionOperationRecord
        {
            OperationId = operationId,
            Kind = "mutation-commit",
            State = ExecutionOperationState.Pending,
            ExpectedPreState = checkpoint.MutationBaselineIdentity,
            ExpectedResult = active.RequiredStaged.MutationSet.MutationSetId.Value.ToString("D"),
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
                    active.RequiredStaged.MutationSet.MutationSetId,
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
        active = active with
        {
            Commit = committed,
            CurrentBatchFullyApplied = IsEntireStagedSetApplied(active.RequiredStaged, committed),
            AppliedFiles = MergePaths(
                active.Request.Baseline.RepositoryPath,
                active.AppliedFiles,
                committed.ChangedFiles),
            AppliedLifecycleChanges = active.AppliedLifecycleChanges
                .Concat(CreateAppliedLifecycleChanges(active.RequiredStaged, committed))
                .ToArray(),
            AppliedLifecycleReconciliations = active.AppliedLifecycleReconciliations
                .Concat(committed.LifecycleReconciliations)
                .ToArray(),
            LifecycleMutationSteps = MergeLifecycleMutationSteps(active, committed),
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
            MutationSetId = active.RequiredStaged.MutationSet.MutationSetId,
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
        var validated = await ValidateExecutionAsync(
            active,
            baseline,
            active.Request.ApprovedPlan.Risks,
            cancellationToken);
        active = validated.Active;
        var validation = validated.Validation;
        var validationArtifact = validated.Artifact;

        var succeeded = validation.Gate.Status == AcceptanceGateStatus.Passed;
        if (succeeded)
        {
            applied = applied with { CorrectionAttempts = 0 };
        }

        if (!active.CurrentBatchFullyApplied)
        {
            await SaveCheckpointAsync(
                applied with
                {
                    Phase = ExecutionCheckpointPhase.ContinuationPending,
                    MutationSetId = null,
                    PendingStepComplete = null,
                    CompletedStepIds = active.AppliedPlanStepIds,
                    ValidationArtifact = validationArtifact,
                    StateArtifact = await PublishStateAsync(active, cancellationToken),
                    NextAction = "explicitly resume remaining approved work after partial mutation authorization",
                    RecordedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken);
            var progress = CreateOutcomeProjection(
                active, validation, provenance, null, ExecutionCheckpointPhase.ContinuationPending);
            return progress with
            {
                BehaviorSummary = [.. progress.BehaviorSummary, "Partial changes applied; explicit continuation is required before proposing remaining work."],
            };
        }

        if (!succeeded && applied.CorrectionAttempts < applied.CorrectionBudget)
        {
            var correctionContext = CreateValidationCorrectionContext(
                validation,
                applied.CorrectionAttempts + 1,
                applied.CorrectionBudget);
            await _events.PublishAsync(
                new ModelCorrectionAttempted(
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    request.RunId,
                    correctionContext.Category,
                    correctionContext.AttemptNumber,
                    correctionContext.MaximumAttempts,
                    correctionContext.SafeReason),
                cancellationToken);
            var correctionScope = CreateExecutionScope(
                active.Request.ApprovedPlan,
                active.AppliedPlanStepIds,
                active.BatchOrdinal + 1,
                MutationBatchPurpose.Correction,
                GetActivatedPaths(active, active.CurrentStepId));
            var correctionProposal = await _proposals.ProposeAsync(
                new ProposeMutationSetCommand(
                    request.SessionId,
                    request.RunId,
                    active.Request.Baseline.WorkspaceId,
                    active.Request.Task,
                    active.Request.ApprovedPlan,
                    RunPhase.CorrectionModelTurn,
                    correctionContext)
                {
                    ExecutionScope = correctionScope,
                    BudgetUsed = active.BudgetUsed,
                    AllowReplanning = active.Request.AllowPlanContinuation,
                },
                cancellationToken);
            if (correctionProposal.ReplanRequested)
            {
                var boundary = await PauseForReplanningAsync(
                    active,
                    applied with
                    {
                        BaselineArtifact = baselineArtifact,
                        ValidationArtifact = validationArtifact,
                        CorrectionAttempts = applied.CorrectionAttempts + 1,
                    },
                    correctionScope,
                    correctionProposal,
                    CancellationToken.None);
                return CreateOutcomeProjection(
                    _runs[request.RunId], validation, provenance, boundary.DiffArtifact, boundary.Phase);
            }

            var correction = correctionProposal.StagedMutationSet
                ?? throw new InvalidOperationException(
                    "A validation correction must contain an exact mutation diff.");
            var correctionDiff = await _artifacts.PublishAsync(
                request.SessionId,
                "executionCorrectionDiff",
                correction.Preview.UnifiedDiff,
                CancellationToken.None);
            active = active with
            {
                Staged = correction,
                BatchOrdinal = correctionScope.BatchOrdinal,
                BudgetUsed = correctionProposal.BudgetUsed ?? active.BudgetUsed,
                CurrentBatchFullyApplied = false,
                Commit = null,
            };
            _runs[request.RunId] = active;
            var correctionState = await PublishStateAsync(active, CancellationToken.None);
            await SaveCheckpointAsync(
                applied with
                {
                    Phase = ExecutionCheckpointPhase.MutationApprovalPending,
                    CurrentPlanStepId = active.CurrentStepId,
                    CurrentPlanStepOrdinal = correctionScope.StepOrdinal,
                    CurrentPlanStepTitle = Bound(correctionScope.ActiveStep.Title, 256),
                    BatchOrdinal = correctionScope.BatchOrdinal,
                    BatchPurpose = MutationBatchPurpose.Correction,
                    PendingStepComplete = active.PendingStepComplete,
                    CompletedStepIds = active.AppliedPlanStepIds,
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
                CancellationToken.None);
            var progress = CreateOutcomeProjection(
                active, validation, provenance, null, ExecutionCheckpointPhase.CorrectionPending);
            return progress with
            {
                BehaviorSummary = [.. progress.BehaviorSummary, "Validation failed; a bounded correction is staged for separate review."],
            };
        }

        if (succeeded)
        {
            if (active.PendingStepComplete == true && active.CurrentBatchFullyApplied)
            {
                active = active with
                {
                    AppliedPlanStepIds = active.AppliedPlanStepIds
                        .Append(active.CurrentStepId)
                        .Distinct()
                        .ToArray(),
                };
            }

            _runs[request.RunId] = active;
            if (active.CurrentBatchFullyApplied
                && active.AppliedPlanStepIds.Count < active.Request.ApprovedPlan.Steps.Count)
            {
                var next = await PrepareNextProposalAsync(
                    active,
                    applied,
                    baselineArtifact,
                    validationArtifact,
                    cancellationToken);
                if (next is not null)
                {
                    var finalDiff = await PublishFinalDiffAsync(next.Value.Active, CancellationToken.None);
                    return CreateOutcomeProjection(
                        next.Value.Active, validation, provenance, finalDiff, next.Value.Continuation.Phase);
                }

                active = _runs[request.RunId];
            }
        }

        return await CompleteAsync(
            active, applied, validation, validationArtifact, provenance, cancellationToken);
    }

    private async Task<ValidationExecutionResult> ValidateExecutionAsync(
        ActiveExecution active,
        BaselineCapture baseline,
        IReadOnlyList<string> residualRisks,
        CancellationToken cancellationToken)
    {
        var committed = active.Commit
            ?? throw new InvalidDataException("Applied execution state has no authoritative commit result.");
        var validation = await _mutationValidation.HandleAsync(
            new ValidateMutationCommand
            {
                Request = active.Request.ValidationRequest,
                BaselineCapture = baseline,
                MutationSet = active.RequiredStaged.MutationSet,
                RequiredApprovalsPresent = committed.AppliedMutations.Count > 0,
                FinalDiffAvailable = active.AppliedFiles.Count > 0,
                ResidualRisks = residualRisks,
            },
            cancellationToken);
        var artifact = await _artifacts.PublishAsync(
            active.Request.SessionId,
            "executionValidation",
            JsonSerializer.Serialize(validation, JsonOptions),
            cancellationToken);
        var validated = active with
        {
            Validation = validation,
            FailedValidationCount = active.FailedValidationCount
                + (validation.Gate.Status == AcceptanceGateStatus.Passed ? 0 : 1),
        };
        _runs[active.Request.RunId] = validated;
        return new ValidationExecutionResult(validated, validation, artifact);
    }

    private async Task<ExecutionOutcomeProjection> CompleteAsync(
        ActiveExecution active,
        ExecutionContinuation applied,
        MutationValidationResult validation,
        ExecutionArtifactReference validationArtifact,
        string provenance,
        CancellationToken cancellationToken)
    {
        var request = active.Request;
        applied = await RequireCheckpointAsync(request.RunId, cancellationToken);
        if (validation.Gate.Status == AcceptanceGateStatus.Passed)
        {
            applied = applied with { CorrectionAttempts = 0 };
        }

        var succeeded = validation.Gate.Status == AcceptanceGateStatus.Passed
            && active.AppliedPlanStepIds.Count == active.Request.ApprovedPlan.Steps.Count;
        active = active with { ApprovalProvenance = provenance };
        if (succeeded && request.AllowPlanContinuation)
        {
            if (active.LastCompletedPlanOrdinal < active.PlanOrdinal)
            {
                active = ArchiveCompletedSteps(active) with { LastCompletedPlanOrdinal = active.PlanOrdinal };
            }

            _runs[request.RunId] = active;
            var boundaryDiff = await PublishFinalDiffAsync(active, cancellationToken);
            var progress = CreateOutcomeProjection(
                active,
                validation,
                provenance,
                boundaryDiff,
                ExecutionCheckpointPhase.PlanContinuationPending);
            var boundaryCheckpoint = applied with
            {
                Phase = ExecutionCheckpointPhase.PlanContinuationPending,
                PlanOrdinal = active.PlanOrdinal,
                CurrentPlanStepId = null,
                CurrentPlanStepOrdinal = null,
                CurrentPlanStepTitle = null,
                PendingStepComplete = null,
                CompletedStepIds = active.CompletedPlanStepIds,
                MutationSetId = null,
                DiffArtifact = boundaryDiff,
                ValidationArtifact = validationArtifact,
                StateArtifact = await PublishStateAsync(active, cancellationToken),
                Operation = null,
                NextAction = "assess the remaining objective and either propose the next plan or complete",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveCheckpointAsync(boundaryCheckpoint, cancellationToken);
            _planCompletions.GetOrAdd(
                (request.RunId, active.PlanOrdinal),
                static _ => new TaskCompletionSource<ExecutionPlanBoundary>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(new ExecutionPlanBoundary
                {
                    PlanOrdinal = active.PlanOrdinal,
                    Progress = progress,
                });
            return progress;
        }

        var status = succeeded
            ? ExecutionCheckpointPhase.Completed
            : ExecutionCheckpointPhase.Failed;
        var finalDiff = await PublishFinalDiffAsync(active, cancellationToken);
        var outcome = CreateOutcomeProjection(active, validation, provenance, finalDiff, status);
        await RecordOutcomeAsync(outcome, cancellationToken);
        await SaveCheckpointAsync(
            applied with
            {
                Phase = status,
                CompletedStepIds = outcome.CompletedStepIds,
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

        var finalDiff = await PublishFinalDiffAsync(active, CancellationToken.None);
        var outcome = CreateOutcomeProjection(
            active, active.Validation, provenance, finalDiff, ExecutionCheckpointPhase.Failed);
        outcome = outcome with
        {
            LifecycleReconciliations = active.AppliedLifecycleReconciliations
                .Concat(currentReconciliations)
                .ToArray(),
            BehaviorSummary = [.. outcome.BehaviorSummary, "Mutation commit failed before authoritative completion."],
            ResidualRisks =
            [
                .. outcome.ResidualRisks,
                "The mutation transaction did not produce an authoritative committed result.",
            ],
        };
        await RecordOutcomeAsync(outcome, CancellationToken.None);
    }

    private async Task RecordOutcomeAsync(ExecutionOutcomeProjection outcome, CancellationToken cancellationToken)
    {
        await _checkpoints.SaveOutcomeAsync(outcome, cancellationToken);
        _terminalOutcomes.GetOrAdd(
            outcome.RunId,
            static _ => new TaskCompletionSource<ExecutionOutcomeProjection>(
                TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(outcome);
        await _events.PublishAsync(
            new ExecutionOutcomeRecorded(
                outcome.SessionId,
                DateTimeOffset.UtcNow,
                outcome.RunId,
                outcome.Status),
            cancellationToken);
    }

    private async Task<ExecutionContinuation> PrepareInitialPlanProposalAsync(
        ExecutionStartRequest request,
        ExecutionContinuation checkpoint,
        ActiveExecution? active,
        CancellationToken cancellationToken)
    {
        var scope = CreateExecutionScope(
            request.ApprovedPlan,
            completedStepIds: [],
            (active?.BatchOrdinal ?? 0) + 1,
            MutationBatchPurpose.Implementation,
            activatedPaths: []);
        var modelTurn = checkpoint with
        {
            Phase = ExecutionCheckpointPhase.ImplementationModelTurn,
            CurrentPlanStepId = scope.ActiveStep.StepId,
            CurrentPlanStepOrdinal = scope.StepOrdinal,
            CurrentPlanStepTitle = Bound(scope.ActiveStep.Title, 256),
            BatchOrdinal = scope.BatchOrdinal,
            BatchPurpose = scope.Purpose,
            CompletedStepIds = active?.CompletedPlanStepIds ?? [],
            NextAction = "admit the first focused propose_mutations call for the approved plan",
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveCheckpointAsync(modelTurn, cancellationToken);
        var proposal = await _proposals.ProposeAsync(
            new ProposeMutationSetCommand(
                request.SessionId,
                request.RunId,
                request.Baseline.WorkspaceId,
                request.Task,
                request.ApprovedPlan,
                RunPhase.ImplementationModelTurn)
            {
                ExecutionScope = scope,
                BudgetUsed = active?.BudgetUsed ?? request.InitialBudgetUsage,
                AllowReplanning = request.AllowPlanContinuation,
            },
            cancellationToken);
        active ??= new ActiveExecution(request, null, scope.ActiveStep.StepId, null, scope.BatchOrdinal, request.InitialBudgetUsage, false, null, null, [], [])
        {
            PlanningBudgetUsed = request.InitialBudgetUsage,
        };
        if (proposal.ReplanRequested)
        {
            return await PauseForReplanningAsync(active, modelTurn, scope, proposal, CancellationToken.None);
        }

        var staged = proposal.StagedMutationSet
            ?? throw new InvalidOperationException(
                "The first approved step cannot complete without current validation evidence.");
        var persisted = await PersistImplementationProposalAsync(
            active,
            modelTurn,
            scope,
            proposal,
            staged,
            preserveValidation: true);
        return persisted.Continuation;
    }

    private async Task<(ActiveExecution Active, ExecutionContinuation Continuation)?> PrepareNextProposalAsync(
        ActiveExecution active,
        ExecutionContinuation checkpoint,
        ExecutionArtifactReference? baselineArtifact,
        ExecutionArtifactReference validationArtifact,
        CancellationToken cancellationToken)
    {
        while (active.AppliedPlanStepIds.Count < active.Request.ApprovedPlan.Steps.Count)
        {
            var scope = CreateExecutionScope(
                active.Request.ApprovedPlan,
                active.AppliedPlanStepIds,
                active.BatchOrdinal + 1,
                MutationBatchPurpose.Implementation,
                GetActivatedPaths(active, SelectCurrentStepId(active))) with
            {
                CanCompleteWithoutChanges = active.CurrentStepId == SelectCurrentStepId(active)
                    && active.CurrentBatchFullyApplied
                    && active.Validation?.Gate.Status == AcceptanceGateStatus.Passed,
            };
            var beforeProposalState = await PublishStateAsync(active, cancellationToken);
            var modelTurn = checkpoint with
            {
                Phase = ExecutionCheckpointPhase.ImplementationModelTurn,
                CurrentPlanStepId = scope.ActiveStep.StepId,
                CurrentPlanStepOrdinal = scope.StepOrdinal,
                CurrentPlanStepTitle = Bound(scope.ActiveStep.Title, 256),
                BatchOrdinal = scope.BatchOrdinal,
                BatchPurpose = scope.Purpose,
                PendingStepComplete = null,
                CompletedStepIds = active.AppliedPlanStepIds,
                MutationSetId = null,
                StateArtifact = beforeProposalState,
                DiffArtifact = null,
                BaselineArtifact = baselineArtifact,
                ValidationArtifact = validationArtifact,
                Operation = null,
                NextAction = "admit the next focused propose_mutations call",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveCheckpointAsync(modelTurn, cancellationToken);
            var proposal = await _proposals.ProposeAsync(
                new ProposeMutationSetCommand(
                    active.Request.SessionId,
                    active.Request.RunId,
                    active.Request.Baseline.WorkspaceId,
                    active.Request.Task,
                    active.Request.ApprovedPlan,
                    RunPhase.ImplementationModelTurn)
                {
                    ExecutionScope = scope,
                    BudgetUsed = active.BudgetUsed,
                    AllowReplanning = active.Request.AllowPlanContinuation,
                },
                cancellationToken);

            if (proposal.ReplanRequested)
            {
                var boundary = await PauseForReplanningAsync(active, modelTurn, scope, proposal, CancellationToken.None);
                return (_runs[active.Request.RunId], boundary);
            }

            if (proposal.IsCompletionOnly)
            {
                if (!scope.CanCompleteWithoutChanges)
                {
                    throw new InvalidOperationException(
                        "The completion-only proposal has no current passing validation evidence for the selected step.");
                }

                active = active with
                {
                    AppliedPlanStepIds = active.AppliedPlanStepIds
                        .Append(scope.ActiveStep.StepId)
                        .Distinct()
                        .ToArray(),
                    PendingStepComplete = true,
                    BatchOrdinal = scope.BatchOrdinal,
                    BudgetUsed = proposal.BudgetUsed ?? active.BudgetUsed,
                };
                _runs[active.Request.RunId] = active;
                checkpoint = modelTurn with
                {
                    CompletedStepIds = active.AppliedPlanStepIds,
                    StateArtifact = await PublishStateAsync(active, CancellationToken.None),
                    NextAction = active.AppliedPlanStepIds.Count == active.Request.ApprovedPlan.Steps.Count
                        ? "assemble terminal execution outcome"
                        : "select the next incomplete approved step",
                    RecordedAt = DateTimeOffset.UtcNow,
                };
                await SaveCheckpointAsync(checkpoint, CancellationToken.None);
                continue;
            }

            var staged = proposal.StagedMutationSet
                ?? throw new InvalidDataException("The proposal result contains neither changes nor completion.");
            return await PersistImplementationProposalAsync(
                active,
                modelTurn,
                scope,
                proposal,
                staged,
                preserveValidation: false);
        }

        return null;
    }

    private async Task<(ActiveExecution Active, ExecutionContinuation Continuation)> PersistImplementationProposalAsync(
        ActiveExecution active,
        ExecutionContinuation modelTurn,
        MutationExecutionScope scope,
        MutationProposalResult proposal,
        StagedMutationSet staged,
        bool preserveValidation)
    {
        // Once admitted, persist the exact candidate and consumed budget before honoring cancellation.
        var diff = await _artifacts.PublishAsync(
            active.Request.SessionId,
            "executionDiff",
            staged.Preview.UnifiedDiff,
            CancellationToken.None);
        active = active with
        {
            Staged = staged,
            CurrentStepId = scope.ActiveStep.StepId,
            PendingStepComplete = proposal.StepComplete,
            BatchOrdinal = scope.BatchOrdinal,
            BudgetUsed = proposal.BudgetUsed ?? active.BudgetUsed,
            PendingPlanInitialization = false,
            CurrentBatchFullyApplied = false,
            Commit = null,
            Validation = preserveValidation ? active.Validation : null,
        };
        _runs[active.Request.RunId] = active;
        var pending = modelTurn with
        {
            Phase = ExecutionCheckpointPhase.MutationApprovalPending,
            PendingStepComplete = proposal.StepComplete,
            MutationSetId = staged.MutationSet.MutationSetId,
            StateArtifact = await PublishStateAsync(active, CancellationToken.None),
            DiffArtifact = diff,
            Operation = null,
            NextAction = "review active-step batch exact diff and obtain separate mutation authorization",
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveCheckpointAsync(pending, CancellationToken.None);
        return (active, pending);
    }

    private async Task<ExecutionContinuation> PauseForReplanningAsync(
        ActiveExecution active,
        ExecutionContinuation checkpoint,
        MutationExecutionScope scope,
        MutationProposalResult proposal,
        CancellationToken cancellationToken)
    {
        if (!active.Request.AllowPlanContinuation || proposal.StagedMutationSet is not null
            || proposal.StepComplete is not null
            || checkpoint.Operation?.State is ExecutionOperationState.Pending or ExecutionOperationState.RecoveryRequired)
        {
            throw new InvalidOperationException("Replanning requires a settled execution and an exclusive replan decision.");
        }

        var reason = Bound(_sanitizer.Sanitize(proposal.Rationale).Trim(), _planLimits.MaximumSummaryCharacters);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        active = active with
        {
            ReplanReason = reason,
            CurrentStepId = scope.ActiveStep.StepId,
            BatchOrdinal = scope.BatchOrdinal,
            PendingStepComplete = null,
            PendingPlanInitialization = false,
            BudgetUsed = proposal.BudgetUsed ?? active.BudgetUsed,
        };
        _runs[active.Request.RunId] = active;
        var diff = await PublishFinalDiffAsync(active, cancellationToken);
        var boundary = checkpoint with
        {
            Phase = ExecutionCheckpointPhase.PlanReplanningPending,
            CurrentPlanStepId = scope.ActiveStep.StepId,
            CurrentPlanStepOrdinal = scope.StepOrdinal,
            CurrentPlanStepTitle = Bound(scope.ActiveStep.Title, _planLimits.MaximumTitleCharacters),
            BatchOrdinal = scope.BatchOrdinal,
            BatchPurpose = scope.Purpose,
            PendingStepComplete = null,
            CompletedStepIds = active.CompletedPlanStepIds.Concat(active.AppliedPlanStepIds).Distinct().ToArray(),
            MutationSetId = null,
            Operation = null,
            DiffArtifact = diff,
            StateArtifact = await PublishStateAsync(active, cancellationToken),
            NextAction = "investigate requested replanning and approve a replacement for unfinished work",
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveCheckpointAsync(boundary, cancellationToken);
        _planCompletions.GetOrAdd(
            (active.Request.RunId, active.PlanOrdinal),
            static _ => new TaskCompletionSource<ExecutionPlanBoundary>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(new ExecutionPlanBoundary
            {
                PlanOrdinal = active.PlanOrdinal,
                PlanUnderRevision = active.Request.ApprovedPlan,
                Progress = CreateOutcomeProjection(active, active.Validation, active.ApprovalProvenance, diff, boundary.Phase),
            });
        return boundary;
    }

    private static ActiveExecution ArchiveCompletedSteps(ActiveExecution active)
    {
        var completed = active.AppliedPlanStepIds.ToHashSet();
        return active with
        {
            CompletedPlanStepIds = active.CompletedPlanStepIds.Concat(completed).Distinct().ToArray(),
            CompletedBehaviorSummary = active.CompletedBehaviorSummary.Concat(active.Request.ApprovedPlan.Steps
                .Where(step => completed.Contains(step.StepId))
                .Select(step => step.ExpectedOutcome)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Bound(value, 512))).Distinct(StringComparer.Ordinal).ToArray(),
            AccumulatedResidualRisks = active.AccumulatedResidualRisks.Concat(active.Request.ApprovedPlan.Risks)
                .Distinct(StringComparer.Ordinal).ToArray(),
            LifecycleMutationSteps = [],
        };
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
        if (checkpoint.SessionId != sessionId)
        {
            throw new UnauthorizedAccessException("The execution does not belong to the requesting session.");
        }

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
        if (active.Request is null
            || active.Request.ValidationRequest is null
            || active.Request.ApprovedPlan?.Steps is null
            || active.AppliedFiles is null)
        {
            throw new InvalidDataException("The continuation-state artifact is incomplete.");
        }

        var legacyFailedValidations = active.LegacyPriorValidations?.OfType<MutationValidationResult>().Count(item =>
            item.Gate.Status != AcceptanceGateStatus.Passed) ?? 0;
        var scopedRequest = EnsurePlanValidationScope(active.Request);
        scopedRequest = scopedRequest with
        {
            ValidationRequest = scopedRequest.ValidationRequest with
            {
                AffectedPaths = MergePaths(
                    scopedRequest.Baseline.RepositoryPath,
                    scopedRequest.ValidationRequest.AffectedPaths,
                    active.AppliedFiles),
            },
        };
        active = active with
        {
            Request = scopedRequest,
            FailedValidationCount = Math.Max(active.FailedValidationCount, legacyFailedValidations),
            LegacyPriorValidations = null,
        };
        if (checkpoint.SchemaVersion == 1)
        {
            var inferredStepId = active.CurrentStepId != default
                ? active.CurrentStepId
                : checkpoint.CurrentPlanStepId
                    ?? ResolveCurrentStep(active.Request, active.RequiredStaged.MutationSet)
                    ?? active.Request.ApprovedPlan.Steps[0].StepId;
            var currentStepHasPassingValidation = active.Validation?.Gate.Status == AcceptanceGateStatus.Passed;
            active = active with
            {
                CurrentStepId = inferredStepId,
                PendingStepComplete = active.PendingStepComplete ?? active.RequiredStaged.StepComplete ?? true,
                BatchOrdinal = Math.Max(1, active.BatchOrdinal),
                BudgetUsed = active.BudgetUsed ?? active.Request.InitialBudgetUsage,
                CurrentBatchFullyApplied = active.Commit is not null
                    && IsEntireStagedSetApplied(active.RequiredStaged, active.Commit),
                AppliedPlanStepIds = currentStepHasPassingValidation
                    ? active.AppliedPlanStepIds
                    : active.AppliedPlanStepIds.Where(stepId => stepId != inferredStepId).ToArray(),
            };
        }

        ValidateActiveExecutionShape(active);
        if (checkpoint.SchemaVersion == 1)
        {
            ValidateResumeState(checkpoint, active);
            await SaveCheckpointAsync(
                checkpoint with
                {
                    SchemaVersion = 2,
                    CurrentPlanStepId = active.CurrentStepId,
                    BatchOrdinal = active.BatchOrdinal,
                    PendingStepComplete = active.PendingStepComplete,
                    CompletedStepIds = active.AppliedPlanStepIds,
                    StateArtifact = await PublishStateAsync(active, cancellationToken),
                },
                cancellationToken);
        }

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

    private async Task<ActiveExecution> CaptureOriginalFilesAsync(
        ActiveExecution active,
        CancellationToken cancellationToken)
    {
        var workspace = _workspaces.GetWorkspace(active.Request.Baseline.WorkspaceId);
        var pathComparer = RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath);
        var originals = new Dictionary<string, ExecutionArtifactReference?>(
            active.OriginalFiles, pathComparer);
        var previouslyApplied = active.AppliedFiles.ToHashSet(pathComparer);
        foreach (var mutation in active.RequiredStaged.MutationSet.Mutations)
        {
            foreach (var path in new[] { mutation.RelativePath, mutation.DestinationRelativePath })
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var normalized = path.Replace('\\', '/');
                if (originals.ContainsKey(normalized) || previouslyApplied.Contains(normalized))
                {
                    continue;
                }

                var content = await workspace.ReadBaselineTextAsync(normalized, cancellationToken);
                originals[normalized] = content is null ? null : await _artifacts.PublishAsync(
                    active.Request.SessionId, "executionOriginalFile", content, cancellationToken);
            }
        }

        return active with { OriginalFiles = originals };
    }

    private async Task<ExecutionArtifactReference?> PublishFinalDiffAsync(
        ActiveExecution active,
        CancellationToken cancellationToken)
    {
        if (active.AppliedFiles.Count == 0)
        {
            return null;
        }

        var workspace = _workspaces.GetWorkspace(active.Request.Baseline.WorkspaceId);
        var originals = new Dictionary<string, ExecutionArtifactReference?>(
            active.OriginalFiles,
            RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath));
        var diff = new StringBuilder();
        foreach (var path in active.AppliedFiles)
        {
            var normalized = path.Replace('\\', '/');
            if (!originals.TryGetValue(normalized, out var reference))
            {
                // Historical runs may lack original bytes; never label their patch history a net diff.
                return null;
            }

            var before = reference is null ? null : await _artifacts.ReadAsync(reference, cancellationToken)
                ?? throw new InvalidDataException("The original execution file artifact is missing or corrupt.");
            var after = await workspace.ReadBaselineTextAsync(normalized, cancellationToken);
            diff.Append(UnifiedTextDiff.Create(
                normalized, before, after, _workspaceLimits.MaximumDiffLinesForLcs, out _, out _));
        }

        return await _artifacts.PublishAsync(
            active.Request.SessionId, "executionFinalDiff", diff.ToString(), cancellationToken);
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
        await _checkpoints.SaveCheckpointAsync(checkpoint with { SchemaVersion = 2 }, cancellationToken);
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
                active.RequiredStaged.MutationSet.MutationSetId,
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
        var everyMutationReconciled = active.RequiredStaged.MutationSet.Mutations.All(mutation =>
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
        string nextAction,
        int planOrdinal = 1)
    {
        return new ExecutionContinuation
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = request.Baseline.WorkspaceId,
            PlanRevision = request.ApprovedPlan.Revision,
            PlanOrdinal = planOrdinal,
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
            || request.CorrectionBudget < 0
            || request.InitialBudgetUsage.Tokens < 0
            || request.InitialBudgetUsage.Calls < 0
            || request.InitialBudgetUsage.WallClock < TimeSpan.Zero
            || request.InitialBudgetUsage.Cost < 0)
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

    private static ExecutionStartRequest EnsurePlanValidationScope(ExecutionStartRequest request)
    {
        var affectedPaths = MergePaths(
            request.Baseline.RepositoryPath,
            request.ValidationRequest.AffectedPaths,
            request.ApprovedPlan.Steps.SelectMany(step => step.GetAffectedPaths()));
        return request with
        {
            ValidationRequest = request.ValidationRequest with { AffectedPaths = affectedPaths },
        };
    }

    private static BuildValidationRequest MergeValidationScope(
        BuildValidationRequest previous,
        BuildValidationRequest current,
        WorkspaceBaseline baseline)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var projects = previous.Projects
            .Concat(current.Projects)
            .GroupBy(project => project.FilePath, comparer)
            .Select(group =>
            {
                var items = group.ToArray();
                var latest = items[^1];
                return latest with
                {
                    TargetFrameworks = items
                        .SelectMany(project => project.TargetFrameworks)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    Confidence = (SemanticConfidenceLevel)items.Min(project => (int)project.Confidence),
                    IsDirectlyChanged = items.Any(project => project.IsDirectlyChanged),
                };
            })
            .ToArray();
        var projectInventory = current.ProjectInventory
            .Concat(previous.ProjectInventory)
            .DistinctBy(project => project.FilePath, comparer)
            .ToArray();
        return current with
        {
            Baseline = baseline,
            Projects = projects,
            AffectedPaths = MergePaths(
                baseline.RepositoryPath,
                previous.AffectedPaths,
                current.AffectedPaths),
            Confidence = (SemanticConfidenceLevel)Math.Min(
                (int)previous.Confidence,
                (int)current.Confidence),
            ProjectInventory = projectInventory,
            Stages = previous.Stages.Concat(current.Stages).Distinct().ToArray(),
        };
    }

    private static IReadOnlyList<string> MergePaths(
        string repositoryPath,
        params IEnumerable<string>[] sources)
    {
        return sources
            .SelectMany(source => source)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/'))
            .Distinct(RepositoryPathPolicy.GetPathComparer(repositoryPath))
            .ToArray();
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
            || active.PlanOrdinal != checkpoint.PlanOrdinal
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
            || (active.Staged is null && (string.IsNullOrWhiteSpace(active.ReplanReason) && !active.PendingPlanInitialization))
            || (active.Staged is not null && (active.Staged.MutationSet is null
                || active.Staged.Preview is null || active.Staged.Conflicts is null))
            || active.CurrentStepId == default
            || active.PlanOrdinal < 1
            || active.LastCompletedPlanOrdinal < 0
            || active.LastCompletedPlanOrdinal > active.PlanOrdinal
            || active.BatchOrdinal < 1
            || active.AppliedFiles is null
            || active.AppliedPlanStepIds is null
            || active.AppliedLifecycleChanges is null
            || active.AppliedLifecycleReconciliations is null
            || active.LifecycleMutationSteps is null
            || active.LifecycleMutationSteps.Any(item => item.StepId == default || item.MutationId == default)
            || active.CompletedPlanStepIds is null
            || active.CompletedBehaviorSummary is null
            || active.AccumulatedResidualRisks is null
            || active.FailedValidationCount < 0
            || active.PlanningBudgetUsed is null
            || active.PlanningBudgetUsed.Tokens < 0
            || active.PlanningBudgetUsed.Calls < 0
            || active.PlanningBudgetUsed.WallClock < TimeSpan.Zero
            || active.PlanningBudgetUsed.Cost < 0)
        {
            throw new InvalidDataException("The continuation-state artifact does not contain active execution state.");
        }
    }

    private static StepId? ResolveCurrentStep(
        ExecutionStartRequest request,
        MutationSet mutationSet)
    {
        var comparer = RepositoryPathPolicy.GetPathComparer(request.Baseline.RepositoryPath);
        var files = mutationSet.Mutations
            .Select(mutation => mutation.RelativePath.Replace('\\', '/'))
            .ToHashSet(comparer);
        return request.ApprovedPlan.Steps.FirstOrDefault(step => step.GetAffectedPaths()
            .Select(path => path.Replace('\\', '/'))
            .Any(files.Contains))?.StepId;
    }

    private MutationExecutionScope CreateExecutionScope(
        ImplementationPlan plan,
        IReadOnlyList<StepId> completedStepIds,
        int batchOrdinal,
        MutationBatchPurpose purpose,
        IReadOnlyList<string> activatedPaths)
    {
        var completed = completedStepIds.ToHashSet();
        var stepIndex = plan.Steps
            .Select((step, index) => (Step: step, Index: index))
            .FirstOrDefault(item => !completed.Contains(item.Step.StepId));
        if (stepIndex.Step is null)
        {
            throw new InvalidOperationException("The approved plan has no incomplete step to propose.");
        }

        return new MutationExecutionScope
        {
            ActiveStep = stepIndex.Step,
            StepOrdinal = stepIndex.Index + 1,
            StepCount = plan.Steps.Count,
            BatchOrdinal = batchOrdinal,
            Purpose = purpose,
            CompletedStepIds = completedStepIds.ToArray(),
            ActivatedPaths = activatedPaths.ToArray(),
            TargetMutations = Math.Min(_batching.TargetMutations, _workspaceLimits.MaximumMutations),
            TargetFiles = _batching.TargetFiles,
            TargetMutationCharacters = Math.Min(
                _batching.TargetMutationCharacters,
                _workspaceLimits.MaximumMutationCharacters),
        };
    }

    private static StepId SelectCurrentStepId(ActiveExecution active)
    {
        var completed = active.AppliedPlanStepIds.ToHashSet();
        return active.Request.ApprovedPlan.Steps
            .First(step => !completed.Contains(step.StepId))
            .StepId;
    }

    private IReadOnlyList<string> GetActivatedPaths(
        ActiveExecution active,
        StepId stepId)
    {
        var workspace = _workspaces.GetWorkspace(active.Request.Baseline.WorkspaceId);
        var mutationIds = active.LifecycleMutationSteps
            .Where(item => item.StepId == stepId)
            .Select(item => item.MutationId)
            .ToHashSet();
        return active.AppliedLifecycleReconciliations
            .Where(item => item.State == FileLifecycleReconciliationState.Applied
                && mutationIds.Contains(item.MutationId))
            .SelectMany(item => new[] { item.SourcePath, item.DestinationPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath))
            .ToArray();
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

    private MutationCorrectionContext CreateValidationCorrectionContext(
        MutationValidationResult validation,
        int attemptNumber,
        int maximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumAttempts);
        return new MutationCorrectionContext(
            ModelCorrectionCategory.PostApplyValidation,
            attemptNumber,
            maximumAttempts,
            CreateValidationCorrectionReason(validation));
    }

    private string CreateValidationCorrectionReason(MutationValidationResult validation)
    {
        var diagnostic = validation.Diagnostics.FirstOrDefault(item =>
            item.Severity == DiagnosticSeverity.Error && !item.IsBaselineDiagnostic);
        if (diagnostic is not null)
        {
            return SanitizeAndBoundCorrectionReason(
                RequireCorrectiveMessages().CreateCompilerValidationReason(
                    diagnostic.Code,
                    diagnostic.File ?? diagnostic.Project,
                    diagnostic.Message));
        }

        var failedTest = validation.Tests.Results.FirstOrDefault(item =>
            item.Outcome == TestOutcome.Failed);
        if (failedTest is not null)
        {
            return SanitizeAndBoundCorrectionReason(
                RequireCorrectiveMessages().CreateTestValidationReason(
                    failedTest.Project.Name,
                    failedTest.Failed));
        }

        return SanitizeAndBoundCorrectionReason(
            RequireCorrectiveMessages().CreateGeneralValidationReason(
                string.Join("; ", validation.Gate.Reasons.Take(3))));
    }

    private CorrectiveMessageFactory RequireCorrectiveMessages()
    {
        return _correctiveMessages;
    }

    private string SanitizeAndBoundCorrectionReason(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var sanitized = _sanitizer.Sanitize(value);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "Validation gate requires correction."
            : BoundCorrectionReason(sanitized);
    }

    private static string BoundCorrectionReason(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.ReplaceLineEndings(" ");
        var builder = new StringBuilder(Math.Min(normalized.Length, 512));
        foreach (var character in normalized)
        {
            if (builder.Length == 512)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
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

    private static IReadOnlyList<FileLifecycleChange> CreateAppliedLifecycleChanges(
        StagedMutationSet staged,
        MutationCommitResult committed)
    {
        HashSet<MutationId> appliedMutationIds = [.. committed.AppliedMutations];
        return staged.Preview.LifecycleChanges
            .Where(change => appliedMutationIds.Contains(change.MutationId))
            .ToArray();
    }

    private static IReadOnlyList<LifecycleMutationStep> MergeLifecycleMutationSteps(
        ActiveExecution active,
        MutationCommitResult committed)
    {
        var result = active.LifecycleMutationSteps.ToList();
        var recordedMutationIds = result.Select(item => item.MutationId).ToHashSet();
        var appliedMutationIds = committed.AppliedMutations.ToHashSet();
        foreach (var reconciliation in committed.LifecycleReconciliations.Where(item =>
            item.State == FileLifecycleReconciliationState.Applied
            && appliedMutationIds.Contains(item.MutationId)))
        {
            if (recordedMutationIds.Add(reconciliation.MutationId))
            {
                result.Add(new LifecycleMutationStep(reconciliation.MutationId, active.CurrentStepId));
            }
        }

        return result;
    }

    private static ExecutionOutcomeProjection CreateOutcomeProjection(
        ActiveExecution active,
        MutationValidationResult? validation,
        string provenance,
        ExecutionArtifactReference? finalDiff,
        ExecutionCheckpointPhase status)
    {
        var failed = status is ExecutionCheckpointPhase.Failed or ExecutionCheckpointPhase.CorrectionPending;
        var currentCompleted = active.AppliedPlanStepIds.ToHashSet();
        var completed = active.CompletedPlanStepIds.Concat(currentCompleted).Distinct().ToArray();
        return new ExecutionOutcomeProjection
        {
            Key = new ProjectionKey(
                status is ExecutionCheckpointPhase.Completed or ExecutionCheckpointPhase.Failed or ExecutionCheckpointPhase.CorrectionPending
                    ? "executionOutcome" : "executionProgress",
                active.Request.RunId.Value.ToString("D")),
            SessionId = active.Request.SessionId,
            RunId = active.Request.RunId,
            Status = status,
            ReplanReason = active.ReplanReason,
            CompletedStepIds = completed,
            UncompletedStepIds = active.Request.ApprovedPlan.Steps.Select(step => step.StepId).Except(completed).ToArray(),
            ChangedFiles = active.AppliedFiles.ToArray(),
            LifecycleChanges = active.AppliedLifecycleChanges.ToArray(),
            LifecycleReconciliations = active.AppliedLifecycleReconciliations.ToArray(),
            BehaviorSummary = active.CompletedBehaviorSummary.Concat(active.Request.ApprovedPlan.Steps
                .Where(step => currentCompleted.Contains(step.StepId))
                .Select(step => step.ExpectedOutcome)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Bound(value, 512))).Distinct(StringComparer.Ordinal).ToArray(),
            FinalDiff = finalDiff,
            Validation = validation,
            ApprovalProvenance = provenance,
            CorrectionAttempts = active.FailedValidationCount,
            RollbackAvailable = active.AppliedFiles.Count > 0,
            ResidualRisks = active.AccumulatedResidualRisks.Concat(active.Request.ApprovedPlan.Risks)
                .Concat(failed ? validation?.Gate.Reasons ?? [] : [])
                .Distinct(StringComparer.Ordinal).ToArray(),
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

    private static BudgetDimensions AddBudgetDelta(
        BudgetDimensions cumulative,
        BudgetDimensions priorPlanning,
        BudgetDimensions currentPlanning)
    {
        var tokenDelta = Math.Max(0, currentPlanning.Tokens - priorPlanning.Tokens);
        var callDelta = Math.Max(0, currentPlanning.Calls - priorPlanning.Calls);
        var wallClockDelta = currentPlanning.WallClock > priorPlanning.WallClock
            ? currentPlanning.WallClock - priorPlanning.WallClock
            : TimeSpan.Zero;
        var costDelta = Math.Max(0, currentPlanning.Cost - priorPlanning.Cost);
        return new BudgetDimensions(
            checked(cumulative.Tokens + tokenDelta),
            checked(cumulative.Calls + callDelta),
            cumulative.WallClock + wallClockDelta,
            cumulative.Cost + costDelta);
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

    private sealed record ValidationExecutionResult(
        ActiveExecution Active,
        MutationValidationResult Validation,
        ExecutionArtifactReference Artifact);

    private sealed record LifecycleMutationStep(
        MutationId MutationId,
        StepId StepId);

    private sealed record ActiveExecution(
        ExecutionStartRequest Request,
        StagedMutationSet? Staged,
        StepId CurrentStepId,
        bool? PendingStepComplete,
        int BatchOrdinal,
        BudgetDimensions? BudgetUsed,
        bool CurrentBatchFullyApplied,
        BaselineCapture? BaselineCapture,
        MutationCommitResult? Commit,
        IReadOnlyList<string> AppliedFiles,
        IReadOnlyList<StepId> AppliedPlanStepIds)
    {
        [JsonIgnore]
        public StagedMutationSet RequiredStaged => Staged
            ?? throw new InvalidDataException("This execution phase requires a staged mutation.");

        public string? ReplanReason { get; init; }

        public bool PreserveDiagnosticBaseline { get; init; }

        public IReadOnlyDictionary<string, ExecutionArtifactReference?> OriginalFiles { get; init; }
            = new Dictionary<string, ExecutionArtifactReference?>();

        public IReadOnlyList<FileLifecycleChange> AppliedLifecycleChanges { get; init; } = [];

        public IReadOnlyList<FileLifecycleReconciliation> AppliedLifecycleReconciliations { get; init; } = [];

        public IReadOnlyList<LifecycleMutationStep> LifecycleMutationSteps { get; init; } = [];

        public MutationValidationResult? Validation { get; init; }

        public int FailedValidationCount { get; init; }

        [JsonPropertyName("PriorValidations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<MutationValidationResult>? LegacyPriorValidations { get; init; }

        public int PlanOrdinal { get; init; } = 1;

        public int LastCompletedPlanOrdinal { get; init; }

        public bool PendingPlanInitialization { get; init; }

        public IReadOnlyList<StepId> CompletedPlanStepIds { get; init; } = [];

        public IReadOnlyList<string> CompletedBehaviorSummary { get; init; } = [];

        public IReadOnlyList<string> AccumulatedResidualRisks { get; init; } = [];

        public string ApprovalProvenance { get; init; } = "approved execution";

        public BudgetDimensions PlanningBudgetUsed { get; init; } = new(0, 0, TimeSpan.Zero);
    }

    private sealed class LegacyMutationProposalProvider : IIncrementalMutationProposalProvider
    {
        private readonly ICommandHandler<ProposeMutationSetCommand, StagedMutationSet> _handler;

        internal LegacyMutationProposalProvider(
            ICommandHandler<ProposeMutationSetCommand, StagedMutationSet> handler)
        {
            _handler = handler;
        }

        public async Task<MutationProposalResult> ProposeAsync(
            ProposeMutationSetCommand command,
            CancellationToken cancellationToken = default)
        {
            var staged = await _handler.HandleAsync(command, cancellationToken);
            return new MutationProposalResult
            {
                StagedMutationSet = staged,
                StepComplete = staged.StepComplete ?? true,
                Rationale = staged.MutationSet.Rationale,
                BudgetUsed = command.BudgetUsed,
            };
        }
    }
}
