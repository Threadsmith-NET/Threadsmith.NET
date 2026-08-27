namespace Threadsmith.Core;

/// <summary>Normalized compiler diagnostic severity.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational compiler output.</summary>
    Info,

    /// <summary>Compiler warning.</summary>
    Warning,

    /// <summary>Compiler error.</summary>
    Error,
}

/// <summary>Confidence-aware baseline classification assigned to a diagnostic.</summary>
public enum DiagnosticClassification
{
    /// <summary>The diagnostic was also present against the committed baseline.</summary>
    Baseline,

    /// <summary>The diagnostic is absent from the committed baseline.</summary>
    Introduced,

    /// <summary>The baseline comparison is not authoritative at the available semantic confidence.</summary>
    ConfidenceDegraded,
}

/// <summary>Stable host-owned compiler diagnostic independent of MSBuild or Roslyn implementation types.</summary>
public sealed record Diagnostic
{
    /// <summary>Current diagnostic schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Stable identity for this normalized occurrence.</summary>
    public required string Id { get; init; }

    /// <summary>Compiler diagnostic code such as <c>CS1503</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Normalized severity.</summary>
    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>Owning project name.</summary>
    public required string Project { get; init; }

    /// <summary>Target framework compiled for this occurrence.</summary>
    public required string TargetFramework { get; init; }

    /// <summary>Slash-normalized repository-relative source path when available.</summary>
    public string? File { get; init; }

    /// <summary>One-based source range when the compiler supplied one.</summary>
    public SourceRange? Range { get; init; }

    /// <summary>Compiler message without terminal formatting.</summary>
    public required string Message { get; init; }

    /// <summary>Mutation most likely responsible for this occurrence.</summary>
    public MutationId? RelatedMutationId { get; init; }

    /// <summary>Stable symbol associated with the mutation when confidence permits correlation.</summary>
    public string? RelatedSymbolId { get; init; }

    /// <summary>Semantic confidence in effect when classification occurred.</summary>
    public required SemanticConfidenceLevel Confidence { get; init; }

    /// <summary>Whether this occurrence matched a diagnostic captured against the committed baseline.</summary>
    public bool IsBaselineDiagnostic { get; init; }

    /// <summary>Confidence-aware classification result.</summary>
    public DiagnosticClassification Classification { get; init; } = DiagnosticClassification.ConfidenceDegraded;
}

/// <summary>Compiler diagnostics captured against one immutable workspace baseline.</summary>
/// <param name="WorkspaceId">Workspace whose committed baseline was built.</param>
/// <param name="BaselineCapturedAt">Identity timestamp of the immutable workspace baseline.</param>
/// <param name="CapturedAt">Time the build capture completed.</param>
/// <param name="Confidence">Semantic confidence at capture time.</param>
/// <param name="Diagnostics">Normalized diagnostics present before mutation.</param>
public sealed record BaselineCapture(
    WorkspaceId WorkspaceId,
    DateTimeOffset BaselineCapturedAt,
    DateTimeOffset CapturedAt,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One project selected by affected-graph traversal.</summary>
/// <param name="Name">Project display name.</param>
/// <param name="FilePath">Confined absolute project path.</param>
/// <param name="TargetFrameworks">Target frameworks that require compilation.</param>
/// <param name="Confidence">Project semantic confidence.</param>
/// <param name="IsDirectlyChanged">Whether a changed file belongs directly to this project.</param>
public sealed record AffectedProject(
    string Name,
    string FilePath,
    IReadOnlyList<string> TargetFrameworks,
    SemanticConfidenceLevel Confidence,
    bool IsDirectlyChanged);

/// <summary>Deterministic affected-project graph result.</summary>
/// <param name="Projects">Directly changed projects followed by transitive dependents.</param>
/// <param name="UnmappedFiles">Changed files not attributable to a known project.</param>
public sealed record AffectedProjectSet(
    IReadOnlyList<AffectedProject> Projects,
    IReadOnlyList<string> UnmappedFiles);

/// <summary>Authoritative validation stages required for a mutation gate.</summary>
public enum MutationValidationStage
{
    /// <summary>Fast in-process semantic validation without launching a build.</summary>
    Semantic,

    /// <summary>Affected projects must compile or produce classified diagnostics.</summary>
    Compile,

    /// <summary>Compiler diagnostics must be classified and correlated to the mutation.</summary>
    Diagnostics,

    /// <summary>Selected affected tests must be discovered and executed.</summary>
    Tests,
}

/// <summary>Parameters for one trusted structured build.</summary>
public sealed record BuildValidationRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Immutable workspace baseline governing confinement and trust.</summary>
    public required WorkspaceBaseline Baseline { get; init; }

    /// <summary>Affected projects to compile; an empty list builds the selected solution or project.</summary>
    public IReadOnlyList<AffectedProject> Projects { get; init; } = [];

    /// <summary>Semantic confidence to carry on normalized diagnostics.</summary>
    public required SemanticConfidenceLevel Confidence { get; init; }

    /// <summary>Complete semantic project inventory used to discover test projects.</summary>
    public IReadOnlyList<SemanticProjectInfo> ProjectInventory { get; init; } = [];

    /// <summary>Validation stages required before accepting the mutation.</summary>
    public IReadOnlyList<MutationValidationStage> Stages { get; init; } =
    [
        MutationValidationStage.Semantic,
        MutationValidationStage.Compile,
        MutationValidationStage.Diagnostics,
        MutationValidationStage.Tests,
    ];
}

/// <summary>Structured result of one or more bounded build processes.</summary>
/// <param name="Succeeded">Whether every build invocation exited successfully.</param>
/// <param name="Diagnostics">Normalized compiler diagnostics.</param>
/// <param name="Targets">Confined targets passed to <c>dotnet build</c>.</param>
/// <param name="Duration">Aggregate wall-clock duration.</param>
public sealed record BuildValidationResult(
    bool Succeeded,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> Targets,
    TimeSpan Duration);

/// <summary>Supported test-host execution modes.</summary>
public enum TestFramework
{
    /// <summary>xUnit through the standard <c>dotnet test</c> contract.</summary>
    XUnit,

    /// <summary>Microsoft.Testing.Platform through the <c>dotnet test</c> contract.</summary>
    MicrosoftTestingPlatform,
}

/// <summary>Normalized outcome for a selected test project.</summary>
public enum TestOutcome
{
    /// <summary>Every executed test passed.</summary>
    Passed,

    /// <summary>At least one executed test failed or the host exited unsuccessfully.</summary>
    Failed,

    /// <summary>No test failed and at least one test was skipped.</summary>
    Skipped,
}

/// <summary>One discovered test project independent of its framework implementation types.</summary>
public sealed record TestProject
{
    /// <summary>Project display name.</summary>
    public required string Name { get; init; }

    /// <summary>Confined absolute project path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Declared target frameworks.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary>Detected test execution framework.</summary>
    public required TestFramework Framework { get; init; }

    /// <summary>Confined absolute project references used for selection.</summary>
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];
}

/// <summary>One framework-neutral discovered test case.</summary>
public sealed record TestCase
{
    /// <summary>Stable test identity within its project.</summary>
    public required string Id { get; init; }

    /// <summary>Fully qualified framework-reported test name.</summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>Owning test project path.</summary>
    public required string ProjectPath { get; init; }
}

/// <summary>Framework-neutral test-project and test-case inventory.</summary>
public sealed record TestDiscovery(
    IReadOnlyList<TestProject> Projects,
    IReadOnlyList<TestCase> TestCases);

/// <summary>Conservative test scope plus host-authored inclusion rationale.</summary>
public sealed record TestSelection
{
    /// <summary>Selected test projects.</summary>
    public IReadOnlyList<TestProject> Projects { get; init; } = [];

    /// <summary>Known test cases in the selected projects.</summary>
    public IReadOnlyList<TestCase> TestCases { get; init; } = [];

    /// <summary>Deterministic reasons explaining the selected scope.</summary>
    public IReadOnlyList<string> Rationale { get; init; } = [];

    /// <summary>Mutations whose changed projects or symbols drove selection.</summary>
    public IReadOnlyList<MutationId> RelatedMutationIds { get; init; } = [];
}

/// <summary>Normalized result for one selected test-project invocation.</summary>
public sealed record TestResult
{
    /// <summary>Executed project.</summary>
    public required TestProject Project { get; init; }

    /// <summary>Normalized aggregate outcome.</summary>
    public required TestOutcome Outcome { get; init; }

    /// <summary>Whether the process completed without timeout or cancellation.</summary>
    public bool ProcessCompleted { get; init; }

    /// <summary>Number of passing tests.</summary>
    public int Passed { get; init; }

    /// <summary>Number of failing tests.</summary>
    public int Failed { get; init; }

    /// <summary>Number of skipped tests.</summary>
    public int Skipped { get; init; }

    /// <summary>Bounded sanitized process output retained for inspection.</summary>
    public required string Output { get; init; }

    /// <summary>Process wall-clock duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Mutations associated with this selected project scope.</summary>
    public IReadOnlyList<MutationId> RelatedMutationIds { get; init; } = [];
}

/// <summary>Discovery, selection, and execution evidence for one validation turn.</summary>
public sealed record TestValidationResult
{
    /// <summary>Explained selected test scope.</summary>
    public required TestSelection Selection { get; init; }

    /// <summary>Normalized results for selected projects.</summary>
    public IReadOnlyList<TestResult> Results { get; init; } = [];

    /// <summary>Whether discovery and every selected invocation completed.</summary>
    public bool Completed { get; init; }

    /// <summary>Total number of passing tests.</summary>
    public int Passed => Results.Sum(result => result.Passed);

    /// <summary>Total number of failing tests.</summary>
    public int Failed => Results.Sum(result => result.Failed);

    /// <summary>Total number of skipped tests.</summary>
    public int Skipped => Results.Sum(result => result.Skipped);
}

/// <summary>Final validation evidence for one mutation validation turn.</summary>
/// <param name="Build">Structured affected-project build result.</param>
/// <param name="Diagnostics">Classified and mutation-correlated diagnostics.</param>
/// <param name="Tests">Explained normalized test evidence.</param>
/// <param name="Gate">Acceptance-gate decision.</param>
public sealed record MutationValidationResult(
    BuildValidationResult Build,
    IReadOnlyList<Diagnostic> Diagnostics,
    TestValidationResult Tests,
    AcceptanceGateResult Gate);

/// <summary>Final validation-gate outcome.</summary>
public enum AcceptanceGateStatus
{
    /// <summary>Validation evidence permits acceptance.</summary>
    Passed,

    /// <summary>An authoritative introduced error blocks acceptance.</summary>
    Failed,

    /// <summary>Degraded confidence requires an explicit human decision.</summary>
    HumanConfirmationRequired,
}

/// <summary>Inputs evaluated by the build-half acceptance gate.</summary>
public sealed record AcceptanceGateRequest
{
    /// <summary>Classified diagnostics from the final build.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>Whether every required validation stage completed.</summary>
    public bool RequiredStagesCompleted { get; init; }

    /// <summary>Normalized test evidence evaluated by the gate.</summary>
    public TestValidationResult? Tests { get; init; }

    /// <summary>Whether the exact final diff is available for review.</summary>
    public bool FinalDiffAvailable { get; init; }

    /// <summary>Whether required mutation approvals are present.</summary>
    public bool RequiredApprovalsPresent { get; init; }

    /// <summary>Known residual risks recorded for the decision.</summary>
    public IReadOnlyList<string> ResidualRisks { get; init; } = [];
}

/// <summary>Acceptance decision with inspectable reasons.</summary>
/// <param name="Status">Gate outcome.</param>
/// <param name="Reasons">Deterministic reasons supporting the outcome.</param>
public sealed record AcceptanceGateResult(
    AcceptanceGateStatus Status,
    IReadOnlyList<string> Reasons);

/// <summary>Minimal governed input for one compilation-correction attempt.</summary>
/// <param name="ChangedCode">Only the relevant changed source fragment.</param>
/// <param name="Diagnostic">The introduced diagnostic being corrected.</param>
/// <param name="Contract">The task or code contract that the correction must preserve.</param>
/// <param name="Attempt">One-based attempt number.</param>
public sealed record CorrectionContext(
    string ChangedCode,
    Diagnostic Diagnostic,
    string Contract,
    int Attempt);

/// <summary>Validation evidence returned after one corrective mutation and recompile.</summary>
/// <param name="ChangedCode">Relevant changed fragment for a possible next attempt.</param>
/// <param name="Diagnostics">Recompiled and classified diagnostics.</param>
public sealed record CorrectionAttemptResult(
    string ChangedCode,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Bounded correction-loop result.</summary>
/// <param name="Succeeded">Whether no introduced errors remain.</param>
/// <param name="Attempts">Number of corrective attempts performed.</param>
/// <param name="BudgetExhausted">Whether introduced errors remain after the configured attempt budget.</param>
/// <param name="Diagnostics">Final classified diagnostics.</param>
public sealed record CorrectionLoopResult(
    bool Succeeded,
    int Attempts,
    bool BudgetExhausted,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Minimal governed input for one test-failure correction attempt.</summary>
/// <param name="ChangedCode">Only the relevant changed source fragment.</param>
/// <param name="Failure">One normalized failed selected-project result.</param>
/// <param name="Contract">The task or code contract that the correction must preserve.</param>
/// <param name="Attempt">One-based attempt number.</param>
public sealed record TestCorrectionContext(
    string ChangedCode,
    TestResult Failure,
    string Contract,
    int Attempt);

/// <summary>Test evidence returned after one corrective mutation and rerun.</summary>
/// <param name="ChangedCode">Relevant changed fragment for a possible next attempt.</param>
/// <param name="Validation">Rerun selection and normalized results.</param>
public sealed record TestCorrectionAttemptResult(
    string ChangedCode,
    TestValidationResult Validation);

/// <summary>Bounded test-correction result.</summary>
/// <param name="Succeeded">Whether selected tests complete without a failed result.</param>
/// <param name="Attempts">Number of corrective attempts performed.</param>
/// <param name="BudgetExhausted">Whether failures remain after the configured attempt budget.</param>
/// <param name="Validation">Final normalized test evidence.</param>
public sealed record TestCorrectionLoopResult(
    bool Succeeded,
    int Attempts,
    bool BudgetExhausted,
    TestValidationResult Validation);

/// <summary>Captures compiler diagnostics against the immutable pre-mutation baseline.</summary>
/// <param name="Request">Trusted baseline validation request.</param>
public sealed record CaptureBaselineBuildCommand(
    BuildValidationRequest Request) : ICommand<BaselineCapture>
{
    /// <summary>Mutation set whose touched paths bound semantic-only baseline diagnostics.</summary>
    public MutationSet? MutationSet { get; init; }
}

/// <summary>Builds and classifies one mutation set through the application boundary.</summary>
public sealed record ValidateMutationCommand : ICommand<MutationValidationResult>
{
    /// <summary>Affected-project build request.</summary>
    public required BuildValidationRequest Request { get; init; }

    /// <summary>Pre-mutation baseline build capture.</summary>
    public required BaselineCapture BaselineCapture { get; init; }

    /// <summary>Mutation set whose effects are being validated.</summary>
    public required MutationSet MutationSet { get; init; }

    /// <summary>Whether required mutation approvals are present.</summary>
    public bool RequiredApprovalsPresent { get; init; }

    /// <summary>Whether the exact final diff is available.</summary>
    public bool FinalDiffAvailable { get; init; }

    /// <summary>Known residual risks retained for acceptance.</summary>
    public IReadOnlyList<string> ResidualRisks { get; init; } = [];
}
