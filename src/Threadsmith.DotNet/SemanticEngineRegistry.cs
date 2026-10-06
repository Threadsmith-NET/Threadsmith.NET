namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Owns one independent semantic engine for each opened workspace.</summary>
public sealed class SemanticEngineRegistry : ISemanticEngineResolver, ISourceEditAnalyzer, IAsyncDisposable
{
    /// <summary>Gets optional, transient startup phase observations shared by the existing semantic owners.</summary>
    public SemanticStartupProgress StartupProgress { get; } = new();

    private readonly TimeSpan? _cancellationBackstop;
    private readonly SemanticResourceLimits _resourceLimits;
    private readonly ConcurrentDictionary<WorkspaceId, SemanticEngine> _engines = new();
    private readonly IPromptLoader _prompts;
    private readonly IDomainEventStream _events;
    private readonly ILoggerFactory _loggerFactory;
    private Func<SemanticLoadRequest, SemanticConfidenceLevel, CancellationToken, Task>? _confidencePublisher;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="SemanticEngineRegistry"/> class.</summary>
    public SemanticEngineRegistry(
        IDomainEventStream events,
        ILoggerFactory loggerFactory,
        IPromptLoader prompts,
        TimeSpan? cancellationBackstop = null,
        SemanticResourceLimits? resourceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (cancellationBackstop <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cancellationBackstop));
        }

        _resourceLimits = resourceLimits ?? new SemanticResourceLimits();
        _resourceLimits.Validate();
        _prompts = prompts;
        _events = events;
        _loggerFactory = loggerFactory;
        _cancellationBackstop = cancellationBackstop;
    }

    /// <summary>Loads semantic state into the engine owned by the request workspace.</summary>
    public Task<SemanticLoadResult> LoadAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetEngine(request.WorkspaceId).LoadAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId)
    {
        return GetEngine(workspaceId).Confidence;
    }

    /// <inheritdoc />
    public Task<SourceEditAnalysis> AnalyzeCandidateAsync(ApplySourceEditCommand command, string repositoryPath, MutationEffectSnapshot snapshot, TimeSpan immediateAllowance, CancellationToken cancellationToken = default)
    {
        return GetEngine(command.WorkspaceId).AnalyzeCandidateAsync(command, repositoryPath, snapshot, immediateAllowance, cancellationToken);
    }

    /// <inheritdoc />
    public SourceEditAnalysis? GetLatestAnalysis(SessionId sessionId, RunId runId, WorkspaceId workspaceId, Guid effectId)
    {
        return GetEngine(workspaceId).GetLatestEditAnalysis(sessionId, runId, effectId);
    }

    /// <inheritdoc />
    public void ConfirmApplied(WorkspaceId workspaceId, Guid effectId)
    {
        GetEngine(workspaceId).ConfirmEditApplied(effectId);
    }

    /// <inheritdoc />
    public void DiscardCandidate(WorkspaceId workspaceId, Guid effectId)
    {
        GetEngine(workspaceId).DiscardEditCandidate(effectId);
    }

    /// <summary>Gets a stable snapshot of the loaded host-owned project inventory.</summary>
    public IReadOnlyList<SemanticProjectInfo> GetProjects(WorkspaceId workspaceId)
    {
        return GetEngine(workspaceId).Projects;
    }

    /// <summary>Gets the authoritative normalized load request for a workspace.</summary>
    public SemanticLoadRequest GetLoadRequest(WorkspaceId workspaceId)
    {
        return GetEngine(workspaceId).LoadedRequest;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
        WorkspaceId workspaceId,
        string query,
        CancellationToken cancellationToken = default)
    {
        return GetEngine(workspaceId).FindSymbolsAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
        WorkspaceId workspaceId,
        string symbolId,
        bool allowTextFallback = false,
        CancellationToken cancellationToken = default)
    {
        return GetEngine(workspaceId).FindReferencesAsync(
                symbolId,
                allowTextFallback,
                cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
        WorkspaceId workspaceId,
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        return GetEngine(workspaceId).FindImplementationsAsync(symbolId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
        WorkspaceId workspaceId,
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        return GetEngine(workspaceId).GetDiagnosticsAsync(projectPaths, changedFiles, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SemanticEngine[] engines = [.. _engines.Values];
        _engines.Clear();
        foreach (var engine in engines)
        {
            await engine.DisposeAsync();
        }
    }

    /// <summary>Loads semantic state while the lifecycle observer controls terminal publication.</summary>
    internal Task<SemanticLoadResult> LoadForBindingAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetEngine(request.WorkspaceId)
            .LoadCoreAsync(
                request,
                publishLoadCompleted: false,
                publishConfidenceChanged: false,
                allowTextFallback: true,
                cancellationToken);
    }

    /// <summary>Acknowledges the observer's exact initial result before background coverage can publish.</summary>
    internal Task CompleteInitialPublicationAsync(SemanticLoadResult result, bool succeeded, CancellationToken cancellationToken)
    {
        return GetEngine(result.WorkspaceId).CompleteInitialPublicationAsync(result, succeeded, cancellationToken);
    }

    /// <summary>Connects progressive coverage to the authoritative workspace binding fan-out.</summary>
    internal void SetConfidencePublisher(Func<SemanticLoadRequest, SemanticConfidenceLevel, CancellationToken, Task> publisher)
    {
        _confidencePublisher = publisher;
        foreach (var engine in _engines.Values)
        {
            engine.SetConfidencePublisher(publisher);
        }
    }

    /// <summary>Gets the concrete engine for an internal serialized semantic mutation.</summary>
    internal SemanticEngine GetEngine(WorkspaceId workspaceId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (workspaceId == default)
        {
            throw new ArgumentException("A semantic query requires a workspace identity.", nameof(workspaceId));
        }

        var engine = _engines.GetOrAdd(workspaceId, _ => new SemanticEngine(
            _events,
            _loggerFactory.CreateLogger<SemanticEngine>(),
            _prompts,
            _cancellationBackstop,
            _resourceLimits,
            StartupProgress));
        if (_confidencePublisher is { } publisher)
        {
            engine.SetConfidencePublisher(publisher);
        }

        return engine;
    }
}
