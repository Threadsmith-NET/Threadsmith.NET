namespace Threadsmith.Core;

/// <summary>Roles the host may assign to one fixed-depth child run.</summary>
public enum AgentRole
{
    /// <summary>Collects read-only repository evidence.</summary>
    Explorer,

    /// <summary>Implements approved non-overlapping plan steps in an isolated worktree.</summary>
    Implementer,

    /// <summary>Reviews security and trust boundaries.</summary>
    SecurityReviewer,

    /// <summary>Reviews observable behavior and test adequacy.</summary>
    TestReviewer,

    /// <summary>Reviews resource and performance risks.</summary>
    PerformanceReviewer,

    /// <summary>Reviews architecture and dependency boundaries.</summary>
    ArchitectureReviewer,
}

/// <summary>Repository authority granted to a child run.</summary>
public enum AgentRunMode
{
    /// <summary>Reads one immutable parent baseline.</summary>
    ReadOnlyBaseline,

    /// <summary>May propose assignment-scoped mutations in a managed worktree.</summary>
    IsolatedWorktreeMutation,

    /// <summary>Reads immutable change and evidence artifacts for review.</summary>
    ReadOnlyReview,
}

/// <summary>Host action after one child fails.</summary>
public enum AgentFailurePolicy
{
    /// <summary>Retain the failure and continue independent siblings.</summary>
    ContinueAndReport,

    /// <summary>Cancel only assignments that depend on the failed child.</summary>
    CancelDependents,

    /// <summary>Cancel the complete delegation.</summary>
    FailDelegation,
}

/// <summary>Terminal child-run state.</summary>
public enum AgentRunStatus
{
    /// <summary>The assignment is waiting for bounded admission.</summary>
    Queued,

    /// <summary>The assignment is active.</summary>
    Running,

    /// <summary>The assignment completed with a valid structured result.</summary>
    Completed,

    /// <summary>The assignment failed.</summary>
    Failed,

    /// <summary>The assignment was cancelled.</summary>
    Cancelled,

    /// <summary>The result belongs to an obsolete attempt or generation.</summary>
    Discarded,
}

/// <summary>Durable delegation lifecycle boundary.</summary>
public enum DelegationCheckpointPhase
{
    /// <summary>The validated delegation has been accepted.</summary>
    Accepted,

    /// <summary>Assignments have been queued.</summary>
    ChildrenQueued,

    /// <summary>At least one child is running.</summary>
    ChildrenRunning,

    /// <summary>Read-only findings have joined.</summary>
    ResearchJoined,

    /// <summary>Worker worktrees and change sets are frozen.</summary>
    WorkersFrozen,

    /// <summary>Independent reviews have joined.</summary>
    ReviewsJoined,

    /// <summary>The parent is waiting for an integration decision.</summary>
    IntegrationPending,

    /// <summary>Selected changes have been staged in the parent workspace.</summary>
    ParentStaged,

    /// <summary>Aggregate validation is active.</summary>
    AggregateValidation,

    /// <summary>The delegation completed.</summary>
    Completed,

    /// <summary>The delegation failed.</summary>
    Failed,

    /// <summary>The delegation was cancelled.</summary>
    Cancelled,
}

/// <summary>Normalized assignment ownership used for overlap and confinement decisions.</summary>
public sealed record AgentAssignmentScope
{
    /// <summary>Repository-relative files owned by the assignment.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Repository-relative directories owned by the assignment.</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];

    /// <summary>Stable project paths affected by the assignment.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>Semantic symbol identities owned by the assignment.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>Shared mutable surfaces requiring exclusive ownership.</summary>
    public IReadOnlyList<string> SharedSurfaces { get; init; } = [];

    /// <summary>Whether semantic/path expansion is sufficiently confident for parallel mutation.</summary>
    public bool IsOwnershipProven { get; init; }
}

/// <summary>Hierarchical limits reserved for one child.</summary>
public sealed record AgentResourceBudget
{
    /// <summary>Creates a usage-metering policy with no cumulative quota enforcement.</summary>
    public static AgentResourceBudget CreateTelemetryOnly(TimeSpan wallTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wallTime, TimeSpan.Zero);
        return new AgentResourceBudget
        {
            EnforceLimits = false,
            ModelTokens = 0,
            ToolCalls = 0,
            EvidenceItems = 0,
            Files = 0,
            Bytes = 0,
            Mutations = 0,
            Processes = 0,
            Builds = 0,
            Tests = 0,
            Corrections = 0,
            WallTime = wallTime,
        };
    }

    /// <summary>Aggregates child policies without converting telemetry-only usage into quotas.</summary>
    public static AgentResourceBudget Aggregate(IReadOnlyList<AgentResourceBudget> budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentOutOfRangeException.ThrowIfZero(budgets.Count);
        if (budgets.Any(item => item is null))
        {
            throw new ArgumentException("Resource policies cannot contain null entries.", nameof(budgets));
        }

        var wallTime = TimeSpan.FromTicks(checked(budgets.Sum(item => item.WallTime.Ticks)));
        if (budgets.Any(item => !item.EnforceLimits))
        {
            return CreateTelemetryOnly(wallTime);
        }

        return new AgentResourceBudget
        {
            ModelTokens = checked(budgets.Sum(item => item.ModelTokens)),
            ToolCalls = checked(budgets.Sum(item => item.ToolCalls)),
            EvidenceItems = checked(budgets.Sum(item => item.EvidenceItems)),
            Files = checked(budgets.Sum(item => item.Files)),
            Bytes = checked(budgets.Sum(item => item.Bytes)),
            Mutations = checked(budgets.Sum(item => item.Mutations)),
            Processes = checked(budgets.Sum(item => item.Processes)),
            Builds = checked(budgets.Sum(item => item.Builds)),
            Tests = checked(budgets.Sum(item => item.Tests)),
            Corrections = checked(budgets.Sum(item => item.Corrections)),
            WallTime = wallTime,
        };
    }

    /// <summary>Maximum model tokens.</summary>
    public long ModelTokens { get; init; } = 32_000;

    /// <summary>Whether cumulative resource limits are enforced instead of recorded as telemetry only.</summary>
    public bool EnforceLimits { get; init; } = true;

    /// <summary>Maximum tool calls.</summary>
    public int ToolCalls { get; init; } = 32;

    /// <summary>Maximum admitted evidence items.</summary>
    public int EvidenceItems { get; init; } = 64;

    /// <summary>Maximum files inspected or changed.</summary>
    public int Files { get; init; } = 128;

    /// <summary>Maximum bytes inspected or changed.</summary>
    public long Bytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Maximum mutation proposals.</summary>
    public int Mutations { get; init; } = 32;

    /// <summary>Maximum process invocations.</summary>
    public int Processes { get; init; } = 4;

    /// <summary>Maximum build invocations.</summary>
    public int Builds { get; init; } = 2;

    /// <summary>Maximum test invocations.</summary>
    public int Tests { get; init; } = 2;

    /// <summary>Maximum correction attempts.</summary>
    public int Corrections { get; init; } = 1;

    /// <summary>Maximum wall-clock duration.</summary>
    public TimeSpan WallTime { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Measured usage charged to both child and parent ledgers.</summary>
public sealed record AgentResourceUsage
{
    /// <summary>Model tokens consumed.</summary>
    public long ModelTokens { get; init; }

    /// <summary>Tool calls consumed.</summary>
    public int ToolCalls { get; init; }

    /// <summary>Evidence items admitted.</summary>
    public int EvidenceItems { get; init; }

    /// <summary>Files inspected or changed.</summary>
    public int Files { get; init; }

    /// <summary>Bytes inspected or changed.</summary>
    public long Bytes { get; init; }

    /// <summary>Mutation proposals consumed.</summary>
    public int Mutations { get; init; }

    /// <summary>Process invocations consumed.</summary>
    public int Processes { get; init; }

    /// <summary>Build invocations consumed.</summary>
    public int Builds { get; init; }

    /// <summary>Test invocations consumed.</summary>
    public int Tests { get; init; }

    /// <summary>Correction attempts consumed.</summary>
    public int Corrections { get; init; }

    /// <summary>Elapsed wall time.</summary>
    public TimeSpan WallTime { get; init; }
}

/// <summary>Immutable policy applied to one child model and tool boundary.</summary>
public sealed record AgentPolicySnapshot
{
    /// <summary>Explicit allowed tool ids.</summary>
    public IReadOnlyList<string> AllowedToolIds { get; init; } = [];

    /// <summary>Explicit denied tool ids.</summary>
    public IReadOnlyList<string> DeniedToolIds { get; init; } = [];

    /// <summary>Maximum trust the child may exercise.</summary>
    public RepositoryTrustLevel TrustCeiling { get; init; } = RepositoryTrustLevel.UntrustedInspection;

    /// <summary>Whether network tools may be selected.</summary>
    public bool AllowNetwork { get; init; }

    /// <summary>Whether non-agent infrastructure processes may be invoked.</summary>
    public bool AllowProcesses { get; init; }

    /// <summary>Frozen repository-relative prohibited path patterns.</summary>
    public IReadOnlyList<string> ProhibitedPaths { get; init; } = [];

    /// <summary>Sensitivity classification used for model routing.</summary>
    public ConversationSensitivity Sensitivity { get; init; } = ConversationSensitivity.None;

    /// <summary>Selected configured model profile.</summary>
    public ModelProfileId ModelProfileId { get; init; }

    /// <summary>Selected reasoning level.</summary>
    public string ReasoningLevel { get; init; } = "none";

    /// <summary>Host rationale for the model selection.</summary>
    public required string ModelSelectionRationale { get; init; }

    /// <summary>Versioned context policy identity.</summary>
    public required string ContextPolicyVersion { get; init; }

    /// <summary>Versioned tool policy identity.</summary>
    public required string ToolPolicyVersion { get; init; }
}

/// <summary>One immutable host-approved child assignment.</summary>
public sealed record AgentAssignment
{
    /// <summary>Stable assignment identity.</summary>
    public required AgentAssignmentId AssignmentId { get; init; }

    /// <summary>Stable child-run identity.</summary>
    public required RunId ChildRunId { get; init; }

    /// <summary>Child role.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>Repository authority mode.</summary>
    public required AgentRunMode Mode { get; init; }

    /// <summary>Concise host-approved objective.</summary>
    public required string Objective { get; init; }

    /// <summary>Explicit questions or tasks.</summary>
    public IReadOnlyList<string> Tasks { get; init; } = [];

    /// <summary>Frozen caller-supplied context treated as untrusted task data.</summary>
    public string InitialContext { get; init; } = string.Empty;

    /// <summary>Structured output schema id and version.</summary>
    public required string OutputSchema { get; init; }

    /// <summary>Explicit stopping condition.</summary>
    public required string StoppingCondition { get; init; }

    /// <summary>Deadline for the child attempt.</summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>Normalized owned scope.</summary>
    public required AgentAssignmentScope Scope { get; init; }

    /// <summary>Narrow child policy snapshot.</summary>
    public required AgentPolicySnapshot Policy { get; init; }

    /// <summary>Reserved child resources.</summary>
    public required AgentResourceBudget Budget { get; init; }

    /// <summary>Assignments that must complete before this assignment may start.</summary>
    public IReadOnlyList<AgentAssignmentId> Dependencies { get; init; } = [];

    /// <summary>Host failure policy.</summary>
    public AgentFailurePolicy FailurePolicy { get; init; } = AgentFailurePolicy.ContinueAndReport;

    /// <summary>Approved plan steps owned by an implementation worker.</summary>
    public IReadOnlyList<StepId> PlanStepIds { get; init; } = [];
}

/// <summary>Immutable parent and repository provenance for one delegation.</summary>
public sealed record DelegationProvenance
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Parent run.</summary>
    public required RunId ParentRunId { get; init; }

    /// <summary>Repository identity.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Exact immutable baseline identity.</summary>
    public required string BaselineIdentity { get; init; }

    /// <summary>Owning workspace.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Approved plan identity when implementation is delegated.</summary>
    public string? ApprovedPlanIdentity { get; init; }

    /// <summary>Approved plan revision when implementation is delegated.</summary>
    public int? ApprovedPlanRevision { get; init; }

    /// <summary>Attempt number.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>Generation used to fence late results.</summary>
    public int Generation { get; init; } = 1;
}

/// <summary>Validated and frozen one-level delegation plan.</summary>
public sealed record DelegationPlan
{
    /// <summary>Current schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Stable delegation identity.</summary>
    public required DelegationId DelegationId { get; init; }

    /// <summary>Immutable parent provenance.</summary>
    public required DelegationProvenance Provenance { get; init; }

    /// <summary>Bounded child assignments.</summary>
    public required IReadOnlyList<AgentAssignment> Assignments { get; init; }

    /// <summary>Aggregate parent budget dominating child reservations.</summary>
    public required AgentResourceBudget ParentBudget { get; init; }

    /// <summary>Whether an explicit user/host policy authorized implementation delegation.</summary>
    public bool ImplementationAuthorized { get; init; }

    /// <summary>When the host froze this plan.</summary>
    public required DateTimeOffset AcceptedAt { get; init; }
}

/// <summary>One cited read-only child finding.</summary>
public sealed record AgentFinding
{
    /// <summary>Stable finding identity.</summary>
    public required Guid FindingId { get; init; }

    /// <summary>Finding category.</summary>
    public required string Category { get; init; }

    /// <summary>Bounded finding summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Admitted evidence citations.</summary>
    public IReadOnlyList<EvidenceId> EvidenceIds { get; init; } = [];

    /// <summary>Repository-relative locations.</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];

    /// <summary>Semantic symbol identities.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>Confidence from zero to one.</summary>
    public double Confidence { get; init; }

    /// <summary>Known uncertainty.</summary>
    public string? Uncertainty { get; init; }

    /// <summary>Risk if the finding is ignored.</summary>
    public string? Risk { get; init; }

    /// <summary>Recommended parent action.</summary>
    public string? Recommendation { get; init; }
}

/// <summary>Schema-valid read-only child result.</summary>
public sealed record AgentFindingSet
{
    /// <summary>Current schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Owning assignment.</summary>
    public required AgentAssignmentId AssignmentId { get; init; }

    /// <summary>Owning child run.</summary>
    public required RunId ChildRunId { get; init; }

    /// <summary>Attempt generation.</summary>
    public required int Generation { get; init; }

    /// <summary>Bounded child-authored synthesis of the cited result.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Typed findings.</summary>
    public IReadOnlyList<AgentFinding> Findings { get; init; } = [];

    /// <summary>Questions the child could not answer.</summary>
    public IReadOnlyList<string> UnresolvedQuestions { get; init; } = [];

    /// <summary>Explicit coverage and omissions.</summary>
    public IReadOnlyList<string> CoverageNotes { get; init; } = [];
}

/// <summary>One advisory independent review finding.</summary>
public sealed record ReviewFinding
{
    /// <summary>Stable finding identity.</summary>
    public required Guid FindingId { get; init; }

    /// <summary>Review role.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>Finding category.</summary>
    public required string Category { get; init; }

    /// <summary>Severity such as info, warning, or blocking.</summary>
    public required string Severity { get; init; }

    /// <summary>Confidence from zero to one.</summary>
    public double Confidence { get; init; }

    /// <summary>Affected repository-relative path.</summary>
    public string? RelativePath { get; init; }

    /// <summary>Affected symbol.</summary>
    public string? Symbol { get; init; }

    /// <summary>Evidence citations.</summary>
    public IReadOnlyList<EvidenceId> EvidenceIds { get; init; } = [];

    /// <summary>Consequence of the finding.</summary>
    public required string Consequence { get; init; }

    /// <summary>Recommended disposition.</summary>
    public required string Recommendation { get; init; }

    /// <summary>Whether integration requires an explicit disposition.</summary>
    public bool RequiresDisposition { get; init; }
}

/// <summary>Schema-valid result from one independent reviewer.</summary>
public sealed record ReviewFindingSet
{
    /// <summary>Current schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Owning assignment.</summary>
    public required AgentAssignmentId AssignmentId { get; init; }

    /// <summary>Reviewer role.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>Attempt generation.</summary>
    public required int Generation { get; init; }

    /// <summary>Advisory findings.</summary>
    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];
}

/// <summary>Frozen result of one isolated implementation worker.</summary>
public sealed record WorkerChangeSet
{
    /// <summary>Current schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Owning assignment.</summary>
    public required AgentAssignmentId AssignmentId { get; init; }

    /// <summary>Owning child run.</summary>
    public required RunId ChildRunId { get; init; }

    /// <summary>Attempt generation.</summary>
    public required int Generation { get; init; }

    /// <summary>Immutable parent baseline identity.</summary>
    public required string ParentBaselineIdentity { get; init; }

    /// <summary>Managed worktree identity.</summary>
    public required string WorktreeIdentity { get; init; }

    /// <summary>Actually touched repository-relative paths.</summary>
    public IReadOnlyList<string> TouchedPaths { get; init; } = [];

    /// <summary>Actually touched semantic symbols.</summary>
    public IReadOnlyList<string> TouchedSymbols { get; init; } = [];

    /// <summary>Exact diff artifact.</summary>
    public required ExecutionArtifactReference DiffArtifact { get; init; }

    /// <summary>Mutation identities applied in the worktree.</summary>
    public IReadOnlyList<MutationSetId> MutationSetIds { get; init; } = [];

    /// <summary>Worker-local validation evidence.</summary>
    public required MutationValidationResult Validation { get; init; }

    /// <summary>Approval and policy provenance inside the worktree.</summary>
    public required string ApprovalProvenance { get; init; }

    /// <summary>Remaining worker risks.</summary>
    public IReadOnlyList<string> ResidualRisks { get; init; } = [];

    /// <summary>Whether the result is complete and eligible for integration.</summary>
    public bool IsComplete { get; init; }
}

/// <summary>Stable reason that partitioning or integration cannot proceed.</summary>
public sealed record AgentConflict(
    string Code,
    string Summary,
    IReadOnlyList<AgentAssignmentId> Assignments,
    IReadOnlyList<string> Paths);

/// <summary>Host partition decision with explicit serial fallback.</summary>
public sealed record AssignmentPartitionDecision
{
    /// <summary>Assignments safe to execute concurrently.</summary>
    public IReadOnlyList<AgentAssignmentId> ParallelAssignments { get; init; } = [];

    /// <summary>Assignments that must execute serially.</summary>
    public IReadOnlyList<AgentAssignmentId> SerialAssignments { get; init; } = [];

    /// <summary>Overlap or confidence reasons.</summary>
    public IReadOnlyList<AgentConflict> Conflicts { get; init; } = [];

    /// <summary>Whether all requested implementation assignments are provably parallel-safe.</summary>
    public bool IsParallelSafe { get; init; }
}

/// <summary>One child terminal result projected without raw transcript content.</summary>
public sealed record AgentRunOutcome
{
    /// <summary>Owning assignment.</summary>
    public required AgentAssignmentId AssignmentId { get; init; }

    /// <summary>Owning child run.</summary>
    public required RunId ChildRunId { get; init; }

    /// <summary>Frozen child role.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>Attempt generation.</summary>
    public required int Generation { get; init; }

    /// <summary>Terminal status.</summary>
    public required AgentRunStatus Status { get; init; }

    /// <summary>Measured resource usage.</summary>
    public required AgentResourceUsage Usage { get; init; }

    /// <summary>Sanitized terminal reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Effective selected model retained for provenance and inspection.</summary>
    public ModelProfileId? ModelProfileId { get; init; }

    /// <summary>Exact parent and child-tool evidence identities rendered to this child.</summary>
    public IReadOnlyList<EvidenceId> DeliveredEvidenceIds { get; init; } = [];

    /// <summary>Read-only findings when produced.</summary>
    public AgentFindingSet? Findings { get; init; }

    /// <summary>Worker change set when produced.</summary>
    public WorkerChangeSet? ChangeSet { get; init; }

    /// <summary>Review findings when produced.</summary>
    public ReviewFindingSet? Review { get; init; }
}

/// <summary>Durable delegation checkpoint and inspectable run tree.</summary>
public sealed record DelegationCheckpoint
{
    /// <summary>Current schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Monotonic write revision used to reject late stale checkpoint saves.</summary>
    public long Revision { get; init; } = 1;

    /// <summary>Delegation identity.</summary>
    public required DelegationId DelegationId { get; init; }

    /// <summary>Immutable provenance.</summary>
    public required DelegationProvenance Provenance { get; init; }

    /// <summary>Durable phase.</summary>
    public required DelegationCheckpointPhase Phase { get; init; }

    /// <summary>Latest child outcomes.</summary>
    public IReadOnlyList<AgentRunOutcome> ChildOutcomes { get; init; } = [];

    /// <summary>Conflict decisions.</summary>
    public IReadOnlyList<AgentConflict> Conflicts { get; init; } = [];

    /// <summary>Next legal host action.</summary>
    public required string NextAction { get; init; }

    /// <summary>Checkpoint time.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>Executes one already-validated child assignment within scheduler policy.</summary>
public interface IAgentAssignmentRunner
{
    /// <summary>Runs one child and returns a schema-valid terminal projection.</summary>
    Task<AgentRunOutcome> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default);
}

/// <summary>Promotes validated child-local results after the authoritative join checkpoint is durable.</summary>
public interface IAgentOutcomeJoiner
{
    /// <summary>Conditionally commits role-specific joined results without exposing child transcripts.</summary>
    /// <returns>Whether the supplied commit arbiter accepted and committed the join.</returns>
    Task<bool> JoinAsync(
        DelegationPlan plan,
        IReadOnlyList<AgentRunOutcome> outcomes,
        Func<bool> tryCommit,
        CancellationToken cancellationToken = default);
}

/// <summary>Host-owned bounded in-process child scheduler.</summary>
public interface IAgentRunScheduler
{
    /// <summary>Runs a validated one-level delegation and observes every child.</summary>
    Task<IReadOnlyList<AgentRunOutcome>> RunAsync(
        DelegationPlan plan,
        IAgentAssignmentRunner runner,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels one active child and its declared dependents.</summary>
    Task<bool> CancelAssignmentAsync(
        DelegationId delegationId,
        AgentAssignmentId assignmentId,
        CancellationToken cancellationToken = default);

    /// <summary>Stops new admission and boundedly joins active children.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Validates and coordinates durable delegation lifecycle.</summary>
public interface IDelegationCoordinator
{
    /// <summary>Validates, persists, and runs a delegation to its next durable boundary.</summary>
    Task<DelegationCheckpoint> StartAsync(
        DelegationPlan plan,
        IAgentAssignmentRunner runner,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the latest inspectable run tree.</summary>
    Task<DelegationCheckpoint?> GetAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels a complete delegation.</summary>
    Task<bool> CancelAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Conservatively proves assignment non-overlap.</summary>
public interface IAssignmentPartitioner
{
    /// <summary>Partitions implementation assignments or returns serial fallback.</summary>
    AssignmentPartitionDecision Partition(DelegationPlan plan);
}

/// <summary>Host-owned managed worktree lease for one implementation assignment.</summary>
public sealed record WorkerWorktreeLease
{
    /// <summary>Owning delegation.</summary>
    public required DelegationId DelegationId { get; init; }

    /// <summary>Owning assignment.</summary>
    public required AgentAssignmentId AssignmentId { get; init; }

    /// <summary>Owning child run.</summary>
    public required RunId ChildRunId { get; init; }

    /// <summary>Managed isolated repository root.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>Exact parent revision used to create the worktree.</summary>
    public required string Revision { get; init; }

    /// <summary>Exact parent baseline identity.</summary>
    public required string BaselineIdentity { get; init; }

    /// <summary>Whether the lease is frozen against further child work.</summary>
    public bool IsFrozen { get; init; }
}

/// <summary>Creates, freezes, and cleans confined worker worktrees.</summary>
public interface IWorkerWorktreeCoordinator
{
    /// <summary>Creates a managed detached worktree at the exact parent revision.</summary>
    Task<WorkerWorktreeLease> CreateAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken = default);

    /// <summary>Freezes and verifies terminal worktree status.</summary>
    Task<WorkerWorktreeLease> FreezeAsync(
        WorkerWorktreeLease lease,
        CancellationToken cancellationToken = default);

    /// <summary>Removes only a worktree owned by this coordinator.</summary>
    Task RemoveAsync(
        WorkerWorktreeLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>Detects worker scope, worker-to-worker, and stale-parent conflicts before restaging.</summary>
public interface IWorkerIntegrationCoordinator
{
    /// <summary>Validates selected frozen worker packages against current parent facts.</summary>
    IReadOnlyList<AgentConflict> DetectConflicts(
        DelegationPlan plan,
        IReadOnlyList<WorkerChangeSet> changeSets,
        string currentParentBaselineIdentity);
}

/// <summary>Persists delegation checkpoints without exposing storage implementation types.</summary>
public interface IDelegationCheckpointStore
{
    /// <summary>Atomically writes one checkpoint unless an equal or newer revision is already durable.</summary>
    /// <returns><see langword="true"/> only when this revision became durable.</returns>
    Task<bool> SaveAsync(
        DelegationCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the latest supported checkpoint.</summary>
    Task<DelegationCheckpoint?> GetAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Starts one accepted delegation.</summary>
public sealed record StartDelegationCommand(
    DelegationPlan Plan,
    IAgentAssignmentRunner Runner) : ICommand<DelegationCheckpoint>;

/// <summary>Inspects one delegation run tree.</summary>
public sealed record GetDelegationCommand(DelegationId DelegationId)
    : ICommand<DelegationCheckpoint?>;

/// <summary>Cancels a complete delegation.</summary>
public sealed record CancelDelegationCommand(DelegationId DelegationId)
    : ICommand<bool>;

/// <summary>Cancels one child assignment.</summary>
public sealed record CancelAgentAssignmentCommand(
    DelegationId DelegationId,
    AgentAssignmentId AssignmentId) : ICommand<bool>;
