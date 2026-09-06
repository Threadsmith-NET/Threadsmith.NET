namespace Threadsmith.Core;

/// <summary>Host-owned cause for establishing current semantic state.</summary>
public enum SemanticRefreshReason
{
    /// <summary>A relevant repository file changed outside Threadsmith.</summary>
    ExternalChange,

    /// <summary>A Threadsmith-owned mutation changed repository files.</summary>
    HostMutation,

    /// <summary>A user request is waiting for known semantic changes.</summary>
    UserAdmission,

    /// <summary>The user explicitly requested an authoritative reload.</summary>
    Manual,

    /// <summary>A watcher or refresh failure requires authoritative recovery.</summary>
    Recovery,
}

/// <summary>Semantic refresh implementation selected by host policy.</summary>
public enum SemanticRefreshMode
{
    /// <summary>Replace text for proven existing loaded C# documents.</summary>
    Incremental,

    /// <summary>Reload the selected solution or project through MSBuild.</summary>
    Full,
}

/// <summary>Safe host-owned classification for a failed semantic refresh.</summary>
public enum SemanticRefreshFailureKind
{
    /// <summary>A file did not remain stable while the refresh was prepared.</summary>
    UnstableSnapshot,

    /// <summary>The repository or workspace binding became obsolete.</summary>
    BindingObsolete,

    /// <summary>The semantic implementation could not establish current state.</summary>
    Infrastructure,
}

/// <summary>Filesystem change shape supplied to the semantic refresh authority.</summary>
public enum SemanticFileChangeKind
{
    /// <summary>An existing file may have changed.</summary>
    Changed,

    /// <summary>A path was created.</summary>
    Created,

    /// <summary>A path was deleted.</summary>
    Deleted,

    /// <summary>A path was renamed from another location.</summary>
    Renamed,

    /// <summary>The change source could not preserve exact notification identity.</summary>
    Uncertain,
}

/// <summary>One bounded path notification supplied to the semantic refresh authority.</summary>
public sealed record SemanticFileChange(
    SessionId SessionId,
    string Path,
    SemanticFileChangeKind Kind,
    SemanticRefreshReason Source = SemanticRefreshReason.ExternalChange,
    string? PreviousPath = null);

/// <summary>One exact repository identity expected from a host-owned transactional write.</summary>
public sealed record SemanticHostWriteExpectation(
    string RelativePath,
    string ContentIdentity,
    bool AllowMissingTransition,
    string? CompensationContentIdentity = null,
    bool ExistedBefore = true);

/// <summary>Opaque binding fence for one registered host mutation transaction.</summary>
public sealed record SemanticHostMutationRegistration(
    SessionId SessionId,
    WorkspaceId WorkspaceId,
    MutationSetId MutationSetId,
    long BindingGeneration);

/// <summary>Registers host writes before their first observable repository mutation.</summary>
public interface ISemanticHostMutationAttribution
{
    /// <summary>Registers all exact identities expected during one host transaction.</summary>
    Task<SemanticHostMutationRegistration?> RegisterExpectedWritesAsync(
        SessionId sessionId,
        WorkspaceId workspaceId,
        MutationSetId mutationSetId,
        IReadOnlyList<SemanticHostWriteExpectation> writes,
        CancellationToken cancellationToken = default);

    /// <summary>Reconciles registered paths after the host transaction has finished.</summary>
    Task CompleteExpectedWritesAsync(
        SemanticHostMutationRegistration registration,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of establishing current semantic state for one session binding.</summary>
public sealed record SemanticRefreshResult(
    SemanticRefreshId RefreshId,
    WorkspaceId WorkspaceId,
    SemanticRefreshReason Reason,
    SemanticRefreshMode Mode,
    int ChangedFileCount,
    long DirtyVersion,
    long AppliedVersion,
    SemanticConfidenceLevel Confidence,
    TimeSpan Duration,
    bool WasRefreshed);

/// <summary>Coordinates semantic freshness before application work is admitted.</summary>
public interface ISemanticRefreshCoordinator : IAsyncDisposable
{
    /// <summary>Returns whether a session has a bound, settled, fully applied semantic generation.</summary>
    bool IsCurrent(SessionId sessionId);

    /// <summary>Resolves the current workspace-scoped admission identity for a bound session.</summary>
    bool TryGetWorkspaceId(SessionId sessionId, out WorkspaceId workspaceId);

    /// <summary>Runs a small synchronous admission registration only if the expected binding is current.</summary>
    bool TryAdmitCurrent(
        SessionId sessionId,
        WorkspaceId expectedWorkspaceId,
        Func<bool> admit);

    /// <summary>Binds one loaded repository selection and begins monitoring its semantic inputs.</summary>
    Task BindAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring and obsoletes pending refresh work for one session.</summary>
    Task UnbindAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>Records a normalized semantic input change without performing work inline.</summary>
    ValueTask ObserveChangeAsync(
        SemanticFileChange change,
        CancellationToken cancellationToken = default);

    /// <summary>Waits until all known semantic changes for a session have been applied.</summary>
    Task<SemanticRefreshResult> EnsureCurrentAsync(
        SessionId sessionId,
        SemanticRefreshReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>Forces and waits for one full semantic refresh for a session.</summary>
    Task<SemanticRefreshResult> ForceRefreshAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Controls the legal boundary at which a replacement semantic generation may publish.</summary>
public interface ISemanticRefreshPublicationGate
{
    /// <summary>Executes semantic publication atomically against run admission and active runs.</summary>
    Task<TResult> PublishAsync<TResult>(
        SessionId sessionId,
        WorkspaceId workspaceId,
        Func<CancellationToken, Task<TResult>> publication,
        CancellationToken cancellationToken = default);
}

/// <summary>Application command that waits for known semantic changes before run admission.</summary>
public sealed record EnsureSemanticCurrentCommand(
    SessionId SessionId,
    SemanticRefreshReason Reason = SemanticRefreshReason.UserAdmission)
    : ICommand<SemanticRefreshResult>;

/// <summary>Application command that forces one authoritative semantic reload.</summary>
public sealed record ForceSemanticRefreshCommand(SessionId SessionId)
    : ICommand<SemanticRefreshResult>;
