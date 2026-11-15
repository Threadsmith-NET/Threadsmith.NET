namespace Threadsmith.ModelTooling.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public static partial class SourceEditAnalysisTests
{
    /// <summary>Rejected retries avoid baseline passes while filtering, retention, eviction and reset preserve evidence.</summary>
    [Theory]
    [InlineData(false, 64, 32)]
    [InlineData(true, 64, 32)]
    [InlineData(false, 1, 32)]
    [InlineData(false, 64, 1)]
    public static async Task RejectedCompilerCandidatesReuseBaselineAndMatchColdEvidence(bool excludeMissingType, int retained, int cachedProjects)
    {
        // Frozen nested deadlines; completion events, not elapsed time, establish results.
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new()
            {
                EditAnalysis = new()
                {
                    Analyzers = new() { Enabled = false },
                    ExcludedDiagnosticIds = excludeMissingType ? ["CS0246"] : [],
                    MaximumCachedProjects = cachedProjects,
                    MaximumRetainedDiagnostics = retained,
                    MaximumReportedDiagnostics = retained,
                },
            },
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var path = Path.Combine(fixture.Root, relative);
        var original = await File.ReadAllTextAsync(path, ct);
        var originalBytes = await File.ReadAllBytesAsync(path, ct);
        var firstText = original + "\npublic sealed class RejectedCompilerA { public MissingTypeA Value; }\n";
        var secondText = original + "\npublic sealed class RejectedCompilerB { public MissingTypeB Value; }\n";
        var firstCommand = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var passesBefore = engine.EditAnalysisStatistics.DiagnosticPasses;

        var first = await AnalyzeCompletedAsync(engine, events, firstCommand, fixture.Root, Snapshot(relative, original, firstText), ct);
        Assert.False(first.Pending);
        Assert.True(first.ProjectsInScope >= 2);
        Assert.Equal(first.ProjectsInScope, first.ProjectsAnalyzed);
        Assert.Equal(passesBefore + (2L * first.ProjectsInScope), engine.EditAnalysisStatistics.DiagnosticPasses);
        engine.DiscardEditCandidate(firstCommand.EffectId);
        var passesAfterFirst = engine.EditAnalysisStatistics.DiagnosticPasses;
        var secondCommand = Command(request.SessionId, firstCommand.RunId, request.WorkspaceId);
        var second = await AnalyzeCompletedAsync(engine, events, secondCommand, fixture.Root, Snapshot(relative, original, secondText), ct);

        Assert.False(second.Pending);
        Assert.Equal(first.ProjectsInScope, second.ProjectsInScope);
        Assert.Equal(second.ProjectsInScope, second.ProjectsAnalyzed);
        // Deferred publication leaves prior evidence available throughout the analysis,
        // even when new evidence will evict it at the final success boundary.
        var retryPasses = (2L * second.ProjectsInScope) - Math.Min(cachedProjects, second.ProjectsInScope);
        Assert.Equal(passesAfterFirst + retryPasses, engine.EditAnalysisStatistics.DiagnosticPasses);
        Assert.Equal(excludeMissingType, second.OutputFiltered);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path, ct));
        Assert.Equal(0L, engine.EditAnalysisStatistics.CandidatePromotions);
        engine.DiscardEditCandidate(secondCommand.EffectId);

        // Full graph reset must clear both slots and reproduce identical cold evidence.
        await engine.RefreshFullAsync(ct);
        await engine.WaitForWarmAsync(ct);
        var passesBeforeCold = engine.EditAnalysisStatistics.DiagnosticPasses;
        var coldCommand = Command(request.SessionId, firstCommand.RunId, request.WorkspaceId);
        var cold = await AnalyzeCompletedAsync(engine, events, coldCommand, fixture.Root, Snapshot(relative, original, secondText), ct);
        Assert.False(cold.Pending);
        Assert.Equal(passesBeforeCold + (2L * cold.ProjectsInScope), engine.EditAnalysisStatistics.DiagnosticPasses);
        AssertEquivalentCompilerEvidence(cold, second);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path, ct));
        engine.DiscardEditCandidate(coldCommand.EffectId);
    }

    /// <summary>Exact promotion makes candidate evidence reusable without losing initial versus introduced origins.</summary>
    [Fact]
    public static async Task PromotedCompilerCandidateBecomesBaselineWithoutLosingOrigins()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } },
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var path = Path.Combine(fixture.Root, relative);
        var original = await File.ReadAllTextAsync(path, ct);
        var broken = original + "\npublic sealed class PromotedCompilerError { public MissingType Value; }\n";
        var command = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var snapshot = Snapshot(relative, original, broken);
        var first = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, snapshot, ct);
        Assert.False(first.Pending);
        Assert.True(first.ErrorComparisonAvailable);
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "CS0246" && diagnostic.Origin == "introduced");
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "CS8805" && diagnostic.Origin == "initial");
        var passesBeforePromotion = engine.EditAnalysisStatistics.DiagnosticPasses;

        engine.ConfirmEditApplied(command.EffectId);
        await engine.RefreshDocumentsAsync([new(path, broken, snapshot.Endpoints[0].AfterSha256!)], ct);
        var promoted = engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId);
        Assert.NotNull(promoted);
        Assert.True(promoted.CandidateReused);
        Assert.Equal(passesBeforePromotion, engine.EditAnalysisStatistics.DiagnosticPasses);
        Assert.Equal(1L, engine.EditAnalysisStatistics.CandidatePromotions);
        var nextCommand = Command(request.SessionId, command.RunId, request.WorkspaceId);
        var extended = broken + "\npublic sealed class CompilerFollowup { }\n";
        var nextSnapshot = Snapshot(relative, broken, extended);
        var next = await AnalyzeCompletedAsync(engine, events, nextCommand, fixture.Root, nextSnapshot, ct);
        Assert.False(next.Pending);
        Assert.True(next.ErrorComparisonAvailable);
        Assert.Equal(0, next.NewErrors);
        Assert.Equal(0, next.ResolvedErrors);
        Assert.Equal(passesBeforePromotion + next.ProjectsInScope, engine.EditAnalysisStatistics.DiagnosticPasses);
        Assert.Contains(next.Diagnostics, diagnostic => diagnostic.Code == "CS0246" && diagnostic.Origin == "introduced");
        Assert.Contains(next.Diagnostics, diagnostic => diagnostic.Code == "CS8805" && diagnostic.Origin == "initial");
        engine.DiscardEditCandidate(nextCommand.EffectId);
    }

    /// <summary>Measures distinct DTO generations across two diagnostic-heavy projects and a rejected retry.</summary>
    [Theory]
    [InlineData(600)]
    [InlineData(1200)]
    public static async Task DiagnosticHeavyCompilerRetentionIsObservedAtPublication(int diagnosticsPerProject)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        foreach (var relativePath in new[] { "Contracts/Services.cs", "App/Program.cs" })
        {
            var diagnosticSource = string.Join("\n", Enumerable.Range(0, diagnosticsPerProject)
                .Select(index => $"class Retention{index} {{ MissingBaseline{index} value; }}"));
            await File.AppendAllTextAsync(Path.Combine(fixture.Root, relativePath), "\n" + diagnosticSource, ct);
        }

        var observations = new List<(int CachedProjects, int StagedProjects, int EvidenceSets, int Findings)>();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } },
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow))
        {
            EditCompilerRetentionObserver = observations.Add,
        };
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        var firstCommand = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var first = await AnalyzeCompletedAsync(
            engine, events, firstCommand, fixture.Root, Snapshot(relative, original, original + "\nclass FirstRetentionCandidate { MissingFirst value; }"), ct);
        Assert.True(first.CompilerCompletedSuccessfully);
        Assert.Equal(2, first.ProjectsInScope);
        Assert.True(first.CurrentErrors >= 2 * diagnosticsPerProject);
        Assert.False(first.FindingRetentionTruncated);
        var firstSteady = observations[^1];
        engine.DiscardEditCandidate(firstCommand.EffectId);
        observations.Clear();
        var secondCommand = Command(request.SessionId, firstCommand.RunId, request.WorkspaceId);
        var retryText = original + "\nclass RetryRetentionCandidate { MissingRetry value; }";
        var retrySnapshot = Snapshot(relative, original, retryText);
        var retry = await AnalyzeCompletedAsync(engine, events, secondCommand, fixture.Root, retrySnapshot, ct);
        Assert.True(retry.CompilerCompletedSuccessfully);
        Assert.False(retry.FindingRetentionTruncated);
        var retryPeak = observations.Max(item => item.Findings);
        var retrySteady = observations[^1];
        TestContext.Current.TestOutputHelper?.WriteLine($"diagnostics/project={diagnosticsPerProject}; first={firstSteady}; retry peak findings={retryPeak}; retry={retrySteady}");
        if (diagnosticsPerProject < SemanticEngine.MaximumCompilerCachedFindingsPerSlot)
        {
            Assert.Equal(2, retrySteady.CachedProjects);
            Assert.Equal(4, retrySteady.EvidenceSets);
            Assert.True(retryPeak > firstSteady.Findings);
            Assert.Equal(firstSteady.Findings, retrySteady.Findings);
        }
        else
        {
            // No truncation: the oversized findings remain fully reported, only reuse is bypassed.
            Assert.Equal((0, 0, 0, 0), firstSteady);
            Assert.Equal((0, 0, 0, 0), retrySteady);
            Assert.Equal(0, retryPeak);
            Assert.True(retry.Diagnostics.Count >= 2 * diagnosticsPerProject);
            Assert.True(retry.ErrorComparisonAvailable);
        }

        // Accepted oversized edits must retain the same initial/introduced labels without slots.
        engine.ConfirmEditApplied(secondCommand.EffectId);
        await engine.RefreshDocumentsAsync([new(Path.Combine(fixture.Root, relative), retryText, retrySnapshot.Endpoints[0].AfterSha256!)], ct);
        var followupCommand = Command(request.SessionId, firstCommand.RunId, request.WorkspaceId);
        var followup = await AnalyzeCompletedAsync(engine, events, followupCommand, fixture.Root, Snapshot(relative, retryText, retryText + "\nclass RetentionFollowup { }"), ct);
        Assert.True(followup.CompilerCompletedSuccessfully);
        Assert.Equal(OrderCompilerDiagnostics(retry.Diagnostics), OrderCompilerDiagnostics(followup.Diagnostics));
        Assert.Contains(followup.Diagnostics, item => item.Origin == "initial");
        Assert.Contains(followup.Diagnostics, item => item.Code == "CS0246" && item.Origin == "introduced");
        Assert.Equal(0, followup.NewErrors);
        engine.DiscardEditCandidate(followupCommand.EffectId);
    }

    private static IOrderedEnumerable<SourceEditDiagnostic> OrderCompilerDiagnostics(IReadOnlyList<SourceEditDiagnostic> diagnostics) => diagnostics
        .OrderBy(item => item.Project, StringComparer.Ordinal)
        .ThenBy(item => item.File, StringComparer.Ordinal)
        .ThenBy(item => item.Line)
        .ThenBy(item => item.Column)
        .ThenBy(item => item.Code, StringComparer.Ordinal)
        .ThenBy(item => item.Message, StringComparer.Ordinal);

    private static void AssertEquivalentCompilerEvidence(SourceEditAnalysis expected, SourceEditAnalysis actual)
    {
        Assert.Equal(expected.ProjectsInScope, actual.ProjectsInScope);
        Assert.Equal(expected.ProjectsAnalyzed, actual.ProjectsAnalyzed);
        Assert.Equal(expected.ProjectsOmitted, actual.ProjectsOmitted);
        Assert.Equal(expected.CandidateInputsComplete, actual.CandidateInputsComplete);
        Assert.Equal(expected.SyntaxDocumentsAnalyzed, actual.SyntaxDocumentsAnalyzed);
        Assert.Equal(expected.SyntaxErrors, actual.SyntaxErrors);
        Assert.Equal(expected.CurrentErrors, actual.CurrentErrors);
        Assert.Equal(expected.CurrentWarnings, actual.CurrentWarnings);
        Assert.Equal(expected.NewErrors, actual.NewErrors);
        Assert.Equal(expected.ResolvedErrors, actual.ResolvedErrors);
        Assert.Equal(expected.ErrorComparisonAvailable, actual.ErrorComparisonAvailable);
        Assert.Equal(expected.OutputFiltered, actual.OutputFiltered);
        Assert.Equal(expected.FindingRetentionTruncated, actual.FindingRetentionTruncated);
        Assert.Equal(expected.FindingProjectionTruncated, actual.FindingProjectionTruncated);
        Assert.Equal(expected.DiagnosticsTruncated, actual.DiagnosticsTruncated);
        Assert.Equal(expected.Diagnostics.ToArray(), actual.Diagnostics.ToArray());
        Assert.Equal(expected.Omissions.ToArray(), actual.Omissions.ToArray());
    }
}
