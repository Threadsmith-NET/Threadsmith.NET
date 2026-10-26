namespace Threadsmith.ModelTooling.Tests;

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Characterizes shared-queue overlap without enabling automatic analyzer feedback.</summary>
public static partial class EditAnalyzerTests
{
    /// <summary>Compiler feedback completes independently; cancellation or promotion discards blocked snapshot analysis.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompilerCompletesWhileAnalyzerIsBlocked(bool promote)
    {
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var analyzer = StageExecutionAnalyzer(fixture.Root);
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        const string original = Violations + "\npublic class AnalyzerBarrier {}";
        await File.WriteAllTextAsync(path, original, ct);
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { CompilationWorkers = 2, InitialPreparationFrontier = 2, EditAnalysis = new() { CandidateLifetimeMilliseconds = int.MaxValue } });
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var source = engine.CaptureAdvancedSnapshot().Solution;
        var document = Assert.Single(source.GetDocumentIdsWithFilePath(path));
        var options = new EditAnalyzerOptions { DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue };
        var pipeName = Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var marker = analyzer + ".block-execution";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await File.WriteAllTextAsync(marker, pipeName, ct);
        var pending = engine.AnalyzeAnalyzerCandidateAsync(source, source, [document], options, false, true, cancellation.Token);
        try
        {
            await TestAnalyzerBarrier.WaitForConnectionAsync(pipe, pending, ct);
            var command = new ApplySourceEditCommand(session, RunId.New(), workspace, Guid.NewGuid(), new() { Rationale = "Verify independent compiler completion.", Mutations = [] });
            var completed = new TaskCompletionSource<SemanticCheckCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = events.Subscribe((item, _) =>
            {
                if (item is SemanticCheckCompleted check && check.Analysis?.EffectId == command.EffectId)
                {
                    completed.TrySetResult(check);
                }

                return Task.CompletedTask;
            });
            var snapshot = new MutationEffectSnapshot(MutationSetId.New(), [], [new("Contracts/AnalyzerCases.cs", AnalyzerHash(original), AnalyzerHash(Repairs)) { FinalBytes = Encoding.UTF8.GetBytes(Repairs) }]);
            Assert.NotNull(await engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.Zero, ct));
            var compiler = await completed.Task.WaitAsync(ct);
            Assert.Equal(SemanticCheckOutcome.Completed, compiler.Outcome);
            Assert.False(pending.IsCompleted);
            if (promote)
            {
                await File.WriteAllTextAsync(path, Repairs, ct);
                var written = await File.ReadAllTextAsync(path, ct);
                engine.ConfirmEditApplied(command.EffectId);
                await engine.RefreshDocumentsAsync([new(path, written, AnalyzerHash(written))], ct);
                Assert.True(engine.GetLatestEditAnalysis(session, command.RunId, command.EffectId)!.CandidateReused);
            }
            else
            {
                await cancellation.CancelAsync();
            }

#pragma warning disable VSTHRD003 // The test owns and joins this explicitly cancelled or superseded observation.
            if (promote)
            {
                var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                Assert.Contains("superseded", rejected.Message, StringComparison.Ordinal);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
#pragma warning restore VSTHRD003
        }
        finally
        {
            File.Delete(marker);
            await cancellation.CancelAsync();
            await TestAnalyzerBarrier.ReleaseAsync(pipe);
        }

        var current = engine.CaptureAdvancedSnapshot().Solution;
        var recovered = await engine.AnalyzeAnalyzerCandidateAsync(current, current.WithDocumentText(document, SourceText.From(Repairs)), [document], options, false, true, ct);
        Assert.False(Assert.Single(recovered.Before).Reused);
    }

    /// <summary>Measures sequential receipt-backed promotions with compiler-first or concurrent analyzer submission.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public static async Task MeasureCompilerAnalyzerOverlapThroughProductionQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Environment.GetEnvironmentVariable("THREADSMITH_ANALYZER_OVERLAP_EXPERIMENT");
        if (string.IsNullOrWhiteSpace(input))
        {
            Assert.Skip("Set THREADSMITH_ANALYZER_OVERLAP_EXPERIMENT to an overlap experiment JSON input path.");
        }

        var experiment = JsonSerializer.Deserialize<AnalyzerOverlapExperiment>(await File.ReadAllTextAsync(input, ct))!;
        Assert.True(experiment.Repetitions > 0 && experiment.LatencyTargetMilliseconds > 0);
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
        var run = RunId.New();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { CompilationWorkers = experiment.Workers, InitialPreparationFrontier = experiment.Workers });
        var loadStarted = Stopwatch.GetTimestamp();
        var load = await engine.LoadAsync(new(session, workspace, root, selection, RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        Assert.True(engine.Confidence >= SemanticConfidenceLevel.PartialCompilation, string.Join(Environment.NewLine, load.Diagnostics));
        var loadMilliseconds = Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds;
        var initial = engine.CaptureAdvancedSnapshot().Solution;
        var documents = initial.GetDocumentIdsWithFilePath(file);
        Assert.NotEmpty(documents);
        var original = (await initial.GetDocument(documents[0])!.GetTextAsync(ct)).ToString();
        if (fixture is null)
        {
            Assert.Contains(experiment.Find, original, StringComparison.Ordinal);
        }

        var records = new List<object>();
        EditAnalyzerComparison? previous = null;
        using var process = Process.GetCurrentProcess();
        for (var iteration = 0; iteration <= experiment.Repetitions; iteration++)
        {
            var command = new ApplySourceEditCommand(session, run, workspace, Guid.NewGuid(), new() { Rationale = "Measure shared-queue compiler/analyzer overlap.", Mutations = [] });
            var started = Stopwatch.GetTimestamp();
            var cpu = process.TotalProcessorTime;
            var completed = new TaskCompletionSource<(SemanticCheckCompleted Check, double Milliseconds)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = events.Subscribe((item, _) =>
            {
                if (item is SemanticCheckCompleted check && check.Analysis?.EffectId == command.EffectId)
                {
                    completed.TrySetResult((check, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                }

                return Task.CompletedTask;
            });
            var source = engine.CaptureAdvancedSnapshot().Solution;
            var beforeText = (await source.GetDocument(documents[0])!.GetTextAsync(ct)).ToString();
            var afterText = (fixture is not null ? (iteration % 2 == 0 ? Repairs : Violations) : original.Replace(experiment.Find, experiment.Replace, StringComparison.Ordinal)) + $"\n// overlap experiment {iteration}";
            var snapshot = new MutationEffectSnapshot(MutationSetId.New(), [], [new(Path.GetRelativePath(root, file), AnalyzerHash(beforeText), AnalyzerHash(afterText)) { FinalBytes = Encoding.UTF8.GetBytes(afterText) }]);
            var passes = engine.EditAnalysisStatistics.DiagnosticPasses;
            Assert.NotNull(await engine.AnalyzeCandidateAsync(command, root, snapshot, TimeSpan.Zero, ct));
            var candidate = engine.CaptureEditCandidateSnapshot(command.EffectId);
            var preparationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!experiment.Concurrent)
            {
                await completed.Task.WaitAsync(ct);
            }

            var analyzerSubmittedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            EditAnalyzerComparison? result = null;
            var analyzerOutcome = "completed";
            try
            {
                result = await engine.AnalyzeAnalyzerCandidateAsync(source, candidate, documents, experiment.Options, false, true, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                analyzerOutcome = "budget-cancelled";
            }

            var analyzerAvailableMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var compiler = await completed.Task.WaitAsync(ct);
            Assert.Equal(SemanticCheckOutcome.Completed, compiler.Check.Outcome);
            Assert.False(compiler.Check.Analysis!.Pending);
            Assert.False(compiler.Check.Analysis.Obsolete);
            var bothAvailableMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var compilerPasses = engine.EditAnalysisStatistics.DiagnosticPasses - passes;
            if (result is not null)
            {
                Assert.All(result.After, project => Assert.False(project.Reused));
                Assert.All(result.Before.Concat(result.After), project => Assert.Empty(project.UnavailableIds));
                if (previous is not null)
                {
                    Assert.All(result.Before, project => Assert.True(project.Reused));
                    Assert.Equal(previous.After.SelectMany(project => project.Diagnostics), result.Before.SelectMany(project => project.Diagnostics));
                }

                if (fixture is not null)
                {
                    foreach (var id in experiment.Options.DiagnosticIds)
                    {
                        Assert.Equal(iteration % 2 != 0, result.After.SelectMany(project => project.Diagnostics).Any(finding => finding.Id == id));
                    }
                }
            }

            // Measure checks against one exact candidate before promotion. The active checkout is never written.
            // The separate barrier regression exercises physical fixture writes while analyzers are still pending.
            var promotionStarted = Stopwatch.GetTimestamp();
            engine.ConfirmEditApplied(command.EffectId);
            await engine.RefreshDocumentsAsync([new(file, afterText, snapshot.Endpoints[0].AfterSha256!)], ct);
            Assert.Same(candidate, engine.CaptureAdvancedSnapshot().Solution);
            Assert.True(engine.GetLatestEditAnalysis(session, run, command.EffectId)!.CandidateReused);
            var promotionMilliseconds = Stopwatch.GetElapsedTime(promotionStarted).TotalMilliseconds;
            process.Refresh();
            records.Add(new
            {
                iteration,
                temperature = iteration == 0 ? "first-use" : "warm",
                preparationMilliseconds,
                compilerAvailableMilliseconds = compiler.Milliseconds,
                compiler = compiler.Check.Analysis,
                compilerPasses,
                analyzerSubmittedMilliseconds,
                analyzerAvailableMilliseconds,
                analyzerOutcome,
                bothAvailableMilliseconds,
                promotionMilliseconds,
                cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                process.WorkingSet64,
                process.PeakWorkingSet64,
                result,
            });
            previous = result;
        }

        await File.WriteAllTextAsync(experiment.OutputPath, JsonSerializer.Serialize(new { experiment, loadMilliseconds, records }, ExperimentJsonOptions), ct);
    }

    private sealed record AnalyzerOverlapExperiment(string Target, string RepositoryRoot, string Selection, string File, string Find, string Replace,
        int Repetitions, int Workers, bool Concurrent, int LatencyTargetMilliseconds, EditAnalyzerOptions Options, string OutputPath, bool Substantive = false);
}
