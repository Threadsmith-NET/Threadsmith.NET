namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Qualifies selected analyzers using the existing evaluated workspace and compiler queue.</summary>
public abstract partial class EditAnalyzerTests
{
    private protected const string Violations = """
        using System.Threading;
        using System.Threading.Tasks;
        public class AnalyzerCases
        {
            public Task ForwardAsync(CancellationToken cancellationToken) => Task.Delay(1);
            public int Value() { ValueTask<int> value = GetValueAsync(); return value.Result; }
            private static async ValueTask<int> GetValueAsync() { await Task.Yield(); return 1; }
            public void Block(Task task) { task.Wait(); }
            public async void Fire() { await Task.Yield(); }
        }
        public class AnalyzerProbe {}
        """;

    private protected const string Repairs = """
        using System.Threading;
        using System.Threading.Tasks;
        public class AnalyzerCases
        {
            public Task ForwardAsync(CancellationToken cancellationToken) => Task.Delay(1, cancellationToken);
            public async Task<int> ValueAsync() { ValueTask<int> value = GetValueAsync(); return await value; }
            private static async ValueTask<int> GetValueAsync() { await Task.Yield(); return 1; }
            public async Task BlockAsync(Task task) { await task; }
            public async Task FireAsync() { await Task.Yield(); }
        }
        public class RepairedProbe {}
        """;

    private protected static readonly string[] FixtureDiagnosticIds = ["CA2016", "CA2012", "VSTHRD002", "VSTHRD100", "TS1170", "TS1171"];

    private protected static async Task ConfigureFixtureAsync(string root)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectPath = Path.Combine(root, "Contracts", "Contracts.csproj");
        var project = XDocument.Load(projectPath);
        project.Root!.Add(new XElement("ItemGroup", new XElement("Analyzer", new XAttribute("Include", Path.Combine(TestSemanticSolution.SourceRoot, "Analyzers", "Microsoft.VisualStudio.Threading.Analyzers.dll"))), new XElement("Analyzer", new XAttribute("Include", Path.Combine(TestSemanticSolution.SourceRoot, "Analyzers", "Microsoft.VisualStudio.Threading.Analyzers.CSharp.dll"))), new XElement("AdditionalFiles", new XAttribute("Include", "analyzer-input.txt"))));
        project.Save(projectPath);
        await File.WriteAllTextAsync(Path.Combine(root, "Contracts", "AnalyzerCases.cs"), Violations, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "Contracts", "analyzer-input.txt"), "enabled", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, ".editorconfig"), "root = true\n[*.cs]\ndotnet_analyzer_diagnostic.severity = none\n" + string.Join('\n', FixtureDiagnosticIds.Select(id => "dotnet_diagnostic." + id + ".severity = suggestion")), cancellationToken);
    }

    private protected static string ExecutionBarrierPath(string root) =>
        Path.Combine(root, "Contracts", "AnalyzerCases.cs.block-execution");
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsSelectedRulesDetectRepairsAndDiscloseCompilationOmissions : EditAnalyzerTests
{
    /// <summary>Known repairs preserve scoped coverage while compilation-wide omissions remain explicit.</summary>
    [Fact]
    public static async Task SelectedRulesDetectRepairsAndDiscloseCompilationOmissions()
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = source.Projects.Single(project => project.Name == "Contracts").Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var candidate = source.WithDocumentText(document.Id, SourceText.From(Repairs));
        var options = new EditAnalyzerOptions
        {
            DiagnosticIds = ["CA2016", "CA2012", "VSTHRD002", "VSTHRD100", "TS1170", "TS1171", "MISSING"],
            BudgetMilliseconds = int.MaxValue,
        };
        var result = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, true, true, TestContext.Current.CancellationToken);
        var before = Assert.Single(result.Before);
        var after = Assert.Single(result.After);
        Assert.Equal(["MISSING"], before.UnavailableIds);
        foreach (var id in options.DiagnosticIds.Except(["TS1171", "MISSING"]))
        {
            Assert.Contains(before.Diagnostics, diagnostic => diagnostic.Id == id && diagnostic.Severity == "Info" && !diagnostic.Suppressed);
            Assert.DoesNotContain(after.Diagnostics, diagnostic => diagnostic.Id == id);
            Assert.Equal(before.Diagnostics.Where(diagnostic => diagnostic.Id == id), before.FullReferenceDiagnostics.Where(diagnostic => diagnostic.Id == id && diagnostic.Path == document.FilePath));
        }

        Assert.DoesNotContain(before.Diagnostics, diagnostic => diagnostic.Id == "TS1171");
        Assert.Contains(before.FullReferenceDiagnostics, diagnostic => diagnostic.Id == "TS1171");
        Assert.Contains(before.Analyzers, analyzer => analyzer.Actions["Compilation"] > 0);
        Assert.NotEmpty(before.Omissions);
        Assert.Equal(0, engine.EditAnalysisStatistics.DiagnosticPasses);
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsConfigurationAdditionalFilesSuppressionAndFailuresRemainDistinct : EditAnalyzerTests
{
    /// <summary>Fresh drivers observe all immutable input changes and never turn failures into empty success.</summary>
    [Fact]
    public static async Task ConfigurationAdditionalFilesSuppressionAndFailuresRemainDistinct()
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var project = source.Projects.Single(project => project.Name == "Contracts");
        var document = project.Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var options = new EditAnalyzerOptions
        {
            DiagnosticIds = ["TS1170"],
            BudgetMilliseconds = int.MaxValue,
        };
        var additional = Assert.Single(project.AdditionalDocuments);
        var candidate = source.WithAdditionalDocumentText(additional.Id, SourceText.From("disable-probe"));
        var additionalResult = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, TestContext.Current.CancellationToken);
        Assert.Contains(Assert.Single(additionalResult.Before).Diagnostics, finding => finding.Id == "TS1170");
        Assert.Empty(Assert.Single(additionalResult.After).Diagnostics);
        candidate = source.WithAdditionalDocumentText(additional.Id, SourceText.From("suppress-probe"));
        var programmatic = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, true, true, TestContext.Current.CancellationToken);
        Assert.Contains(Assert.Single(programmatic.Before).Diagnostics, finding => finding.Id == "TS1170" && !finding.Suppressed);
        Assert.Contains(Assert.Single(programmatic.After).Diagnostics, finding => finding.Id == "TS1170" && finding.Suppressed);
        Assert.Contains(Assert.Single(programmatic.After).FullReferenceDiagnostics, finding => finding.Id == "TS1170" && finding.Suppressed);
        Assert.Contains(Assert.Single(programmatic.After).Analyzers, analyzer => analyzer.Suppressions.Contains("SPR1170:TS1170", StringComparer.Ordinal) && analyzer.Actions["Suppression"] > 0);
        var config = project.AnalyzerConfigDocuments.Single(item => item.Name == ".editorconfig");
        candidate = source.WithAnalyzerConfigDocumentText(config.Id, SourceText.From("root = true\n[*.cs]\ndotnet_diagnostic.TS1170.severity = none\n"));
        var disabled = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, TestContext.Current.CancellationToken);
        Assert.Empty(Assert.Single(disabled.After).Diagnostics);
        Assert.Empty(Assert.Single(disabled.After).UnavailableIds);
        candidate = source.WithDocumentText(document.Id, SourceText.From("#pragma warning disable TS1170\npublic class AnalyzerProbe {}"));
        var suppressed = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, TestContext.Current.CancellationToken);
        Assert.Contains(Assert.Single(suppressed.After).Diagnostics, finding => finding.Id == "TS1170" && finding.Suppressed);
        candidate = source.WithDocumentText(document.Id, SourceText.From("public class AnalyzerFailure {}"));
        await Assert.ThrowsAsync<AggregateException>(() => engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, TestContext.Current.CancellationToken));
        candidate = source.WithProjectAnalyzerReferences(project.Id, []);
        var missing = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, TestContext.Current.CancellationToken);
        Assert.Equal(["TS1170"], Assert.Single(missing.After).UnavailableIds);
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsCancelledAnalyzerExecutionIsAbandonedAndNeverReturnedClean : EditAnalyzerTests
{
    /// <summary>The existing lifetime wrapper abandons a non-cooperative action at explicit cancellation.</summary>
    [Fact]
    public static async Task CancelledAnalyzerExecutionIsAbandonedAndNeverReturnedClean()
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        await using var events = new DomainEventStream();
        var clock = new ObservedTimeProvider();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: clock);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = source.Projects.Single(project => project.Name == "Contracts").Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var candidate = source.WithDocumentText(document.Id, SourceText.From("public class AnalyzerBarrier {}"));
        var pipeName = Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var marker = ExecutionBarrierPath(fixture.Root);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(marker, pipeName, TestContext.Current.CancellationToken);
        var pending = engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], new() { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue }, false, true, cancellation.Token);
        try
        {
            await TestAnalyzerBarrier.WaitForConnectionAsync(pipe, pending, TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(2));
#pragma warning disable VSTHRD003 // The test owns the operation and joins its explicitly cancelled observation.

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
#pragma warning restore VSTHRD003
        }
        finally
        {
            File.Delete(marker);
            await TestAnalyzerBarrier.ReleaseAsync(pipe);
        }

        var recovered = await engine.AnalyzeAnalyzerCandidateAsync(source, source.WithDocumentText(document.Id, SourceText.From(Repairs)), [document.Id], new() { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue }, false, true, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(recovered.Before).Reused);
    }
}
