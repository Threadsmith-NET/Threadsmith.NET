namespace Threadsmith.ModelTooling.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises reuse through the existing bounded evidence store and real writer promotion.</summary>
public static partial class EditAnalyzerTests
{
    /// <summary>Only matching snapshot, coverage and execution policy can reuse complete baseline evidence.</summary>
    [Fact]
    public static async Task BaselineReuseRequiresMatchingScopeAndPolicy()
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), ct);
        await engine.WaitForWarmAsync(ct);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var project = source.Projects.Single(project => project.Name == "Contracts");
        var document = project.Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var options = new EditAnalyzerOptions { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue };
        var candidate = source.WithDocumentText(document.Id, SourceText.From(Repairs));
        var first = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, ct);
        Assert.False(Assert.Single(first.Before).Reused);
        var second = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, ct);
        var reused = Assert.Single(second.Before);
        Assert.True(reused.Reused);
        Assert.Equal(Assert.Single(first.Before).Diagnostics, reused.Diagnostics);
        Assert.Equal(0, reused.AnalysisMilliseconds);
        Assert.Equal(0, reused.PreparationMilliseconds);
        Assert.All(reused.Analyzers, analyzer => Assert.Equal(0, analyzer.ExecutionMilliseconds));
        Assert.False(Assert.Single(second.After).Reused);

        var policy = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options with { ConcurrentAnalysis = true }, false, true, ct);
        Assert.False(Assert.Single(policy.Before).Reused);
        var selection = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options with { DiagnosticIds = ["TS1170", "TS1171"] }, false, true, ct);
        Assert.False(Assert.Single(selection.Before).Reused);
        var scope = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, project.DocumentIds, options, false, true, ct);
        Assert.False(Assert.Single(scope.Before).Reused);
        var full = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, project.DocumentIds, options, true, true, ct);
        Assert.False(Assert.Single(full.Before).Reused);
        Assert.NotEmpty(Assert.Single(full.Before).FullReferenceDiagnostics);
        var control = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, project.DocumentIds, options, false, false, ct);
        Assert.False(Assert.Single(control.Before).Reused);
    }

    /// <summary>Candidate evidence becomes the baseline only after the existing writer receipt promotes that exact solution.</summary>
    [Fact]
    public static async Task WriterPromotionReusesCandidateEvidenceAndExternalRefreshInvalidatesIt()
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var ct = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = source.Projects.Single(project => project.Name == "Contracts").Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var options = new EditAnalyzerOptions { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue };
        var command = new ApplySourceEditCommand(session, RunId.New(), workspace, Guid.NewGuid(), new() { Rationale = "Verify analyzer evidence promotion.", Mutations = [] });
        var snapshot = new MutationEffectSnapshot(MutationSetId.New(), [], [new("Contracts/AnalyzerCases.cs", AnalyzerHash(Violations), AnalyzerHash(Repairs)) { FinalBytes = Encoding.UTF8.GetBytes(Repairs) }]);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted check && check.Analysis?.EffectId == command.EffectId)
            {
                completed.TrySetResult();
            }

            return Task.CompletedTask;
        });
        Assert.NotNull(await engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.Zero, ct));
        await completed.Task.WaitAsync(ct);
        var candidate = engine.CaptureEditCandidateSnapshot(command.EffectId);
        var initial = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, ct);
        Assert.NotEmpty(Assert.Single(initial.Before).Diagnostics);
        Assert.Empty(Assert.Single(initial.After).Diagnostics);
        var compilerPasses = engine.EditAnalysisStatistics.DiagnosticPasses;
        engine.ConfirmEditApplied(command.EffectId);
        await engine.RefreshDocumentsAsync([new(document.FilePath!, Repairs, AnalyzerHash(Repairs))], ct);
        var promoted = engine.CaptureAdvancedSnapshot().Solution;
        Assert.Same(candidate, promoted);
        Assert.Equal(compilerPasses, engine.EditAnalysisStatistics.DiagnosticPasses);
        var next = await engine.AnalyzeAnalyzerCandidateAsync(promoted, promoted.WithDocumentText(document.Id, SourceText.From(Violations)), [document.Id], options, false, true, ct);
        Assert.True(Assert.Single(next.Before).Reused);
        Assert.Empty(Assert.Single(next.Before).Diagnostics);
        Assert.NotEmpty(Assert.Single(next.After).Diagnostics);

        await engine.RefreshDocumentsAsync([new(document.FilePath!, Violations, AnalyzerHash(Violations))], ct);
        var refreshed = engine.CaptureAdvancedSnapshot().Solution;
        var external = await engine.AnalyzeAnalyzerCandidateAsync(refreshed, refreshed.WithDocumentText(document.Id, SourceText.From(Repairs)), [document.Id], options, false, true, ct);
        Assert.False(Assert.Single(external.Before).Reused);
        Assert.NotEmpty(Assert.Single(external.Before).Diagnostics);
    }

    /// <summary>Failed pairs do not retain their successful baseline, and oversized evidence remains uncached.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FailedAndOversizedResultsCannotSeedReuse(bool bounded)
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { MaximumRetainedDiagnostics = bounded ? 1 : 64, MaximumReportedDiagnostics = 1 } });
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), ct);
        await engine.WaitForWarmAsync(ct);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = source.Projects.Single(project => project.Name == "Contracts").Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var options = new EditAnalyzerOptions { DiagnosticIds = bounded ? ["CA2016", "CA2012", "VSTHRD002", "VSTHRD100", "TS1170"] : ["TS1170"], BudgetMilliseconds = int.MaxValue };
        var candidate = source.WithDocumentText(document.Id, SourceText.From(bounded ? Repairs : "public class AnalyzerFailure {}"));
        if (bounded)
        {
            var oversized = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, ct);
            Assert.True(Assert.Single(oversized.Before).Diagnostics.Count > 1);
        }
        else
        {
            await Assert.ThrowsAsync<AggregateException>(() => engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, ct));
        }

        var recovered = await engine.AnalyzeAnalyzerCandidateAsync(source, source.WithDocumentText(document.Id, SourceText.From(Repairs)), [document.Id], options, false, true, ct);
        Assert.False(Assert.Single(recovered.Before).Reused);
    }

    /// <summary>Reloaded configuration and additional inputs invalidate prior evidence even with unchanged source text.</summary>
    [Theory]
    [InlineData("configuration")]
    [InlineData("additional")]
    public static async Task ReloadedAnalyzerInputsInvalidateEvidence(string input)
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var path = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        var document = Assert.Single(source.GetDocumentIdsWithFilePath(path));
        var options = new EditAnalyzerOptions { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue };
        var first = await engine.AnalyzeAnalyzerCandidateAsync(source, source.WithDocumentText(document, SourceText.From(Repairs)), [document], options, false, true, ct);
        Assert.NotEmpty(Assert.Single(first.Before).Diagnostics);
        if (input == "configuration")
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".editorconfig"), "root = true\n[*.cs]\ndotnet_diagnostic.TS1170.severity = none\n", ct);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Contracts", "analyzer-input.txt"), "disable-probe", ct);
        }

        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        var reloaded = engine.CaptureAdvancedSnapshot().Solution;
        var reloadedDocument = Assert.Single(reloaded.GetDocumentIdsWithFilePath(path));
        var second = await engine.AnalyzeAnalyzerCandidateAsync(reloaded, reloaded.WithDocumentText(reloadedDocument, SourceText.From(Repairs)), [reloadedDocument], options, false, true, ct);
        Assert.False(Assert.Single(second.Before).Reused);
        Assert.Empty(Assert.Single(second.Before).Diagnostics);
    }

    private static string AnalyzerHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
