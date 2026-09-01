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

/// <summary>Request policy for associated non-C# artifacts returned by code exploration.</summary>
public enum CodeExploreAssociatedArtifactsMode
{
    /// <summary>Discover bounded artifacts automatically when the selected C# slice provides relationship evidence.</summary>
    Auto,

    /// <summary>Do not discover or project associated non-C# artifacts.</summary>
    Disabled,

    /// <summary>Discover and project associated artifacts whenever relationship evidence is available.</summary>
    Enabled,
}

/// <summary>Closed relationship between a selected C# source anchor and an associated artifact.</summary>
public enum CodeExploreArtifactRelationshipKind
{
    /// <summary>The artifact path was explicitly supplied by the request.</summary>
    ExplicitPath,

    /// <summary>The artifact is a Roslyn additional document in the selected project.</summary>
    AdditionalDocument,

    /// <summary>The artifact is a Roslyn analyzer configuration document in the selected project.</summary>
    AnalyzerConfiguration,

    /// <summary>The artifact is project metadata associated with the selected project.</summary>
    ProjectItem,

    /// <summary>The artifact is a project resource associated with the selected project.</summary>
    ProjectResource,

    /// <summary>The artifact path or file name came from a selected C# source literal.</summary>
    SourceLiteralPath,

    /// <summary>The selected C# source references a configuration key, section, or checked-in configuration artifact.</summary>
    ConfigurationReference,

    /// <summary>The selected C# source references a prompt, template, or response artifact.</summary>
    PromptReference,

    /// <summary>The artifact was found by bounded exact-name lookup from a proven source literal.</summary>
    BoundedExactNameInference,
}

/// <summary>Evidence strength for an associated artifact relationship.</summary>
public enum CodeExploreArtifactEvidenceLevel
{
    /// <summary>The artifact path was explicitly requested and then confined by host policy.</summary>
    ExplicitRequest,

    /// <summary>The relationship is anchored in selected C# syntax or semantic evidence.</summary>
    CompilerProven,

    /// <summary>The relationship comes from already-loaded project or workspace metadata.</summary>
    ProjectProven,

    /// <summary>The relationship comes from a safe literal inside selected C# source.</summary>
    SourceLiteral,

    /// <summary>The relationship comes from bounded textual inference under a proven source/project scope, such as exact-name lookup or inert project metadata text.</summary>
    BoundedTextualInference,
}

/// <summary>Closed textual media classification for associated artifacts.</summary>
public enum CodeExploreArtifactMediaKind
{
    /// <summary>JSON or JSON-with-comments source data.</summary>
    Json,

    /// <summary>XML source data, including project and resource metadata.</summary>
    Xml,

    /// <summary>.NET project metadata source such as project, props, or targets files.</summary>
    ProjectMetadata,

    /// <summary>Markdown source text.</summary>
    Markdown,

    /// <summary>Prompt or template source text.</summary>
    Prompt,

    /// <summary>Configuration source text.</summary>
    Configuration,

    /// <summary>Schema source text.</summary>
    Schema,

    /// <summary>Other inert bounded text.</summary>
    Text,
}

/// <summary>Deterministic ranking tier assigned to an inferred code-exploration candidate.</summary>
public enum CodeExploreCandidateTier
{
    /// <summary>The candidate was explicitly pinned by a path, stable id, or exact anchor.</summary>
    Pinned,

    /// <summary>The candidate matched an exact qualified declaration identity.</summary>
    ExactQualified,

    /// <summary>The candidate matched a distinctive declaration identifier.</summary>
    DistinctiveIdentifier,

    /// <summary>The candidate matched multiple independent declaration, path, project, or container terms.</summary>
    MultiTermStructural,

    /// <summary>The candidate is connected to higher-ranked selected anchors through compiler-known structure.</summary>
    GraphConnected,

    /// <summary>The candidate is a lower-ranked single-term or peripheral match.</summary>
    Peripheral,
}

/// <summary>Closed reason flags explaining why an inferred candidate was selected or retained.</summary>
[Flags]
public enum CodeExploreSelectionReason
{
    /// <summary>No selection reason was recorded.</summary>
    None = 0,

    /// <summary>The candidate was explicitly pinned by the request.</summary>
    Pinned = 1 << 0,

    /// <summary>The candidate matched an exact declaration identifier.</summary>
    ExactIdentifier = 1 << 1,

    /// <summary>The candidate matched an exact qualified name or container-qualified name.</summary>
    QualifiedName = 1 << 2,

    /// <summary>The candidate's containing type or namespace corroborated another query term.</summary>
    ContainingType = 1 << 3,

    /// <summary>The candidate covered multiple independent query terms.</summary>
    MultiTerm = 1 << 4,

    /// <summary>Multiple candidates in the same file corroborated one another.</summary>
    CoLocated = 1 << 5,

    /// <summary>The candidate was connected to selected anchors through compiler-known graph evidence.</summary>
    GraphConnected = 1 << 6,

    /// <summary>The candidate is part of the returned flow spine.</summary>
    FlowSpine = 1 << 7,

    /// <summary>The candidate is a compiler-known implementation or override branch.</summary>
    Implementation = 1 << 8,

    /// <summary>The candidate is a compiler-known caller of a selected anchor.</summary>
    Caller = 1 << 9,

    /// <summary>The candidate's project or file classification matched explicit user focus.</summary>
    UserFocus = 1 << 10,

    /// <summary>The candidate is in a test project or test-classified path.</summary>
    Test = 1 << 11,

    /// <summary>The candidate is in generated source.</summary>
    Generated = 1 << 12,

    /// <summary>The candidate matched a repository-relative C# path span.</summary>
    Path = 1 << 13,

    /// <summary>The candidate remained only as peripheral follow-up evidence.</summary>
    Peripheral = 1 << 14,

    /// <summary>Query concepts matched catalog-derived segments of the declaration name.</summary>
    NameSegment = 1 << 15,
}

/// <summary>Recoverable availability state for source-bearing code exploration.</summary>
public enum CodeExploreAvailabilityStatus
{
    /// <summary>The semantic workspace was available and the result contains ordinary code-exploration evidence.</summary>
    Available,

    /// <summary>No workspace identity was attached to the invocation.</summary>
    NoWorkspaceOpen,

    /// <summary>The semantic workspace identity exists but compiler-aware state is not available yet.</summary>
    SemanticWorkspaceUnavailable,

    /// <summary>The semantic workspace confidence is below the minimum required for source-bearing exploration.</summary>
    SemanticReadinessBelowMinimum,

    /// <summary>No loaded C# project currently has a usable compilation.</summary>
    NoCompiledProjects,

    /// <summary>The request did not match compiler-known declarations or confined C# paths.</summary>
    NoMatchingDeclarations,

    /// <summary>Policy removed all otherwise relevant source from the result.</summary>
    NoSourceAfterPolicy,

    /// <summary>The invocation timed out after returning partial safe evidence.</summary>
    TimedOutPartial,

    /// <summary>The loaded workspace is one project, so repository-wide source and usage coverage is incomplete.</summary>
    ProjectScopedPartial,
}

/// <summary>Closed advisory action kinds emitted for model-facing code-exploration recovery.</summary>
public enum CodeExploreNextActionKind
{
    /// <summary>Use the returned current source for the advertised line ranges.</summary>
    UseReturnedSource,

    /// <summary>Use unchanged source already visible in the current model request.</summary>
    UseBackReference,

    /// <summary>Retry with an exact continuation target returned by this result.</summary>
    FollowContinuation,

    /// <summary>Refine the request with a more exact symbol, stable id, or path anchor.</summary>
    RefineAnchor,

    /// <summary>Open or select a workspace before retrying semantic exploration.</summary>
    OpenWorkspace,

    /// <summary>Wait for semantic loading or compilation readiness before retrying.</summary>
    WaitForWorkspace,

    /// <summary>Use a narrower non-semantic tool for a specific remaining gap.</summary>
    UseGranularFallback,

    /// <summary>Ask the user for the missing workspace, anchor, or scope decision.</summary>
    AskUser,
}

/// <summary>Source guarantee category derived from authoritative source and frontier fields.</summary>
public enum CodeExploreSourceGuaranteeKind
{
    /// <summary>The range is current line-numbered source projected through host output sanitization and backed by source digests for that range.</summary>
    ReadEquivalent,

    /// <summary>The range is current line-numbered source projected through host output sanitization but only partially covers the selected span.</summary>
    Partial,

    /// <summary>The source range was omitted before text was emitted.</summary>
    Omitted,

    /// <summary>The semantic span or continuation cursor drifted from current file identity.</summary>
    Drifted,

    /// <summary>The range is unchanged source already present in the current model request.</summary>
    BackReference,
}

/// <summary>Closed target kind for model-facing not-shown code-exploration evidence.</summary>
public enum CodeExploreNotShownTargetKind
{
    /// <summary>C# source was not emitted or was emitted only partially.</summary>
    Source,

    /// <summary>A compiler-known declaration was selected or discovered but not source-emitted.</summary>
    Symbol,

    /// <summary>An associated artifact was not emitted or was emitted only partially.</summary>
    Artifact,

    /// <summary>The result has a broader omission not tied to one exact file range.</summary>
    General,
}

/// <summary>Repository scale tier used to adapt code-exploration presentation and default source envelopes.</summary>
public enum CodeExploreRepositoryScaleTier
{
    /// <summary>No compiler-aware C# project inventory was available.</summary>
    Unknown,

    /// <summary>A very small repository or workspace slice.</summary>
    Tiny,

    /// <summary>A small repository or workspace slice.</summary>
    Small,

    /// <summary>A medium repository or workspace slice.</summary>
    Medium,

    /// <summary>A large repository or workspace slice.</summary>
    Large,

    /// <summary>A very large repository or workspace slice.</summary>
    VeryLarge,
}

/// <summary>Bounded prose verbosity selected for model-facing code-exploration presentation.</summary>
public enum CodeExplorePresentationVerbosity
{
    /// <summary>Emit only the highest-value presentation hints.</summary>
    Compact,

    /// <summary>Emit the normal bounded presentation shape.</summary>
    Standard,

    /// <summary>Emit additional not-shown guidance to prevent scattershot follow-ups.</summary>
    Guided,
}

/// <summary>Coarse relevance band for one source file considered by code exploration.</summary>
public enum CodeExploreFileRelevanceBand
{
    /// <summary>The file was explicitly pinned or contains primary flow-spine source.</summary>
    Primary,

    /// <summary>The file has strong structural relevance and should usually receive source.</summary>
    Strong,

    /// <summary>The file is useful supporting evidence.</summary>
    Supporting,

    /// <summary>The file is weak tail evidence and should usually be named by continuation only.</summary>
    Peripheral,
}

/// <summary>Final model-visible source outcome for one relevance-ranked file.</summary>
public enum CodeExploreFileOutputStatus
{
    /// <summary>The final output state was not classified.</summary>
    Unknown,

    /// <summary>The final result contains source lines for this file.</summary>
    SourceReturned,

    /// <summary>The final result references source already visible in the current model request.</summary>
    BackReferenceOnly,

    /// <summary>The file is named by an exact continuation target without returned source lines.</summary>
    ContinuationOnly,

    /// <summary>The file was omitted by policy, drift, or source-read safety checks.</summary>
    OmittedByPolicyOrSafety,

    /// <summary>The file source was removed during final model-budget trimming.</summary>
    RemovedByModelBudget,
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

    /// <summary>Maximum associated non-C# artifacts returned.</summary>
    public int MaximumAssociatedArtifacts { get; init; } = 4;

    /// <summary>Maximum associated artifact candidates considered before ranking and projection.</summary>
    public int MaximumAssociatedArtifactCandidates { get; init; } = 24;

    /// <summary>Maximum total associated artifact characters returned.</summary>
    public int MaximumAssociatedArtifactCharacters { get; init; } = 12_000;

    /// <summary>Maximum associated artifact characters returned for one artifact.</summary>
    public int MaximumPerAssociatedArtifactCharacters { get; init; } = 4_096;

    /// <summary>Maximum bytes read from one associated artifact for content identity and projection.</summary>
    public int MaximumAssociatedArtifactBytes { get; init; } = 128 * 1024;

    /// <summary>Maximum exact-name artifact matches retained from one bounded directory lookup.</summary>
    public int MaximumAssociatedArtifactNameMatches { get; init; } = 8;

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

/// <summary>Repository-relative associated-artifact path anchor for source-bearing code exploration.</summary>
public sealed record CodeExploreArtifactPathAnchor
{
    /// <summary>Repository-relative path to a supported textual artifact.</summary>
    public required string Path { get; init; }

    /// <summary>Optional one-based start line for an exact artifact range.</summary>
    public int? Line { get; init; }

    /// <summary>Optional inclusive one-based end line for an exact artifact range.</summary>
    public int? EndLine { get; init; }

    /// <summary>Optional expected file digest copied from a prior artifact continuation target.</summary>
    public string? ExpectedFileSha256 { get; init; }

    /// <summary>Optional expected semantic workspace generation copied from a prior artifact continuation target.</summary>
    public long? ExpectedWorkspaceGeneration { get; init; }
}

/// <summary>Exact source-bearing code exploration request.</summary>
public sealed record CodeExploreRequest
{
    /// <summary>Bounded user/model query text; exact-looking values may be treated as anchors.</summary>
    public required string Query { get; init; }

    /// <summary>Exploration intent applied after exact anchors resolve.</summary>
    public CodeExploreMode Mode { get; init; }

    /// <summary>Associated non-C# artifact discovery policy.</summary>
    public CodeExploreAssociatedArtifactsMode AssociatedArtifacts { get; init; }

    /// <summary>Exact simple, qualified, metadata, or documentation-comment symbol anchors.</summary>
    public IReadOnlyList<string> ExactSymbolAnchors { get; init; } = [];

    /// <summary>Stable semantic symbol identities returned by prior semantic tools.</summary>
    public IReadOnlyList<string> SymbolIds { get; init; } = [];

    /// <summary>Repository-relative C# path and optional line anchors.</summary>
    public IReadOnlyList<CodeExplorePathAnchor> PathAnchors { get; init; } = [];

    /// <summary>Explicit repository-relative non-C# artifact path anchors.</summary>
    public IReadOnlyList<CodeExploreArtifactPathAnchor> AssociatedArtifactPathAnchors { get; init; } = [];

    /// <summary>Explicit result, source, ambiguity, and time limits.</summary>
    public CodeExploreLimits Limits { get; init; } = new();
}

/// <summary>Bounded deterministic interpretation of a natural-language code-exploration query.</summary>
public sealed record CodeExploreQueryInterpretation(
    IReadOnlyList<string> ExactIdentifiers,
    IReadOnlyList<string> QualifiedNames,
    IReadOnlyList<string> StableSymbolIds,
    IReadOnlyList<string> PathLikeSpans,
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> IgnoredTerms,
    IReadOnlyList<string> UnresolvedTerms);

/// <summary>Bounded summary of declaration-catalog and candidate discovery for a code-exploration query.</summary>
public sealed record CodeExploreDiscoverySummary(
    int CatalogEntryCount,
    int CandidateCount,
    int SelectedCount,
    bool CatalogComplete,
    bool CandidateLimitReached,
    IReadOnlyList<string> AmbiguityGroups,
    string BudgetSource);

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

/// <summary>One selected or omitted deterministic candidate considered by natural-language code exploration.</summary>
public sealed record CodeExploreCandidateSummary(
    SemanticSymbolIdentity? Symbol,
    CodeExploreLocation? Location,
    string? FilePath,
    CodeExploreCandidateTier Tier,
    CodeExploreSelectionReason Reasons,
    int Rank,
    bool Selected,
    string Reason,
    string? AmbiguityGroup);

/// <summary>One advisory next action derived from authoritative code-exploration result state.</summary>
public sealed record CodeExploreNextActionHint(
    CodeExploreNextActionKind Kind,
    string Message,
    string? FilePath = null,
    SourceRange? Range = null,
    string? ContinuationAnchor = null);

/// <summary>Availability and safe recovery guidance for one code-exploration invocation.</summary>
public sealed record CodeExploreAvailability(
    CodeExploreAvailabilityStatus Status,
    string Reason,
    bool IsRetryable,
    SemanticConfidenceLevel? CurrentReadiness,
    SemanticConfidenceLevel? MinimumReadinessRequired,
    bool GranularFallbackMayHelp,
    IReadOnlyList<CodeExploreNextActionHint> RecommendedActions);

/// <summary>Per-range source guarantee derived from returned source or current-request back-references.</summary>
public sealed record CodeExploreSourceGuarantee(
    CodeExploreSourceGuaranteeKind Kind,
    string FilePath,
    SourceRange Range,
    bool IsCurrent,
    bool IsVerbatim,
    bool IsLineNumbered,
    bool IsReadEquivalent,
    string? FileSha256,
    string? RangeSha256,
    IReadOnlyList<string> SymbolIds,
    string Message);

/// <summary>Bounded target intentionally not emitted as new source in a code-exploration result.</summary>
public sealed record CodeExploreNotShownTarget(
    CodeExploreNotShownTargetKind Kind,
    string? FilePath,
    SourceRange? Range,
    string Reason,
    string? ContinuationAnchor = null,
    string? ExpectedFileSha256 = null,
    long? WorkspaceGeneration = null);

/// <summary>Bounded model-facing presentation synthesized from authoritative code-exploration fields.</summary>
public sealed record CodeExplorePresentation(
    string ModelSummary,
    IReadOnlyList<CodeExploreSourceGuarantee> SourceGuarantees,
    IReadOnlyList<CodeExploreNotShownTarget> NotShownTargets,
    IReadOnlyList<CodeExploreNextActionHint> NextActions);

/// <summary>Repository scale inputs used to adapt code-exploration defaults and presentation.</summary>
public sealed record CodeExploreRepositoryScale(
    CodeExploreRepositoryScaleTier Tier,
    int ProjectCount,
    int CompiledProjectCount,
    int TotalCSharpDocumentCount,
    int CompiledCSharpDocumentCount,
    int GeneratedCSharpDocumentCount,
    int? TargetFrameworkCount,
    int? DeclarationCatalogEntryCount,
    bool? DeclarationCatalogComplete,
    int AssociatedArtifactCandidateCount);

/// <summary>Effective code-exploration output envelope after repository-scale and model-budget clamps.</summary>
public sealed record CodeExploreAdaptiveBudget(
    CodeExploreRepositoryScale RepositoryScale,
    int EffectiveMaximumFiles,
    int EffectiveMaximumSourceCharacters,
    int EffectiveMaximumPerFileSourceCharacters,
    int EffectiveMaximumCandidateSummaries,
    int RecommendedFollowUpCount,
    CodeExplorePresentationVerbosity PresentationVerbosity,
    string BudgetSource);

/// <summary>Inspectable file-level relevance and source-allocation outcome without source text.</summary>
public sealed record CodeExploreFileRelevanceSummary(
    string FilePath,
    int Rank,
    CodeExploreFileRelevanceBand Band,
    CodeExploreSelectionReason Reasons,
    int SelectedSymbolCount,
    int QueryTermCoverage,
    bool SourceCliffed,
    int AllocatedCharacters,
    int SpentCharacters,
    string Reason,
    CodeExploreFileOutputStatus OutputStatus = CodeExploreFileOutputStatus.Unknown);

/// <summary>Per-section source allocation outcome for code exploration.</summary>
public sealed record CodeExploreAllocationFileSummary(
    string FilePath,
    int AllowedCharacters,
    int SpentCharacters,
    CodeExploreSourceCompleteness Completeness,
    bool UsefulSection,
    string? OmissionReason);

/// <summary>Source-budget allocation summary for one code-exploration result.</summary>
public sealed record CodeExploreAllocationSummary(
    int TotalSourceCharacters,
    int ReservedCharacters,
    int SpentSourceCharacters,
    string BudgetSource,
    IReadOnlyList<CodeExploreAllocationFileSummary> Files);

/// <summary>Bounded textual content returned for an associated artifact.</summary>
public sealed record CodeExploreArtifactContent(
    SourceRange Range,
    IReadOnlyList<string> NumberedLines,
    string FileSha256,
    string? RangeSha256,
    CodeExploreSourceCompleteness Completeness,
    IReadOnlyList<string> OmittedRanges,
    string? ContinuationAnchor,
    int ReturnedCharacters);

/// <summary>One continuation target for omitted or partially returned associated artifact content.</summary>
public sealed record CodeExploreArtifactContinuationTarget(
    string FilePath,
    int? StartLine,
    int? EndLine,
    string? ExpectedFileSha256,
    long? WorkspaceGeneration,
    string Reason)
{
    /// <summary>Semantic source symbol that established the artifact relationship, when known.</summary>
    public string? OriginSymbolId { get; init; }

    /// <summary>Repository-relative semantic source path that established the artifact relationship, when known.</summary>
    public string? OriginFilePath { get; init; }

    /// <summary>Semantic source range that established the artifact relationship, when known.</summary>
    public SourceRange? OriginRange { get; init; }
}

/// <summary>One non-C# artifact or logical prompt/configuration reference associated with a selected C# semantic source anchor.</summary>
public sealed record CodeExploreAssociatedArtifact(
    string? FilePath,
    CodeExploreArtifactMediaKind MediaKind,
    string ProjectName,
    string? OriginSymbolId,
    string OriginFilePath,
    SourceRange OriginRange,
    CodeExploreArtifactRelationshipKind Relationship,
    CodeExploreArtifactEvidenceLevel Evidence,
    IReadOnlyList<string> SelectionReasons,
    CodeExploreArtifactContent? Content,
    IReadOnlyList<string> Omissions,
    string? LogicalName = null);

/// <summary>Independent completeness and accounting for associated artifact discovery.</summary>
public sealed record CodeExploreArtifactCoverage(
    int InspectedSourceAnchors,
    int InspectedProjects,
    int InspectedDirectories,
    int CandidateCount,
    int ReturnedCount,
    int OmittedCount,
    int SpentCharacters,
    bool Complete,
    bool CandidateLimitReached,
    bool FileLimitReached,
    bool CharacterLimitReached,
    bool TimeLimitReached,
    IReadOnlyList<string> Omissions,
    IReadOnlyList<CodeExploreArtifactContinuationTarget> ContinuationTargets);

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

/// <summary>One exact source range currently visible in the canonical model request.</summary>
public sealed record ModelVisibleSourceEntry(
    string HolderId,
    string ToolCallId,
    string RepositoryPath,
    WorkspaceId? WorkspaceId,
    long WorkspaceGeneration,
    string FilePath,
    SourceRange Range,
    string FileSha256,
    string? RangeSha256,
    int EmittedCharacters);

/// <summary>Request-local host-owned frontier of source ranges already visible to the model after host output sanitization.</summary>
public sealed record ModelVisibleSourceFrontier(
    string RepositoryPath,
    WorkspaceId? WorkspaceId,
    long FrontierGeneration,
    IReadOnlyList<ModelVisibleSourceEntry> Entries,
    int EntryCount,
    int RangeCount,
    int SourceCharacters);

/// <summary>Actual source range emitted by a code-explore result.</summary>
public sealed record CodeExploreEmissionRecord(
    string FilePath,
    SourceRange Range,
    string FileSha256,
    string? RangeSha256,
    int EmittedCharacters);

/// <summary>Precise reference to unchanged source already present in the current model request.</summary>
public sealed record CodeExploreBackReference(
    string HolderId,
    string ToolCallId,
    string FilePath,
    SourceRange Range,
    string FileSha256,
    string? RangeSha256,
    IReadOnlyList<string> SymbolIds,
    string Reason);

/// <summary>Bounded source deduplication accounting for one code-explore result.</summary>
public sealed record CodeExploreDedupSummary(
    int CandidateRanges,
    int CoveredRanges,
    int SuppressedRanges,
    int ReEmittedRanges,
    int ReclaimedCharacters,
    int UsedForNewSourceCharacters,
    IReadOnlyList<string> Reasons);

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
    CodeExploreBlastRadius? BlastRadius = null,
    CodeExploreQueryInterpretation? QueryInterpretation = null,
    CodeExploreDiscoverySummary? Discovery = null,
    IReadOnlyList<CodeExploreCandidateSummary>? CandidateSummaries = null,
    CodeExploreAllocationSummary? Allocation = null,
    IReadOnlyList<CodeExploreBackReference>? BackReferences = null,
    CodeExploreDedupSummary? Deduplication = null,
    IReadOnlyList<CodeExploreEmissionRecord>? Emissions = null,
    IReadOnlyList<CodeExploreAssociatedArtifact>? AssociatedArtifacts = null,
    CodeExploreArtifactCoverage? ArtifactCoverage = null,
    CodeExploreAvailability? Availability = null,
    CodeExplorePresentation? Presentation = null,
    CodeExploreAdaptiveBudget? AdaptiveBudget = null,
    IReadOnlyList<CodeExploreFileRelevanceSummary>? FileRelevance = null)
{
    /// <summary>Gets source follow-up targets omitted by a downstream result envelope.</summary>
    public int OmittedSourceContinuationCount { get; init; }
}

/// <summary>Bounded current source text read through the host tool policy boundary.</summary>
public sealed record CodeExploreSourceText(
    string Path,
    string Text,
    string FileSha256);

/// <summary>Bounded current associated artifact text read through the host tool policy boundary.</summary>
public sealed record CodeExploreArtifactText(
    string Path,
    string Text,
    string FileSha256,
    CodeExploreArtifactMediaKind MediaKind,
    long LengthBytes);

/// <summary>One supported artifact path found by bounded exact-name lookup.</summary>
public sealed record CodeExploreArtifactFileMatch(
    string Path,
    CodeExploreArtifactMediaKind MediaKind);

/// <summary>Result of a bounded exact-name associated-artifact lookup.</summary>
public sealed record CodeExploreArtifactFileSearchResult(
    IReadOnlyList<CodeExploreArtifactFileMatch> Matches,
    int InspectedEntries,
    bool Truncated);

/// <summary>Safe policy classification for one candidate associated-artifact path.</summary>
public sealed record CodeExploreArtifactPathProbe(
    bool IsSupported,
    CodeExploreArtifactMediaKind? MediaKind,
    string? RejectionReason);

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

/// <summary>Policy-owned artifact reader used by code exploration for associated non-C# evidence.</summary>
public interface ICodeExploreArtifactReader
{
    /// <summary>Returns whether the path is a supported textual artifact under current policy.</summary>
    bool IsSupportedTextArtifactPath(string path);

    /// <summary>Returns a safe policy classification for one candidate artifact path without reading file content.</summary>
    CodeExploreArtifactPathProbe ProbeArtifactPath(string path);

    /// <summary>Reads a bounded current artifact after policy, regular-file, media, and size checks.</summary>
    Task<CodeExploreArtifactText> ReadArtifactTextAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken = default);

    /// <summary>Finds supported artifact files by exact file name below a bounded policy-checked directory.</summary>
    Task<CodeExploreArtifactFileSearchResult> FindArtifactFilesByNameAsync(
        string directoryPath,
        string fileName,
        int maximumMatches,
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
        CancellationToken cancellationToken = default,
        ModelVisibleSourceFrontier? visibleSourceFrontier = null);
}
