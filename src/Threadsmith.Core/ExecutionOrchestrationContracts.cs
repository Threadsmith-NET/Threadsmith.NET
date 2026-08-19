namespace Threadsmith.Core;

/// <summary>Durable execution-orchestration checkpoint phases appended without renumbering legacy run phases.</summary>
public enum ExecutionCheckpointPhase
{
    /// <summary>The approved plan is ready for implementation.</summary>
    PlanApproved,

    /// <summary>Implementation context and repository state are being prepared.</summary>
    ImplementationPreparing,

    /// <summary>The implementation model turn is active.</summary>
    ImplementationModelTurn,

    /// <summary>A mutation proposal has been recorded.</summary>
    MutationProposed,

    /// <summary>The proposal is staged and its exact diff is durable.</summary>
    MutationStaged,

    /// <summary>A mutation authorization decision is required.</summary>
    MutationApprovalPending,

    /// <summary>The immutable diagnostic baseline is being captured.</summary>
    BaselineValidation,

    /// <summary>A mutation side effect has durable pending intent.</summary>
    MutationApplyPending,

    /// <summary>The mutation was reconciled as applied.</summary>
    MutationApplied,

    /// <summary>Affected-project build validation is active.</summary>
    BuildValidation,

    /// <summary>Selected-test validation is active.</summary>
    TestValidation,

    /// <summary>A bounded correction is required.</summary>
    CorrectionPending,

    /// <summary>A bounded correction model turn is active.</summary>
    CorrectionModelTurn,

    /// <summary>The authoritative outcome is being assembled.</summary>
    CompletionPending,

    /// <summary>The run completed successfully.</summary>
    Completed,

    /// <summary>The run failed.</summary>
    Failed,

    /// <summary>The run was cancelled.</summary>
    Cancelled,

    /// <summary>The applied mutation was rolled back.</summary>
    RolledBack,
}

/// <summary>Durable state of one idempotent side-effect operation.</summary>
public enum ExecutionOperationState
{
    /// <summary>The operation intent is durable but its effect is not yet authoritative.</summary>
    Pending,

    /// <summary>The effect was reconciled and recorded.</summary>
    Completed,

    /// <summary>The effect was safely undone.</summary>
    RolledBack,

    /// <summary>The actual effect could not be proven and execution failed closed.</summary>
    RecoveryRequired,
}

/// <summary>Stable reference to bounded content-addressed execution evidence.</summary>
public sealed record ExecutionArtifactReference(
    string ContentHash,
    string Kind,
    long Length);

/// <summary>One write-ahead side-effect intent and its reconciliation state.</summary>
public sealed record ExecutionOperationRecord
{
    /// <summary>Stable idempotency identity.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>Sanitized operation kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Current write-ahead state.</summary>
    public required ExecutionOperationState State { get; init; }

    /// <summary>Hash or identity of the expected pre-state.</summary>
    public required string ExpectedPreState { get; init; }

    /// <summary>Expected result identity when known.</summary>
    public string? ExpectedResult { get; init; }

    /// <summary>Sanitized reconciliation decision.</summary>
    public string? Reconciliation { get; init; }
}

/// <summary>Versioned plan-step-correlated model mutation proposal.</summary>
public sealed record MutationProposalEnvelope
{
    /// <summary>Current supported schema.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Owning plan revision.</summary>
    public required int PlanRevision { get; init; }

    /// <summary>Approved plan steps addressed by the proposal.</summary>
    public required IReadOnlyList<StepId> PlanStepIds { get; init; }

    /// <summary>Model-authored changes from which the host creates an identity-bound mutation set.</summary>
    public required MutationProposalSet MutationSet { get; init; }

    /// <summary>Expected host-observable outcomes.</summary>
    public IReadOnlyList<string>? ExpectedOutcomes { get; init; } = [];

    /// <summary>Expected validation checks.</summary>
    public IReadOnlyList<string>? ValidationExpectations { get; init; } = [];
}

/// <summary>Model-authored mutation-set content without host-owned execution identities.</summary>
public sealed record MutationProposalSet
{
    /// <summary>Ordered proposed changes.</summary>
    public required IReadOnlyList<MutationProposalChange> Mutations { get; init; }

    /// <summary>Why the changes implement the approved plan.</summary>
    public required string Rationale { get; init; }

    /// <summary>Projects expected to be affected.</summary>
    public IReadOnlyList<string>? AffectedProjects { get; init; } = [];

    /// <summary>Diagnostics expected to be resolved.</summary>
    public IReadOnlyList<string>? ExpectedDiagnosticsResolved { get; init; } = [];

    /// <summary>Tests expected to validate the changes.</summary>
    public IReadOnlyList<string>? ExpectedTests { get; init; } = [];

    /// <summary>Model-supplied risk classification subject to host recomputation.</summary>
    public MutationRisk? Risk { get; init; } = MutationRisk.Medium;

    /// <summary>Requested validation policy subject to host validation.</summary>
    public string? ValidationPolicy { get; init; } = "default";
}

/// <summary>One model-authored change without a host-owned mutation identity.</summary>
public sealed record MutationProposalChange
{
    /// <summary>Typed mutation operation.</summary>
    public required MutationType Type { get; init; }

    /// <summary>Slash-normalized repository-relative source path.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Expected baseline hash, or <see langword="null"/> for creation.</summary>
    public string? BaselineSha256 { get; init; }

    /// <summary>Exact source identity for delete and move operations.</summary>
    public ExpectedFileIdentity? ExpectedIdentity { get; init; }

    /// <summary>Slash-normalized destination for a move operation.</summary>
    public string? DestinationRelativePath { get; init; }

    /// <summary>Required destination state for create and move operations.</summary>
    public DestinationExpectation? DestinationExpectation { get; init; } = Threadsmith.Core.DestinationExpectation.Absent;

    /// <summary>Explicit content for create or move-plus-edit.</summary>
    public FileContentDescriptor? Content { get; init; }

    /// <summary>Lifecycle risk supplied for review and recomputed by the host.</summary>
    public FileLifecycleRisk? LifecycleRisk { get; init; }

    /// <summary>Optional project-file association.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>Zero-based character offset for replacement.</summary>
    public int? StartOffset { get; init; }

    /// <summary>Number of baseline characters replaced.</summary>
    public int? Length { get; init; }

    /// <summary>Expected text at the replacement range.</summary>
    public string? ExpectedText { get; init; }

    /// <summary>Replacement text.</summary>
    public string? ReplacementText { get; init; } = string.Empty;

    /// <summary>Stable semantic symbol correlated to the change when available.</summary>
    public string? RelatedSymbolId { get; init; }
}

/// <summary>Atomic durable continuation for one approved-plan execution.</summary>
public sealed record ExecutionContinuation
{
    /// <summary>Current checkpoint schema.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Mutation workspace.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Approved plan revision.</summary>
    public required int PlanRevision { get; init; }

    /// <summary>Stable approved-plan identity.</summary>
    public required string PlanHash { get; init; }

    /// <summary>Current durable orchestration phase.</summary>
    public required ExecutionCheckpointPhase Phase { get; init; }

    /// <summary>Current approved plan step when applicable.</summary>
    public StepId? CurrentPlanStepId { get; init; }

    /// <summary>Immutable original diagnostic baseline identity.</summary>
    public required string DiagnosticBaselineIdentity { get; init; }

    /// <summary>Current promoted transactional baseline identity.</summary>
    public required string MutationBaselineIdentity { get; init; }

    /// <summary>Monotonic transactional baseline generation.</summary>
    public int MutationBaselineGeneration { get; init; }

    /// <summary>Current mutation set when one exists.</summary>
    public MutationSetId? MutationSetId { get; init; }

    /// <summary>Bounded host-owned continuation-state artifact required for explicit resume.</summary>
    public ExecutionArtifactReference? StateArtifact { get; init; }

    /// <summary>Exact-diff artifact.</summary>
    public ExecutionArtifactReference? DiffArtifact { get; init; }

    /// <summary>Pre-mutation diagnostic capture artifact.</summary>
    public ExecutionArtifactReference? BaselineArtifact { get; init; }

    /// <summary>Authoritative validation artifact.</summary>
    public ExecutionArtifactReference? ValidationArtifact { get; init; }

    /// <summary>Current side-effect operation when one is pending or reconciled.</summary>
    public ExecutionOperationRecord? Operation { get; init; }

    /// <summary>Mutation policy identity recorded before application.</summary>
    public string? PolicyIdentity { get; init; }

    /// <summary>Combined correction attempts used.</summary>
    public int CorrectionAttempts { get; init; }

    /// <summary>Combined correction attempt limit.</summary>
    public int CorrectionBudget { get; init; }

    /// <summary>Next legal host action.</summary>
    public required string NextAction { get; init; }

    /// <summary>Checkpoint timestamp.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>Host-authored terminal result derived only from authoritative execution evidence.</summary>
public sealed record ExecutionOutcomeProjection : IProjection
{
    /// <inheritdoc />
    public required ProjectionKey Key { get; init; }

    /// <summary>Current outcome schema.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Terminal phase.</summary>
    public required ExecutionCheckpointPhase Status { get; init; }

    /// <summary>Completed approved step ids.</summary>
    public IReadOnlyList<StepId> CompletedStepIds { get; init; } = [];

    /// <summary>Uncompleted approved step ids.</summary>
    public IReadOnlyList<StepId> UncompletedStepIds { get; init; } = [];

    /// <summary>Changed, created, deleted, or moved repository-relative files.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    /// <summary>Explicit applied create, delete, move, and case-only move effects.</summary>
    public IReadOnlyList<FileLifecycleChange> LifecycleChanges { get; init; } = [];

    /// <summary>Exact-identity reconciliation results for applied lifecycle operations.</summary>
    public IReadOnlyList<FileLifecycleReconciliation> LifecycleReconciliations { get; init; } = [];

    /// <summary>Host-derived bounded behavior summary.</summary>
    public IReadOnlyList<string> BehaviorSummary { get; init; } = [];

    /// <summary>Exact final diff evidence.</summary>
    public ExecutionArtifactReference? FinalDiff { get; init; }

    /// <summary>Authoritative validation evidence.</summary>
    public MutationValidationResult? Validation { get; init; }

    /// <summary>Mutation approval and policy provenance.</summary>
    public required string ApprovalProvenance { get; init; }

    /// <summary>Number of correction attempts performed.</summary>
    public int CorrectionAttempts { get; init; }

    /// <summary>Whether rollback remains available.</summary>
    public bool RollbackAvailable { get; init; }

    /// <summary>Known assumptions and residual risks.</summary>
    public IReadOnlyList<string> ResidualRisks { get; init; } = [];

    /// <summary>Cancellation/resumption history.</summary>
    public IReadOnlyList<string> ContinuationHistory { get; init; } = [];
}

/// <summary>Start input assembled by the host after plan approval.</summary>
public sealed record ExecutionStartRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Current immutable workspace baseline.</summary>
    public required WorkspaceBaseline Baseline { get; init; }

    /// <summary>Approved task.</summary>
    public required TaskSpecification Task { get; init; }

    /// <summary>Approved plan.</summary>
    public required ImplementationPlan ApprovedPlan { get; init; }

    /// <summary>Build request for the exact pre-mutation workspace.</summary>
    public required BuildValidationRequest ValidationRequest { get; init; }

    /// <summary>Combined compiler/test correction limit.</summary>
    public int CorrectionBudget { get; init; } = 3;
}

/// <summary>Host authorization for applying one staged execution mutation.</summary>
public sealed record ContinueExecutionRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Explicit or host-policy mutation approval.</summary>
    public required MutationApproval Approval { get; init; }

    /// <summary>Sanitized approval/policy provenance.</summary>
    public required string ApprovalProvenance { get; init; }
}

/// <summary>Result returned after an execution mutation is authoritatively applied but before validation completes.</summary>
public sealed record ExecutionApplyResult
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Applied mutation set.</summary>
    public required MutationSetId MutationSetId { get; init; }

    /// <summary>Changed repository-relative files.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    /// <summary>Explicit lifecycle reconciliation evidence from the commit.</summary>
    public IReadOnlyList<FileLifecycleReconciliation> LifecycleReconciliations { get; init; } = [];

    /// <summary>Checkpoint reached after the mutation side effect was reconciled as applied.</summary>
    public required ExecutionContinuation Continuation { get; init; }
}

/// <summary>Persists atomic orchestration checkpoints and authoritative outcomes.</summary>
public interface IExecutionCheckpointStore
{
    /// <summary>Atomically writes the latest checkpoint.</summary>
    Task SaveCheckpointAsync(
        ExecutionContinuation checkpoint,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the latest supported checkpoint or returns an inspectable unsupported result.</summary>
    Task<ExecutionContinuation?> GetCheckpointAsync(
        RunId runId,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically writes one terminal outcome.</summary>
    Task SaveOutcomeAsync(
        ExecutionOutcomeProjection outcome,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one terminal outcome.</summary>
    Task<ExecutionOutcomeProjection?> GetOutcomeAsync(
        RunId runId,
        CancellationToken cancellationToken = default);
}

/// <summary>Stores bounded execution evidence without exposing persistence implementation types.</summary>
public interface IExecutionArtifactPublisher
{
    /// <summary>Stores sanitized content-addressed evidence.</summary>
    Task<ExecutionArtifactReference> PublishAsync(
        SessionId sessionId,
        string kind,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>Reads and verifies evidence by content hash.</summary>
    Task<string?> ReadAsync(
        ExecutionArtifactReference reference,
        CancellationToken cancellationToken = default);
}

/// <summary>Host-owned serial approved-plan execution facade.</summary>
public interface IExecutionOrchestrator
{
    /// <summary>Starts implementation for an approved plan and stops at the next required host decision.</summary>
    Task<ExecutionContinuation> StartAsync(
        ExecutionStartRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Continues a staged execution after mutation authorization.</summary>
    Task<ExecutionOutcomeProjection> ContinueAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Explicitly resumes an interrupted nonterminal run after fail-closed revalidation.</summary>
    Task<ExecutionContinuation> ResumeAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default);

    /// <summary>Waits for the authoritative terminal execution outcome.</summary>
    Task<ExecutionOutcomeProjection> WaitForOutcomeAsync(
        RunId runId,
        CancellationToken cancellationToken = default);
}

/// <summary>Explicitly resumes an eligible interrupted execution.</summary>
public sealed record ResumeRunCommand(SessionId SessionId, RunId RunId)
    : ICommand<ExecutionContinuation>;

/// <summary>Continues a staged execution with a separate mutation authorization.</summary>
public sealed record ContinueExecutionCommand(ContinueExecutionRequest Request)
    : ICommand<ExecutionOutcomeProjection>;

/// <summary>Pre-captures validation baseline evidence while mutation review is pending.</summary>
public sealed record PrepareExecutionValidationCommand(SessionId SessionId, RunId RunId)
    : ICommand<ExecutionContinuation>;

/// <summary>Applies an approved execution mutation and stops before post-apply validation.</summary>
public sealed record ApplyExecutionMutationCommand(ContinueExecutionRequest Request)
    : ICommand<ExecutionApplyResult>;

/// <summary>Gets the active staged mutation for a host review surface.</summary>
public sealed record GetExecutionMutationCommand(SessionId SessionId, RunId RunId)
    : ICommand<StagedMutationSet?>;
