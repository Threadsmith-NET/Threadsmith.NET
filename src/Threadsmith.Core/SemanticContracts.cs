namespace Threadsmith.Core;

/// <summary>Confidence in compiler-aware repository knowledge, ordered weakest to strongest.</summary>
public enum SemanticConfidenceLevel
{
    /// <summary>No project information is available.</summary>
    None,

    /// <summary>Project files and text are available without a compilation.</summary>
    TextOnly,

    /// <summary>The evaluated project graph is available without compilations.</summary>
    ProjectGraphOnly,

    /// <summary>Only a subset of projects has a usable compilation.</summary>
    PartialCompilation,

    /// <summary>Every loaded project has a usable compilation.</summary>
    FullSemantic,
}

/// <summary>A one-based source range suitable for display and persistence.</summary>
public sealed record SourceRange(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>A stable, serializable compiler symbol identity.</summary>
public sealed record SemanticSymbolIdentity(
    string Id,
    string DisplayName,
    string Kind);

/// <summary>A host-owned source location for a semantic result.</summary>
public sealed record SemanticSourceLocation(
    string ProjectName,
    string TargetFramework,
    string FilePath,
    SourceRange Range,
    bool IsGenerated,
    bool IsLinked);

/// <summary>A symbol declaration discovered by the semantic engine.</summary>
public sealed record SymbolResult(
    SemanticSymbolIdentity Symbol,
    SemanticSourceLocation Location,
    SemanticConfidenceLevel SemanticConfidence);

/// <summary>A source reference to a stable symbol identity.</summary>
public sealed record ReferenceResult(
    SemanticSymbolIdentity Symbol,
    SemanticSourceLocation Location,
    SemanticConfidenceLevel SemanticConfidence);

/// <summary>An implementation of an interface or overridable symbol.</summary>
public sealed record ImplementationResult(
    SemanticSymbolIdentity Symbol,
    SemanticSourceLocation Location,
    SemanticConfidenceLevel SemanticConfidence);

/// <summary>One project and target-framework view in a semantic solution.</summary>
public sealed record SemanticProjectInfo(
    string Name,
    string FilePath,
    IReadOnlyList<string> TargetFrameworks,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences);

/// <summary>Parameters required to load a compiler-aware workspace.</summary>
public sealed record SemanticLoadRequest(
    SessionId SessionId,
    WorkspaceId WorkspaceId,
    string RepositoryPath,
    string SolutionPath,
    RepositoryTrustLevel TrustLevel,
    IReadOnlyList<string>? ProhibitedPaths = null);

/// <summary>Host-owned result of semantic workspace loading.</summary>
public sealed record SemanticLoadResult(
    WorkspaceId WorkspaceId,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<SemanticProjectInfo> Projects,
    IReadOnlyList<string> Diagnostics);

/// <summary>Screening decision for a proposed mutation set before user approval.</summary>
public enum PreMutationGateDecision
{
    /// <summary>Cheap gates found no blocking diagnostics.</summary>
    PassedCheapGates,

    /// <summary>Focused diagnostics should be returned to the model for proposal repair.</summary>
    RepairableDiagnostics,

    /// <summary>The host found a non-repairable proposal or environment failure.</summary>
    NonRepairableHostFailure,

    /// <summary>Optional checks were unavailable but the proposal may continue with explicit omissions.</summary>
    DegradedProceedWithWarning,

    /// <summary>The pre-mutation analysis budget was exhausted.</summary>
    BudgetExhausted,
}

/// <summary>Read-only compiler-aware repository operations.</summary>
public interface ISemanticEngine : IAsyncDisposable
{
    /// <summary>Current aggregate semantic confidence.</summary>
    SemanticConfidenceLevel Confidence { get; }

    /// <summary>Loads a solution and creates compiler-aware project views.</summary>
    Task<SemanticLoadResult> LoadAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Finds symbol declarations by name.</summary>
    Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>Finds references by stable symbol id.</summary>
    Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
        string symbolId,
        bool allowTextFallback = false,
        CancellationToken cancellationToken = default);

    /// <summary>Finds implementations by stable symbol id.</summary>
    Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
        string symbolId,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a changed path for turn-boundary invalidation.</summary>
    void QueueInvalidation(string path);

    /// <summary>Applies queued invalidations at a turn boundary.</summary>
    Task<SemanticConfidenceLevel> ApplyInvalidationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Retries the last load to promote degraded confidence.</summary>
    Task<SemanticLoadResult> PromoteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Resolves semantic queries against workspace-isolated engine state.</summary>
public interface ISemanticEngineResolver
{
    /// <summary>Gets the current confidence for one workspace.</summary>
    SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId);

    /// <summary>Finds declarations in one workspace.</summary>
    Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
        WorkspaceId workspaceId,
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>Finds references in one workspace.</summary>
    Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
        WorkspaceId workspaceId,
        string symbolId,
        bool allowTextFallback = false,
        CancellationToken cancellationToken = default);

    /// <summary>Gets fast Roslyn diagnostics from the already-loaded semantic workspace.</summary>
    Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
        WorkspaceId workspaceId,
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default);

    /// <summary>Finds implementations in one workspace.</summary>
    Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
        WorkspaceId workspaceId,
        string symbolId,
        CancellationToken cancellationToken = default);
}
