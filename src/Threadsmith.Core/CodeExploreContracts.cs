namespace Threadsmith.Core;

/// <summary>Kind of exact anchor supplied to source-bearing code exploration.</summary>
public enum CodeExploreAnchorKind
{
    /// <summary>The required query was interpreted as an exact anchor.</summary>
    Query,

    /// <summary>An exact C# declaration name or qualified identity.</summary>
    SymbolName,

    /// <summary>A stable semantic symbol identity.</summary>
    SymbolId,

    /// <summary>A repository-relative C# path with an optional one-based line.</summary>
    Path,
}

/// <summary>Outcome for one exact code-exploration anchor.</summary>
public enum CodeExploreResolutionOutcome
{
    /// <summary>The anchor resolved to one selected declaration or path.</summary>
    Resolved,

    /// <summary>The anchor matched multiple material candidates.</summary>
    Ambiguous,

    /// <summary>The anchor did not match the loaded semantic workspace or repository path.</summary>
    NotFound,

    /// <summary>The anchor is outside the initial exact-anchor capability.</summary>
    Unsupported,

    /// <summary>The anchor resolved only to evidence that policy omitted.</summary>
    Omitted,
}

/// <summary>Exploration intent for source-bearing code exploration.</summary>
public enum CodeExploreMode
{
    /// <summary>Use host defaults for the provided exact anchors.</summary>
    Auto,

    /// <summary>Return source and a compact semantic survey around the anchors.</summary>
    Survey,

    /// <summary>Prioritize compiler-proven flow paths among exact anchors.</summary>
    Flow,

    /// <summary>Prioritize compact impact evidence for the exact anchors.</summary>
    Impact,
}

/// <summary>Path source-selection mode for source-bearing code exploration.</summary>
public enum CodeExplorePathSelectionMode
{
    /// <summary>Use the default path behavior: whole file without a line, containing declaration with a line, or tail when <see cref="CodeExplorePathAnchor.StartAtLine"/> is true.</summary>
    Auto,

    /// <summary>Select the whole C# source file.</summary>
    WholeFile,

    /// <summary>Select the containing declaration for <see cref="CodeExplorePathAnchor.Line"/>.</summary>
    ContainingDeclaration,

    /// <summary>Select exactly one line from <see cref="CodeExplorePathAnchor.Line"/>.</summary>
    SingleLine,

    /// <summary>Select source from <see cref="CodeExplorePathAnchor.Line"/> through the end of the file.</summary>
    TailWindow,

    /// <summary>Select the exact one-based line range from <see cref="CodeExplorePathAnchor.Line"/> through <see cref="CodeExplorePathAnchor.EndLine"/>.</summary>
    ExactLineRange,
}

/// <summary>Role assigned to a symbol in a bounded code-exploration flow slice.</summary>
public enum CodeExploreFlowNodeRole
{
    /// <summary>The symbol was explicitly named by a resolved anchor.</summary>
    NamedAnchor,

    /// <summary>The symbol connects named anchors through a compiler-proven call path.</summary>
    Connector,

    /// <summary>The symbol is a compiler-known implementation or override branch.</summary>
    DispatchBranch,
}

/// <summary>Static proof classification for a code-exploration flow edge.</summary>
public enum CodeExploreEdgeProofKind
{
    /// <summary>The compiler resolved the call relationship from loaded syntax and symbols.</summary>
    CompilerProvenCall,

    /// <summary>The compiler resolved the call site but runtime dispatch may choose a bounded implementation branch.</summary>
    CompilerKnownDispatchBoundary,
}

/// <summary>Boundary where static flow cannot safely invent a runtime continuation.</summary>
public enum CodeExploreFlowBoundaryKind
{
    /// <summary>An interface or virtual call has multiple possible runtime targets.</summary>
    RuntimeDispatch,

    /// <summary>A delegate invocation has no statically known target in the bounded slice.</summary>
    Delegate,

    /// <summary>The compiler could not classify the runtime target more precisely.</summary>
    Unknown,
}

/// <summary>Completeness for a returned source range.</summary>
public enum CodeExploreSourceCompleteness
{
    /// <summary>The complete selected declaration or requested range is present.</summary>
    Complete,

    /// <summary>The selected declaration or requested range was truncated by source limits.</summary>
    Partial,

    /// <summary>The source was not emitted.</summary>
    Omitted,

    /// <summary>The semantic span did not match current file content and was omitted.</summary>
    Drifted,
}

/// <summary>Explicit limits for one source-bearing code exploration.</summary>
public sealed record CodeExploreLimits
{
    /// <summary>Maximum exact anchors considered from the request.</summary>
    public int MaximumAnchors { get; init; } = 16;

    /// <summary>Maximum ambiguity alternatives retained per anchor.</summary>
    public int MaximumAlternatives { get; init; } = 20;

    /// <summary>Maximum source-bearing file sections returned.</summary>
    public int MaximumFiles { get; init; } = 8;

    /// <summary>Maximum total source characters returned across sections.</summary>
    public int MaximumSourceCharacters { get; init; } = 50_000;

    /// <summary>Maximum source characters returned for one file section.</summary>
    public int MaximumPerFileSourceCharacters { get; init; } = 16_384;

    /// <summary>Maximum selected flow paths among exact anchors.</summary>
    public int MaximumFlowPaths { get; init; } = 8;

    /// <summary>Maximum unnamed connector symbols retained across selected flow paths.</summary>
    public int MaximumFlowBridgeSymbols { get; init; } = 24;

    /// <summary>Maximum graph depth considered while finding compiler-proven flow paths.</summary>
    public int MaximumFlowDepth { get; init; } = 4;

    /// <summary>Maximum distinct flow nodes considered or returned.</summary>
    public int MaximumFlowNodes { get; init; } = 200;

    /// <summary>Maximum flow edges considered or returned.</summary>
    public int MaximumFlowEdges { get; init; } = 500;

    /// <summary>Maximum compiler-known dispatch branches returned.</summary>
    public int MaximumDispatchBranches { get; init; } = 24;

    /// <summary>Maximum compact blast-radius items returned.</summary>
    public int MaximumBlastRadiusItems { get; init; } = 32;

    /// <summary>Maximum elapsed query time in milliseconds.</summary>
    public int TimeoutMilliseconds { get; init; } = 10_000;
}

/// <summary>Repository-relative C# path anchor for source-bearing code exploration.</summary>
public sealed record CodeExplorePathAnchor
{
    /// <summary>Repository-relative path to a C# source file.</summary>
    public required string Path { get; init; }

    /// <summary>Optional one-based line used to select the containing declaration or a bounded region.</summary>
    public int? Line { get; init; }

    /// <summary>Optional inclusive one-based end line for exact source-window anchors.</summary>
    public int? EndLine { get; init; }

    /// <summary>Whether <see cref="Line"/> starts an exact source window instead of selecting its containing declaration.</summary>
    public bool StartAtLine { get; init; }

    /// <summary>Explicit path source-selection mode for replayable continuations.</summary>
    public CodeExplorePathSelectionMode SelectionMode { get; init; }

    /// <summary>Optional expected file digest copied from a prior continuation target.</summary>
    public string? ExpectedFileSha256 { get; init; }

    /// <summary>Optional expected semantic workspace generation copied from a prior continuation target.</summary>
    public long? ExpectedWorkspaceGeneration { get; init; }
}

/// <summary>Exact source-bearing code exploration request.</summary>
public sealed record CodeExploreRequest
{
    /// <summary>Bounded user/model query text; exact-looking values may be treated as anchors.</summary>
    public required string Query { get; init; }

    /// <summary>Exploration intent applied after exact anchors resolve.</summary>
    public CodeExploreMode Mode { get; init; }

    /// <summary>Exact simple, qualified, metadata, or documentation-comment symbol anchors.</summary>
    public IReadOnlyList<string> ExactSymbolAnchors { get; init; } = [];

    /// <summary>Stable semantic symbol identities returned by prior semantic tools.</summary>
    public IReadOnlyList<string> SymbolIds { get; init; } = [];

    /// <summary>Repository-relative C# path and optional line anchors.</summary>
    public IReadOnlyList<CodeExplorePathAnchor> PathAnchors { get; init; } = [];

    /// <summary>Explicit result, source, ambiguity, and time limits.</summary>
    public CodeExploreLimits Limits { get; init; } = new();
}

/// <summary>Repository-relative semantic location returned by code exploration.</summary>
public sealed record CodeExploreLocation(
    string ProjectName,
    string TargetFramework,
    string FilePath,
    SourceRange Range,
    bool IsGenerated,
    bool IsLinked);

/// <summary>One ambiguity alternative for an exact code-exploration anchor.</summary>
public sealed record CodeExploreAlternative(
    SemanticSymbolIdentity Symbol,
    CodeExploreLocation? Location);

/// <summary>Resolution details for one exact input anchor.</summary>
public sealed record CodeExploreAnchorResolution(
    string Input,
    CodeExploreAnchorKind Kind,
    CodeExploreResolutionOutcome Outcome,
    SemanticSymbolIdentity? SelectedSymbol,
    CodeExploreLocation? SelectedLocation,
    IReadOnlyList<CodeExploreAlternative> Alternatives,
    string Reason);

/// <summary>Returned source range with exact content identity.</summary>
public sealed record CodeExploreSourceRange(
    SourceRange Range,
    IReadOnlyList<string> NumberedLines,
    string? FileSha256,
    string? RangeSha256,
    CodeExploreSourceCompleteness Completeness,
    IReadOnlyList<string> OmittedRanges,
    string? ContinuationAnchor);

/// <summary>One grouped source section returned by code exploration.</summary>
public sealed record CodeExploreFileSection(
    string FilePath,
    string ProjectName,
    string TargetFramework,
    IReadOnlyList<SemanticSymbolIdentity> SemanticIdentities,
    CodeExploreSourceRange Source,
    bool IsGenerated,
    bool IsLinked,
    string SelectionReason);

/// <summary>Explicit continuation target for omitted or partially returned source.</summary>
public sealed record CodeExploreContinuationTarget(
    CodeExploreAnchorKind Kind,
    string Anchor,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    bool StartAtLine,
    CodeExplorePathSelectionMode? SelectionMode,
    string? ExpectedFileSha256,
    long? WorkspaceGeneration,
    string Reason);

/// <summary>Independent completeness dimensions for one code-exploration result.</summary>
public sealed record CodeExploreCoverage(
    bool SymbolResolutionComplete,
    bool CompiledProjectCoverageComplete,
    bool SourceComplete,
    bool OutputComplete,
    IReadOnlyList<string> Omissions);

/// <summary>One selected compiler-proven path between exact anchors.</summary>
public sealed record CodeExploreFlowPath(
    string FromSymbolId,
    string ToSymbolId,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<int> EdgeOrdinals,
    bool IsComplete,
    string Reason);

/// <summary>One semantic symbol node in a bounded code-exploration flow slice.</summary>
public sealed record CodeExploreFlowNode(
    SemanticSymbolIdentity Symbol,
    CodeExploreFlowNodeRole Role,
    int Depth,
    int? SourceSectionIndex,
    bool IsNamedAnchor,
    bool IsConnector,
    IReadOnlyList<CodeExploreLocation> Locations);

/// <summary>One compiler-proven call edge in a bounded code-exploration flow slice.</summary>
public sealed record CodeExploreFlowEdge(
    int Ordinal,
    string CallerSymbolId,
    string CalleeSymbolId,
    CallDispatchKind DispatchKind,
    CodeExploreLocation? CallSite,
    bool IsAmbiguous,
    bool ClosesCycle,
    CodeExploreEdgeProofKind ProofKind,
    string Proof);

/// <summary>One compiler-known implementation or override returned for a dispatch branch.</summary>
public sealed record CodeExploreDispatchTarget(
    SemanticSymbolIdentity Symbol,
    CodeExploreLocation? Location,
    int? SourceSectionIndex);

/// <summary>Bounded compiler-known implementations or overrides for one ambiguous dispatch root.</summary>
public sealed record CodeExploreDispatchBranch(
    SemanticSymbolIdentity DispatchRoot,
    CodeExploreLocation? CallSite,
    IReadOnlyList<CodeExploreDispatchTarget> Implementations,
    int ReturnedCount,
    int TotalCount,
    IReadOnlyList<string> Omissions);

/// <summary>Static-analysis boundary where code exploration stops instead of inventing runtime flow.</summary>
public sealed record CodeExploreFlowBoundary(
    CodeExploreFlowBoundaryKind Kind,
    string SymbolId,
    CodeExploreLocation? CallSite,
    string Reason,
    IReadOnlyList<string> ContinuationAnchors);

/// <summary>Bounded compiler-proven flow evidence for exact code-exploration anchors.</summary>
public sealed record CodeExploreFlow(
    IReadOnlyList<CodeExploreFlowPath> Paths,
    IReadOnlyList<CodeExploreFlowNode> Nodes,
    IReadOnlyList<CodeExploreFlowEdge> Edges,
    IReadOnlyList<CodeExploreDispatchBranch> DispatchBranches,
    IReadOnlyList<CodeExploreFlowBoundary> Boundaries,
    SemanticTraversalSummary Traversal);

/// <summary>One compact blast-radius evidence item for a primary code-exploration anchor.</summary>
public sealed record CodeExploreBlastRadiusItem(
    string AnchorSymbolId,
    ImpactKind Kind,
    SemanticSymbolIdentity? Symbol,
    CodeExploreLocation? Location,
    string? ProjectName,
    string Reason);

/// <summary>Compact impact evidence attached to a source-bearing code-exploration result.</summary>
public sealed record CodeExploreBlastRadius(
    IReadOnlyList<CodeExploreBlastRadiusItem> Items,
    int ReturnedCallers,
    int TotalCallers,
    int ReturnedImplementations,
    int TotalImplementations,
    int ReturnedProjects,
    int TotalProjects,
    int ReturnedTests,
    int TotalTests,
    IReadOnlyList<string> Omissions,
    IReadOnlyList<CodeExploreContinuationTarget> ContinuationTargets);

/// <summary>Source-bearing exact code exploration result fenced to one semantic workspace generation.</summary>
public sealed record CodeExploreResult(
    long WorkspaceGeneration,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<CodeExploreAnchorResolution> ResolvedAnchors,
    IReadOnlyList<CodeExploreFileSection> FileSections,
    CodeExploreCoverage Coverage,
    IReadOnlyList<string> Omissions,
    IReadOnlyList<CodeExploreContinuationTarget> ContinuationTargets,
    CodeExploreFlow? Flow = null,
    CodeExploreBlastRadius? BlastRadius = null);

/// <summary>Bounded current source text read through the host tool policy boundary.</summary>
public sealed record CodeExploreSourceText(
    string Path,
    string Text,
    string FileSha256);

/// <summary>Policy-owned source reader used by code exploration for current file identity checks.</summary>
public interface ICodeExploreSourceReader
{
    /// <summary>Returns whether the path is authorized for source inspection before any content read occurs.</summary>
    bool IsPathAllowed(string path);

    /// <summary>Reads a bounded current source file after policy, regular-file, and size checks.</summary>
    Task<CodeExploreSourceText> ReadTextAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>Workspace-scoped exact source-bearing code exploration.</summary>
public interface ICodeExploreService
{
    /// <summary>Resolves exact C# anchors and projects bounded current source from one semantic generation.</summary>
    Task<CodeExploreResult> QueryCodeExploreAsync(
        WorkspaceId workspaceId,
        CodeExploreRequest request,
        ICodeExploreSourceReader sourceReader,
        CancellationToken cancellationToken = default);
}
