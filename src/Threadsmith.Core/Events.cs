namespace Threadsmith.Core;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Base contract for versioned immutable domain events.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SessionCreated), "sessionCreated")]
[JsonDerivedType(typeof(RepositoryOpened), "repositoryOpened")]
[JsonDerivedType(typeof(SolutionLoaded), "solutionLoaded")]
[JsonDerivedType(typeof(TaskIntentRecorded), "taskIntentRecorded")]
[JsonDerivedType(typeof(AcceptanceCriteriaRecorded), "acceptanceCriteriaRecorded")]
[JsonDerivedType(typeof(EvidenceAdded), "evidenceAdded")]
[JsonDerivedType(typeof(PlanProposed), "planProposed")]
[JsonDerivedType(typeof(PlanSanityCheckCompleted), "planSanityCheckCompleted")]
[JsonDerivedType(typeof(PlanAutoApproved), "planAutoApproved")]
[JsonDerivedType(typeof(PlanApprovalPolicyChanged), "planApprovalPolicyChanged")]
[JsonDerivedType(typeof(PlanRevisionRequested), "planRevisionRequested")]
[JsonDerivedType(typeof(ContextAssembled), "contextAssembled")]
[JsonDerivedType(typeof(ActiveTurnCompactionStarted), "activeTurnCompactionStarted")]
[JsonDerivedType(typeof(ActiveTurnCompactionCompleted), "activeTurnCompactionCompleted")]
[JsonDerivedType(typeof(ApprovalRequested), "approvalRequested")]
[JsonDerivedType(typeof(ApprovalGranted), "approvalGranted")]
[JsonDerivedType(typeof(ApprovalDenied), "approvalDenied")]
[JsonDerivedType(typeof(ToolInvocationStarted), "toolInvocationStarted")]
[JsonDerivedType(typeof(ToolInvocationCompleted), "toolInvocationCompleted")]
[JsonDerivedType(typeof(SemanticCheckStarted), "semanticCheckStarted")]
[JsonDerivedType(typeof(SemanticCheckCompleted), "semanticCheckCompleted")]
[JsonDerivedType(typeof(MutationProposalStarted), "mutationProposalStarted")]
[JsonDerivedType(typeof(MutationProposalRepairAttempted), "mutationProposalRepairAttempted")]
[JsonDerivedType(typeof(ModelCorrectionAttempted), "modelCorrectionAttempted")]
[JsonDerivedType(typeof(PreMutationAnalysisCompleted), "preMutationAnalysisCompleted")]
[JsonDerivedType(typeof(SemanticMutationWarningObserved), "semanticMutationWarningObserved")]
[JsonDerivedType(typeof(MutationSetProposed), "mutationSetProposed")]
[JsonDerivedType(typeof(MutationApplied), "mutationApplied")]
[JsonDerivedType(typeof(MutationSetRolledBack), "mutationSetRolledBack")]
[JsonDerivedType(typeof(BuildStarted), "buildStarted")]
[JsonDerivedType(typeof(DiagnosticObserved), "diagnosticObserved")]
[JsonDerivedType(typeof(TestRunCompleted), "testRunCompleted")]
[JsonDerivedType(typeof(ExtensionDiscovered), "extensionDiscovered")]
[JsonDerivedType(typeof(ExtensionActivated), "extensionActivated")]
[JsonDerivedType(typeof(ExtensionLoadFailed), "extensionLoadFailed")]
[JsonDerivedType(typeof(ExtensionDraining), "extensionDraining")]
[JsonDerivedType(typeof(ExtensionUnloaded), "extensionUnloaded")]
[JsonDerivedType(typeof(ExtensionUnloadFailed), "extensionUnloadFailed")]
[JsonDerivedType(typeof(SemanticConfidenceChanged), "semanticConfidenceChanged")]
[JsonDerivedType(typeof(SemanticLoadCompleted), "semanticLoadCompleted")]
[JsonDerivedType(typeof(RunTransitioned), "runTransitioned")]
[JsonDerivedType(typeof(RunTransitionFailed), "runTransitionFailed")]
[JsonDerivedType(typeof(ModelOutputObserved), "modelOutputObserved")]
[JsonDerivedType(typeof(ModelReasoningObserved), "modelReasoningObserved")]
[JsonDerivedType(typeof(ConversationMessageArchived), "conversationMessageArchived")]
[JsonDerivedType(typeof(ConversationModeChanged), "conversationModeChanged")]
[JsonDerivedType(typeof(ConversationMemoryPromoted), "conversationMemoryPromoted")]
[JsonDerivedType(typeof(ConversationMemorySuperseded), "conversationMemorySuperseded")]
[JsonDerivedType(typeof(ConversationMemoryInvalidated), "conversationMemoryInvalidated")]
[JsonDerivedType(typeof(RepositoryMemoryRemembered), "repositoryMemoryRemembered")]
[JsonDerivedType(typeof(RepositoryMemorySuperseded), "repositoryMemorySuperseded")]
[JsonDerivedType(typeof(RepositoryMemoryValidityChanged), "repositoryMemoryValidityChanged")]
[JsonDerivedType(typeof(ConversationSummarySnapshotReplaced), "conversationSummarySnapshotReplaced")]
[JsonDerivedType(typeof(ExecutionCheckpointWritten), "executionCheckpointWritten")]
[JsonDerivedType(typeof(ExecutionSideEffectRecorded), "executionSideEffectRecorded")]
[JsonDerivedType(typeof(ExecutionResumeRecorded), "executionResumeRecorded")]
[JsonDerivedType(typeof(ExecutionOutcomeRecorded), "executionOutcomeRecorded")]
[JsonDerivedType(typeof(DelegationCheckpointWritten), "delegationCheckpointWritten")]
[JsonDerivedType(typeof(AgentRunLifecycleObserved), "agentRunLifecycleObserved")]
[JsonDerivedType(typeof(SkillCatalogRefreshed), "skillCatalogRefreshed")]
[JsonDerivedType(typeof(SkillVerificationDecided), "skillVerificationDecided")]
[JsonDerivedType(typeof(SkillWorkflowCheckpointWritten), "skillWorkflowCheckpointWritten")]
[JsonDerivedType(typeof(SkillInvocationCompleted), "skillInvocationCompleted")]
[JsonDerivedType(typeof(HookInvocationStartedEvent), "hookInvocationStarted")]
[JsonDerivedType(typeof(HookInvocationCompletedEvent), "hookInvocationCompleted")]
[JsonDerivedType(typeof(HookRepositoryApprovalChanged), "hookRepositoryApprovalChanged")]
[JsonDerivedType(typeof(RunCompleted), "runCompleted")]
public interface IDomainEvent
{
    /// <summary>Gets the schema version.</summary>
    int SchemaVersion { get; }

    /// <summary>Gets the event timestamp.</summary>
    DateTimeOffset OccurredAt { get; }

    /// <summary>Gets the session.</summary>
    SessionId SessionId { get; }
}

/// <summary>Base domain event.</summary>
public abstract record DomainEvent(SessionId SessionId, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <inheritdoc />
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>A session was created.</summary>
public sealed record SessionCreated(SessionId SessionId, DateTimeOffset OccurredAt, string Name)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>A repository was opened.</summary>
public sealed record RepositoryOpened(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    string Path,
    WorkspaceId WorkspaceId = default,
    RepositoryTrustLevel TrustLevel = RepositoryTrustLevel.UntrustedInspection,
    IReadOnlyList<string>? ProhibitedPaths = null)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>A solution was loaded.</summary>
public sealed record SolutionLoaded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    string Path,
    WorkspaceId WorkspaceId = default,
    IReadOnlyList<string>? TargetFrameworks = null)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>Task intent was recorded.</summary>
public sealed record TaskIntentRecorded(SessionId SessionId, DateTimeOffset OccurredAt, string Intent)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>Acceptance criteria were recorded as explicit task state.</summary>
public sealed record AcceptanceCriteriaRecorded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    IReadOnlyList<AcceptanceCriterion> Criteria) : DomainEvent(SessionId, OccurredAt);

/// <summary>Evidence was added.</summary>
public sealed record EvidenceAdded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    EvidenceId EvidenceId,
    string Kind) : DomainEvent(SessionId, OccurredAt);

/// <summary>A plan was proposed.</summary>
public sealed record PlanProposed(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    string Summary,
    RunId RunId = default,
    ImplementationPlan? Plan = null,
    ApprovalId ApprovalId = default,
    PlanReviewStatus ReviewStatus = PlanReviewStatus.Pending)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>Cheap host-owned plan sanity checks completed before approval presentation or auto-approval.</summary>
public sealed record PlanSanityCheckCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    int Revision,
    PlanRiskClassification Risk,
    int IssueCount,
    int BlockingIssueCount,
    int RepairableIssueCount,
    int AffectedFileCount) : DomainEvent(SessionId, OccurredAt);

/// <summary>A structured plan was approved by host-owned plan approval policy.</summary>
public sealed record PlanAutoApproved(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    ApprovalId ApprovalId,
    PlanApprovalPolicy Policy,
    PlanRiskClassification Risk,
    int Revision,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>The plan approval policy changed through a host command or interactive surface.</summary>
public sealed record PlanApprovalPolicyChanged(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    PlanApprovalPolicy Policy,
    string Scope) : DomainEvent(SessionId, OccurredAt);

/// <summary>A user requested a revised plan proposal.</summary>
public sealed record PlanRevisionRequested(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    string Instructions) : DomainEvent(SessionId, OccurredAt);

/// <summary>A governed model context and its inspectable execution record were assembled.</summary>
public sealed record ContextAssembled(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ContextInspectionProjection Inspection) : DomainEvent(SessionId, OccurredAt);

/// <summary>An active-turn candidate operation began under host-owned pressure policy.</summary>
public sealed record ActiveTurnCompactionStarted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    ModelProfileId CandidateProfileId,
    int BeforeInputTokens,
    int PressureTargetTokens) : DomainEvent(SessionId, OccurredAt);

/// <summary>An active-turn candidate operation ended without exposing summary or tool content.</summary>
public sealed record ActiveTurnCompactionCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    ModelProfileId CandidateProfileId,
    ActiveTurnCompactionInspectionStatus Status,
    int BeforeInputTokens,
    int AfterInputTokens,
    long? DurationMilliseconds) : DomainEvent(SessionId, OccurredAt);

/// <summary>Identifies the host-owned boundary requesting approval.</summary>
public enum ApprovalRequestKind
{
    /// <summary>The kind is absent or originated from a legacy event.</summary>
    Unspecified = 0,

    /// <summary>A structured implementation plan requires review.</summary>
    Plan = 1,

    /// <summary>An exact staged mutation set requires review.</summary>
    MutationSet = 2,

    /// <summary>A governed tool invocation requires review.</summary>
    ToolInvocation = 3,
}

/// <summary>Approval was requested.</summary>
public sealed record ApprovalRequested(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ApprovalId ApprovalId,
    string Action,
    ApprovalRequestKind Kind = ApprovalRequestKind.Unspecified) : DomainEvent(SessionId, OccurredAt);

/// <summary>Approval was granted.</summary>
public sealed record ApprovalGranted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ApprovalId ApprovalId) : DomainEvent(SessionId, OccurredAt);

/// <summary>Approval was explicitly denied.</summary>
public sealed record ApprovalDenied(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ApprovalId ApprovalId,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>A tool invocation started.</summary>
public sealed record ToolInvocationStarted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ToolInvocationId ToolInvocationId,
    string ToolName,
    RunId RunId = default,
    string RequestedBy = "host",
    ToolActivitySource? Source = null,
    string? ActivityDetail = null) : DomainEvent(SessionId, OccurredAt);

/// <summary>A tool invocation completed.</summary>
public sealed record ToolInvocationCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ToolInvocationId ToolInvocationId,
    bool Succeeded,
    string? ResultJson = null,
    string? Error = null,
    bool IsTruncated = false,
    ToolActivitySource? Source = null,
    long? ElapsedMilliseconds = null,
    OperationActivityOutcome Outcome = OperationActivityOutcome.Unknown,
    string? ModelResultContent = null) : DomainEvent(SessionId, OccurredAt);

/// <summary>A semantic check started.</summary>
public sealed record SemanticCheckStarted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    SemanticCheckId SemanticCheckId,
    SemanticCheckPhase Phase,
    string CheckName) : DomainEvent(SessionId, OccurredAt);

/// <summary>A semantic check completed.</summary>
public sealed record SemanticCheckCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    SemanticCheckId SemanticCheckId,
    SemanticCheckPhase Phase,
    string CheckName,
    SemanticCheckOutcome Outcome,
    long? ElapsedMilliseconds = null,
    string? Detail = null) : DomainEvent(SessionId, OccurredAt);

/// <summary>Approved-plan execution started or retried one governed mutation proposal attempt.</summary>
public sealed record MutationProposalStarted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    int AttemptNumber,
    int MaximumAttempts) : DomainEvent(SessionId, OccurredAt);

/// <summary>Historical mutation-proposal repair event retained for durable replay.</summary>
public sealed record MutationProposalRepairAttempted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    int AttemptNumber,
    int MaximumAttempts,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>Model-visible corrective retry categories.</summary>
public enum ModelCorrectionCategory
{
    /// <summary>Provider-boundary malformed invocation correction.</summary>
    ProviderInvocation,

    /// <summary>Conversation tool-batch correction.</summary>
    ToolBatch,

    /// <summary>Plan schema correction.</summary>
    PlanSchema,

    /// <summary>Plan sanity correction.</summary>
    PlanSanity,

    /// <summary>Mutation proposal correction.</summary>
    MutationProposal,

    /// <summary>Pre-mutation analysis correction.</summary>
    PreMutationAnalysis,

    /// <summary>Post-apply validation correction.</summary>
    PostApplyValidation,

    /// <summary>Empty assistant response correction.</summary>
    EmptyResponse,
}

/// <summary>A recoverable model request was rejected and retried through a bounded corrective message.</summary>
public sealed record ModelCorrectionAttempted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    ModelCorrectionCategory Category,
    int AttemptNumber,
    int MaximumAttempts,
    string SafeReason) : DomainEvent(SessionId, OccurredAt);

/// <summary>Pre-mutation Roslyn screening completed before staging or approval.</summary>
public sealed record PreMutationAnalysisCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    MutationSetId MutationSetId,
    PreMutationGateDecision Decision,
    int DiagnosticCount,
    int BlockingDiagnosticCount,
    int OmissionCount,
    SemanticConfidenceLevel Confidence) : DomainEvent(SessionId, OccurredAt);

/// <summary>A semantic mutation completed with incomplete-confidence warning evidence.</summary>
public sealed record SemanticMutationWarningObserved(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    SemanticConfidenceLevel Confidence,
    string Message) : DomainEvent(SessionId, OccurredAt);

/// <summary>A mutation set was proposed.</summary>
public sealed record MutationSetProposed(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    MutationSetId MutationSetId,
    MutationPreview? Preview = null,
    MutationApprovalLevel RequiredApproval = MutationApprovalLevel.EntireSet,
    ApprovalId ApprovalId = default,
    WorkspaceIsolationMode IsolationMode = WorkspaceIsolationMode.TrackedInPlace)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>A mutation was applied.</summary>
public sealed record MutationApplied(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    MutationId MutationId,
    MutationSetId MutationSetId = default,
    string? RelativePath = null) : DomainEvent(SessionId, OccurredAt)
{
    /// <summary>Applied mutation operation.</summary>
    public MutationType Type { get; init; } = MutationType.ReplaceText;

    /// <summary>Normalized move destination when present.</summary>
    public string? DestinationRelativePath { get; init; }
}

/// <summary>A committed or staged mutation set was rolled back.</summary>
public sealed record MutationSetRolledBack(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    MutationSetId MutationSetId,
    IReadOnlyList<string> RestoredFiles) : DomainEvent(SessionId, OccurredAt);

/// <summary>A build started.</summary>
public sealed record BuildStarted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    IReadOnlyList<string>? Targets = null)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>A diagnostic was observed.</summary>
public sealed record DiagnosticObserved(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    string Code,
    string Message,
    Diagnostic? StructuredDiagnostic = null) : DomainEvent(SessionId, OccurredAt);

/// <summary>A test run completed.</summary>
public sealed record TestRunCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    int Passed,
    int Failed,
    int Skipped = 0,
    TestValidationResult? StructuredResult = null) : DomainEvent(SessionId, OccurredAt);

/// <summary>An extension was discovered.</summary>
public sealed record ExtensionDiscovered(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ExtensionId ExtensionId) : DomainEvent(SessionId, OccurredAt);

/// <summary>An extension was activated.</summary>
public sealed record ExtensionActivated(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ExtensionId ExtensionId) : DomainEvent(SessionId, OccurredAt);

/// <summary>An extension failed to load or activate.</summary>
public sealed record ExtensionLoadFailed(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ExtensionId ExtensionId,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>An extension began draining.</summary>
public sealed record ExtensionDraining(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ExtensionId ExtensionId) : DomainEvent(SessionId, OccurredAt);

/// <summary>An extension unloaded.</summary>
public sealed record ExtensionUnloaded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ExtensionId ExtensionId) : DomainEvent(SessionId, OccurredAt);

/// <summary>An extension failed to unload.</summary>
public sealed record ExtensionUnloadFailed(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ExtensionId ExtensionId,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>Semantic confidence changed.</summary>
public sealed record SemanticConfidenceChanged : DomainEvent
{
    /// <summary>Initializes a new instance of the <see cref="SemanticConfidenceChanged"/> class.</summary>
    public SemanticConfidenceChanged(
        SessionId sessionId,
        DateTimeOffset occurredAt,
        string confidence)
        : base(sessionId, occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confidence);
        if (!Enum.TryParse<SemanticConfidenceLevel>(confidence, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new ArgumentException(
                $"'{confidence}' is not a durable semantic confidence value.",
                nameof(confidence));
        }

        Confidence = confidence;
    }

    /// <summary>Current host-owned <see cref="SemanticConfidenceLevel"/> name.</summary>
    public string Confidence { get; init; }
}

/// <summary>Semantic loading completed for one workspace, including an unavailable result.</summary>
public sealed record SemanticLoadCompleted : DomainEvent
{
    /// <summary>Initializes a new instance of the <see cref="SemanticLoadCompleted"/> class.</summary>
    public SemanticLoadCompleted(
        SessionId sessionId,
        DateTimeOffset occurredAt,
        WorkspaceId workspaceId,
        string confidence)
        : base(sessionId, occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confidence);
        if (!Enum.TryParse<SemanticConfidenceLevel>(confidence, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new ArgumentException(
                $"'{confidence}' is not a durable semantic confidence value.",
                nameof(confidence));
        }

        WorkspaceId = workspaceId;
        Confidence = confidence;
    }

    /// <summary>Workspace whose semantic load completed.</summary>
    public WorkspaceId WorkspaceId { get; init; }

    /// <summary>Current host-owned <see cref="SemanticConfidenceLevel"/> name.</summary>
    public string Confidence { get; init; }
}

/// <summary>A run transitioned.</summary>
public sealed record RunTransitioned(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    RunPhase Source,
    RunPhase Destination) : DomainEvent(SessionId, OccurredAt);

/// <summary>A run transition failed.</summary>
public sealed record RunTransitionFailed(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    RunPhase Source,
    RunPhase Destination,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>Model output was observed.</summary>
public sealed record ModelOutputObserved(SessionId SessionId, DateTimeOffset OccurredAt, string Text)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>Model reasoning text was observed during streaming.</summary>
public sealed record ModelReasoningObserved(SessionId SessionId, DateTimeOffset OccurredAt, string Text)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>A sanitized visible message was archived.</summary>
public sealed record ConversationMessageArchived(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ConversationMessageId MessageId,
    RunId RunId,
    long Sequence,
    ConversationRole Role,
    ConversationSensitivity Sensitivity,
    bool UsesArtifact) : DomainEvent(SessionId, OccurredAt);

/// <summary>The session conversation mode changed for future requests.</summary>
public sealed record ConversationModeChanged(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ConversationContextMode Mode) : DomainEvent(SessionId, OccurredAt);

/// <summary>A validated item entered governed conversation memory.</summary>
public sealed record ConversationMemoryPromoted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ConversationMemoryId MemoryId,
    ConversationMemoryKind Kind) : DomainEvent(SessionId, OccurredAt);

/// <summary>A later governed item superseded an older item without deleting it.</summary>
public sealed record ConversationMemorySuperseded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ConversationMemoryId SupersededId,
    ConversationMemoryId ReplacementId) : DomainEvent(SessionId, OccurredAt);

/// <summary>A governed memory item became ineligible for future selection.</summary>
public sealed record ConversationMemoryInvalidated(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    ConversationMemoryId MemoryId,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>A repository-scoped memory item was remembered through a host-owned boundary.</summary>
public sealed record RepositoryMemoryRemembered(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    string RepositoryIdentity,
    RepositoryMemoryId MemoryId,
    RepositoryMemoryKind Kind,
    RepositoryMemoryAuthority Authority) : DomainEvent(SessionId, OccurredAt);

/// <summary>A repository-scoped memory correction superseded an older item.</summary>
public sealed record RepositoryMemorySuperseded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    string RepositoryIdentity,
    RepositoryMemoryId SupersededId,
    RepositoryMemoryId ReplacementId) : DomainEvent(SessionId, OccurredAt);

/// <summary>A repository-scoped memory item changed selection validity without deleting audit metadata.</summary>
public sealed record RepositoryMemoryValidityChanged(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    string RepositoryIdentity,
    RepositoryMemoryId MemoryId,
    RepositoryMemoryValidity Validity,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>The active structured conversation summary was atomically replaced.</summary>
public sealed record ConversationSummarySnapshotReplaced(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    long SnapshotVersion,
    long ThroughMessageSequence,
    int ActiveItemCount) : DomainEvent(SessionId, OccurredAt);

/// <summary>An approved-plan execution checkpoint was durably written.</summary>
public sealed record ExecutionCheckpointWritten(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    ExecutionCheckpointPhase Phase,
    string NextAction) : DomainEvent(SessionId, OccurredAt);

/// <summary>A write-ahead side-effect intent, result, or reconciliation was recorded.</summary>
public sealed record ExecutionSideEffectRecorded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    Guid OperationId,
    string Kind,
    ExecutionOperationState State) : DomainEvent(SessionId, OccurredAt);

/// <summary>An explicit resume request was accepted or denied.</summary>
public sealed record ExecutionResumeRecorded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    bool Succeeded,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>A host-authored authoritative execution outcome was recorded.</summary>
public sealed record ExecutionOutcomeRecorded(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    ExecutionCheckpointPhase Status) : DomainEvent(SessionId, OccurredAt);

/// <summary>A durable parallel-agent delegation boundary was recorded.</summary>
public sealed record DelegationCheckpointWritten(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    DelegationId DelegationId,
    RunId ParentRunId,
    DelegationCheckpointPhase Phase,
    int Generation,
    string NextAction) : DomainEvent(SessionId, OccurredAt);

/// <summary>One child reached an observable lifecycle state.</summary>
public sealed record AgentRunLifecycleObserved(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    DelegationId DelegationId,
    AgentAssignmentId AssignmentId,
    RunId ChildRunId,
    AgentRole Role,
    AgentRunStatus Status,
    int Generation,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>A metadata-only skill catalog refresh completed.</summary>
public sealed record SkillCatalogRefreshed(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    long Generation,
    int CandidateCount) : DomainEvent(SessionId, OccurredAt);

/// <summary>A package verification or revocation decision was recorded.</summary>
public sealed record SkillVerificationDecided(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    SkillId SkillId,
    string Version,
    string Digest,
    SkillScope Scope,
    SkillVerificationState State,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>A durable declarative workflow boundary was recorded.</summary>
public sealed record SkillWorkflowCheckpointWritten(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    SkillInvocationId InvocationId,
    SkillWorkflowId WorkflowId,
    SkillId SkillId,
    string Version,
    string Digest,
    SkillInvocationStatus Status,
    int Generation,
    string NextAction) : DomainEvent(SessionId, OccurredAt);

/// <summary>A skill invocation reached an authoritative terminal state.</summary>
public sealed record SkillInvocationCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    SkillInvocationId InvocationId,
    SkillId SkillId,
    string Version,
    string Digest,
    SkillInvocationStatus Status,
    string Reason) : DomainEvent(SessionId, OccurredAt);

/// <summary>A lifecycle-hook invocation started.</summary>
public sealed record HookInvocationStartedEvent(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    HookInvocationId InvocationId,
    HookPoint HookPoint,
    HookHandlerId HandlerId,
    Guid OperationId) : DomainEvent(SessionId, OccurredAt);

/// <summary>A lifecycle-hook invocation reached a normalized outcome.</summary>
public sealed record HookInvocationCompletedEvent(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    HookInvocationId InvocationId,
    HookPoint HookPoint,
    HookHandlerId HandlerId,
    Guid OperationId,
    HookInvocationStatus Status,
    HookDecisionKind Decision,
    string? Code) : DomainEvent(SessionId, OccurredAt);

/// <summary>An exact external repository-hook approval was granted or revoked.</summary>
public sealed record HookRepositoryApprovalChanged(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    string RepositoryIdentity,
    HookHandlerId HandlerId,
    HookConfigurationDigest ConfigurationDigest,
    bool Approved) : DomainEvent(SessionId, OccurredAt);

/// <summary>A run completed.</summary>
public sealed record RunCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    bool Succeeded) : DomainEvent(SessionId, OccurredAt);

/// <summary>Provides the stable allow-listed mapping used by durable event storage.</summary>
public static class DomainEventJson
{
    /// <summary>Gets every registered event name and concrete type.</summary>
    public static IReadOnlyDictionary<string, Type> RegisteredTypes { get; } =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["sessionCreated"] = typeof(SessionCreated),
            ["repositoryOpened"] = typeof(RepositoryOpened),
            ["solutionLoaded"] = typeof(SolutionLoaded),
            ["taskIntentRecorded"] = typeof(TaskIntentRecorded),
            ["acceptanceCriteriaRecorded"] = typeof(AcceptanceCriteriaRecorded),
            ["evidenceAdded"] = typeof(EvidenceAdded),
            ["planProposed"] = typeof(PlanProposed),
            ["planSanityCheckCompleted"] = typeof(PlanSanityCheckCompleted),
            ["planAutoApproved"] = typeof(PlanAutoApproved),
            ["planApprovalPolicyChanged"] = typeof(PlanApprovalPolicyChanged),
            ["planRevisionRequested"] = typeof(PlanRevisionRequested),
            ["contextAssembled"] = typeof(ContextAssembled),
            ["activeTurnCompactionStarted"] = typeof(ActiveTurnCompactionStarted),
            ["activeTurnCompactionCompleted"] = typeof(ActiveTurnCompactionCompleted),
            ["approvalRequested"] = typeof(ApprovalRequested),
            ["approvalGranted"] = typeof(ApprovalGranted),
            ["approvalDenied"] = typeof(ApprovalDenied),
            ["toolInvocationStarted"] = typeof(ToolInvocationStarted),
            ["toolInvocationCompleted"] = typeof(ToolInvocationCompleted),
            ["semanticCheckStarted"] = typeof(SemanticCheckStarted),
            ["semanticCheckCompleted"] = typeof(SemanticCheckCompleted),
            ["mutationProposalStarted"] = typeof(MutationProposalStarted),
            ["mutationProposalRepairAttempted"] = typeof(MutationProposalRepairAttempted),
            ["modelCorrectionAttempted"] = typeof(ModelCorrectionAttempted),
            ["preMutationAnalysisCompleted"] = typeof(PreMutationAnalysisCompleted),
            ["semanticMutationWarningObserved"] = typeof(SemanticMutationWarningObserved),
            ["mutationSetProposed"] = typeof(MutationSetProposed),
            ["mutationApplied"] = typeof(MutationApplied),
            ["mutationSetRolledBack"] = typeof(MutationSetRolledBack),
            ["buildStarted"] = typeof(BuildStarted),
            ["diagnosticObserved"] = typeof(DiagnosticObserved),
            ["testRunCompleted"] = typeof(TestRunCompleted),
            ["extensionDiscovered"] = typeof(ExtensionDiscovered),
            ["extensionActivated"] = typeof(ExtensionActivated),
            ["extensionLoadFailed"] = typeof(ExtensionLoadFailed),
            ["extensionDraining"] = typeof(ExtensionDraining),
            ["extensionUnloaded"] = typeof(ExtensionUnloaded),
            ["extensionUnloadFailed"] = typeof(ExtensionUnloadFailed),
            ["semanticConfidenceChanged"] = typeof(SemanticConfidenceChanged),
            ["semanticLoadCompleted"] = typeof(SemanticLoadCompleted),
            ["runTransitioned"] = typeof(RunTransitioned),
            ["runTransitionFailed"] = typeof(RunTransitionFailed),
            ["modelOutputObserved"] = typeof(ModelOutputObserved),
            ["modelReasoningObserved"] = typeof(ModelReasoningObserved),
            ["conversationMessageArchived"] = typeof(ConversationMessageArchived),
            ["conversationModeChanged"] = typeof(ConversationModeChanged),
            ["conversationMemoryPromoted"] = typeof(ConversationMemoryPromoted),
            ["conversationMemorySuperseded"] = typeof(ConversationMemorySuperseded),
            ["conversationMemoryInvalidated"] = typeof(ConversationMemoryInvalidated),
            ["repositoryMemoryRemembered"] = typeof(RepositoryMemoryRemembered),
            ["repositoryMemorySuperseded"] = typeof(RepositoryMemorySuperseded),
            ["repositoryMemoryValidityChanged"] = typeof(RepositoryMemoryValidityChanged),
            ["conversationSummarySnapshotReplaced"] = typeof(ConversationSummarySnapshotReplaced),
            ["executionCheckpointWritten"] = typeof(ExecutionCheckpointWritten),
            ["executionSideEffectRecorded"] = typeof(ExecutionSideEffectRecorded),
            ["executionResumeRecorded"] = typeof(ExecutionResumeRecorded),
            ["executionOutcomeRecorded"] = typeof(ExecutionOutcomeRecorded),
            ["delegationCheckpointWritten"] = typeof(DelegationCheckpointWritten),
            ["agentRunLifecycleObserved"] = typeof(AgentRunLifecycleObserved),
            ["skillCatalogRefreshed"] = typeof(SkillCatalogRefreshed),
            ["skillVerificationDecided"] = typeof(SkillVerificationDecided),
            ["skillWorkflowCheckpointWritten"] = typeof(SkillWorkflowCheckpointWritten),
            ["skillInvocationCompleted"] = typeof(SkillInvocationCompleted),
            ["hookInvocationStarted"] = typeof(HookInvocationStartedEvent),
            ["hookInvocationCompleted"] = typeof(HookInvocationCompletedEvent),
            ["hookRepositoryApprovalChanged"] = typeof(HookRepositoryApprovalChanged),
            ["runCompleted"] = typeof(RunCompleted),
        };

    private static IReadOnlyDictionary<Type, string> EventNames { get; } = RegisteredTypes
        .ToDictionary(pair => pair.Value, pair => pair.Key);

    /// <summary>Gets the stable discriminator for a concrete event.</summary>
    public static string GetDiscriminator(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return EventNames.TryGetValue(domainEvent.GetType(), out var name)
            ? name
            : throw new NotSupportedException($"Unregistered domain event type {domainEvent.GetType().FullName}.");
    }

    /// <summary>Serializes an event using its registered concrete type.</summary>
    public static string Serialize(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _ = GetDiscriminator(domainEvent);
        return JsonSerializer.Serialize(domainEvent, domainEvent.GetType());
    }

    /// <summary>Deserializes an allow-listed event with a supported schema version.</summary>
    public static IDomainEvent Deserialize(string discriminator, int schemaVersion, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (!RegisteredTypes.TryGetValue(discriminator, out var type))
        {
            throw new InvalidDataException($"Unknown domain event type '{discriminator}'.");
        }

        if (schemaVersion < 0
            || (schemaVersion > 1
                && !(schemaVersion == 2
                    && (string.Equals(discriminator, "repositoryOpened", StringComparison.Ordinal)
                        || string.Equals(discriminator, "planProposed", StringComparison.Ordinal)
                        || string.Equals(discriminator, "approvalRequested", StringComparison.Ordinal)
                        || string.Equals(discriminator, "mutationSetProposed", StringComparison.Ordinal)
                        || string.Equals(discriminator, "mutationApplied", StringComparison.Ordinal)))))
        {
            throw new NotSupportedException($"Unsupported event schema version {schemaVersion}.");
        }

        return JsonSerializer.Deserialize(json, type) as IDomainEvent
            ?? throw new InvalidDataException("Stored event payload is invalid.");
    }
}

/// <summary>Represents a live event-stream subscription.</summary>
public interface IDomainEventSubscription : IAsyncDisposable
{
}

/// <summary>Publishes every domain event to every independently buffered subscriber.</summary>
public interface IDomainEventStream : IAsyncDisposable
{
    /// <summary>Subscribes an ordered handler with bounded buffering.</summary>
    IDomainEventSubscription Subscribe(
        Func<IDomainEvent, CancellationToken, Task> handler,
        int capacity = 256);

    /// <summary>Publishes an event and waits until every current subscriber handles it.</summary>
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
