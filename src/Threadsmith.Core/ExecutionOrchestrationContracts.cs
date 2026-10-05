namespace Threadsmith.Core;

using System.Text.Json.Serialization;

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

    /// <summary>Applied partial work was validated; explicit resume is required before proposing more.</summary>
    ContinuationPending,

    /// <summary>The current plan completed and the objective is ready for another planning turn.</summary>
    PlanContinuationPending,

    /// <summary>The unfinished plan requires renewed evidence and a separately approved replacement.</summary>
    PlanReplanningPending,
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

/// <summary>Model-authored mutation-set content without host-owned execution identities.</summary>
public sealed record MutationProposalSet
{
    /// <summary>Ordered proposed changes.</summary>
    public required IReadOnlyList<MutationProposalChange> Mutations { get; init; }

    /// <summary>Why the requested source changes are needed.</summary>
    public required string Rationale { get; init; }

    /// <summary>Projects expected to be affected.</summary>
    public IReadOnlyList<string>? AffectedProjects { get; init; } = [];

    /// <summary>Diagnostics expected to be resolved.</summary>
    public IReadOnlyList<string>? ExpectedDiagnosticsResolved { get; init; } = [];

    /// <summary>Tests expected to validate the changes.</summary>
    public IReadOnlyList<string>? ExpectedTests { get; init; } = [];

    /// <summary>Model-supplied risk classification subject to host recomputation.</summary>
    public MutationRisk? Risk { get; init; } = MutationRisk.Medium;
}

/// <summary>Purpose of one proposal within incremental approved-plan execution.</summary>
public enum MutationBatchPurpose
{
    /// <summary>Ordinary forward implementation work.</summary>
    Implementation,

    /// <summary>A correction responding to failed validation.</summary>
    Correction,
}

/// <summary>Model-authored lifecycle content without host-computed byte identity.</summary>
public sealed record MutationProposalContent
{
    /// <summary>Complete text content.</summary>
    public required string Text { get; init; }

    /// <summary>Optional explicit encoding; new files default to UTF-8 and edits preserve their source encoding.</summary>
    public FileTextEncoding? Encoding { get; init; }

    /// <summary>Optional explicit newline normalization; new files default to LF and edits otherwise preserve supplied endings.</summary>
    public FileNewline? Newline { get; init; }
}

/// <summary>One model-authored operation without a host-owned mutation identity.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateFileMutationProposal), "CreateFile")]
[JsonDerivedType(typeof(DeleteFileMutationProposal), "DeleteFile")]
[JsonDerivedType(typeof(ReplaceTextMutationProposal), "ReplaceText")]
[JsonDerivedType(typeof(RenameSymbolMutationProposal), "RenameSymbol")]
[JsonDerivedType(typeof(MoveFileMutationProposal), "MoveFile")]
public abstract record MutationProposalChange
{
    /// <summary>Slash-normalized repository-relative source path.</summary>
    public required string RelativePath { get; init; }
}

/// <summary>Model-authored file creation.</summary>
public sealed record CreateFileMutationProposal : MutationProposalChange
{
    /// <summary>Complete content for the new file.</summary>
    public required MutationProposalContent Content { get; init; }

    /// <summary>Optional project-file association.</summary>
    public string? ProjectFilePath { get; init; }
}

/// <summary>Model-authored file deletion.</summary>
public sealed record DeleteFileMutationProposal : MutationProposalChange
{
    /// <summary>Optional project-file association.</summary>
    public string? ProjectFilePath { get; init; }
}

/// <summary>Model-authored exact text replacement.</summary>
public sealed record ReplaceTextMutationProposal : MutationProposalChange
{
    /// <summary>Optional zero-based UTF-16 offset; required only to disambiguate repeated text or place an empty insertion.</summary>
    public int? StartOffset { get; init; }

    /// <summary>Expected text at the replacement range.</summary>
    public required string ExpectedText { get; init; }

    /// <summary>Replacement text.</summary>
    public required string ReplacementText { get; init; }

    /// <summary>Stable semantic symbol correlated to the change when available.</summary>
    public string? RelatedSymbolId { get; init; }

    /// <summary>Optional project-file association.</summary>
    public string? ProjectFilePath { get; init; }
}

/// <summary>Model-authored compiler-aware symbol rename.</summary>
public sealed record RenameSymbolMutationProposal : MutationProposalChange
{
    /// <summary>Stable semantic symbol selected for rename.</summary>
    public required string RelatedSymbolId { get; init; }

    /// <summary>New symbol identifier.</summary>
    public required string ReplacementText { get; init; }

    /// <summary>Optional project-file association.</summary>
    public string? ProjectFilePath { get; init; }
}

/// <summary>Model-authored file relocation.</summary>
public sealed record MoveFileMutationProposal : MutationProposalChange
{
    /// <summary>Slash-normalized destination for the relocation.</summary>
    public required string DestinationRelativePath { get; init; }

    /// <summary>Optional complete content for a move-plus-edit operation.</summary>
    public MutationProposalContent? Content { get; init; }

    /// <summary>Optional project-file association.</summary>
    public string? ProjectFilePath { get; init; }
}

/// <summary>Atomic durable continuation for one approved-plan execution.</summary>
public sealed record ExecutionContinuation
{
    /// <summary>Current checkpoint schema.</summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Mutation workspace.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Approved plan revision.</summary>
    public required int PlanRevision { get; init; }

    /// <summary>One-based plan tranche ordinal within the user objective.</summary>
    public int PlanOrdinal { get; init; } = 1;

    /// <summary>Stable approved-plan identity.</summary>
    public required string PlanHash { get; init; }

    /// <summary>Current durable orchestration phase.</summary>
    public required ExecutionCheckpointPhase Phase { get; init; }

    /// <summary>Current approved plan step when applicable.</summary>
    public StepId? CurrentPlanStepId { get; init; }

    /// <summary>One-based ordinal of the current approved plan step.</summary>
    public int? CurrentPlanStepOrdinal { get; init; }

    /// <summary>Bounded display title of the current approved plan step.</summary>
    public string? CurrentPlanStepTitle { get; init; }

    /// <summary>One-based proposal ordinal across the execution.</summary>
    public int BatchOrdinal { get; init; }

    /// <summary>Purpose of the current proposal.</summary>
    public MutationBatchPurpose BatchPurpose { get; init; } = MutationBatchPurpose.Implementation;

    /// <summary>Completion hint attached to the current proposal.</summary>
    public bool? PendingStepComplete { get; init; }

    /// <summary>Approved steps with supported completion evidence.</summary>
    public IReadOnlyList<StepId> CompletedStepIds { get; init; } = [];

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

    /// <summary>Cumulative execution-budget usage observed at this boundary.</summary>
    public BudgetDimensions? BudgetUsed { get; init; }

    /// <summary>Whether rollback remains available.</summary>
    public bool RollbackAvailable { get; init; }

    /// <summary>Known assumptions and residual risks.</summary>
    public IReadOnlyList<string> ResidualRisks { get; init; } = [];

    /// <summary>Cancellation/resumption history.</summary>
    public IReadOnlyList<string> ContinuationHistory { get; init; } = [];

    /// <summary>Sanitized model explanation for a paused, unfinished plan.</summary>
    public string? ReplanReason { get; init; }
}

/// <summary>Persists atomic orchestration checkpoints and authoritative outcomes.</summary>
public interface IExecutionCheckpointStore
{
    /// <summary>Detects legacy write intents that cannot prove their disk outcome, including unbound history.</summary>
    Task<bool> HasUnresolvedLegacyEffectsAsync(string repositoryIdentity, CancellationToken cancellationToken = default);

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
