namespace Threadsmith.Context;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Typed evidence categories used by phase-specific context policy.</summary>
public enum EvidenceKind
{
    /// <summary>Repository structure and project inventory.</summary>
    RepositoryMap,

    /// <summary>Bounded source or configuration excerpt.</summary>
    SourceExcerpt,

    /// <summary>Compiler-aware symbol or project fact.</summary>
    SemanticFact,

    /// <summary>Normalized result from a governed tool.</summary>
    ToolResult,

    /// <summary>User constraint or acceptance-related fact.</summary>
    UserConstraint,

    /// <summary>Accepted decision that must survive reduction.</summary>
    Decision,

    /// <summary>Diagnostic or validation result.</summary>
    Diagnostic,

    /// <summary>Failure and attempted remedy.</summary>
    Failure,
}

/// <summary>Sensitivity classification carried into model-selection policy.</summary>
public enum EvidenceSensitivity
{
    /// <summary>No sensitive content is known.</summary>
    None,

    /// <summary>Repository content requires a profile that permits sensitive data.</summary>
    Sensitive,
}

/// <summary>Host-owned provenance for one evidence item.</summary>
public sealed record EvidenceProvenance
{
    /// <summary>Repository-relative source path when applicable.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Optional one-based source range.</summary>
    public SourceRange? SourceRange { get; init; }

    /// <summary>Repository revision or baseline hash.</summary>
    public string? RepositoryRevision { get; init; }

    /// <summary>Tool invocation that produced the evidence.</summary>
    public ToolInvocationId? ToolInvocationId { get; init; }

    /// <summary>Compiler-awareness confidence at collection time.</summary>
    public SemanticConfidenceLevel SemanticConfidence { get; init; }

    /// <summary>Stable source description for non-file evidence.</summary>
    public required string Source { get; init; }

    /// <summary>Child run that produced joined evidence.</summary>
    public RunId? ChildRunId { get; init; }

    /// <summary>Child assignment that produced joined evidence.</summary>
    public AgentAssignmentId? AgentAssignmentId { get; init; }

    /// <summary>Configured model profile used by the child.</summary>
    public ModelProfileId? ModelProfileId { get; init; }

    /// <summary>Immutable baseline identity observed by the child.</summary>
    public string? BaselineIdentity { get; init; }
}

/// <summary>One governed evidence item with provenance, relevance, and invalidation state.</summary>
public sealed record Evidence
{
    /// <summary>Stable evidence identity.</summary>
    public required EvidenceId EvidenceId { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run when evidence is run-specific.</summary>
    public RunId? RunId { get; init; }

    /// <summary>Typed evidence category.</summary>
    public EvidenceKind Kind { get; init; }

    /// <summary>Sanitized, bounded content supplied to governed context.</summary>
    public required string Content { get; init; }

    /// <summary>Source and confidence metadata.</summary>
    public required EvidenceProvenance Provenance { get; init; }

    /// <summary>Collection timestamp.</summary>
    public DateTimeOffset CollectedAt { get; init; }

    /// <summary>Selection score from zero to one.</summary>
    public double Relevance { get; init; }

    /// <summary>Estimated content tokens.</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Sensitivity used by model-selection policy.</summary>
    public EvidenceSensitivity Sensitivity { get; init; }

    /// <summary>Keys whose invalidation makes this evidence stale.</summary>
    public IReadOnlyList<string> InvalidationKeys { get; init; } = [];

    /// <summary>Whether the item is stale and excluded from new requests.</summary>
    public bool IsStale { get; init; }

    /// <summary>Inspectable stale reason.</summary>
    public string? StaleReason { get; init; }
}

/// <summary>Stores governed evidence and applies queued invalidations at turn boundaries.</summary>
public interface IEvidenceStore
{
    /// <summary>Adds or replaces one evidence item.</summary>
    Task AddAsync(Evidence evidence, CancellationToken cancellationToken = default);

    /// <summary>Gets a detached session evidence snapshot.</summary>
    IReadOnlyList<Evidence> Snapshot(SessionId sessionId);

    /// <summary>Copies one session's detached governed evidence into another session.</summary>
    void CopySession(SessionId sourceSessionId, SessionId destinationSessionId);

    /// <summary>Queues a session-scoped invalidation without changing the current turn snapshot.</summary>
    void QueueInvalidation(SessionId sessionId, string key, string reason);

    /// <summary>Applies one session's queued invalidations and returns the newly stale item count.</summary>
    Task<int> ApplyInvalidationsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>One model-visible tool contract included in governed context.</summary>
public sealed record ContextToolSchema(
    string Id,
    string Description,
    string JsonSchema,
    bool PreferStrictArguments = false);

/// <summary>Input state for one phase-specific context assembly.</summary>
public sealed record ContextAssemblyRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Current execution phase.</summary>
    public RunPhase Phase { get; init; }

    /// <summary>Explicit task state.</summary>
    public required TaskSpecification Task { get; init; }

    /// <summary>Normalized repository root used to resolve append files.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>Stable repository identity used for repository-scoped local memory.</summary>
    public string? RepositoryIdentity { get; init; }

    /// <summary>Configured prohibited repository paths.</summary>
    public IReadOnlyList<string> ProhibitedPaths { get; init; } = [];

    /// <summary>Repository-relative file or directory scope used for hierarchical instructions.</summary>
    public string? WorkingScope { get; init; }

    /// <summary>Monotonic trust/policy generation included in instruction identity.</summary>
    public long TrustGeneration { get; init; }

    /// <summary>How canonical tool schemas are transported to the selected adapter.</summary>
    public ToolTransportMode ToolTransportMode { get; init; } = ToolTransportMode.Native;

    /// <summary>Tool contracts currently available to the model.</summary>
    public IReadOnlyList<ContextToolSchema> ToolSchemas { get; init; } = [];

    /// <summary>Required model features for this request.</summary>
    public ModelCapabilitySet RequiredCapabilities { get; init; } = new();

    /// <summary>Hard model-selection constraints.</summary>
    public ModelSelectionConstraints ModelConstraints { get; init; } = new();

    /// <summary>User or session default model, which takes precedence over advisory hints.</summary>
    public ModelProfileId? DefaultModelProfileId { get; init; }

    /// <summary>Pending plan supplied as explicit governed state during revision.</summary>
    public ImplementationPlan? PlanUnderRevision { get; init; }

    /// <summary>Approved plan that bounds mutation preparation.</summary>
    public ImplementationPlan? ApprovedPlan { get; init; }

    /// <summary>Immutable baseline identity and hashes supplied for mutation preparation.</summary>
    public WorkspaceBaseline? MutationBaseline { get; init; }

    /// <summary>Transient host context visible only to the current assembled request.</summary>
    public IReadOnlyList<string> CurrentTurnHostContext { get; init; } = [];

    /// <summary>Authoritative archived current user message.</summary>
    public ConversationMessageId? CurrentMessageId { get; init; }

    /// <summary>Session override for future conversation selection.</summary>
    public ConversationContextMode? ConversationModeOverride { get; init; }

    /// <summary>Layer supplying the optional mode override.</summary>
    public string? ConversationModeSource { get; init; }
}

/// <summary>Governed model input and its inspectable execution record.</summary>
public sealed record ContextAssemblyResult(
    string ModelInput,
    WorkloadClass WorkloadClass,
    ModelCapabilitySet RequiredCapabilities,
    ModelSelectionConstraints ModelConstraints,
    ModelResolution? ModelResolution,
    ContextInspectionProjection Inspection,
    IReadOnlyList<ModelMessage>? Messages = null,
    ModelRequestLayout? Layout = null,
    ModelWireEstimate? WireEstimate = null,
    string? ToolInventoryDigest = null,
    string? InstructionBundleDigest = null);

/// <summary>Assembles model input from explicit state rather than transcript replay.</summary>
public interface IContextAssembler
{
    /// <summary>Builds one governed request and records its inspection snapshot.</summary>
    Task<ContextAssemblyResult> AssembleAsync(
        ContextAssemblyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the most recent detached inspection record for a run.</summary>
    ContextInspectionProjection? GetInspection(RunId runId);

    /// <summary>Records one bounded pre-sampling active-turn assessment.</summary>
    Task UpdateActiveTurnInspectionAsync(
        SessionId sessionId,
        RunId runId,
        ActiveTurnCompactionInspectionProjection activeTurn,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Records request-local visible source frontier counts without source content.</summary>
    Task UpdateVisibleSourceFrontierInspectionAsync(
        SessionId sessionId,
        RunId runId,
        VisibleSourceFrontierInspectionProjection visibleSourceFrontier,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Invalidates every cached inspection after a shared model-selection change.</summary>
    void InvalidateInspections();
}

/// <summary>One configured prompt-append asset after sanitization and versioning.</summary>
public sealed record PromptAppendSegment(
    string Id,
    string Version,
    string ContentHash,
    string SourcePath,
    int Position,
    string Content);

/// <summary>Parameters for safe project prompt-append loading.</summary>
public sealed record PromptAppendLoadRequest(
    string RepositoryPath,
    IReadOnlyList<string> ConfiguredPaths,
    IReadOnlyList<string> ProhibitedPaths);

/// <summary>Kind of repository instruction source.</summary>
public enum RepositoryInstructionSourceKind
{
    /// <summary>Applicable hierarchical AGENTS.md file.</summary>
    Agents,

    /// <summary>Configured prompt-append asset.</summary>
    PromptAppend,
}

/// <summary>One confined normalized source in an instruction bundle.</summary>
public sealed record RepositoryInstructionSource(
    RepositoryInstructionSourceKind Kind,
    string Id,
    string RelativePath,
    string Version,
    string Content,
    int Position);

/// <summary>Deterministic repository instruction bundle resolved at a turn boundary.</summary>
public sealed record RepositoryInstructionBundle
{
    /// <summary>Canonical repository root.</summary>
    public required string RepositoryRoot { get; init; }

    /// <summary>Canonical repository-relative working scope.</summary>
    public required string WorkingScope { get; init; }

    /// <summary>Ordered parent-to-child AGENTS.md and configured append sources.</summary>
    public IReadOnlyList<RepositoryInstructionSource> Sources { get; init; } = [];

    /// <summary>Exact bundle identity and content digest.</summary>
    public required string Digest { get; init; }
}

/// <summary>Resolves confined hierarchical repository instructions at each turn boundary.</summary>
public interface IRepositoryInstructionResolver
{
    /// <summary>Resolves and fingerprints the applicable source chain.</summary>
    Task<RepositoryInstructionBundle> ResolveAsync(
        string repositoryPath,
        string? workingScope,
        IReadOnlyList<PromptAppendSegment> promptAppends,
        IReadOnlyList<string> prohibitedPaths,
        long trustGeneration,
        CancellationToken cancellationToken = default);

    /// <summary>Invalidates cached bundles after watcher loss or repository-wide change.</summary>
    void InvalidateRepository(string repositoryPath);
}

/// <summary>Loads ordered, bounded, versioned project prompt-append assets.</summary>
public interface IPromptAppendLoader
{
    /// <summary>Loads the current turn-boundary snapshot.</summary>
    Task<IReadOnlyList<PromptAppendSegment>> LoadAsync(
        PromptAppendLoadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Queues one repository-relative or absolute append path for next-boundary refresh.</summary>
    void QueueInvalidation(string repositoryPath, string path);

    /// <summary>Queues all cached append paths for a repository for next-boundary refresh.</summary>
    void QueueRepositoryInvalidation(string repositoryPath);
}

/// <summary>Outcome of one advisory model-preference hint.</summary>
public sealed record ModelHintResolution(string Source, ModelProfileId ProfileId, string Reason);

/// <summary>Host-owned per-request model resolution and rationale.</summary>
public sealed record ModelResolution(
    ModelProfileId ProfileId,
    int ContextWindow,
    int MaximumOutputTokens,
    IReadOnlyList<ModelHintResolution> AppliedHints,
    IReadOnlyList<ModelHintResolution> IgnoredHints,
    IReadOnlyList<string> Rationale,
    int RequestOutputTokenReserve = 0)
{
    /// <summary>Gets the effective per-request reserve, including compatibility for older callers.</summary>
    public int EffectiveRequestOutputTokenReserve => RequestOutputTokenReserve > 0
        ? RequestOutputTokenReserve
        : MaximumOutputTokens;
}

/// <summary>Provides a turn-boundary snapshot of active advisory model hints.</summary>
public interface IModelPreferenceSnapshotProvider
{
    /// <summary>Gets detached hints for a workload.</summary>
    IReadOnlyList<ModelPreferenceHint> Snapshot(WorkloadClass workloadClass);
}

/// <summary>Resolves a configured model under host policy and advisory hints.</summary>
public interface IModelResolver
{
    /// <summary>Gets the largest configured input capacity available for provisional evidence admission.</summary>
    int MaximumInputTokenBudget { get; }

    /// <summary>Resolves a configured model and records applied and ignored hints.</summary>
    ModelResolution Resolve(
        WorkloadClass workloadClass,
        ModelCapabilitySet requiredCapabilities,
        ModelSelectionConstraints constraints,
        ModelProfileId? defaultModelProfileId = null);
}
