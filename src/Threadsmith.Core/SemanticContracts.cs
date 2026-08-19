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

/// <summary>Source of a pre-mutation diagnostic.</summary>
public enum PreMutationDiagnosticSource
{
    /// <summary>Roslyn parse diagnostics for proposed source text.</summary>
    Syntax,

    /// <summary>Roslyn semantic model diagnostics for an affected document.</summary>
    Semantic,

    /// <summary>Roslyn compilation diagnostics for an affected project.</summary>
    Compilation,

    /// <summary>Trusted or isolated analyzer/code-style diagnostics.</summary>
    Analyzer,

    /// <summary>Host validation detected a proposal problem before Roslyn analysis.</summary>
    HostValidation,
}

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

/// <summary>One would-be file content in an in-memory pre-mutation overlay.</summary>
public sealed record PreMutationOverlayFile
{
    /// <summary>Slash-normalized repository-relative path.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Final text after applying the proposed mutation, or <see langword="null" /> when deleted.</summary>
    public string? Text { get; init; }

    /// <summary>Mutation most directly responsible for the final content.</summary>
    public MutationId? RelatedMutationId { get; init; }
}

/// <summary>Request for read-only Roslyn analysis over proposed mutation content.</summary>
public sealed record PreMutationAnalysisRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public RunId RunId { get; init; }

    /// <summary>Workspace whose semantic state should be used.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Immutable baseline that the mutation targets.</summary>
    public required WorkspaceBaseline Baseline { get; init; }

    /// <summary>Proposed mutation set being screened.</summary>
    public required MutationSet MutationSet { get; init; }

    /// <summary>In-memory final content for changed source files.</summary>
    public IReadOnlyList<PreMutationOverlayFile> OverlayFiles { get; init; } = [];
}

/// <summary>Focused host-owned diagnostic for pre-mutation proposal repair.</summary>
public sealed record PreMutationDiagnostic
{
    /// <summary>Diagnostic source.</summary>
    public required PreMutationDiagnosticSource Source { get; init; }

    /// <summary>Compiler or analyzer diagnostic code.</summary>
    public required string Code { get; init; }

    /// <summary>Normalized severity.</summary>
    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>Repository-relative file path when available.</summary>
    public string? File { get; init; }

    /// <summary>One-based source range when available.</summary>
    public SourceRange? Range { get; init; }

    /// <summary>Diagnostic message.</summary>
    public required string Message { get; init; }

    /// <summary>Owning project name when known.</summary>
    public string? Project { get; init; }

    /// <summary>Target framework when known.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Mutation most likely responsible for this diagnostic.</summary>
    public MutationId? RelatedMutationId { get; init; }

    /// <summary>Single changed source line or hunk-local excerpt for correction context.</summary>
    public string? ChangedHunk { get; init; }

    /// <summary>Containing type/member or syntax node where available.</summary>
    public string? ContainingSymbol { get; init; }
}

/// <summary>Advisory pre-mutation candidate score before expensive validation.</summary>
public sealed record MutationCandidateScore
{
    /// <summary>Whether syntax checks completed without blocking errors.</summary>
    public bool SyntaxClean { get; init; }

    /// <summary>Whether semantic/compilation checks completed without blocking errors.</summary>
    public bool SemanticClean { get; init; }

    /// <summary>Whether trusted analyzer checks completed without blocking diagnostics.</summary>
    public bool AnalyzerClean { get; init; }

    /// <summary>Whether every target file stayed within the approved plan scope.</summary>
    public bool ScopeClean { get; init; } = true;

    /// <summary>Total blocking diagnostics.</summary>
    public int BlockingDiagnosticCount { get; init; }
}

/// <summary>Result of a pre-mutation analysis screening pass.</summary>
public sealed record PreMutationAnalysisResult
{
    /// <summary>Final cheap-gate decision.</summary>
    public required PreMutationGateDecision Decision { get; init; }

    /// <summary>Focused diagnostics for proposal repair.</summary>
    public IReadOnlyList<PreMutationDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Explicit omitted or degraded checks.</summary>
    public IReadOnlyList<string> Omissions { get; init; } = [];

    /// <summary>Semantic confidence used while screening.</summary>
    public SemanticConfidenceLevel Confidence { get; init; } = SemanticConfidenceLevel.None;

    /// <summary>Advisory candidate score.</summary>
    public MutationCandidateScore Score { get; init; } = new();
}

/// <summary>Read-only pre-mutation analyzer for proposed C# changes.</summary>
public interface IPreMutationAnalyzer
{
    /// <summary>Runs bounded in-memory checks before staging or approval.</summary>
    Task<PreMutationAnalysisResult> AnalyzeAsync(
        PreMutationAnalysisRequest request,
        CancellationToken cancellationToken = default);
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
