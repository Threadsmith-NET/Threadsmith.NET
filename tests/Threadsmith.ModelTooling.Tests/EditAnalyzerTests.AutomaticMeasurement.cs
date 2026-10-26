namespace Threadsmith.ModelTooling.Tests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public static partial class EditAnalyzerTests
{
    /// <summary>Repeats the frozen overlap workload through automatic admission and immediate writer promotion.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public static async Task MeasureAutomaticAnalyzerFeedbackAndCompetingQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Environment.GetEnvironmentVariable("THREADSMITH_ADVISORY_EXPERIMENT");
        if (string.IsNullOrWhiteSpace(input))
        {
            Assert.Skip("Set THREADSMITH_ADVISORY_EXPERIMENT to an overlap experiment JSON input path.");
        }

        var experiment = JsonSerializer.Deserialize<AnalyzerOverlapExperiment>(await File.ReadAllTextAsync(input, ct))!;
        using var fixture = experiment.Target == "fixture" ? new TestSemanticSolution() : null;
        if (fixture is not null)
        {
            await ConfigureFixtureAsync(fixture.Root);
        }

        var root = fixture?.Root ?? Path.GetFullPath(experiment.RepositoryRoot);
        var selection = fixture is null ? Path.GetFullPath(experiment.Selection, root) : Path.Combine(root, "SmallDotNetSolution.sln");
        var file = fixture is null ? Path.GetFullPath(experiment.File, root) : Path.Combine(root, "Contracts", "AnalyzerCases.cs");
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { CompilationWorkers = experiment.Workers, InitialPreparationFrontier = experiment.Workers, EditAnalysis = new() { Analyzers = experiment.Options with { Enabled = true } } });
        var load = await engine.LoadAsync(new(session, workspace, root, selection, RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        Assert.True(engine.Confidence >= SemanticConfidenceLevel.PartialCompilation, string.Join(Environment.NewLine, load.Diagnostics));
        var initial = engine.CaptureAdvancedSnapshot().Solution;
        var document = initial.GetDocumentIdsWithFilePath(file)[0];
        var original = (await initial.GetDocument(document)!.GetTextAsync(ct)).ToString();
        var records = new List<object>();
        using var process = Process.GetCurrentProcess();
        for (var iteration = 0; iteration <= experiment.Repetitions; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            var cpu = process.TotalProcessorTime;
            var compiler = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            double? analyzerMilliseconds = null;
            await using var subscription = events.Subscribe((item, _) =>
            {
                if (item is SemanticCheckCompleted check)
                {
                    if (check.CheckName == "advisory edit analyzers")
                    {
                        analyzerMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                    else if (check.CheckName == "advisory edit analysis")
                    {
                        compiler.TrySetResult(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                }

                return Task.CompletedTask;
            });
            var source = engine.CaptureAdvancedSnapshot().Solution;
            var before = (await source.GetDocument(document)!.GetTextAsync(ct)).ToString();
            var body = fixture is not null ? iteration % 2 == 0 ? Repairs : Violations
                : experiment.Substantive && iteration % 2 != 0 ? original : original.Replace(experiment.Find, experiment.Replace, StringComparison.Ordinal);
            var after = body + $"\n// overlap experiment {iteration}";
            var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = ApplyAutomaticCandidateAsync(engine, events, session, workspace, root, file, before, after, promoted, ct);
            await promoted.Task.WaitAsync(ct);
            var queryStarted = Stopwatch.GetTimestamp();
            await engine.FindSymbolsAsync(fixture is null ? "SemanticEngine" : "AnalyzerCases", ct);
            var queryMilliseconds = Stopwatch.GetElapsedTime(queryStarted).TotalMilliseconds;
#pragma warning disable VSTHRD003 // Measurement owns the overlapping automatic edit observation.
            var analysis = await pending;
#pragma warning restore VSTHRD003
            var compilerMilliseconds = await compiler.Task.WaitAsync(ct);
            process.Refresh();
            records.Add(new
            {
                iteration,
                temperature = iteration == 0 ? "first-use" : "warm",
                analyzerMilliseconds,
                compilerMilliseconds,
                queryMilliseconds,
                cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                process.WorkingSet64,
                process.PeakWorkingSet64,
                analysis,
            });
        }

        await File.WriteAllTextAsync(experiment.OutputPath, JsonSerializer.Serialize(new { experiment, records }, ExperimentJsonOptions), ct);
    }
}
