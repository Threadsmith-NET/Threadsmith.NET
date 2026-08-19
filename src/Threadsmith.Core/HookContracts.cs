namespace Threadsmith.Core;

using System.Text.Json.Serialization;

/// <summary>Closed host-owned lifecycle boundary catalog.</summary>
public enum HookPoint
{
    /// <summary>A trusted repository lifecycle completed.</summary>
    RepositoryOpened,

    /// <summary>A normalized model request is ready for provider I/O.</summary>
    BeforeModelRequest,

    /// <summary>A normalized model request completed or failed.</summary>
    AfterModelRequest,

    /// <summary>A validated tool request is ready for execution.</summary>
    BeforeToolInvocation,

    /// <summary>A normalized tool invocation completed or failed.</summary>
    AfterToolInvocation,

    /// <summary>A valid plan was proposed before approval.</summary>
    PlanProposed,

    /// <summary>A plan approval became durable.</summary>
    PlanApproved,

    /// <summary>An exact mutation diff became durable.</summary>
    MutationStaged,

    /// <summary>A mutation transaction became durable.</summary>
    MutationApplied,

    /// <summary>A defined validation scope is ready to start.</summary>
    BeforeValidation,

    /// <summary>Authoritative validation evidence became available.</summary>
    AfterValidation,

    /// <summary>A bounded correction attempt started.</summary>
    CorrectionStarted,

    /// <summary>A successful terminal run outcome became durable.</summary>
    RunCompleted,

    /// <summary>A failed terminal run outcome became durable.</summary>
    RunFailed,

    /// <summary>An extension generation activated successfully.</summary>
    ExtensionConnected,

    /// <summary>An MCP connection published its imported capabilities.</summary>
    McpConnected,
}

/// <summary>Handler transport kind.</summary>
public enum HookAdapterKind
{
    /// <summary>Tracked executable process.</summary>
    Executable,

    /// <summary>Bounded HTTP endpoint.</summary>
    Http,

    /// <summary>Already-connected MCP capability.</summary>
    Mcp,

    /// <summary>Leased extension capability.</summary>
    Extension,
}

/// <summary>Configuration ownership scope.</summary>
public enum HookHandlerScope
{
    /// <summary>Organization-owned configuration.</summary>
    Organization,

    /// <summary>Machine-owned configuration.</summary>
    Machine,

    /// <summary>User-owned configuration.</summary>
    User,

    /// <summary>Untrusted repository declaration.</summary>
    Repository,
}

/// <summary>Maximum effective authority.</summary>
public enum HookAuthority
{
    /// <summary>Findings cannot block host progress.</summary>
    Advisory,

    /// <summary>A trusted managed grant may block an eligible pre-action.</summary>
    ManagedBlocking,
}

/// <summary>Failure behavior requested by a declaration.</summary>
public enum HookFailureMode
{
    /// <summary>Record failure and continue.</summary>
    FailOpen,

    /// <summary>Block only when a managed grant makes this effective.</summary>
    FailClosed,
}

/// <summary>Normalized handler result kind.</summary>
public enum HookResultKind
{
    /// <summary>No finding.</summary>
    Acknowledge,

    /// <summary>Advisory findings.</summary>
    Advice,

    /// <summary>Denial request.</summary>
    Deny,

    /// <summary>Normalized failure.</summary>
    Failure,
}

/// <summary>Final host decision at a hook boundary.</summary>
public enum HookDecisionKind
{
    /// <summary>Continue the owning action.</summary>
    Continue,

    /// <summary>Block the pending action.</summary>
    Block,

    /// <summary>The owning action was cancelled.</summary>
    Cancelled,
}

/// <summary>Invocation audit status.</summary>
public enum HookInvocationStatus
{
    /// <summary>Invocation started.</summary>
    Started,

    /// <summary>Handler acknowledged.</summary>
    Acknowledged,

    /// <summary>Handler advised.</summary>
    Advised,

    /// <summary>Handler denied.</summary>
    Denied,

    /// <summary>Handler failed.</summary>
    Failed,

    /// <summary>Handler timed out.</summary>
    TimedOut,

    /// <summary>Invocation was cancelled.</summary>
    Cancelled,

    /// <summary>Handler was skipped.</summary>
    Skipped,
}

/// <summary>Data classes a handler may receive.</summary>
[Flags]
public enum HookDataScope
{
    /// <summary>Identities and classifications.</summary>
    Metadata = 1,

    /// <summary>Bounded normalized summaries.</summary>
    Summaries = 2,

    /// <summary>Content-addressed artifact identities.</summary>
    ArtifactReferences = 4,

    /// <summary>Explicitly granted sensitive content.</summary>
    SensitiveContent = 8,
}

/// <summary>Stable hook invocation identity.</summary>
public readonly record struct HookInvocationId(Guid Value)
{
    /// <summary>Creates an identity.</summary>
    public static HookInvocationId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Stable handler identity.</summary>
public readonly record struct HookHandlerId(string Value)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }
}

/// <summary>Immutable normalized configuration digest.</summary>
public readonly record struct HookConfigurationDigest(string Value)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }
}

/// <summary>Immutable handler identity used for trust and policy matching.</summary>
public sealed record HookHandlerIdentity(HookHandlerId Id, string Version, HookConfigurationDigest ConfigurationDigest);

/// <summary>Per-handler resource bounds.</summary>
public sealed record HookHandlerLimits
{
    /// <summary>Maximum invocation duration.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum request bytes.</summary>
    public int MaximumInputBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum response bytes.</summary>
    public int MaximumOutputBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum concurrent invocations.</summary>
    public int MaximumConcurrency { get; init; } = 1;

    /// <summary>Number of transient retries after the first attempt.</summary>
    public int MaximumRetries { get; init; }
}

/// <summary>Adapter-neutral handler declaration.</summary>
public sealed record HookHandlerDescriptor
{
    /// <summary>Current declaration schema.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Immutable identity.</summary>
    public required HookHandlerIdentity Identity { get; init; }

    /// <summary>Owning configuration scope.</summary>
    public required HookHandlerScope Scope { get; init; }

    /// <summary>Transport adapter.</summary>
    public required HookAdapterKind AdapterKind { get; init; }

    /// <summary>Enabled request; eligibility remains host evaluated.</summary>
    public bool Enabled { get; init; }

    /// <summary>Deterministically ordered hook points.</summary>
    public IReadOnlyList<HookPoint> HookPoints { get; init; } = [];

    /// <summary>Adapter target identity, never a credential.</summary>
    public required string Target { get; init; }

    /// <summary>Requested authority.</summary>
    public HookAuthority RequestedAuthority { get; init; } = HookAuthority.Advisory;

    /// <summary>Requested failure behavior.</summary>
    public HookFailureMode RequestedFailureMode { get; init; } = HookFailureMode.FailOpen;

    /// <summary>Resource bounds.</summary>
    public HookHandlerLimits Limits { get; init; } = new();

    /// <summary>Requested data classes.</summary>
    public HookDataScope RequestedDataScope { get; init; } = HookDataScope.Metadata;

    /// <summary>Logical secret-reference names, never values.</summary>
    public IReadOnlyList<string> SecretReferences { get; init; } = [];

    /// <summary>Whether an interrupted pre-action notification is explicitly idempotent.</summary>
    public bool Idempotent { get; init; }

    /// <summary>Managed-policy ordering priority; repository declarations cannot make it effective.</summary>
    public int Priority { get; init; }
}

/// <summary>Exact external approval for one repository declaration fingerprint.</summary>
public sealed record HookRepositoryApproval
{
    /// <summary>Normalized repository identity.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Approved immutable handler identity.</summary>
    public required HookHandlerIdentity HandlerIdentity { get; init; }

    /// <summary>Approved target.</summary>
    public required string Target { get; init; }

    /// <summary>Approved hook points.</summary>
    public IReadOnlyList<HookPoint> HookPoints { get; init; } = [];

    /// <summary>Approved secret-reference names.</summary>
    public IReadOnlyList<string> SecretReferences { get; init; } = [];

    /// <summary>Approval time.</summary>
    public required DateTimeOffset ApprovedAt { get; init; }
}

/// <summary>Trusted non-repository managed blocking grant.</summary>
public sealed record HookManagedPolicyGrant
{
    /// <summary>Immutable granted handler identity.</summary>
    public required HookHandlerIdentity HandlerIdentity { get; init; }

    /// <summary>Eligible pre-action points.</summary>
    public IReadOnlyList<HookPoint> HookPoints { get; init; } = [];

    /// <summary>Denial codes allowed to block.</summary>
    public IReadOnlyList<string> AllowedDenialCodes { get; init; } = [];

    /// <summary>Effective managed failure behavior.</summary>
    public HookFailureMode FailureMode { get; init; }

    /// <summary>Maximum granted data classes.</summary>
    public HookDataScope DataScope { get; init; } = HookDataScope.Metadata;

    /// <summary>Maximum granted logical secret names.</summary>
    public IReadOnlyList<string> SecretReferences { get; init; } = [];

    /// <summary>Sanitized authority source.</summary>
    public required string AuthoritySource { get; init; }
}

/// <summary>Host-owned bounded lifecycle invocation.</summary>
public sealed record HookInvocationEnvelope
{
    /// <summary>Current envelope schema.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Boundary being observed.</summary>
    public required HookPoint HookPoint { get; init; }

    /// <summary>Unique invocation.</summary>
    public required HookInvocationId InvocationId { get; init; }

    /// <summary>Selected immutable handler.</summary>
    public required HookHandlerIdentity HandlerIdentity { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run when present.</summary>
    public RunId? RunId { get; init; }

    /// <summary>Repository identity when present.</summary>
    public string? RepositoryIdentity { get; init; }

    /// <summary>Stable owning operation identity.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>Monotonic operation generation.</summary>
    public int Generation { get; init; }

    /// <summary>Invocation attempt.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>Host timestamp.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Effective disclosed data classes.</summary>
    public HookDataScope DataScope { get; init; } = HookDataScope.Metadata;

    /// <summary>Bounded normalized point-specific metadata.</summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; } = new Dictionary<string, string>();

    /// <summary>Effective logical secret-reference names; never values.</summary>
    public IReadOnlyList<string> SecretReferences { get; init; } = [];

    /// <summary>Allowed content-addressed artifact references.</summary>
    public IReadOnlyList<ExecutionArtifactReference> Artifacts { get; init; } = [];

    /// <summary>Call-chain identities used for recursion suppression.</summary>
    public IReadOnlyList<string> CallChain { get; init; } = [];
}

/// <summary>Closed handler response union.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HookAcknowledgeResult), "acknowledge")]
[JsonDerivedType(typeof(HookAdviceResult), "advice")]
[JsonDerivedType(typeof(HookDenyResult), "deny")]
[JsonDerivedType(typeof(HookFailureResult), "failure")]
public abstract record HookHandlerResult
{
    /// <summary>Response schema.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Normalized kind.</summary>
    public abstract HookResultKind Kind { get; }
}

/// <summary>Successful acknowledgement.</summary>
public sealed record HookAcknowledgeResult : HookHandlerResult
{
    /// <inheritdoc />
    public override HookResultKind Kind => HookResultKind.Acknowledge;
}

/// <summary>Bounded advisory findings.</summary>
public sealed record HookAdviceResult(IReadOnlyList<string> Findings, IReadOnlyList<string>? Labels = null) : HookHandlerResult
{
    /// <inheritdoc />
    public override HookResultKind Kind => HookResultKind.Advice;
}

/// <summary>Stable denial response.</summary>
public sealed record HookDenyResult(string Code, string Explanation) : HookHandlerResult
{
    /// <inheritdoc />
    public override HookResultKind Kind => HookResultKind.Deny;
}

/// <summary>Normalized handler failure.</summary>
public sealed record HookFailureResult(string Code, string Explanation, bool Transient = false) : HookHandlerResult
{
    /// <inheritdoc />
    public override HookResultKind Kind => HookResultKind.Failure;
}

/// <summary>One normalized handler audit record.</summary>
public sealed record HookAuditRecord
{
    /// <summary>Invocation identity.</summary>
    public required HookInvocationId InvocationId { get; init; }

    /// <summary>Hook point.</summary>
    public required HookPoint HookPoint { get; init; }

    /// <summary>Handler identity.</summary>
    public required HookHandlerIdentity HandlerIdentity { get; init; }

    /// <summary>Owning operation.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>Repository identity when present.</summary>
    public string? RepositoryIdentity { get; init; }

    /// <summary>Invocation status.</summary>
    public required HookInvocationStatus Status { get; init; }

    /// <summary>Effective authority.</summary>
    public required HookAuthority Authority { get; init; }

    /// <summary>Effective failure mode.</summary>
    public required HookFailureMode FailureMode { get; init; }

    /// <summary>Raw normalized result kind when present.</summary>
    public HookResultKind? ResultKind { get; init; }

    /// <summary>Bounded stable code.</summary>
    public string? Code { get; init; }

    /// <summary>Sanitized bounded explanation.</summary>
    public string? Explanation { get; init; }

    /// <summary>Host decision following this result.</summary>
    public required HookDecisionKind Decision { get; init; }

    /// <summary>Granted data scope.</summary>
    public HookDataScope DataScope { get; init; }

    /// <summary>Granted logical secret names; never values.</summary>
    public IReadOnlyList<string> SecretReferences { get; init; } = [];

    /// <summary>Sanitized authority source.</summary>
    public string? AuthoritySource { get; init; }

    /// <summary>Duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Recorded timestamp.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>Aggregate host decision returned to an owning boundary.</summary>
public sealed record HookBoundaryDecision(HookDecisionKind Decision, IReadOnlyList<HookAuditRecord> AuditRecords, IReadOnlyList<string> Advice);

/// <summary>Adapter invocation boundary.</summary>
public interface IHookHandlerAdapter
{
    /// <summary>Supported adapter kind.</summary>
    HookAdapterKind Kind { get; }

    /// <summary>Invokes one eligible handler.</summary>
    Task<HookHandlerResult> InvokeAsync(HookHandlerDescriptor descriptor, HookInvocationEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>Stores exact repository approvals and durable audit records outside repository control.</summary>
public interface IHookStore
{
    /// <summary>Reads exact approval.</summary>
    Task<HookRepositoryApproval?> GetApprovalAsync(string repositoryIdentity, HookHandlerIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Writes exact external approval.</summary>
    Task SaveApprovalAsync(HookRepositoryApproval approval, CancellationToken cancellationToken = default);

    /// <summary>Revokes approval.</summary>
    Task RevokeApprovalAsync(string repositoryIdentity, HookHandlerId handlerId, CancellationToken cancellationToken = default);

    /// <summary>Appends one bounded audit row.</summary>
    Task AppendAuditAsync(HookAuditRecord record, CancellationToken cancellationToken = default);

    /// <summary>Queries recent audit rows.</summary>
    Task<IReadOnlyList<HookAuditRecord>> QueryAuditAsync(string? repositoryIdentity, HookHandlerId? handlerId, int maximumCount, CancellationToken cancellationToken = default);
}

/// <summary>Host-owned lifecycle coordinator.</summary>
public interface IHookCoordinator
{
    /// <summary>Returns the current immutable declaration snapshot.</summary>
    IReadOnlyList<HookHandlerDescriptor> Handlers { get; }

    /// <summary>Gets one current normalized declaration.</summary>
    HookHandlerDescriptor? GetHandler(HookHandlerId handlerId);

    /// <summary>Applies a process-local enablement override without granting trust or authority.</summary>
    bool SetEnabled(HookHandlerId handlerId, bool enabled);

    /// <summary>Evaluates and invokes the eligible handlers for one authoritative boundary.</summary>
    Task<HookBoundaryDecision> InvokeAsync(HookPoint point, SessionId sessionId, RunId? runId, string? repositoryIdentity, Guid operationId, int generation, IReadOnlyDictionary<string, string>? payload = null, IReadOnlyList<ExecutionArtifactReference>? artifacts = null, IReadOnlyList<string>? callChain = null, CancellationToken cancellationToken = default);

    /// <summary>Evaluates and invokes one selected eligible handler for an explicit management test.</summary>
    Task<HookBoundaryDecision> InvokeHandlerAsync(HookHandlerId handlerId, HookPoint point, SessionId sessionId, RunId? runId, string? repositoryIdentity, Guid operationId, int generation, IReadOnlyDictionary<string, string>? payload = null, IReadOnlyList<ExecutionArtifactReference>? artifacts = null, IReadOnlyList<string>? callChain = null, CancellationToken cancellationToken = default);
}

/// <summary>Lists configured hook handlers.</summary>
public sealed record ListHooksCommand : ICommand<IReadOnlyList<HookHandlerDescriptor>>;
/// <summary>Inspects one configured hook handler.</summary>
public sealed record InspectHookCommand(HookHandlerId HandlerId) : ICommand<HookHandlerDescriptor?>;

/// <summary>Applies a process-local handler enablement override.</summary>
public sealed record SetHookEnabledCommand(HookHandlerId HandlerId, bool Enabled) : ICommand<bool>;

/// <summary>Queries bounded hook audit history.</summary>
public sealed record QueryHookAuditCommand(string? RepositoryIdentity, HookHandlerId? HandlerId, int MaximumCount = 100) : ICommand<IReadOnlyList<HookAuditRecord>>;
/// <summary>Approves one exact repository hook declaration externally.</summary>
public sealed record ApproveRepositoryHookCommand(SessionId SessionId, HookRepositoryApproval Approval) : ICommand<bool>;
/// <summary>Revokes one repository hook approval.</summary>
public sealed record RevokeRepositoryHookCommand(SessionId SessionId, string RepositoryIdentity, HookHandlerId HandlerId) : ICommand<bool>;
/// <summary>Tests one enabled handler without granting authority.</summary>
public sealed record TestHookCommand(HookHandlerId HandlerId, SessionId SessionId, string? RepositoryIdentity) : ICommand<HookBoundaryDecision>;
