namespace Threadsmith.Core;

/// <summary>Authority assigned to validation evidence.</summary>
public enum ValidationAuthority
{
    /// <summary>Ad hoc evidence that cannot satisfy an acceptance gate.</summary>
    Exploratory,

    /// <summary>Evidence produced only by the governed M11 validation boundary.</summary>
    Authoritative,
}

/// <summary>Closed native validation operation categories.</summary>
public enum ValidationInvocationKind
{
    /// <summary>Package dependency and advisory inspection.</summary>
    NuGetHealth,

    /// <summary>Compilation.</summary>
    Build,

    /// <summary>Analyzer execution.</summary>
    Analyzer,

    /// <summary>Formatting verification.</summary>
    FormatCheck,

    /// <summary>Test discovery.</summary>
    TestDiscovery,

    /// <summary>Targeted test execution.</summary>
    TargetedTest,
}

/// <summary>Closed build configurations accepted by native validation tools.</summary>
public enum DotNetBuildConfiguration
{
    /// <summary>Debug configuration.</summary>
    Debug,

    /// <summary>Release configuration.</summary>
    Release,
}

/// <summary>Package advisory source mode.</summary>
public enum PackageHealthSourceMode
{
    /// <summary>Use only existing restore assets without network access.</summary>
    Offline,

    /// <summary>Query host-configured trusted package sources.</summary>
    ConfiguredSources,
}

/// <summary>One normalized dependency from existing restore assets.</summary>
public sealed record NuGetDependencyNode(
    string Id,
    string ResolvedVersion,
    string Source,
    bool IsDirect,
    string TargetFramework,
    IReadOnlyList<string> Dependencies);

/// <summary>Package advisory categories.</summary>
public enum PackageAdvisoryKind
{
    /// <summary>Known vulnerability.</summary>
    Vulnerability,

    /// <summary>Package deprecation.</summary>
    Deprecation,

    /// <summary>Newer version is available.</summary>
    Outdated,
}

/// <summary>One bounded normalized package advisory.</summary>
public sealed record PackageAdvisory(
    string PackageId,
    string ResolvedVersion,
    PackageAdvisoryKind Kind,
    string Severity,
    string? AdvisoryUrl,
    string Source);

/// <summary>Typed package-health request.</summary>
public sealed record NuGetDependencyHealthRequest
{
    /// <summary>Repository-relative project path.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Whether transitive dependencies are returned.</summary>
    public bool IncludeTransitive { get; init; } = true;

    /// <summary>Offline or explicitly configured-source advisory mode.</summary>
    public PackageHealthSourceMode SourceMode { get; init; }
}

/// <summary>Normalized package health with freshness and completeness.</summary>
public sealed record NuGetDependencyHealthResult(
    IReadOnlyList<NuGetDependencyNode> Dependencies,
    IReadOnlyList<PackageAdvisory> Advisories,
    DateTimeOffset? AssetsGeneratedAt,
    DateTimeOffset InspectedAt,
    bool IsComplete,
    bool IsOffline,
    bool IsStale,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Omissions,
    bool IsTruncated,
    ValidationAuthority Authority);

/// <summary>Common closed target for exploratory build, analyzer, and format operations.</summary>
public abstract record DotNetValidationTargetRequest
{
    /// <summary>Repository-relative solution or project path.</summary>
    public required string TargetPath { get; init; }

    /// <summary>Closed build configuration.</summary>
    public DotNetBuildConfiguration Configuration { get; init; }

    /// <summary>Optional validated target framework.</summary>
    public string? TargetFramework { get; init; }
}

/// <summary>Exploratory typed build request.</summary>
public sealed record BuildToolRequest : DotNetValidationTargetRequest;

/// <summary>Exploratory typed analyzer request.</summary>
public sealed record AnalyzerToolRequest : DotNetValidationTargetRequest;

/// <summary>Read-only formatter verification request.</summary>
public sealed record FormatCheckRequest
{
    /// <summary>Repository-relative solution or project path.</summary>
    public required string TargetPath { get; init; }
}

/// <summary>Normalized exploratory validation invocation.</summary>
public sealed record ValidationToolResult(
    string InvocationId,
    ValidationInvocationKind Kind,
    ValidationAuthority Authority,
    bool Succeeded,
    string EffectiveScope,
    IReadOnlyList<string> EffectiveArguments,
    IReadOnlyList<Diagnostic> Diagnostics,
    string Output,
    int ExitCode,
    TimeSpan Duration,
    bool TimedOut,
    bool IsTruncated);

/// <summary>Diagnostic origin filter.</summary>
public enum DiagnosticOrigin
{
    /// <summary>Compiler-produced diagnostics.</summary>
    Compiler,

    /// <summary>Analyzer-enabled build diagnostics.</summary>
    Analyzer,
}

/// <summary>Bounded diagnostic query over exploratory native runs.</summary>
public sealed record DiagnosticQuery
{
    /// <summary>Optional exact invocation identity.</summary>
    public string? InvocationId { get; init; }

    /// <summary>Optional owning run identity.</summary>
    public RunId? RunId { get; init; }

    /// <summary>Optional exact project.</summary>
    public string? Project { get; init; }

    /// <summary>Optional repository-relative file.</summary>
    public string? File { get; init; }

    /// <summary>Optional exact diagnostic code.</summary>
    public string? Code { get; init; }

    /// <summary>Optional severity.</summary>
    public DiagnosticSeverity? Severity { get; init; }

    /// <summary>Optional origin.</summary>
    public DiagnosticOrigin? Origin { get; init; }

    /// <summary>Optional baseline classification.</summary>
    public DiagnosticClassification? BaselineClass { get; init; }

    /// <summary>Optional opaque host-issued token for the next page of an earlier query.</summary>
    public string? ContinuationToken { get; init; }
}

/// <summary>One diagnostic plus its run provenance.</summary>
public sealed record DiagnosticQueryItem(
    string InvocationId,
    RunId RunId,
    string ScopePath,
    DiagnosticOrigin Origin,
    ValidationAuthority Authority,
    Diagnostic Diagnostic);

/// <summary>Paged diagnostic query result.</summary>
public sealed record DiagnosticQueryResult(
    IReadOnlyList<DiagnosticQueryItem> Items,
    int Total,
    string? ContinuationToken);

/// <summary>Atomic exact trait selector for test discovery.</summary>
public sealed record TestTraitSelector
{
    /// <summary>Exact bounded trait name.</summary>
    public required string Name { get; init; }

    /// <summary>Exact bounded trait value.</summary>
    public required string Value { get; init; }
}

/// <summary>Typed test discovery request for one host-validated project.</summary>
public sealed record TestDiscoveryRequest
{
    /// <summary>Repository-relative test project path.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Optional exact namespace filter.</summary>
    public string? Namespace { get; init; }

    /// <summary>Optional exact class filter.</summary>
    public string? ClassName { get; init; }

    /// <summary>Optional exact method filter.</summary>
    public string? MethodName { get; init; }

    /// <summary>Optional atomic exact trait filter.</summary>
    public TestTraitSelector? Trait { get; init; }
}

/// <summary>Stable host-issued discovered test identity.</summary>
public sealed record DiscoveredTestId(string Value);

/// <summary>One normalized discovered test.</summary>
public sealed record DiscoveredTest(
    DiscoveredTestId Id,
    string FullyQualifiedName,
    string ProjectPath,
    string? Namespace,
    string? ClassName,
    string? MethodName,
    IReadOnlyDictionary<string, string> Traits);

/// <summary>Bounded test discovery result.</summary>
public sealed record TestDiscoveryResult(
    string DiscoveryId,
    IReadOnlyList<DiscoveredTest> Tests,
    string DiscoverySource,
    string EffectiveFilter,
    DateTimeOffset DiscoveredAt,
    bool IsTruncated,
    ValidationAuthority Authority);

/// <summary>Typed targeted execution by a previously issued stable identity.</summary>
public sealed record TargetedTestRequest
{
    /// <summary>Host-issued discovery identity.</summary>
    public required DiscoveredTestId TestId { get; init; }

    /// <summary>Closed build configuration.</summary>
    public DotNetBuildConfiguration Configuration { get; init; }

    /// <summary>Optional validated target framework.</summary>
    public string? TargetFramework { get; init; }
}

/// <summary>Normalized targeted test result.</summary>
public sealed record TargetedTestResult(
    DiscoveredTest Test,
    string EffectiveFilter,
    ValidationAuthority Authority,
    TestOutcome Outcome,
    int Passed,
    int Failed,
    int Skipped,
    string Output,
    TimeSpan Duration,
    bool TimedOut,
    bool IsTruncated,
    IReadOnlyList<string> Attachments);

/// <summary>Host boundary for Plan-42 package and exploratory validation tools.</summary>
public interface INativeValidationToolService
{
    /// <summary>Gets trusted configured advisory-source hosts declared to tool policy.</summary>
    IReadOnlyList<string> ConfiguredNetworkHosts { get; }

    /// <summary>Gets logical advisory-source credentials declared to tool policy.</summary>
    IReadOnlyList<string> ConfiguredSecretReferences { get; }

    /// <summary>Inspects package dependencies and optional configured-source advisories.</summary>
    Task<NuGetDependencyHealthResult> InspectPackagesAsync(string repositoryPath, RunId runId, NuGetDependencyHealthRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs an exploratory build.</summary>
    Task<ValidationToolResult> BuildAsync(string repositoryPath, RunId runId, BuildToolRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs analyzers through a closed build invocation.</summary>
    Task<ValidationToolResult> AnalyzeAsync(string repositoryPath, RunId runId, AnalyzerToolRequest request, CancellationToken cancellationToken = default);

    /// <summary>Checks formatting without applying changes.</summary>
    Task<ValidationToolResult> CheckFormatAsync(string repositoryPath, RunId runId, FormatCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>Queries normalized diagnostics from exploratory runs.</summary>
    Task<DiagnosticQueryResult> QueryDiagnosticsAsync(string repositoryPath, DiagnosticQuery query, CancellationToken cancellationToken = default);

    /// <summary>Discovers stable test identities.</summary>
    Task<TestDiscoveryResult> DiscoverTestsAsync(string repositoryPath, RunId runId, TestDiscoveryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resolves a stable test identity to its repository-relative project for policy evaluation.</summary>
    string ResolveTestProjectPath(string repositoryPath, DiscoveredTestId testId);

    /// <summary>Runs exactly one previously discovered test.</summary>
    Task<TargetedTestResult> RunTargetedTestAsync(string repositoryPath, RunId runId, TargetedTestRequest request, CancellationToken cancellationToken = default);
}
