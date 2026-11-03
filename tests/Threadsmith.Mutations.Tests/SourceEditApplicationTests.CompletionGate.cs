namespace Threadsmith.Mutations.Tests;

using Threadsmith.Core;
using Xunit;

public sealed partial class SourceEditApplicationTests
{
    /// <summary>Incomplete final-source evidence cannot validate an applied edit.</summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("obsolete")]
    [InlineData("uncommitted")]
    [InlineData("wrong-effect")]
    [InlineData("zero-projects")]
    [InlineData("partial-projects")]
    [InlineData("omitted-projects")]
    [InlineData("warnings-unavailable")]
    [InlineData("retention-truncated")]
    [InlineData("projection-truncated")]
    [InlineData("partial-analyzer-documents")]
    [InlineData("analyzer-truncated")]
    public static async Task CompletionGateRejectsIncompleteFinalAnalysis(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var clean = CreateCleanCompletionAnalysis(analyzer);
        analyzer.Publish(scenario switch
        {
            "pending" => clean with { Pending = true },
            "obsolete" => clean with { Obsolete = true },
            "uncommitted" => clean with { CommittedGeneration = null },
            "wrong-effect" => clean with { EffectId = Guid.NewGuid() },
            "zero-projects" => clean with { ProjectsAnalyzed = 0, ProjectsInScope = 0 },
            "partial-projects" => clean with { ProjectsInScope = 2 },
            "omitted-projects" => clean with { ProjectsOmitted = 1 },
            "warnings-unavailable" => clean with { CurrentWarnings = null },
            "retention-truncated" => clean with { FindingRetentionTruncated = true },
            "projection-truncated" => clean with { FindingProjectionTruncated = true },
            "partial-analyzer-documents" => clean with { Analyzers = clean.Analyzers with { DocumentsInScope = 2 } },
            "analyzer-truncated" => clean with { Analyzers = clean.Analyzers with { Truncated = true } },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        });

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome!.Status);
        Assert.Contains(outcome.ResidualRisks, risk => risk.StartsWith("Validation incomplete:", StringComparison.Ordinal));
        Assert.Equal("class Changed { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
    }

    /// <summary>Compiler completion cannot hide an incomplete enabled analyzer check.</summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("partial")]
    [InlineData("unavailable")]
    [InlineData("skipped")]
    [InlineData("cancelled")]
    [InlineData("timed-out")]
    [InlineData("failed")]
    public static async Task CompletionGateRejectsIncompleteAnalyzersDespiteCompleteCompiler(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var clean = CreateCleanCompletionAnalysis(analyzer);
        analyzer.Publish(clean with { Analyzers = clean.Analyzers with { Status = status } });

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome!.Status);
    }

    /// <summary>Delivered findings still block success unless they are initial or informational.</summary>
    [Theory]
    [InlineData("compiler", DiagnosticSeverity.Error, "introduced", false)]
    [InlineData("compiler", DiagnosticSeverity.Warning, "introduced", false)]
    [InlineData("analyzer", DiagnosticSeverity.Error, "introduced", false)]
    [InlineData("analyzer", DiagnosticSeverity.Warning, "introduced", false)]
    [InlineData("compiler", DiagnosticSeverity.Error, "unknown", false)]
    [InlineData("analyzer", DiagnosticSeverity.Warning, "unknown", false)]
    [InlineData("compiler", DiagnosticSeverity.Error, "initial", true)]
    [InlineData("compiler", DiagnosticSeverity.Warning, "initial", true)]
    [InlineData("analyzer", DiagnosticSeverity.Error, "initial", true)]
    [InlineData("analyzer", DiagnosticSeverity.Warning, "initial", true)]
    [InlineData("analyzer", DiagnosticSeverity.Info, "introduced", true)]
    public static async Task CompletionGateDistinguishesActionableAndBaselineFindings(string producer, DiagnosticSeverity severity, string origin, bool succeeds)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var clean = CreateCleanCompletionAnalysis(analyzer);
        var compiler = producer == "compiler";
        analyzer.Publish(clean with
        {
            CurrentErrors = compiler && severity == DiagnosticSeverity.Error ? 1 : 0,
            CurrentWarnings = compiler && severity == DiagnosticSeverity.Warning ? 1 : 0,
            Analyzers = clean.Analyzers with { ReportableFindings = compiler ? 0 : 1 },
            Diagnostics = [new("TEST001", "Completion gate finding", "Example.cs", 1, "Example", "net10.0", origin) { Severity = severity, Producer = producer }],
        });
        Assert.NotNull(edits.TakeFeedback(fixture.SessionId, fixture.RunId));
        Assert.Null(edits.TakeFeedback(fixture.SessionId, fixture.RunId));

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(succeeds ? ExecutionCheckpointPhase.Completed : ExecutionCheckpointPhase.Failed, outcome!.Status);
    }

    /// <summary>Clean completed evidence permits success without requiring disabled analyzers.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public static async Task CompletionGateAllowsCleanCompleteFinalAnalysis(bool analyzersEnabled, bool comparisonAvailable)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var clean = CreateCleanCompletionAnalysis(analyzer);
        analyzer.Publish(clean with { ErrorComparisonAvailable = comparisonAvailable, Analyzers = analyzersEnabled ? clean.Analyzers : new() });

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome!.Status);
    }

    /// <summary>Current-state evidence without a baseline cannot excuse remaining errors or warnings.</summary>
    [Theory]
    [InlineData("compiler", DiagnosticSeverity.Error)]
    [InlineData("compiler", DiagnosticSeverity.Warning)]
    [InlineData("analyzer", DiagnosticSeverity.Warning)]
    public static async Task CompletionGateRejectsUnknownFindingsWithoutComparison(string producer, DiagnosticSeverity severity)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var clean = CreateCleanCompletionAnalysis(analyzer);
        var compiler = producer == "compiler";
        analyzer.Publish(clean with
        {
            ErrorComparisonAvailable = false,
            CurrentErrors = compiler && severity == DiagnosticSeverity.Error ? 1 : 0,
            CurrentWarnings = compiler && severity == DiagnosticSeverity.Warning ? 1 : 0,
            Analyzers = clean.Analyzers with { ReportableFindings = compiler ? 0 : 1, NewFindings = null, ResolvedFindings = null },
            Diagnostics = [new("TEST001", "Unknown graph-refresh finding", "Example.cs", 1, "Example", "net10.0", "unknown") { Severity = severity, Producer = producer }],
        });

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome!.Status);
    }

    /// <summary>Consuming an unavailable notice does not erase its validation obligation.</summary>
    [Fact]
    public static async Task CompletionGateRetainsUnavailableOwnerAfterFeedbackConsumption()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        var command = fixture.Replace("Example", "Changed", false);
        await edits.HandleAsync(command, ct);
        analyzer.DiscardCandidate(fixture.WorkspaceId, command.EffectId);
        Assert.NotNull(edits.TakeFeedback(fixture.SessionId, fixture.RunId));
        Assert.Null(edits.TakeFeedback(fixture.SessionId, fixture.RunId));

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome!.Status);
    }

    /// <summary>A repair needs its own completed analysis before it can succeed.</summary>
    [Fact]
    public static async Task CleanRepairWithNewEffectRechecksFinalAnalysis()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        var first = fixture.Replace("Example", "Changed", false);
        await edits.HandleAsync(first, ct);
        analyzer.Publish(CreateCleanCompletionAnalysis(analyzer) with { NewErrors = 1 });
        var failed = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);
        Assert.Equal(ExecutionCheckpointPhase.Failed, failed!.Status);

        var repair = fixture.Replace("Changed", "Repaired", false);
        await edits.HandleAsync(repair, ct);
        Assert.NotEqual(first.EffectId, analyzer.Latest!.EffectId);
        analyzer.Publish(CreateCleanCompletionAnalysis(analyzer) with { Pending = true });
        var pending = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);
        Assert.Equal(ExecutionCheckpointPhase.Failed, pending!.Status);
        analyzer.Publish(CreateCleanCompletionAnalysis(analyzer));

        var completed = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Completed, completed!.Status);
        Assert.Equal("class Repaired { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
    }

    /// <summary>Zero wait skips observation time but cannot turn pending checks into success or loop forever.</summary>
    [Fact]
    public static async Task ZeroFinalWaitStillRejectsPendingValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([], pending: true);
        var edits = fixture.CreateApplication(analyzer: analyzer, editOptions: new() { FinalFeedbackWaitMilliseconds = 0 });
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);

        var pending = await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct);
        Assert.True(pending!.Pending);
        Assert.Null(await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct));
        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome!.Status);
    }

    private static SourceEditAnalysis CreateCleanCompletionAnalysis(AdvisoryAnalyzer analyzer)
    {
        var applied = Assert.IsType<SourceEditAnalysis>(analyzer.Latest);
        Assert.NotNull(applied.CommittedGeneration);
        return applied with
        {
            Revision = applied.Revision + 1,
            Pending = false,
            Obsolete = false,
            ProjectsAnalyzed = 1,
            ProjectsInScope = 1,
            ErrorComparisonAvailable = true,
            CurrentErrors = 0,
            CurrentWarnings = 0,
            NewErrors = 0,
            Diagnostics = [],
            Omissions = [],
            Analyzers = new() { Status = "completed", DocumentsInScope = 1, DocumentsAnalyzed = 1 },
        };
    }
}
