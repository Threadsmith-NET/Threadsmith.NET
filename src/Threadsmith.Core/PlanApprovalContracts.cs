namespace Threadsmith.Core;

/// <summary>Controls whether structured implementation plans require explicit user approval.</summary>
public enum PlanApprovalPolicy
{
    /// <summary>Requires approval for every valid implementation plan.</summary>
    ReviewAll,

    /// <summary>Automatically approves low-risk valid plans and prompts for riskier plans.</summary>
    ReviewRisky,

    /// <summary>Automatically approves low- and moderate-risk valid plans for the current session.</summary>
    TrustSession,

    /// <summary>Persistently approves low- and moderate-risk valid plans for the exact repository identity.</summary>
    AlwaysTrustRepo,

    /// <summary>Automatically approves every valid non-blocked plan after explicit trusted selection.</summary>
    AutoApproveAllValid,
}

/// <summary>Host-owned risk classification assigned after plan sanity checks.</summary>
public enum PlanRiskClassification
{
    /// <summary>Small, exact, and ordinary source, test, or documentation scope.</summary>
    Low,

    /// <summary>Broader or partially lifecycle/configuration-related scope that remains mechanically checkable.</summary>
    Moderate,

    /// <summary>Broad, destructive, dependency, generated, binary, secret-adjacent, or policy-sensitive scope.</summary>
    High,

    /// <summary>A hard guardrail prevents the plan from being approved or shown for ordinary review.</summary>
    Blocked,
}

/// <summary>Decision source for a plan after sanity checks and policy evaluation.</summary>
public enum PlanApprovalDecisionKind
{
    /// <summary>The plan must be shown to a user for explicit approval.</summary>
    RequiresReview,

    /// <summary>The plan is approved by host-owned policy after passing sanity checks.</summary>
    AutoApproved,

    /// <summary>The plan is denied by a hard host-owned guardrail.</summary>
    Blocked,
}

/// <summary>One kind of issue detected by cheap repository plan sanity checks.</summary>
public enum PlanSanityIssueKind
{
    /// <summary>The plan did not declare concrete structured file intents.</summary>
    EmptyFileIntents,

    /// <summary>A path is rooted, escapes the repository, or is syntactically invalid.</summary>
    InvalidPath,

    /// <summary>The plan targets Git metadata.</summary>
    GitMetadataPath,

    /// <summary>The plan targets a prohibited or secret-bearing path.</summary>
    ProtectedPath,

    /// <summary>A bare or glob-like path is ambiguous.</summary>
    AmbiguousPath,

    /// <summary>An edit-like step targets a file absent from the current baseline.</summary>
    MissingExistingFile,

    /// <summary>A create-like step targets a file that already exists.</summary>
    CreateTargetExists,

    /// <summary>The plan touches generated outputs or generated source.</summary>
    GeneratedPath,

    /// <summary>The plan touches binary or non-text assets.</summary>
    BinaryPath,

    /// <summary>The plan includes create, delete, move, or rename lifecycle scope.</summary>
    LifecycleChange,

    /// <summary>The plan touches project, package, build, or application configuration.</summary>
    ConfigurationOrDependencyChange,

    /// <summary>The plan deletes or removes test coverage.</summary>
    TestDeletion,

    /// <summary>The plan exceeds bounded file-count or path-byte limits.</summary>
    ScopeLimitExceeded,

    /// <summary>Required host sanity evidence was unavailable, so policy auto-approval is forbidden.</summary>
    EvidenceUnavailable,
}

/// <summary>One bounded sanitized sanity-check issue for a proposed plan.</summary>
public sealed record PlanSanityIssue
{
    /// <summary>Issue kind.</summary>
    public required PlanSanityIssueKind Kind { get; init; }

    /// <summary>Optional normalized repository-relative path associated with the issue.</summary>
    public string? RelativePath { get; init; }

    /// <summary>Whether a model plan revision may repair this issue.</summary>
    public bool IsRepairable { get; init; }

    /// <summary>Whether this issue blocks presentation or auto-approval until repaired.</summary>
    public bool IsBlocking { get; init; }

    /// <summary>Bounded host-owned explanation safe for transcript and durable event state.</summary>
    public required string Message { get; init; }
}

/// <summary>Request for cheap host-owned plan sanity checks.</summary>
public sealed record PlanSanityCheckRequest
{
    /// <summary>Structured plan to check.</summary>
    public required ImplementationPlan Plan { get; init; }

    /// <summary>Repository root used for path confinement.</summary>
    public required string RepositoryRoot { get; init; }

    /// <summary>Current immutable workspace baseline when available.</summary>
    public WorkspaceBaseline? Baseline { get; init; }

    /// <summary>Current repository trust level.</summary>
    public RepositoryTrustLevel TrustLevel { get; init; } = RepositoryTrustLevel.UntrustedInspection;

    /// <summary>Configured prohibited path globs or prefixes.</summary>
    public IReadOnlyList<string> ProhibitedPaths { get; init; } = [];

    /// <summary>Maximum declared affected path entries accepted for one plan before de-duplication.</summary>
    public int MaximumAffectedPaths { get; init; } = 100;

    /// <summary>Maximum total affected path characters retained for one plan.</summary>
    public int MaximumPathBytes { get; init; } = 16 * 1024;
}

/// <summary>Result of cheap host-owned sanity checks for one proposed plan.</summary>
public sealed record PlanSanityCheckResult
{
    /// <summary>Risk classification computed from structured plan scope and issues.</summary>
    public PlanRiskClassification Risk { get; init; }

    /// <summary>Bounded sanitized issues found by the checker.</summary>
    public IReadOnlyList<PlanSanityIssue> Issues { get; init; } = [];

    /// <summary>Distinct normalized affected paths retained by the checker.</summary>
    public IReadOnlyList<string> NormalizedAffectedPaths { get; init; } = [];

    /// <summary>Total affected path entries declared by the plan before de-duplication.</summary>
    public int DeclaredAffectedPathCount { get; init; }

    /// <summary>Whether any blocking issue may be repaired by a model plan revision.</summary>
    public bool HasRepairableBlockingIssues => Issues.Any(issue => issue.IsBlocking && issue.IsRepairable);

    /// <summary>Whether any hard blocking issue must fail closed.</summary>
    public bool HasNonRepairableBlockingIssues => Issues.Any(issue => issue.IsBlocking && !issue.IsRepairable);

    /// <summary>Whether the plan passed hard sanity gates.</summary>
    public bool Passed => Risk != PlanRiskClassification.Blocked
        && !HasRepairableBlockingIssues
        && !HasNonRepairableBlockingIssues;
}

/// <summary>Host-owned approval decision for a sanity-checked plan.</summary>
public sealed record PlanApprovalDecision
{
    /// <summary>Decision kind.</summary>
    public required PlanApprovalDecisionKind Kind { get; init; }

    /// <summary>Effective policy used for the decision.</summary>
    public required PlanApprovalPolicy Policy { get; init; }

    /// <summary>Risk classification used for the decision.</summary>
    public required PlanRiskClassification Risk { get; init; }

    /// <summary>Concise safe explanation.</summary>
    public required string Reason { get; init; }
}

/// <summary>Checks structured plans before approval or policy auto-approval.</summary>
public interface IPlanSanityChecker
{
    /// <summary>Runs cheap bounded repository sanity checks without mutation, build, restore, or process execution.</summary>
    Task<PlanSanityCheckResult> CheckAsync(
        PlanSanityCheckRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Determines whether sanity-checked plans require manual approval.</summary>
public interface IPlanApprovalPolicy
{
    /// <summary>Gets the effective plan approval policy for the current process session.</summary>
    PlanApprovalPolicy CurrentPolicy { get; }

    /// <summary>Rebinds repository-scoped policy state and persistence to the active repository.</summary>
    Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default);

    /// <summary>Changes the session policy and persists or revokes repository-wide trust when required.</summary>
    Task SetPolicyAsync(
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>Computes the policy decision for a sanity-checked plan.</summary>
    PlanApprovalDecision Decide(PlanSanityCheckResult result, RepositoryTrustLevel trustLevel);
}

/// <summary>Gets the current plan approval policy.</summary>
public sealed record GetPlanApprovalPolicyCommand() : ICommand<PlanApprovalPolicy>;

/// <summary>Sets the current plan approval policy.</summary>
public sealed record SetPlanApprovalPolicyCommand(
    PlanApprovalPolicy Policy,
    SessionId? SessionId = null,
    string Scope = "session") : ICommand<PlanApprovalPolicy>;
