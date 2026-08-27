namespace Threadsmith.Core;

/// <summary>Mutation operations understood by the host-owned transactional workspace.</summary>
public enum MutationType
{
    /// <summary>Creates a new text file.</summary>
    CreateFile,

    /// <summary>Deletes an existing text file.</summary>
    DeleteFile,

    /// <summary>Replaces an exact text range.</summary>
    ReplaceText,

    /// <summary>Applies a validated unified text diff.</summary>
    ApplyUnifiedDiff,

    /// <summary>Replaces one bounded Roslyn syntax node.</summary>
    ReplaceSyntaxNode,

    /// <summary>Renames one Roslyn symbol and its loaded references.</summary>
    RenameSymbol,

    /// <summary>Moves one exact baseline file to an absent destination.</summary>
    MoveFile,
}

/// <summary>Expected state of a lifecycle-operation destination.</summary>
public enum DestinationExpectation
{
    /// <summary>The destination must not exist in either the immutable baseline or live workspace.</summary>
    Absent,
}

/// <summary>Text encoding retained or selected for lifecycle content.</summary>
public enum FileTextEncoding
{
    /// <summary>UTF-8 without a byte-order mark.</summary>
    Utf8,

    /// <summary>UTF-8 with a byte-order mark.</summary>
    Utf8Bom,
}

/// <summary>Newline convention retained or selected for lifecycle content.</summary>
public enum FileNewline
{
    /// <summary>Line-feed newlines.</summary>
    Lf,

    /// <summary>Carriage-return plus line-feed newlines.</summary>
    CrLf,
}

/// <summary>Host-observed identity required for an existing lifecycle source.</summary>
public sealed record ExpectedFileIdentity
{
    /// <summary>Exact SHA-256 of the baseline bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Exact baseline byte count.</summary>
    public long ByteLength { get; init; }
}

/// <summary>Bounded text content plus its exact byte representation metadata.</summary>
public sealed record FileContentDescriptor
{
    /// <summary>Text content.</summary>
    public required string Text { get; init; }

    /// <summary>Encoding used when the file is written.</summary>
    public FileTextEncoding Encoding { get; init; } = FileTextEncoding.Utf8;

    /// <summary>Newline convention used when the file is written.</summary>
    public FileNewline Newline { get; init; } = FileNewline.Lf;

    /// <summary>SHA-256 of the encoded content when supplied by the proposal source.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>Lifecycle-specific risk classification retained in review and outcomes.</summary>
public enum FileLifecycleRisk
{
    /// <summary>A new bounded file is added.</summary>
    Additive,

    /// <summary>An existing file is relocated without content loss.</summary>
    Relocation,

    /// <summary>An existing file is removed.</summary>
    Destructive,

    /// <summary>A project, build, package, or runtime configuration surface changes.</summary>
    ProjectSystem,
}

/// <summary>Risk assigned to a proposed mutation set.</summary>
public enum MutationRisk
{
    /// <summary>Small, local, and mechanically reversible.</summary>
    Low,

    /// <summary>Touches multiple files or a compiler-aware contract.</summary>
    Medium,

    /// <summary>Broad, destructive, or difficult to validate.</summary>
    High,
}

/// <summary>User or host-policy decision required before a staged set can commit.</summary>
public enum MutationApprovalLevel
{
    /// <summary>Render the proposal without allowing application.</summary>
    PreviewOnly,

    /// <summary>Approve every mutation in the set.</summary>
    EntireSet,

    /// <summary>Approve only explicitly selected files.</summary>
    SelectedFiles,

    /// <summary>Approve only explicitly selected mutations.</summary>
    SelectedMutations,

    /// <summary>Allow only host policy to approve a bounded low-risk set; model output must never select it.</summary>
    PolicyAutoApproved,

    /// <summary>Apply now but require explicit acceptance after validation.</summary>
    ApplyThenAccept,
}

/// <summary>Filesystem isolation used for a transactional workspace.</summary>
public enum WorkspaceIsolationMode
{
    /// <summary>Stage in memory and commit to the current tracked worktree.</summary>
    TrackedInPlace,

    /// <summary>Stage and commit inside a dedicated Git worktree.</summary>
    GitWorktree,

    /// <summary>Stage and commit inside a temporary repository copy.</summary>
    TemporaryCopy,
}

/// <summary>One bounded host-owned text mutation.</summary>
public sealed record Mutation
{
    /// <summary>Current mutation operation schema.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Stable mutation identity.</summary>
    public required MutationId MutationId { get; init; }

    /// <summary>Typed mutation operation.</summary>
    public required MutationType Type { get; init; }

    /// <summary>Slash-normalized path relative to the repository root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Expected baseline hash, or <see langword="null"/> for file creation.</summary>
    public string? BaselineSha256 { get; init; }

    /// <summary>Exact baseline identity for delete and move operations.</summary>
    public ExpectedFileIdentity? ExpectedIdentity { get; init; }

    /// <summary>Slash-normalized destination for a move operation.</summary>
    public string? DestinationRelativePath { get; init; }

    /// <summary>Required destination state for create and move operations.</summary>
    public DestinationExpectation DestinationExpectation { get; init; } = DestinationExpectation.Absent;

    /// <summary>Explicit content and byte representation for create or move-plus-edit.</summary>
    public FileContentDescriptor? Content { get; init; }

    /// <summary>Lifecycle risk supplied for review and recomputed by the host.</summary>
    public FileLifecycleRisk? LifecycleRisk { get; init; }

    /// <summary>Optional project-file association; it never authorizes or implies a hidden project edit.</summary>
    public string? ProjectFilePath { get; init; }

    /// <summary>Zero-based character offset for a replacement.</summary>
    public int StartOffset { get; init; }

    /// <summary>Number of baseline characters replaced.</summary>
    public int Length { get; init; }

    /// <summary>Expected text at the replacement range when supplied.</summary>
    public string? ExpectedText { get; init; }

    /// <summary>Replacement or created-file content.</summary>
    public string ReplacementText { get; init; } = string.Empty;

    /// <summary>Stable semantic symbol correlated to this mutation when available.</summary>
    public string? RelatedSymbolId { get; init; }

    /// <summary>Whether the UI shows this mutation's individual preview.</summary>
    public bool PreviewEnabled { get; init; } = true;
}

/// <summary>A bounded, ordered mutation proposal tied to one immutable baseline.</summary>
public sealed record MutationSet
{
    /// <summary>Stable mutation-set identity.</summary>
    public required MutationSetId MutationSetId { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run when the set originated from model execution.</summary>
    public RunId RunId { get; init; }

    /// <summary>Workspace whose baseline the proposal targets.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Capture time of the immutable baseline used to prepare the set.</summary>
    public required DateTimeOffset BaselineCapturedAt { get; init; }

    /// <summary>Baseline Git revision when one was available.</summary>
    public string? BaselineRevision { get; init; }

    /// <summary>Ordered mutations.</summary>
    public required IReadOnlyList<Mutation> Mutations { get; init; }

    /// <summary>Why the set is proposed.</summary>
    public required string Rationale { get; init; }

    /// <summary>Projects expected to be affected.</summary>
    public IReadOnlyList<string> AffectedProjects { get; init; } = [];

    /// <summary>Diagnostics expected to be resolved.</summary>
    public IReadOnlyList<string> ExpectedDiagnosticsResolved { get; init; } = [];

    /// <summary>Tests expected to validate the change.</summary>
    public IReadOnlyList<string> ExpectedTests { get; init; } = [];

    /// <summary>Risk classification supplied by the proposal source.</summary>
    public MutationRisk Risk { get; init; } = MutationRisk.Medium;

    /// <summary>Whether every target is contained by the accepted plan's declared file scope.</summary>
    public bool IsWithinApprovedPlan { get; init; }

    /// <summary>Required approval mode.</summary>
    public MutationApprovalLevel RequiredApproval { get; init; } = MutationApprovalLevel.EntireSet;

    /// <summary>Named validation policy applied after commit.</summary>
    public string ValidationPolicy { get; init; } = "default";
}

/// <summary>One file-level or mutation-level diff rendered for review.</summary>
public sealed record MutationDiff(
    MutationId MutationId,
    string RelativePath,
    string UnifiedDiff,
    bool PreviewEnabled)
{
    /// <summary>Operation represented by this exact change.</summary>
    public MutationType Type { get; init; } = MutationType.ReplaceText;

    /// <summary>Move destination when the operation relocates a file.</summary>
    public string? DestinationRelativePath { get; init; }

    /// <summary>Whether a move changes only filesystem casing.</summary>
    public bool IsCaseOnlyMove { get; init; }

    /// <summary>Lifecycle-specific host risk.</summary>
    public FileLifecycleRisk? LifecycleRisk { get; init; }
}

/// <summary>One normalized lifecycle effect included in an aggregate preview or outcome.</summary>
public sealed record FileLifecycleChange(
    MutationId MutationId,
    MutationType Type,
    string SourcePath,
    string? DestinationPath,
    bool IsCaseOnlyMove,
    FileLifecycleRisk Risk);

/// <summary>Exact aggregate and per-change preview for a staged mutation set.</summary>
public sealed record MutationPreview(
    MutationSetId MutationSetId,
    string UnifiedDiff,
    IReadOnlyList<MutationDiff> Changes,
    int AddedLines,
    int RemovedLines)
{
    /// <summary>Explicit added, deleted, moved, and case-renamed effects.</summary>
    public IReadOnlyList<FileLifecycleChange> LifecycleChanges { get; init; } = [];
}

/// <summary>Identity-based reconciliation state for a lifecycle transaction.</summary>
public enum FileLifecycleReconciliationState
{
    /// <summary>No filesystem effect was observed.</summary>
    NotStarted,

    /// <summary>Every expected final identity was observed.</summary>
    Applied,

    /// <summary>A partial effect was restored to its baseline identity.</summary>
    Compensated,

    /// <summary>External state conflicts with both baseline and expected final identities.</summary>
    Conflicted,

    /// <summary>The host cannot prove one safe state.</summary>
    Indeterminate,
}

/// <summary>Reconciliation result for one lifecycle operation.</summary>
public sealed record FileLifecycleReconciliation(
    MutationId MutationId,
    FileLifecycleReconciliationState State,
    string SourcePath,
    string? DestinationPath,
    string Reason);

/// <summary>One conflict that prevents a mutation set from committing.</summary>
public sealed record MutationConflict(
    MutationId? MutationId,
    string RelativePath,
    string Reason,
    string? ExpectedSha256 = null,
    string? ActualSha256 = null);

/// <summary>Conflict-detection result for a staged mutation set.</summary>
public sealed record ConflictReport(
    MutationSetId MutationSetId,
    IReadOnlyList<MutationConflict> Conflicts)
{
    /// <summary>Whether any conflict blocks application.</summary>
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>Prepared copy-on-write staging state and its review artifacts.</summary>
public sealed record StagedMutationSet(
    MutationSet MutationSet,
    MutationPreview Preview,
    ConflictReport Conflicts,
    ApprovalId ApprovalId)
{
    /// <summary>Approved plan steps explicitly correlated by the validated proposal source.</summary>
    public IReadOnlyList<StepId> PlanStepIds { get; init; } = [];
}

/// <summary>Explicit authorization selecting which staged mutations may commit.</summary>
public sealed record MutationApproval
{
    /// <summary>Approval mode granted by the user or host policy.</summary>
    public required MutationApprovalLevel Level { get; init; }

    /// <summary>Approved repository-relative files for selected-file approval.</summary>
    public IReadOnlyList<string> SelectedFiles { get; init; } = [];

    /// <summary>Approved mutation ids for selected-mutation approval.</summary>
    public IReadOnlyList<MutationId> SelectedMutations { get; init; } = [];

    /// <summary>Identity of the approval event when user authorization was required.</summary>
    public ApprovalId? ApprovalId { get; init; }
}

/// <summary>Result of committing an approved mutation set.</summary>
public sealed record MutationCommitResult(
    MutationSetId MutationSetId,
    IReadOnlyList<MutationId> AppliedMutations,
    IReadOnlyList<string> ChangedFiles,
    string BaselineRevision,
    bool RequiresAcceptance)
{
    /// <summary>Identity-based lifecycle reconciliation results.</summary>
    public IReadOnlyList<FileLifecycleReconciliation> LifecycleReconciliations { get; init; } = [];
}

/// <summary>Result of restoring files changed by an applied mutation set.</summary>
public sealed record MutationRollbackResult(
    MutationSetId MutationSetId,
    IReadOnlyList<string> RestoredFiles,
    ConflictReport Conflicts);

/// <summary>Host-owned descriptor for an isolated workspace location.</summary>
public sealed record WorkspaceIsolation(
    WorkspaceIsolationMode Mode,
    string RepositoryPath,
    string? GitRevision = null);

/// <summary>Transactional file workspace with immutable baseline reads and private staging.</summary>
public interface ITransactionalWorkspace : IAsyncDisposable
{
    /// <summary>Immutable repository baseline captured for this workspace.</summary>
    WorkspaceBaseline Baseline { get; }

    /// <summary>Active isolation mode and repository location.</summary>
    WorkspaceIsolation Isolation { get; }

    /// <summary>Reads content from the immutable baseline, never from private staging.</summary>
    Task<string?> ReadBaselineTextAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Reads private staged content for preview and validation.</summary>
    Task<string?> ReadStagedTextAsync(
        MutationSetId mutationSetId,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Validates and applies a proposed set to a private copy-on-write staging view.</summary>
    Task<StagedMutationSet> StageAsync(
        MutationSet mutationSet,
        CancellationToken cancellationToken = default);

    /// <summary>Enables or disables the individual preview for one staged change.</summary>
    Task<MutationPreview> SetPreviewEnabledAsync(
        MutationSetId mutationSetId,
        MutationId mutationId,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    /// <summary>Commits the explicitly approved portion of one staged set.</summary>
    Task<MutationCommitResult> CommitAsync(
        MutationSetId mutationSetId,
        MutationApproval approval,
        CancellationToken cancellationToken = default);

    /// <summary>Classifies lifecycle effects from exact current source and destination identities.</summary>
    Task<IReadOnlyList<FileLifecycleReconciliation>> ReconcileLifecycleAsync(
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default);

    /// <summary>Discards uncommitted staging or safely restores a committed set.</summary>
    Task<MutationRollbackResult> RollbackAsync(
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves transactional workspaces and registers staged sets for command-boundary ownership checks.</summary>
public interface ITransactionalWorkspaceResolver
{
    /// <summary>Gets a registered transactional workspace.</summary>
    ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId);

    /// <summary>Stages a mutation set and registers its session ownership for later review commands.</summary>
    Task<StagedMutationSet> StageAsync(
        MutationSet mutationSet,
        CancellationToken cancellationToken = default);

    /// <summary>Promotes the reconciled repository bytes to the next transactional mutation baseline generation.</summary>
    Task<WorkspaceBaseline> PromoteBaselineAsync(
        WorkspaceId workspaceId,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default);
}

/// <summary>Stages a bounded mutation proposal and requests its required approval.</summary>
public sealed record StageMutationSetCommand(
    MutationSet MutationSet) : ICommand<StagedMutationSet>;

/// <summary>Gets one exact staged mutation set after its review-ready event is observable.</summary>
public sealed record GetMutationReviewCommand(
    SessionId SessionId,
    MutationSetId MutationSetId) : ICommand<StagedMutationSet>;

/// <summary>Changes whether one staged mutation is shown as an individual preview.</summary>
public sealed record SetMutationPreviewCommand(
    SessionId SessionId,
    MutationSetId MutationSetId,
    MutationId MutationId,
    bool IsEnabled) : ICommand<MutationPreview>;

/// <summary>Commits an explicitly approved staged mutation set.</summary>
public sealed record CommitMutationSetCommand(
    SessionId SessionId,
    MutationSetId MutationSetId,
    MutationApproval Approval) : ICommand<MutationCommitResult>;

/// <summary>Discards staging or restores a committed mutation set.</summary>
public sealed record RollbackMutationSetCommand(
    SessionId SessionId,
    MutationSetId MutationSetId) : ICommand<MutationRollbackResult>;

/// <summary>Requests one governed model mutation proposal against an approved plan.</summary>
public sealed record ProposeMutationSetCommand(
    SessionId SessionId,
    RunId RunId,
    WorkspaceId WorkspaceId,
    TaskSpecification Task,
    ImplementationPlan ApprovedPlan,
    RunPhase Phase = RunPhase.MutationPreparation,
    string? CorrectionEvidence = null) : ICommand<StagedMutationSet>
{
    /// <summary>One-based approved-plan validation correction cycle, when applicable.</summary>
    public int CorrectionAttempt { get; init; }

    /// <summary>Maximum approved-plan validation correction cycles, when applicable.</summary>
    public int CorrectionLimit { get; init; }
}

/// <summary>Parameters for compiler-aware symbol rename.</summary>
public sealed record RenameSymbolMutationRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public RunId RunId { get; init; }

    /// <summary>Semantic workspace.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Immutable file baseline.</summary>
    public required WorkspaceBaseline Baseline { get; init; }

    /// <summary>Stable documentation-comment symbol id.</summary>
    public required string SymbolId { get; init; }

    /// <summary>Requested new symbol name.</summary>
    public required string NewName { get; init; }

    /// <summary>Reason for the rename.</summary>
    public required string Rationale { get; init; }
}

/// <summary>Parameters for bounded replacement of one syntax node.</summary>
public sealed record SyntaxReplacementMutationRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public RunId RunId { get; init; }

    /// <summary>Semantic workspace.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Immutable file baseline.</summary>
    public required WorkspaceBaseline Baseline { get; init; }

    /// <summary>Slash-normalized path relative to the repository root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Zero-based syntax-node start offset.</summary>
    public required int StartOffset { get; init; }

    /// <summary>Exact syntax-node length.</summary>
    public required int Length { get; init; }

    /// <summary>Replacement expression, statement, or member text.</summary>
    public required string ReplacementText { get; init; }

    /// <summary>Optional stable symbol expected to own the node.</summary>
    public string? SymbolId { get; init; }

    /// <summary>Reason for the replacement.</summary>
    public required string Rationale { get; init; }
}

/// <summary>Compiler-aware mutation proposal with any confidence-scope warnings.</summary>
public sealed record SemanticMutationResult(
    MutationSet MutationSet,
    IReadOnlyList<string> Warnings,
    SemanticConfidenceLevel Confidence);

/// <summary>Compiler-aware operations that emit host-owned transactional mutations.</summary>
public interface ISemanticMutationEngine
{
    /// <summary>Proposes a symbol rename at partial-compilation confidence or better.</summary>
    Task<SemanticMutationResult> RenameSymbolAsync(
        RenameSymbolMutationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Proposes replacement of one exact syntax node.</summary>
    Task<SemanticMutationResult> ReplaceSyntaxNodeAsync(
        SyntaxReplacementMutationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Proposes a Roslyn symbol rename through the application command boundary.</summary>
public sealed record RenameSymbolCommand(
    RenameSymbolMutationRequest Request) : ICommand<SemanticMutationResult>;

/// <summary>Proposes a bounded Roslyn syntax-node replacement through the application boundary.</summary>
public sealed record ReplaceSyntaxNodeCommand(
    SyntaxReplacementMutationRequest Request) : ICommand<SemanticMutationResult>;
