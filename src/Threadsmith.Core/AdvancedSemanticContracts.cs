namespace Threadsmith.Core;

/// <summary>Direction used while traversing a semantic call hierarchy.</summary>
public enum CallHierarchyDirection
{
    /// <summary>Return callers of the root and subsequently discovered symbols.</summary>
    Incoming,

    /// <summary>Return callees of the root and subsequently discovered symbols.</summary>
    Outgoing,

    /// <summary>Return both incoming and outgoing edges.</summary>
    Both,
}

/// <summary>Compiler-known dispatch classification for one call edge.</summary>
public enum CallDispatchKind
{
    /// <summary>A non-virtual direct method call.</summary>
    Direct,

    /// <summary>A static method call.</summary>
    Static,

    /// <summary>A constructor call.</summary>
    Constructor,

    /// <summary>An interface member call whose runtime target may vary.</summary>
    Interface,

    /// <summary>A virtual or abstract member call whose runtime target may vary.</summary>
    Virtual,

    /// <summary>An extension-method call.</summary>
    Extension,

    /// <summary>A local-function call.</summary>
    LocalFunction,

    /// <summary>A delegate invocation whose runtime target is unknown.</summary>
    Delegate,

    /// <summary>The compiler could not classify dispatch more precisely.</summary>
    Unknown,
}

/// <summary>Explicit bounds for a semantic graph traversal.</summary>
public sealed record SemanticTraversalLimits
{
    /// <summary>Maximum graph depth, where zero returns only direct edges.</summary>
    public int MaximumDepth { get; init; } = 2;

    /// <summary>Maximum distinct returned nodes.</summary>
    public int MaximumNodes { get; init; } = 200;

    /// <summary>Maximum returned edges.</summary>
    public int MaximumEdges { get; init; } = 500;

    /// <summary>Maximum elapsed query time in milliseconds.</summary>
    public int TimeoutMilliseconds { get; init; } = 10_000;
}

/// <summary>Requests a bounded call hierarchy rooted at one stable semantic symbol.</summary>
public sealed record CallHierarchyRequest
{
    /// <summary>Stable documentation-comment symbol id returned by semantic discovery.</summary>
    public required string SymbolId { get; init; }

    /// <summary>Requested traversal direction.</summary>
    public CallHierarchyDirection Direction { get; init; } = CallHierarchyDirection.Both;

    /// <summary>Traversal limits.</summary>
    public SemanticTraversalLimits Limits { get; init; } = new();
}

/// <summary>One symbol node in a call hierarchy.</summary>
public sealed record CallHierarchyNode(
    SemanticSymbolIdentity Symbol,
    IReadOnlyList<SemanticSourceLocation> Locations,
    int Depth);

/// <summary>One source-proven call relationship.</summary>
public sealed record CallHierarchyEdge(
    string CallerSymbolId,
    string CalleeSymbolId,
    CallDispatchKind DispatchKind,
    SemanticSourceLocation? CallSite,
    bool IsAmbiguous,
    bool ClosesCycle);

/// <summary>Summary of bounded traversal work and omitted evidence.</summary>
public sealed record SemanticTraversalSummary(
    int VisitedNodes,
    int ReturnedEdges,
    bool IsComplete,
    bool DepthLimitReached,
    bool NodeLimitReached,
    bool EdgeLimitReached,
    bool TimeLimitReached,
    IReadOnlyList<string> Omissions);

/// <summary>Bounded call hierarchy result fenced to one semantic workspace generation.</summary>
public sealed record CallHierarchyResult(
    long WorkspaceGeneration,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<CallHierarchyNode> Nodes,
    IReadOnlyList<CallHierarchyEdge> Edges,
    SemanticTraversalSummary Traversal);

/// <summary>Relationship represented by a symbol-impact node.</summary>
public enum ImpactKind
{
    /// <summary>The requested root symbol.</summary>
    RootSymbol,

    /// <summary>A source reference.</summary>
    Reference,

    /// <summary>An incoming caller.</summary>
    Caller,

    /// <summary>An implementation or override.</summary>
    Implementation,

    /// <summary>A containing or dependent project.</summary>
    Project,

    /// <summary>A project classified as a test project.</summary>
    Test,

    /// <summary>A diagnostic related to the symbol or source.</summary>
    Diagnostic,

    /// <summary>A generated document.</summary>
    GeneratedDocument,

    /// <summary>A linked source document.</summary>
    LinkedDocument,
}

/// <summary>Requests explainable bounded impact evidence for one semantic symbol.</summary>
public sealed record SymbolImpactRequest
{
    /// <summary>Stable semantic symbol id.</summary>
    public required string SymbolId { get; init; }

    /// <summary>Traversal limits.</summary>
    public SemanticTraversalLimits Limits { get; init; } = new();
}

/// <summary>One host-owned node in an impact graph.</summary>
public sealed record ImpactNode(
    string Id,
    string DisplayName,
    ImpactKind Kind,
    SemanticSourceLocation? Location,
    string? ProjectName);

/// <summary>One reasoned impact relationship.</summary>
public sealed record ImpactEdge(
    string FromId,
    string ToId,
    ImpactKind Kind,
    string Reason);

/// <summary>Explainable bounded impact result; it is evidence rather than whole-program proof.</summary>
public sealed record SymbolImpactResult(
    long WorkspaceGeneration,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<ImpactNode> Nodes,
    IReadOnlyList<ImpactEdge> Edges,
    SemanticTraversalSummary Traversal);

/// <summary>Closed syntax shape supported by structural C# search.</summary>
public enum CSharpPatternKind
{
    /// <summary>Any declaration.</summary>
    Declaration,

    /// <summary>A type declaration.</summary>
    TypeDeclaration,

    /// <summary>A method declaration.</summary>
    MethodDeclaration,

    /// <summary>A property declaration.</summary>
    PropertyDeclaration,

    /// <summary>A field declaration.</summary>
    FieldDeclaration,

    /// <summary>An attribute.</summary>
    Attribute,

    /// <summary>An invocation expression.</summary>
    Invocation,

    /// <summary>An object-creation expression.</summary>
    ObjectCreation,

    /// <summary>A member-access expression.</summary>
    MemberAccess,
}

/// <summary>Versioned inert C# syntax predicate. Values are names and closed modifiers, never executable text.</summary>
public sealed record CSharpPattern
{
    /// <summary>Pattern schema version. Version 1 is currently supported.</summary>
    public int? Version { get; init; } = 1;

    /// <summary>Required syntax shape.</summary>
    public CSharpPatternKind Kind { get; init; }

    /// <summary>Optional exact simple identifier.</summary>
    public string? Name { get; init; }

    /// <summary>Optional exact containing type name.</summary>
    public string? ContainingType { get; init; }

    /// <summary>Required closed C# modifiers, such as public, static, async, or partial.</summary>
    public IReadOnlyList<string>? RequiredModifiers { get; init; } = [];

    /// <summary>Required exact attribute simple names, with or without the Attribute suffix.</summary>
    public IReadOnlyList<string>? RequiredAttributes { get; init; } = [];

    /// <summary>Optional capture name for the complete matched node.</summary>
    public string? Capture { get; init; }
}

/// <summary>Requests bounded syntax-aware C# pattern matching.</summary>
public sealed record CSharpPatternSearchRequest
{
    /// <summary>Closed structured pattern.</summary>
    public required CSharpPattern Pattern { get; init; }

    /// <summary>Optional repository-relative file or directory scope.</summary>
    public string? Path { get; init; }

    /// <summary>Maximum returned matches.</summary>
    public int MaximumMatches { get; init; } = 200;

    /// <summary>Maximum elapsed query time in milliseconds.</summary>
    public int TimeoutMilliseconds { get; init; } = 10_000;
}

/// <summary>One named structural capture and source range.</summary>
public sealed record CSharpPatternCapture(string Name, SourceRange Range, string Text);

/// <summary>One structural C# match with bounded captures.</summary>
public sealed record CSharpPatternMatch(
    CSharpPatternKind Kind,
    SemanticSourceLocation Location,
    IReadOnlyList<CSharpPatternCapture> Captures);

/// <summary>Bounded structural-search result.</summary>
public sealed record CSharpPatternSearchResult(
    long WorkspaceGeneration,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<CSharpPatternMatch> Matches,
    bool IsComplete,
    IReadOnlyList<string> Omissions);

/// <summary>Known origin category for a generated document.</summary>
public enum GeneratedCodeOrigin
{
    /// <summary>Origin metadata is unavailable.</summary>
    Unknown,

    /// <summary>The document was identified by a generated-file naming/path convention.</summary>
    FileConvention,

    /// <summary>Roslyn exposes the document as source-generator output.</summary>
    SourceGenerator,

    /// <summary>The compiler or SDK supplied the document.</summary>
    CompilerOrSdk,
}

/// <summary>Queries already-loaded generated documents without running generators.</summary>
public sealed record GeneratedCodeQuery
{
    /// <summary>Optional repository-relative project or document path filter.</summary>
    public string? Path { get; init; }

    /// <summary>Whether bounded source content is included.</summary>
    public bool IncludeContent { get; init; }

    /// <summary>Maximum returned documents.</summary>
    public int MaximumDocuments { get; init; } = 100;

    /// <summary>Maximum content characters per returned document.</summary>
    public int MaximumContentCharacters { get; init; } = 16_384;
}

/// <summary>Host-owned metadata and optional bounded content for one generated document.</summary>
public sealed record GeneratedDocumentInfo(
    string Id,
    string Name,
    string ProjectName,
    string FilePath,
    bool IsLinked,
    GeneratedCodeOrigin Origin,
    string? OriginName,
    string? Content,
    bool ContentTruncated);

/// <summary>Generated-code inventory fenced to one semantic generation.</summary>
public sealed record GeneratedCodeResult(
    long WorkspaceGeneration,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<GeneratedDocumentInfo> Documents,
    bool IsComplete,
    IReadOnlyList<string> Omissions);

/// <summary>Workspace-scoped advanced semantic queries implemented by the compiler-aware subsystem.</summary>
public interface IAdvancedSemanticQueryService
{
    /// <summary>Builds a bounded incoming/outgoing call hierarchy.</summary>
    Task<CallHierarchyResult> QueryCallHierarchyAsync(
        WorkspaceId workspaceId,
        CallHierarchyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Builds an explainable bounded impact graph.</summary>
    Task<SymbolImpactResult> QuerySymbolImpactAsync(
        WorkspaceId workspaceId,
        SymbolImpactRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a closed inert structural C# query.</summary>
    Task<CSharpPatternSearchResult> SearchCSharpPatternAsync(
        WorkspaceId workspaceId,
        CSharpPatternSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Inspects generated documents already present in the loaded workspace.</summary>
    Task<GeneratedCodeResult> QueryGeneratedCodeAsync(
        WorkspaceId workspaceId,
        GeneratedCodeQuery request,
        CancellationToken cancellationToken = default);
}
