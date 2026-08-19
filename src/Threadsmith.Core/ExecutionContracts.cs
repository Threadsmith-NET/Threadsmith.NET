namespace Threadsmith.Core;

/// <summary>Execution lifecycle phases.</summary>
public enum RunPhase
{
    /// <summary>Request intake.</summary>
    Intake,

    /// <summary>Repository discovery.</summary>
    RepositoryDiscovery,

    /// <summary>Evidence collection.</summary>
    EvidenceCollection,

    /// <summary>Change planning.</summary>
    ChangePlanning,

    /// <summary>Waiting for plan approval.</summary>
    AwaitingPlanApproval,

    /// <summary>Preparing mutations.</summary>
    MutationPreparation,

    /// <summary>Waiting for mutation approval.</summary>
    AwaitingMutationApproval,

    /// <summary>Applying mutations.</summary>
    Mutation,

    /// <summary>Compiling.</summary>
    Compilation,

    /// <summary>Testing.</summary>
    Testing,

    /// <summary>Verifying.</summary>
    Verification,

    /// <summary>Waiting for acceptance.</summary>
    AwaitingAcceptance,

    /// <summary>Completed.</summary>
    Completion,

    /// <summary>Failed.</summary>
    Failed,

    /// <summary>Cancelled.</summary>
    Cancelled,

    /// <summary>Rolled back.</summary>
    RolledBack,

    /// <summary>An approved plan is preparing implementation.</summary>
    ImplementationPreparing,

    /// <summary>An implementation model turn is active.</summary>
    ImplementationModelTurn,

    /// <summary>A model mutation proposal was recorded.</summary>
    MutationProposed,

    /// <summary>A mutation proposal was staged transactionally.</summary>
    MutationStaged,

    /// <summary>The exact pre-mutation diagnostic baseline is being captured.</summary>
    BaselineValidation,

    /// <summary>A mutation application has durable pending intent.</summary>
    MutationApplyPending,

    /// <summary>A bounded correction is pending.</summary>
    CorrectionPending,

    /// <summary>A bounded correction model turn is active.</summary>
    CorrectionModelTurn,

    /// <summary>The authoritative terminal outcome is being assembled.</summary>
    CompletionPending,
}

/// <summary>Approval strength required by an operation.</summary>
public enum ApprovalLevel
{
    /// <summary>No approval.</summary>
    None,

    /// <summary>Host policy approval. The host's policy gate (trust, permissions, path rules) must
    /// permit the operation; no explicit user approval is required. Distinct from <see cref="None"/> as a
    /// declared intent that host policy, not the absence of policy, governs the operation.</summary>
    HostPolicy,

    /// <summary>Explicit user approval.</summary>
    User,
}

/// <summary>Retry classification used before retrying work.</summary>
public enum RetryClassification
{
    /// <summary>May retry.</summary>
    TransientProvider,

    /// <summary>Mutation caused a compiler error.</summary>
    MutationCompileError,

    /// <summary>Failure existed at baseline.</summary>
    BaselineFailure,

    /// <summary>Malformed provider data.</summary>
    MalformedOutput,

    /// <summary>Do not retry.</summary>
    Permanent,
}

/// <summary>Describes a legal state transition and its policy.</summary>
public sealed record TransitionContract
{
    /// <summary>Source phase.</summary>
    public required RunPhase Source { get; init; }

    /// <summary>Destination phase.</summary>
    public required RunPhase Destination { get; init; }

    /// <summary>Trigger name.</summary>
    public required string Trigger { get; init; }

    /// <summary>Required evidence kinds.</summary>
    public IReadOnlyList<string> RequiredEvidence { get; init; } = [];

    /// <summary>Allowed tool categories.</summary>
    public IReadOnlyList<string> AllowedToolCategories { get; init; } = [];

    /// <summary>Required approval.</summary>
    public ApprovalLevel Approval { get; init; }

    /// <summary>Retry classification.</summary>
    public RetryClassification Retry { get; init; } = RetryClassification.Permanent;

    /// <summary>Whether cancellation is permitted.</summary>
    public bool IsCancellable { get; init; } = true;

    /// <summary>Rollback behavior.</summary>
    public string RollbackBehavior { get; init; } = "Discard staging";
}

/// <summary>Validates and performs run transitions.</summary>
public interface IStateMachine
{
    /// <summary>Gets the current phase.</summary>
    RunPhase Phase { get; }

    /// <summary>Transitions to a new phase.</summary>
    Task TransitionAsync(RunPhase destination, string trigger, CancellationToken cancellationToken = default);
}

/// <summary>Immutable baseline visible to readers.</summary>
public interface IBaselineSnapshot
{
    /// <summary>Gets its revision.</summary>
    long Revision { get; }

    /// <summary>Gets an item.</summary>
    string? Get(string key);
}

/// <summary>Private copy-on-write staging view.</summary>
public interface IStagingView
{
    /// <summary>Stages an item.</summary>
    void Set(string key, string value);

    /// <summary>Gets an item.</summary>
    string? Get(string key);
}

/// <summary>Owns staging and publishes it only at a turn boundary.</summary>
public interface ITurn : IAsyncDisposable
{
    /// <summary>Gets the baseline captured at turn start.</summary>
    IBaselineSnapshot Baseline { get; }

    /// <summary>Gets private staging.</summary>
    IStagingView Staging { get; }

    /// <summary>Queues an invalidation.</summary>
    void Invalidate(string key);

    /// <summary>Commits at the boundary.</summary>
    Task<IBaselineSnapshot> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards staged state.</summary>
    Task CancelAsync(CancellationToken cancellationToken = default);
}
