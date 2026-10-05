namespace Threadsmith.Core;

using System.Collections.ObjectModel;

/// <summary>One verifiable condition that a planned change must satisfy.</summary>
public sealed record AcceptanceCriterion(string Description, bool IsRequired = true);

/// <summary>Explicit task state used instead of transcript replay.</summary>
public sealed record TaskSpecification(
    string Intent,
    IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria,
    IReadOnlyList<string>? UserConstraints = null);

/// <summary>Declared file lifecycle operation for one planned path.</summary>
public enum PlanFileChangeKind
{
    /// <summary>Modify an existing repository file.</summary>
    Modify,

    /// <summary>Create a new repository file.</summary>
    Create,

    /// <summary>Delete an existing repository file.</summary>
    Delete,

    /// <summary>Move an existing repository file to a new path.</summary>
    Move,

    /// <summary>Rename an existing repository file to a new path.</summary>
    Rename,
}

/// <summary>One model-declared file lifecycle intent in an implementation-plan step.</summary>
public sealed record PlanFileIntent
{
    /// <summary>Declared lifecycle operation.</summary>
    public required PlanFileChangeKind Kind { get; init; }

    /// <summary>Repository-relative source or target path.</summary>
    public required string Path { get; init; }

    /// <summary>Repository-relative destination path for move or rename operations.</summary>
    public string? DestinationPath { get; init; }

    /// <summary>Returns every repository-relative path governed by this intent.</summary>
    public IReadOnlyList<string> GetAffectedPaths()
    {
        return string.IsNullOrWhiteSpace(DestinationPath)
            ? [Path]
            : [Path, DestinationPath];
    }
}

/// <summary>One ordered, reviewable implementation-plan step.</summary>
public sealed record ImplementationPlanStep
{
    /// <summary>Stable step identity used by later mutation and validation stages.</summary>
    public required StepId StepId { get; init; }

    /// <summary>Short action-oriented title.</summary>
    public required string Title { get; init; }

    /// <summary>Bounded description of the intended change.</summary>
    public required string Description { get; init; }

    /// <summary>Structured repository file intents expected to be affected.</summary>
    public IReadOnlyList<PlanFileIntent> FileIntents { get; init; } = [];

    /// <summary>Observable result expected after the step.</summary>
    public required string ExpectedOutcome { get; init; }

    /// <summary>Validation expectations consumed by later milestones.</summary>
    public IReadOnlyList<string> Validation { get; init; } = [];

    /// <summary>Returns every repository-relative path governed by this step.</summary>
    public IReadOnlyList<string> GetAffectedPaths()
    {
        return [.. FileIntents.SelectMany(intent => intent.GetAffectedPaths())];
    }
}

/// <summary>Versioned structured implementation plan proposed by a model or user revision.</summary>
public sealed record ImplementationPlan
{
    /// <summary>Supported plan contract version.</summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>Revision number within the owning run.</summary>
    public int Revision { get; init; } = 1;

    /// <summary>Concise plan summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Ordered implementation steps.</summary>
    public IReadOnlyList<ImplementationPlanStep> Steps { get; init; } = [];

    /// <summary>Cross-cutting risks that require review.</summary>
    public IReadOnlyList<string> Risks { get; init; } = [];

    /// <summary>Questions that remain unresolved before mutation.</summary>
    public IReadOnlyList<string> OutstandingQuestions { get; init; } = [];
}

/// <summary>Current review state for a proposed plan.</summary>
public enum PlanReviewStatus
{
    /// <summary>No plan has been proposed.</summary>
    None,

    /// <summary>A plan is waiting for a user decision.</summary>
    Pending,

    /// <summary>The plan was approved.</summary>
    Approved,

    /// <summary>The plan was rejected.</summary>
    Rejected,

    /// <summary>The user requested a revised proposal.</summary>
    RevisionRequested,
}

/// <summary>Versioned prompt asset referenced by a model execution record.</summary>
public sealed record PromptAssetReference(
    string Id,
    string Version,
    string Source,
    int Position,
    int CharacterCount);

/// <summary>One evidence-selection decision visible in the context inspector.</summary>
public sealed record ContextEvidenceProjection(
    EvidenceId EvidenceId,
    string Kind,
    bool Included,
    string Rationale,
    int EstimatedTokens,
    bool IsStale);

/// <summary>One conversation-history or governed-memory inclusion decision.</summary>
public sealed record ConversationContextItemProjection
{
    /// <summary>Stable message or memory identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Message role or governed-memory category.</summary>
    public required string Kind { get; init; }

    /// <summary>Whether the item entered the assembled request.</summary>
    public required bool Included { get; init; }

    /// <summary>Host-owned inclusion, omission, or reduction rationale.</summary>
    public required string Rationale { get; init; }

    /// <summary>Estimated tokens charged to the category.</summary>
    public required int EstimatedTokens { get; init; }

    /// <summary>Originating message identifiers for governed memory.</summary>
    public IReadOnlyList<ConversationMessageId> SourceMessageIds { get; init; } = [];

    /// <summary>Originating run identifiers.</summary>
    public IReadOnlyList<RunId> SourceRunIds { get; init; } = [];

    /// <summary>Originating evidence identifiers.</summary>
    public IReadOnlyList<EvidenceId> SourceEvidenceIds { get; init; } = [];

    /// <summary>Deterministic retrieval score when applicable.</summary>
    public double? Score { get; init; }
}

/// <summary>One repository-scoped memory inclusion or omission decision.</summary>
public sealed record RepositoryMemoryContextItemProjection
{
    /// <summary>Stable repository-memory identifier.</summary>
    public required RepositoryMemoryId Id { get; init; }

    /// <summary>Explicit origin supplied by the host.</summary>
    public RepositoryMemoryOrigin Origin { get; init; }

    /// <summary>Selection behavior of the memory.</summary>
    public RepositoryMemoryType MemoryType { get; init; } = RepositoryMemoryType.Situational;

    /// <summary>Content revision selected before final dispatch.</summary>
    public long Revision { get; init; }

    /// <summary>Qualified lexical branch rank when present.</summary>
    public int? LexicalRank { get; init; }

    /// <summary>Qualified semantic branch rank when present.</summary>
    public int? SemanticRank { get; init; }

    /// <summary>Compatible-space cosine score for diagnostics only.</summary>
    public double? CosineSimilarity { get; init; }

    /// <summary>Optional raw cross-encoder relevance logit for inspection only; higher is better.</summary>
    public double? CrossEncoderScore { get; init; }

    /// <summary>Whether the item entered the assembled request.</summary>
    public required bool Included { get; init; }

    /// <summary>Host-owned inclusion or omission rationale.</summary>
    public required string Rationale { get; init; }

    /// <summary>Estimated tokens charged to repository-memory context.</summary>
    public required int EstimatedTokens { get; init; }

    /// <summary>Deterministic retrieval score when eligible.</summary>
    public double? Score { get; init; }
}

/// <summary>Closed active-turn pressure and compaction assessment states.</summary>
public enum ActiveTurnCompactionInspectionStatus
{
    /// <summary>The active-turn feature is disabled by host policy.</summary>
    Disabled,

    /// <summary>The complete request is below the pressure target.</summary>
    BelowPressure,

    /// <summary>Pressure was reached but no complete delivered prefix is eligible.</summary>
    NoEligiblePrefix,

    /// <summary>A prior failure is under bounded round backoff.</summary>
    Backoff,

    /// <summary>A validated summary replaced an eligible prefix.</summary>
    Completed,

    /// <summary>Candidate validation rejected the replacement.</summary>
    ValidationRejected,

    /// <summary>Candidate generation failed within its bounded call budget.</summary>
    ProviderFailure,

    /// <summary>Cancellation retained the original continuation.</summary>
    Cancelled,

    /// <summary>A valid candidate did not provide the configured minimum reduction.</summary>
    InsufficientSavings,

    /// <summary>The emergency compatibility reducer was required.</summary>
    EmergencyReduction,

    /// <summary>The unchanged request could not fit without reducing a never-delivered group.</summary>
    CapacityExceeded,

    /// <summary>The execution budget could not admit a summary-and-continuation path.</summary>
    BudgetAdmissionRejected,

    /// <summary>Exact source coverage resolved pressure without a summarizer call.</summary>
    DeterministicReduction,
}

/// <summary>Reason the host assessed active-turn compaction.</summary>
public enum ActiveTurnCompactionPressureReason
{
    /// <summary>No pressure was present.</summary>
    None,

    /// <summary>The canonical request reached the configured context threshold.</summary>
    Context,

    /// <summary>The remaining execution budget could not admit the ordinary request.</summary>
    ExecutionBudget,

    /// <summary>Both context and execution-budget pressure were present.</summary>
    ContextAndExecutionBudget,
}

/// <summary>Bounded active-turn pressure and compaction inspection metadata.</summary>
public sealed record ActiveTurnCompactionInspectionProjection
{
    /// <summary>Monotonic pre-sampling assessment sequence.</summary>
    public required int AssessmentSequence { get; init; }

    /// <summary>Closed assessment outcome.</summary>
    public required ActiveTurnCompactionInspectionStatus Status { get; init; }

    /// <summary>Objective reason that caused this assessment.</summary>
    public ActiveTurnCompactionPressureReason PressureReason { get; init; }

    /// <summary>Ordinary admission dimensions that caused execution-budget pressure.</summary>
    public BudgetExhaustionDimension BudgetPressureDimensions { get; init; }

    /// <summary>Combined summary-and-continuation tokens checked for the latest candidate attempt.</summary>
    public long? CombinedAdmissionTokens { get; init; }

    /// <summary>Combined summary-and-continuation calls checked for the latest candidate attempt.</summary>
    public int? CombinedAdmissionCalls { get; init; }

    /// <summary>Known combined admission-cost lower bound.</summary>
    public decimal? CombinedAdmissionCost { get; init; }

    /// <summary>Whether the combined cost includes reviewed pricing for every request.</summary>
    public bool CombinedAdmissionCostIsComplete { get; init; }

    /// <summary>Known combined wall-clock lower bound.</summary>
    public TimeSpan? CombinedAdmissionWallClock { get; init; }

    /// <summary>Whether the combined duration includes a reliable bound for every request.</summary>
    public bool CombinedAdmissionWallClockIsComplete { get; init; }

    /// <summary>Number of prepared candidate attempts checked in the latest assessment.</summary>
    public int CandidateAttemptCount { get; init; }

    /// <summary>Exact source ranges considered by deterministic projection.</summary>
    public int SourceCandidateRangeCount { get; init; }

    /// <summary>Exact source ranges removed from the final request.</summary>
    public int SourceRemovedRangeCount { get; init; }

    /// <summary>Exact source ranges retained in the final request.</summary>
    public int SourceRetainedRangeCount { get; init; }

    /// <summary>Tool results that did not expose supported exact source metadata.</summary>
    public int SourceOpaqueResultCount { get; init; }

    /// <summary>Source characters replaced before receipt and recovery-schema overhead.</summary>
    public int SourceReclaimedCharacters { get; init; }

    /// <summary>Whether exact projection resolved pressure without invoking the summarizer.</summary>
    public bool SummaryAvoidedBySourceProjection { get; init; }

    /// <summary>Provider-prepared input tokens for the latest summary attempt.</summary>
    public int? SummaryPreparedInputTokens { get; init; }

    /// <summary>Reserved output tokens for the latest summary attempt.</summary>
    public int? SummaryAdmissionOutputTokens { get; init; }

    /// <summary>Combined admission dimensions that rejected the latest candidate attempt.</summary>
    public BudgetExhaustionDimension AdmissionRejectedDimensions { get; init; }

    /// <summary>Canonical complete-request input estimate before replacement.</summary>
    public required int BeforeInputTokens { get; init; }

    /// <summary>Canonical complete-request input estimate after replacement, when attempted.</summary>
    public int? AfterInputTokens { get; init; }

    /// <summary>Selected-model emergency input boundary.</summary>
    public required int MaximumInputTokens { get; init; }

    /// <summary>Configured operational trigger below the emergency boundary.</summary>
    public required int PressureTargetTokens { get; init; }

    /// <summary>Output reserve included by pressure assessment.</summary>
    public required int OutputReserveTokens { get; init; }

    /// <summary>Configured newest-raw retention target, independent of request pressure.</summary>
    public required int ConfiguredRetentionTargetTokens { get; init; }

    /// <summary>Newest-raw target used for prefix selection; ordinary compaction uses the configured target unchanged.</summary>
    public required int EffectiveRetentionTargetTokens { get; init; }

    /// <summary>Complete delivered groups eligible for a prefix cut.</summary>
    public required int EligibleGroupCount { get; init; }

    /// <summary>Groups replaced by the activated summary.</summary>
    public required int CompactedGroupCount { get; init; }

    /// <summary>Raw recent groups retained exactly.</summary>
    public required int RetainedGroupCount { get; init; }

    /// <summary>Estimated tokens in the exact retained raw groups.</summary>
    public required int RetainedGroupTokens { get; init; }

    /// <summary>Active cumulative summary version.</summary>
    public required int SummaryVersion { get; init; }

    /// <summary>Cumulative oldest prior-summary items pruned under active bounds.</summary>
    public required int PrunedPriorItemCount { get; init; }

    /// <summary>Provider-neutral generation that fences rewritten request history.</summary>
    public required long HistoryRewriteGeneration { get; init; }

    /// <summary>Active summary hash without summary content.</summary>
    public string? SummaryContentHash { get; init; }

    /// <summary>Candidate profile identity without provider credentials.</summary>
    public Guid? CandidateProfileId { get; init; }

    /// <summary>First compacted group sequence for the latest successful cut.</summary>
    public long? CompactedFromGroupSequence { get; init; }

    /// <summary>Last compacted group sequence for the latest successful cut.</summary>
    public long? CompactedThroughGroupSequence { get; init; }

    /// <summary>Remaining bounded backoff rounds.</summary>
    public required int BackoffRoundsRemaining { get; init; }

    /// <summary>Bounded host rationale without summary or tool content.</summary>
    public required string Rationale { get; init; }
}

/// <summary>Inspectable source-frontier counts for request-local code-explore deduplication.</summary>
public sealed record VisibleSourceFrontierInspectionProjection(
    int EntryCount,
    int RangeCount,
    int SourceCharacters,
    long FrontierGeneration,
    string Rationale);

/// <summary>Inspectable record of one governed context assembly.</summary>
public sealed record ContextInspectionProjection
{
    /// <summary>Latest actual request accounting; transient and never part of durable assembly events.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ContextUsageSnapshot? RequestUsage { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Phase whose policy assembled the request.</summary>
    public RunPhase Phase { get; init; }

    /// <summary>Total estimated request tokens.</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Configured context budget.</summary>
    public int TokenBudget { get; init; }

    /// <summary>Token estimates keyed by governed category.</summary>
    public IReadOnlyDictionary<string, int> TokensByCategory { get; init; } =
        new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Included and omitted evidence with rationale.</summary>
    public IReadOnlyList<ContextEvidenceProjection> Evidence { get; init; } = [];

    /// <summary>Prompt assets and append segments referenced by the request.</summary>
    public IReadOnlyList<PromptAssetReference> PromptAssets { get; init; } = [];

    /// <summary>Resolved configured model, when model profiles are configured.</summary>
    public ModelProfileId? ModelProfileId { get; init; }

    /// <summary>Applied and ignored model-selection rationale.</summary>
    public IReadOnlyList<string> ModelRationale { get; init; } = [];

    /// <summary>Reduction actions performed while fitting the budget.</summary>
    public IReadOnlyList<string> Reductions { get; init; } = [];

    /// <summary>Effective conversation mode for this request.</summary>
    public ConversationContextMode ConversationMode { get; init; } = ConversationContextMode.ConversationAware;

    /// <summary>Configuration or session layer supplying the effective mode.</summary>
    public string ConversationModeSource { get; init; } = "compiled-default";

    /// <summary>Authoritative current archived message.</summary>
    public ConversationMessageId? CurrentMessageId { get; init; }

    /// <summary>Active structured summary version.</summary>
    public long? ConversationSummaryVersion { get; init; }

    /// <summary>Last archived sequence represented by the active summary.</summary>
    public long? CompactedThroughMessageSequence { get; init; }

    /// <summary>Recent-message, summary, retrieval, stale, superseded, and mode decisions.</summary>
    public IReadOnlyList<ConversationContextItemProjection> ConversationItems { get; init; } = [];

    /// <summary>Repository-scoped memory inclusion, omission, staleness, and pressure decisions.</summary>
    public IReadOnlyList<RepositoryMemoryContextItemProjection> RepositoryMemoryItems { get; init; } = [];

    /// <summary>Branch availability and search-window omissions before context admission.</summary>
    public RepositoryMemorySearchDetails? RepositoryMemorySearch { get; init; }

    /// <summary>Estimated percentage of the selected model context window used.</summary>
    public double ContextPressurePercent { get; init; }

    /// <summary>Latest pre-sampling active-turn continuation assessment.</summary>
    public ActiveTurnCompactionInspectionProjection? ActiveTurnCompaction { get; init; }

    /// <summary>Whether compaction should run at the next safe turn boundary.</summary>
    public bool CompactionRecommended { get; init; }

    /// <summary>Request-local visible source frontier used for conservative code-explore deduplication.</summary>
    public VisibleSourceFrontierInspectionProjection? VisibleSourceFrontier { get; init; }

    /// <summary>Most recent actual provider-submission receipt outcome; absent for assembly-only previews.</summary>
    public RepositoryMemoryDispatchInspection? RepositoryMemoryDispatch { get; init; }

    /// <summary>Inspectable reason for the next compaction decision.</summary>
    public string? CompactionRationale { get; init; }

    /// <summary>Structured request layout version.</summary>
    public int RequestLayoutVersion { get; init; }

    /// <summary>Stable cache family for this phase and generation.</summary>
    public string? CacheFamily { get; init; }

    /// <summary>Digest of the exact stable message prefix.</summary>
    public string? StablePrefixDigest { get; init; }

    /// <summary>Digest of the canonical eligible tool inventory.</summary>
    public string? ToolInventoryDigest { get; init; }

    /// <summary>Digest of the hierarchical repository instruction bundle.</summary>
    public string? InstructionBundleDigest { get; init; }

    /// <summary>Estimated logical unique content tokens.</summary>
    public int LogicalTokens { get; init; }

    /// <summary>Estimated provider-wire input tokens including native tool schemas and framing.</summary>
    public int WireInputTokens { get; init; }

    /// <summary>Estimated stable-prefix wire tokens.</summary>
    public int StablePrefixTokens { get; init; }

    /// <summary>Estimated native tool-schema tokens.</summary>
    public int NativeToolTokens { get; init; }

    /// <summary>Estimated textual fallback tool-schema tokens.</summary>
    public int TextToolTokens { get; init; }

    /// <summary>Estimated provider framing tokens.</summary>
    public int FramingTokens { get; init; }

    /// <summary>Estimated exact request-owned provider-instruction tokens.</summary>
    public int ProviderInstructionTokens { get; init; }

    /// <summary>Whether native or textual tool transport is used.</summary>
    public string ToolTransportMode { get; init; } = "unknown";
}

/// <summary>Approves the pending plan for a run.</summary>
public sealed record ApprovePlanCommand(SessionId SessionId, RunId RunId) : ICommand<bool>;

/// <summary>Rejects the pending plan for a run.</summary>
public sealed record RejectPlanCommand(SessionId SessionId, RunId RunId, string Reason) : ICommand<bool>;

/// <summary>Requests a new plan proposal using governed revision instructions.</summary>
public sealed record RevisePlanCommand(
    SessionId SessionId,
    RunId RunId,
    string RevisionInstructions) : ICommand<bool>;
