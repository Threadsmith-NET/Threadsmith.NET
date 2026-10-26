namespace Threadsmith.ModelTooling.Tests;

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
public static partial class EditAnalyzerTests
{
    private const string Violations = """
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

    private const string Repairs = """
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

    private static readonly JsonSerializerOptions ExperimentJsonOptions = new() { WriteIndented = true };
    private static readonly string[] FixtureDiagnosticIds = ["CA2016", "CA2012", "VSTHRD002", "VSTHRD100", "TS1170", "TS1171"];

    /// <summary>Known repairs preserve scoped coverage while compilation-wide omissions remain explicit.</summary>
    [Fact]
    public static async Task SelectedRulesDetectRepairsAndDiscloseCompilationOmissions()
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = source.Projects.Single(project => project.Name == "Contracts").Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var candidate = source.WithDocumentText(document.Id, SourceText.From(Repairs));
        var options = new EditAnalyzerOptions { DiagnosticIds = ["CA2016", "CA2012", "VSTHRD002", "VSTHRD100", "TS1170", "TS1171", "MISSING"], BudgetMilliseconds = int.MaxValue };

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

    /// <summary>Fresh drivers observe all immutable input changes and never turn failures into empty success.</summary>
    [Fact]
    public static async Task ConfigurationAdditionalFilesSuppressionAndFailuresRemainDistinct()
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var project = source.Projects.Single(project => project.Name == "Contracts");
        var document = project.Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var options = new EditAnalyzerOptions { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue };
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
        Assert.Contains(Assert.Single(disabled.After).Configuration, rule => rule.Id == "TS1170" && rule.EffectiveSeverity == "Suppress");

        candidate = source.WithDocumentText(document.Id, SourceText.From("#pragma warning disable TS1170\npublic class AnalyzerProbe {}"));
        var suppressed = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, TestContext.Current.CancellationToken);
        Assert.Contains(Assert.Single(suppressed.After).Diagnostics, finding => finding.Id == "TS1170" && finding.Suppressed);

        candidate = source.WithDocumentText(document.Id, SourceText.From("public class AnalyzerFailure {}"));
        await Assert.ThrowsAsync<AggregateException>(() => engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, TestContext.Current.CancellationToken));
        candidate = source.WithProjectAnalyzerReferences(project.Id, []);
        var missing = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], options, false, true, TestContext.Current.CancellationToken);
        Assert.Equal(["TS1170"], Assert.Single(missing.After).UnavailableIds);
    }

    /// <summary>The existing lifetime wrapper abandons a non-cooperative action at explicit cancellation.</summary>
    [Fact]
    public static async Task CancelledAnalyzerExecutionIsAbandonedAndNeverReturnedClean()
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var analyzer = StageExecutionAnalyzer(fixture.Root);
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        await engine.LoadAsync(TestSemanticSolution.CreateLoadRequest(fixture.Root), TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = source.Projects.Single(project => project.Name == "Contracts").Documents.Single(document => document.Name == "AnalyzerCases.cs");
        var candidate = source.WithDocumentText(document.Id, SourceText.From("public class AnalyzerBarrier {}"));
        var pipeName = Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var marker = analyzer + ".block-execution";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(marker, pipeName, TestContext.Current.CancellationToken);
        var pending = engine.AnalyzeAnalyzerCandidateAsync(source, candidate, [document.Id], new() { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue }, false, true, cancellation.Token);
        try
        {
            await TestAnalyzerBarrier.WaitForConnectionAsync(pipe, pending, TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
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

    /// <summary>Opt-in elapsed measurements take workload, budget and output location from explicit input.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public static async Task MeasureSelectedAnalyzersThroughProductionQueue()
    {
        var input = Environment.GetEnvironmentVariable("THREADSMITH_ANALYZER_EXPERIMENT");
        if (string.IsNullOrWhiteSpace(input))
        {
            Assert.Skip("Set THREADSMITH_ANALYZER_EXPERIMENT to an experiment JSON input path.");
        }

        var experiment = JsonSerializer.Deserialize<AnalyzerExperiment>(await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken))!;
        Assert.True(experiment.Repetitions > 0 && experiment.LatencyTargetMilliseconds > 0);
        using var fixture = experiment.Target == "fixture" ? new TestSemanticSolution() : null;
        if (fixture is not null)
        {
            await ConfigureFixtureAsync(fixture.Root);
        }

        var root = fixture?.Root ?? Path.GetFullPath(experiment.RepositoryRoot);
        var selection = fixture is null ? Path.GetFullPath(experiment.Selection, root) : Path.Combine(root, "SmallDotNetSolution.sln");
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { CompilationWorkers = experiment.Workers, InitialPreparationFrontier = experiment.Workers });
        var loadStarted = Stopwatch.GetTimestamp();
        var load = await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, selection, RepositoryTrustLevel.TrustedBuild), TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        Assert.True(engine.Confidence >= SemanticConfidenceLevel.PartialCompilation, string.Join(Environment.NewLine, load.Diagnostics));
        var loadMilliseconds = Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds;
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var file = fixture is null ? Path.GetFullPath(experiment.File, root) : Path.Combine(root, "Contracts", "AnalyzerCases.cs");
        var documents = source.GetDocumentIdsWithFilePath(file);
        Assert.NotEmpty(documents);
        var records = new List<object>();
        using var process = Process.GetCurrentProcess();
        for (var iteration = 0; iteration <= experiment.Repetitions; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            var cpu = process.TotalProcessorTime;
            var candidate = source;
            foreach (var id in documents)
            {
                var text = (await source.GetDocument(id)!.GetTextAsync(TestContext.Current.CancellationToken)).ToString();
                candidate = candidate.WithDocumentText(id, SourceText.From(fixture is not null ? Repairs + $"\n// edit {iteration}" : text.Replace(experiment.Find, experiment.Replace, StringComparison.Ordinal) + $"\n// analyzer experiment {iteration}"));
            }

            var preparation = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var exploration = experiment.ConcurrentExploration ? engine.FindSymbolsAsync("SemanticEngine", TestContext.Current.CancellationToken) : null;
            EditAnalyzerComparison? result = null;
            var outcome = "completed";
            try
            {
                result = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, documents, experiment.Options, false, experiment.ReuseBaseline, TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                outcome = "budget-cancelled";
            }

            var available = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (exploration is not null)
            {
#pragma warning disable VSTHRD003 // The measurement owns its overlapping production query.
                await exploration;
#pragma warning restore VSTHRD003
            }

            process.Refresh();
            records.Add(new
            {
                iteration,
                temperature = iteration == 0 ? "first-use" : "warm",
                outcome,
                preparation,
                available,
                cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                process.WorkingSet64,
                process.PeakWorkingSet64,
                result,
            });
        }

        var rapid = new List<object>();
        for (var edit = 0; edit < experiment.RapidEdits; edit++)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var candidate = source.WithDocumentText(documents[0], SourceText.From(fixture is not null ? Repairs + $"\n// rapid {edit}" : await source.GetDocument(documents[0])!.GetTextAsync(cancellation.Token) + $"\n// rapid {edit}"));
            var started = Stopwatch.GetTimestamp();
            var pending = engine.AnalyzeAnalyzerCandidateAsync(source, candidate, documents, experiment.Options, false, experiment.ReuseBaseline, cancellation.Token);
            await Task.Yield();
            await cancellation.CancelAsync();
            var outcome = "completed-before-cancellation";
            try
            {
#pragma warning disable VSTHRD003 // The harness owns and explicitly cancels each rapid edit observation.
                await pending;
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                outcome = "cancelled";
            }

            rapid.Add(new { edit, outcome, elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }

        // Reference coverage is outside every edit latency sample and only runs on the small fixture.
        var reference = fixture is not null
            ? await engine.AnalyzeAnalyzerCandidateAsync(source, source.WithDocumentText(documents[0], SourceText.From(Repairs)), documents, experiment.Options, true, true, TestContext.Current.CancellationToken)
            : null;
        await File.WriteAllTextAsync(experiment.OutputPath, JsonSerializer.Serialize(new { experiment, loadMilliseconds, records, rapid, reference }, ExperimentJsonOptions), TestContext.Current.CancellationToken);
    }

    private static async Task ConfigureFixtureAsync(string root)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectPath = Path.Combine(root, "Contracts", "Contracts.csproj");
        var project = XDocument.Load(projectPath);
        project.Root!.Add(new XElement(
            "ItemGroup",
            new XElement("Analyzer", new XAttribute("Include", Path.Combine(TestSemanticSolution.SourceRoot, "Analyzers", "Microsoft.VisualStudio.Threading.Analyzers.dll"))),
            new XElement("Analyzer", new XAttribute("Include", Path.Combine(TestSemanticSolution.SourceRoot, "Analyzers", "Microsoft.VisualStudio.Threading.Analyzers.CSharp.dll"))),
            new XElement("AdditionalFiles", new XAttribute("Include", "analyzer-input.txt"))));
        project.Save(projectPath);
        await File.WriteAllTextAsync(Path.Combine(root, "Contracts", "AnalyzerCases.cs"), Violations, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "Contracts", "analyzer-input.txt"), "enabled", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, ".editorconfig"), "root = true\n[*.cs]\n" + string.Join('\n', FixtureDiagnosticIds.Select(id => "dotnet_diagnostic." + id + ".severity = suggestion")), cancellationToken);
    }

    private static string StageExecutionAnalyzer(string root)
    {
        // The loader retains DLLs for process lifetime; each barrier needs its own staged assembly.
        var staging = Path.Combine(AppContext.BaseDirectory, "plan117", "analyzer-execution", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var analyzer = Path.Combine(staging, "Threadsmith.SemanticFixtures.Roslyn59.dll");
        File.Copy(Path.Combine(TestSemanticSolution.SourceRoot, "Analyzers", Path.GetFileName(analyzer)), analyzer);
        var projectPath = Path.Combine(root, "Contracts", "Contracts.csproj");
        var projectXml = XDocument.Load(projectPath);
        projectXml.Descendants("Analyzer").Single(item => item.Attribute("Include")!.Value.EndsWith(Path.GetFileName(analyzer), StringComparison.Ordinal)).SetAttributeValue("Include", analyzer);
        projectXml.Save(projectPath);
        return analyzer;
    }

    private sealed record AnalyzerExperiment(string Target, string RepositoryRoot, string Selection, string File, string Find, string Replace,
        int Repetitions, int Workers, bool ConcurrentExploration, int LatencyTargetMilliseconds, EditAnalyzerOptions Options, string OutputPath, int RapidEdits = 0, bool ReuseBaseline = true);
}
