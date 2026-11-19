namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises reuse through the existing bounded evidence store and real writer promotion.</summary>
public abstract partial class EditAnalyzerTests
{
    private protected static string AnalyzerHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsWriterPromotionReusesCandidateEvidenceAndExternalRefreshInvalidatesIt : EditAnalyzerTests
{
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
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = source.Projects.Single(project => project.Name == "Contracts").Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var options = new EditAnalyzerOptions
        {
            DiagnosticIds = ["TS1170"],
            BudgetMilliseconds = int.MaxValue,
        };
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
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsFailedAndOversizedResultsCannotSeedReuse : EditAnalyzerTests
{
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
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { MaximumRetainedDiagnostics = bounded ? 1 : 64, MaximumReportedDiagnostics = 1 } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), ct);
        await engine.WaitForWarmAsync(ct);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = source.Projects.Single(project => project.Name == "Contracts").Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var options = new EditAnalyzerOptions
        {
            DiagnosticIds = bounded ? ["CA2016", "CA2012", "VSTHRD002", "VSTHRD100", "TS1170"] : ["TS1170"],
            BudgetMilliseconds = int.MaxValue,
        };
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
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public static class EditAnalyzerTestsReloadedAnalyzerInputsInvalidateEvidence
{
    /// <summary>Reloaded configuration and additional inputs invalidate prior evidence even with unchanged source text.</summary>
    [Theory]
    [InlineData("configuration")]
    [InlineData("additional")]
    public static async Task ReloadedAnalyzerInputsInvalidateEvidence(string input)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("threadsmith-analyzer-reload-");
        try
        {
            var projectPath = Path.Combine(root.FullName, "Probe.csproj");
            var sourcePath = Path.Combine(root.FullName, "Probe.cs");
            var configPath = Path.Combine(root.FullName, ".editorconfig");
            var additionalPath = Path.Combine(root.FullName, "analyzer-input.txt");
            var analyzerPath = Path.Combine(TestSemanticSolution.SourceRoot, "Analyzers", "Threadsmith.SemanticFixtures.Roslyn59.dll");
            var project = new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("EnableNETAnalyzers", false)),
                new XElement(
                    "ItemGroup",
                    new XElement("Analyzer", new XAttribute("Include", analyzerPath)),
                    new XElement("AdditionalFiles", new XAttribute("Include", "analyzer-input.txt"))));
            File.Copy(Path.Combine(TestSemanticSolution.SourceRoot, "global.json"), Path.Combine(root.FullName, "global.json"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "Directory.Build.props"), "<Project />", ct);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "Directory.Build.targets"), "<Project />", ct);
            await File.WriteAllTextAsync(projectPath, project.ToString(), ct);
            await File.WriteAllTextAsync(sourcePath, "public class AnalyzerProbe {}", ct);
            await File.WriteAllTextAsync(configPath, "root = true\n[*.cs]\ndotnet_diagnostic.TS1170.severity = suggestion\n", ct);
            await File.WriteAllTextAsync(additionalPath, "enabled", ct);

            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
            var request = new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root.FullName, projectPath, RepositoryTrustLevel.TrustedBuild);
            var options = new EditAnalyzerOptions { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue };
            var repair = SourceText.From("public class RepairedProbe {}");

            await engine.LoadAsync(request, ct);
            await engine.WaitForWarmAsync(ct);
            var source = engine.CaptureAdvancedSnapshot().Solution;
            var document = Assert.Single(source.GetDocumentIdsWithFilePath(sourcePath));
            var first = await engine.AnalyzeAnalyzerCandidateAsync(source, source.WithDocumentText(document, repair), [document], options, false, true, ct);
            Assert.Equal("TS1170", Assert.Single(Assert.Single(first.Before).Diagnostics).Id);
            var unchanged = await engine.AnalyzeAnalyzerCandidateAsync(source, source.WithDocumentText(document, repair), [document], options, false, true, ct);
            Assert.True(Assert.Single(unchanged.Before).Reused);

            if (input == "configuration")
            {
                await File.WriteAllTextAsync(configPath, "root = true\n[*.cs]\ndotnet_diagnostic.TS1170.severity = none\n", ct);
            }
            else
            {
                await File.WriteAllTextAsync(additionalPath, "disable-probe", ct);
            }

            await engine.LoadAsync(request, ct);
            await engine.WaitForWarmAsync(ct);
            var reloaded = engine.CaptureAdvancedSnapshot().Solution;
            var reloadedDocument = Assert.Single(reloaded.GetDocumentIdsWithFilePath(sourcePath));
            var second = await engine.AnalyzeAnalyzerCandidateAsync(reloaded, reloaded.WithDocumentText(reloadedDocument, repair), [reloadedDocument], options, false, true, ct);
            Assert.False(Assert.Single(second.Before).Reused);
            Assert.Empty(Assert.Single(second.Before).Diagnostics);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
