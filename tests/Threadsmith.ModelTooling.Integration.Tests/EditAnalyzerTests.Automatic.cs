namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public abstract partial class EditAnalyzerTests
{
    private protected sealed class AnalyzerTestLogger : ILogger<SemanticEngine>
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Exceptions.Enqueue(exception);
            }
        }
    }

    private protected static MutationEffectSnapshot AutomaticSnapshot(string root, string file, string before, string after) => new(MutationSetId.New(), [], [new(Path.GetRelativePath(root, file), AnalyzerHash(before), AnalyzerHash(after)) { FinalBytes = Encoding.UTF8.GetBytes(after) }]);

    private protected static async Task<SourceEditAnalysis> ApplyAutomaticCandidateAsync(SemanticEngine engine, DomainEventStream events, SessionId session, WorkspaceId workspace, string root, string file, string before, string after, TaskCompletionSource? promoted, CancellationToken cancellationToken)
    {
        var command = new ApplySourceEditCommand(session, RunId.New(), workspace, Guid.NewGuid(), new() { Rationale = "Check automatic analyzer feedback.", Mutations = [] });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { CheckName: "advisory edit analyzers", Analysis: { } analysis } && analysis.EffectId == command.EffectId)
            {
                completed.TrySetResult();
            }

            return Task.CompletedTask;
        });
        Assert.NotNull(await engine.AnalyzeCandidateAsync(command, root, AutomaticSnapshot(root, file, before, after), TimeSpan.Zero, cancellationToken));
        engine.ConfirmEditApplied(command.EffectId);
        await engine.RefreshDocumentsAsync([new(file, after, AnalyzerHash(after))], cancellationToken);
        promoted?.TrySetResult();
        await completed.Task.WaitAsync(cancellationToken);
        return engine.GetLatestEditAnalysis(session, command.RunId, command.EffectId)!;
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsAutomaticFeedbackCountsSeveritiesBeforeRetention : EditAnalyzerTests
{
    /// <summary>Analyzer severity totals include reportable findings omitted by retention limits.</summary>
    [Theory]
    [InlineData(0, DiagnosticSeverity.Info)]
    [InlineData(5, DiagnosticSeverity.Error)]
    public static async Task AutomaticFeedbackCountsSeveritiesBeforeRetention(int retainedLimit, DiagnosticSeverity severity)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var file = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        var source = string.Join('\n', Enumerable.Range(0, 70).Select(index => $"namespace Probe{index} {{ public class AnalyzerProbe {{}} }}"));
        await File.WriteAllTextAsync(file, source, ct);
        var configuredSeverity = severity == DiagnosticSeverity.Error ? "error" : severity == DiagnosticSeverity.Warning ? "warning" : "suggestion";
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, ".editorconfig"), $"\ndotnet_diagnostic.TS1170.severity = {configuredSeverity}\n", ct);
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { MaximumRetainedDiagnostics = retainedLimit, Analyzers = new() { Enabled = true, DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue } } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var result = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, source, source + "\n// edit", null, ct);
        Assert.Equal(retainedLimit == 0 ? "completed" : "partial", result.Analyzers.Status);
        Assert.Equal(retainedLimit == 0 ? 70 : retainedLimit, result.Diagnostics.Count(item => item.Code == "TS1170"));
        Assert.Equal(70, result.Analyzers.ReportableFindings);
        Assert.Equal(severity == DiagnosticSeverity.Error ? 70 : 0, result.Analyzers.Errors);
        Assert.Equal(severity == DiagnosticSeverity.Warning ? 70 : 0, result.Analyzers.Warnings);
        Assert.Equal(severity == DiagnosticSeverity.Info ? 70 : 0, result.Analyzers.Suggestions);
        Assert.Equal(retainedLimit > 0, result.Analyzers.Truncated);
        Assert.Equal(retainedLimit > 0, result.DiagnosticsTruncated);
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsAutomaticBlockedWorkIgnoresDeadlineAndCoalescesReplacement : EditAnalyzerTests
{
    /// <summary>A physical analyzer barrier does not prevent compiler/query progress or matching promotion.</summary>
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public static async Task AutomaticBlockedWorkIgnoresDeadlineAndCoalescesReplacement(bool replace, bool advanceClock, bool lateFault)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var analyzer = StageExecutionAnalyzer(fixture.Root);
        var file = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { CompilationWorkers = 2, EditAnalysis = new() { Analyzers = new() { Enabled = true, DiagnosticIds = ["TS1170"], BudgetMilliseconds = 1, ColdStartBudgetMilliseconds = 1 } } }, timeProvider: clock);
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var pipeName = Guid.NewGuid().ToString("N")[..16];
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var marker = analyzer + ".block-execution";
        await File.WriteAllTextAsync(marker, pipeName, ct);
        var command = new ApplySourceEditCommand(session, RunId.New(), workspace, Guid.NewGuid(), new() { Rationale = "Exercise automatic candidate ownership.", Mutations = [] });
        var compiler = new TaskCompletionSource<SourceEditAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var advisory = new TaskCompletionSource<SourceEditAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { Analysis: { } analysis } check && analysis.EffectId == command.EffectId)
            {
                (check.CheckName == "advisory edit analyzers" ? advisory : compiler).TrySetResult(analysis);
            }

            return Task.CompletedTask;
        });
        var blocked = "public class AnalyzerProbe {} public class " + (lateFault ? "AnalyzerBarrierFailure" : "AnalyzerBarrier") + " {}";
        var snapshot = AutomaticSnapshot(fixture.Root, file, Violations, blocked);
        var receipt = await engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.Zero, ct);
        Assert.NotNull(receipt);
        try
        {
            await TestAnalyzerBarrier.WaitForConnectionAsync(pipe, advisory.Task, ct);
            var compilerResult = await compiler.Task.WaitAsync(ct);
            Assert.False(compilerResult.Pending);
            Assert.Equal("pending", compilerResult.Analyzers.Status);
            Assert.NotEmpty(await engine.FindSymbolsAsync("AnalyzerCases", ct));
            await File.WriteAllTextAsync(file, blocked, ct);
            engine.ConfirmEditApplied(command.EffectId);
            await engine.RefreshDocumentsAsync([new(file, blocked, AnalyzerHash(blocked))], ct);
            Assert.True(engine.GetLatestEditAnalysis(session, command.RunId, command.EffectId)!.CandidateReused);
            Task<SourceEditAnalysis>? replacement = null;
            if (advanceClock)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                Assert.False(advisory.Task.IsCompleted);
                Assert.Equal(1, engine.ActiveAdvisoryOperations);
            }

            if (replace)
            {
                var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                replacement = ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, blocked, Repairs, promoted, ct);
                await promoted.Task.WaitAsync(ct);
                Assert.NotEmpty(await engine.FindSymbolsAsync("AnalyzerCases", ct));
                Assert.Equal(1, engine.ActiveAdvisoryOperations);
                Assert.False(replacement.IsCompleted);
                Assert.Equal(1, engine.GetAdvisoryOperationsStarted());
            }

            File.Delete(marker);
            await TestAnalyzerBarrier.ReleaseAsync(pipe);
            var result = await advisory.Task.WaitAsync(ct);
            if (replacement is not null)
            {
                Assert.Equal(replace, result.Obsolete);
#pragma warning disable VSTHRD003 // The test owns the replacement and joins it after releasing the physical action.

                var current = await replacement;
#pragma warning restore VSTHRD003
                Assert.False(current.Obsolete);
                Assert.Equal(lateFault ? "failed" : "completed", current.Analyzers.Status);
                Assert.Equal(0, current.Analyzers.ReportableFindings);
                if (lateFault)
                {
                    var retried = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, Repairs, Repairs + "\n// after fault", null, ct);
                    Assert.Equal("completed", retried.Analyzers.Status);
                }
            }
            else
            {
                Assert.False(result.Obsolete);
                Assert.NotNull(result.CommittedGeneration);
                Assert.Equal("completed", result.Analyzers.Status);
                Assert.Equal(1, result.Analyzers.ReportableFindings);
            }
        }
        finally
        {
            File.Delete(marker);
            await TestAnalyzerBarrier.ReleaseAsync(pipe);
        }
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class EditAnalyzerTestsAutomaticUnavailableSuppressedAndFaultedEvidenceIsHonest : EditAnalyzerTests
{
    /// <summary>Project rules ignore legacy selection, honor suppression and remain enabled after failure.</summary>
    [Theory]
    [InlineData("legacy-selection")]
    [InlineData("suppressed")]
    [InlineData("fault")]
    public static async Task AutomaticUnavailableSuppressedAndFaultedEvidenceIsHonest(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = true, DiagnosticIds = scenario == "legacy-selection" ? ["MISSING"] : ["TS1170"], BudgetMilliseconds = int.MaxValue } } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var file = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        var after = scenario is "installed" or "legacy-selection" ? "public class AnalyzerProbe {}" : scenario == "fault" ? "public class AnalyzerFailure {}" : "#pragma warning disable TS1170\npublic class AnalyzerProbe {}";
        var result = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, Violations, after, null, ct);
        Assert.Equal(scenario == "fault" ? "failed" : "completed", result.Analyzers.Status);
        if (scenario is "installed" or "legacy-selection")
        {
            var finding = Assert.Single(result.Diagnostics, item => item.Producer == "analyzer");
            Assert.Equal("TS1170", finding.Code);
            Assert.Equal(14, finding.Column);
            Assert.StartsWith("Threadsmith.SemanticFixtures.Roslyn59/", finding.AnalyzerIdentity);
        }
        else
        {
            Assert.DoesNotContain(result.Diagnostics, item => item.Producer == "analyzer");
        }

        if (scenario == "fault")
        {
            // The failing source is still the next comparison's baseline. Once replaced,
            // the following edit must run the same installed analyzer successfully.
            result = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, after, Violations, null, ct);
            Assert.Equal("failed", result.Analyzers.Status);
            result = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, Violations, Violations + "\n// recovered", null, ct);
            Assert.Equal("completed", result.Analyzers.Status);
            Assert.Contains(result.Diagnostics, item => item.Code == "TS1170");
        }
    }
}
