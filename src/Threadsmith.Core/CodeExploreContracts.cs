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

/// <summary>Source-bearing exact code exploration result fenced to one semantic workspace generation.</summary>
public sealed record CodeExploreResult(
    long WorkspaceGeneration,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<CodeExploreAnchorResolution> ResolvedAnchors,
    IReadOnlyList<CodeExploreFileSection> FileSections,
    CodeExploreCoverage Coverage,
    IReadOnlyList<string> Omissions,
    IReadOnlyList<CodeExploreContinuationTarget> ContinuationTargets);

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
