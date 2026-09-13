namespace Threadsmith.Core;

/// <summary>Private host provenance that opts one assignment into focused completion validation.</summary>
public sealed record FocusedReviewBinding(
    SkillInvocationId InvocationId,
    int Generation,
    string RecipeDigest,
    SkillPackageIdentity Package,
    string ProcedureId,
    string InstructionDigest,
    string SchemaDigest,
    string SnapshotIdentity,
    AgentRole Role,
    int ContractVersion = 1);

/// <summary>One frozen textual source; original bytes are identified independently from sanitized content.</summary>
public sealed record FocusedReviewFile(
    string Path,
    string Digest,
    string Content,
    bool InScope,
    IReadOnlyList<FocusedReviewRange> ChangedRanges,
    string? BaselineContent = null,
    bool Deleted = false);

/// <summary>Inclusive one-based source interval.</summary>
public sealed record FocusedReviewRange(int Start, int End);

/// <summary>One explicit acceptance criterion, retained in document order.</summary>
public sealed record FocusedReviewCriterion(string Id, string Text, int Line, bool RequiresExecution);

/// <summary>Frozen requirements evidence with an explicit source base.</summary>
public sealed record FocusedReviewRequirements(
    string Source,
    string Path,
    string Digest,
    string Content,
    IReadOnlyList<FocusedReviewCriterion> Criteria);

/// <summary>Immutable review evidence and acquisition provenance, shared by all specialists.</summary>
public sealed record FocusedReviewTarget
{
    /// <summary>Public source mode.</summary>
    public required string Mode { get; init; }

    /// <summary>Repository owning report delivery, independent of the reviewed target.</summary>
    public string? InvokingRepository { get; init; }

    /// <summary>Credential-free reviewed repository identity.</summary>
    public required string Repository { get; init; }

    /// <summary>Captured branch, if any.</summary>
    public string? Branch { get; init; }

    /// <summary>Captured immutable HEAD commit.</summary>
    public required string Revision { get; init; }

    /// <summary>Resolved comparison branch, when supplied or inferred from local metadata.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Immutable merge base; null means snapshot audit.</summary>
    public string? MergeBase { get; init; }

    /// <summary>Content identity covering scope, revisions, files and exclusions.</summary>
    public required string Identity { get; init; }

    /// <summary>Explicit user review objective, never host authority.</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>Frozen textual files available to the confined reader.</summary>
    public IReadOnlyList<FocusedReviewFile> Files { get; init; } = [];

    /// <summary>Honest omitted-scope reasons.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>Optional complete criterion inventory.</summary>
    public FocusedReviewRequirements? Requirements { get; init; }
}

/// <summary>Runtime-only verified private procedure and its completion validator; never persisted as a public result.</summary>
public interface IFocusedReviewCompletionPolicy
{
    /// <summary>Exact immutable binding expected by the runner.</summary>
    FocusedReviewBinding Binding { get; }

    /// <summary>Verified private instruction content.</summary>
    string Instructions { get; }

    /// <summary>Pinned safe native output schema.</summary>
    string OutputSchema { get; }

    /// <summary>Maximum same-conversation format corrections; zero disables format retry.</summary>
    int MaximumCorrections { get; }

    /// <summary>Charges a focused model turn against the declared skill allocation.</summary>
    void AdmitModelTurn();

    /// <summary>Charges focused tool requests against the declared skill allocation.</summary>
    void AdmitToolCalls(int count);

    /// <summary>Records a delivered frozen read for citation validation.</summary>
    void RecordRead(string path, int startLine, int endLine);

    /// <summary>Validates and returns a canonical specialist value, or rejects invalid output.</summary>
    string Validate(AgentAssignment assignment, string response);
}

/// <summary>Execution boundary for the exact verified public review workflow.</summary>
public interface IFocusedReviewExecutor
{
    /// <summary>Freezes host launch provenance and role assignments under existing authority and budgets.</summary>
    Task<IReadOnlyList<DelegationPlan>> PrepareAsync(
        SkillInvocationPlan invocation,
        FocusedReviewTarget target,
        IReadOnlyList<IFocusedReviewCompletionPolicy> procedures,
        CancellationToken cancellationToken = default);

    /// <summary>Runs one previously persisted batch or reads durable outcomes without restarting inference.</summary>
    Task<IReadOnlyList<AgentRunOutcome>> ExecuteAsync(
        SkillInvocationPlan invocation,
        DelegationPlan plan,
        FocusedReviewTarget target,
        IReadOnlyList<IFocusedReviewCompletionPolicy> procedures,
        bool restoreOnly,
        CancellationToken cancellationToken = default);
}

/// <summary>Narrow optional workflow action adapter. Unmatched packages retain waiting proposal semantics.</summary>
public interface ISkillReviewActionHandler
{
    /// <summary>Host-owned defaults for an explicit public review, separate from ordinary native procedure defaults.</summary>
    SkillBudget DefaultBudget { get; }

    /// <summary>Checks exact package, deployment and step identity without loading private content into discovery.</summary>
    bool Handles(SkillCatalogCandidate candidate, SkillWorkflowStep step);

    /// <summary>Executes or reconciles the verified review action and returns validated public output only.</summary>
    Task<string> ExecuteAsync(
        SkillCatalogCandidate candidate,
        SkillInvocationPlan plan,
        SkillWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
}

/// <summary>Host result marker carrying a canonical review projection to the ordinary output pipeline.</summary>
public interface IFocusedReviewToolResult
{
    /// <summary>Canonical report or saved receipt from an exactly bound review action.</summary>
    FocusedReviewDelivery? ReviewDelivery { get; }
}

/// <summary>Public canonical review output; unrelated skill rendering does not use this contract.</summary>
public sealed record FocusedReviewDelivery(
    string Status,
    string DeliveryMode,
    string ContentDigest,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SavedPath,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Markdown,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DeliveryError,
    int ReportFormatVersion = 1);
