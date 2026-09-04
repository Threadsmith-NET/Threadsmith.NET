namespace Threadsmith.Tui;

using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;

/// <summary>Terminal-independent presenter state retained for source compatibility.</summary>
public sealed record ShellSnapshot(
    string Navigation,
    string Workspace,
    string Composer,
    string Status,
    string? RepositoryPath = null,
    RepositoryTrustLevel? RepositoryTrust = null,
    string? SolutionPath = null,
    IReadOnlyList<string>? TargetFrameworks = null,
    SemanticConfidenceLevel SemanticConfidence = SemanticConfidenceLevel.None,
    bool IsSemanticLoadComplete = false)
{
    /// <summary>Creates the compatibility snapshot from shared interaction state.</summary>
    internal static ShellSnapshot FromInteraction(InteractionShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ShellSnapshot(
            snapshot.Navigation,
            snapshot.Workspace,
            snapshot.Composer,
            snapshot.Status,
            snapshot.RepositoryPath,
            snapshot.RepositoryTrust,
            snapshot.SolutionPath,
            snapshot.TargetFrameworks,
            snapshot.SemanticConfidence,
            snapshot.IsSemanticLoadComplete);
    }
}

/// <summary>Repository workflow result retained for source compatibility.</summary>
/// <param name="Repository">Opened repository, or <see langword="null" />.</param>
/// <param name="Solution">Selected solution, or <see langword="null" />.</param>
/// <param name="UsedRememberedSolution">Whether the remembered solution was used.</param>
public sealed record RepositoryOpenWorkflowResult(
    RepositoryOpenResult? Repository,
    SolutionSelectionResult? Solution,
    bool UsedRememberedSolution = false)
{
    /// <summary>Creates the compatibility result from the shared interaction workflow.</summary>
    internal static RepositoryOpenWorkflowResult FromInteraction(
        InteractionRepositoryOpenWorkflowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RepositoryOpenWorkflowResult(
            result.Repository,
            result.Solution,
            result.UsedRememberedSolution);
    }
}

/// <summary>Compatibility facade over the frontend-neutral interaction presenter.</summary>
public sealed class TuiPresenter : InteractionPresenter
{
    /// <summary>Initializes a new instance of the <see cref="TuiPresenter" /> class.</summary>
    public TuiPresenter(ICommandDispatcher dispatcher, IProjectionStore projections)
        : base(dispatcher, projections)
    {
    }

    /// <summary>Renders host-owned state through the compatibility snapshot.</summary>
    public new async Task<ShellSnapshot> RenderAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return ShellSnapshot.FromInteraction(await base.RenderAsync(sessionId, cancellationToken));
    }
}

/// <summary>Compatibility facade over the frontend-neutral interaction controller.</summary>
public sealed class TuiController : InteractionController
{
    /// <summary>Initializes a new instance of the <see cref="TuiController" /> class.</summary>
    public TuiController(TuiPresenter presenter)
        : base(presenter)
    {
    }

    /// <summary>Runs the repository workflow through the compatibility result.</summary>
    public new async Task<RepositoryOpenWorkflowResult> OpenRepositoryWorkflowAsync(
        string repositoryPath,
        Func<CancellationToken, Task<RepositoryTrustLevel?>> requestTrustAsync,
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>> selectSolutionAsync,
        Func<RepositoryTrustState, CancellationToken, Task<RepositoryTrustLevel?>>?
            requestTrustUpgradeAsync = null,
        RepositoryTrustLevel? requestedTrust = null,
        string? requestedSolutionPath = null,
        CancellationToken cancellationToken = default)
    {
        var result = await base.OpenRepositoryWorkflowAsync(
            repositoryPath,
            requestTrustAsync,
            selectSolutionAsync,
            requestTrustUpgradeAsync,
            requestedTrust,
            requestedSolutionPath,
            cancellationToken);
        return RepositoryOpenWorkflowResult.FromInteraction(result);
    }

    /// <summary>Renders the current projection through the compatibility snapshot.</summary>
    public new async Task<ShellSnapshot> RenderAsync(CancellationToken cancellationToken = default)
    {
        return ShellSnapshot.FromInteraction(await base.RenderAsync(cancellationToken));
    }
}

/// <summary>Compatibility facade over the shared bounded event dispatcher.</summary>
public sealed class UiEventDispatcher
{
    private readonly InteractionEventDispatcher _inner;

    /// <summary>Initializes a new instance of the <see cref="UiEventDispatcher" /> class.</summary>
    public UiEventDispatcher(int capacity = 256)
    {
        _inner = new InteractionEventDispatcher(capacity);
    }

    /// <summary>Queues an event with backpressure.</summary>
    public Task QueueAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        return _inner.QueueAsync(domainEvent, cancellationToken);
    }

    /// <summary>Signals that no further events will be queued.</summary>
    public void Complete()
    {
        _inner.Complete();
    }

    /// <summary>Drains available events in one ordered batch.</summary>
    public Task DrainAsync(
        Func<IReadOnlyList<IDomainEvent>, CancellationToken, Task> renderAsync,
        CancellationToken cancellationToken = default)
    {
        return _inner.DrainAsync(renderAsync, cancellationToken);
    }
}
