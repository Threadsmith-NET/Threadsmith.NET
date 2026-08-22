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

    /// <summary>Repository-memory category.</summary>
    public required RepositoryMemoryKind Kind { get; init; }

    /// <summary>Authority supporting the memory item.</summary>
    public required RepositoryMemoryAuthority Authority { get; init; }

    /// <summary>Current validity state.</summary>
    public required RepositoryMemoryValidity Validity { get; init; }

    /// <summary>Whether the item entered the assembled request.</summary>
    public required bool Included { get; init; }

    /// <summary>Host-owned inclusion or omission rationale.</summary>
    public required string Rationale { get; init; }

    /// <summary>Estimated tokens charged to repository-memory context.</summary>
    public required int EstimatedTokens { get; init; }

    /// <summary>Deterministic retrieval score when eligible.</summary>
    public double? Score { get; init; }
}

/// <summary>Inspectable record of one governed context assembly.</summary>
public sealed record ContextInspectionProjection
{
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

    /// <summary>Estimated percentage of the selected model context window used.</summary>
    public double ContextPressurePercent { get; init; }

    /// <summary>Whether compaction should run at the next safe turn boundary.</summary>
    public bool CompactionRecommended { get; init; }

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
