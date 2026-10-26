namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public static partial class EditAnalyzerTests
{
    /// <summary>Automatic scoped evidence preserves suggestions, shared exclusions and comparable repairs.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public static async Task AutomaticFindingsAreQualifiedFilteredAndRepaired(bool exclude, bool asError)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        if (asError)
        {
            var config = Path.Combine(fixture.Root, ".editorconfig");
            var text = await File.ReadAllTextAsync(config, ct);
            await File.WriteAllTextAsync(config, text.Replace("dotnet_diagnostic.CA2016.severity = suggestion", "dotnet_diagnostic.CA2016.severity = error", StringComparison.Ordinal), ct);
        }

        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new() { EditAnalysis = new() { CandidateLifetimeMilliseconds = int.MaxValue, ExcludedDiagnosticIds = exclude ? ["CA2016"] : [], Analyzers = new() { Enabled = true, BudgetMilliseconds = int.MaxValue } } });
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var file = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        var first = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, Violations, Violations + "\n// candidate", null, ct);
        Assert.Equal("completed", first.Analyzers.Status);
        var analyzerFindings = first.Diagnostics.Where(item => item.Producer == "analyzer").ToArray();
        Assert.Equal(exclude ? 3 : 4, analyzerFindings.Select(item => item.Code).Distinct().Count());
        Assert.Equal(analyzerFindings.Length, first.Analyzers.ReportableFindings);
        Assert.Equal(exclude, first.Analyzers.OutputFiltered);
        Assert.Equal(0, first.Analyzers.NewFindings);
        Assert.All(first.Diagnostics.Where(item => item.Producer == "analyzer"), item =>
        {
            Assert.NotNull(item.AnalyzerIdentity);
            Assert.Equal("initial", item.Origin);
            Assert.Equal(asError && item.Code == "CA2016" ? DiagnosticSeverity.Error : DiagnosticSeverity.Info, item.Severity);
        });
        if (exclude)
        {
            Assert.DoesNotContain(first.Diagnostics, item => item.Code == "CA2016");
        }

        var repaired = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, Violations + "\n// candidate", Repairs, null, ct);
        Assert.Equal("completed", repaired.Analyzers.Status);
        Assert.Equal(0, repaired.Analyzers.ReportableFindings);
        Assert.Equal(first.Analyzers.ReportableFindings, repaired.Analyzers.ResolvedFindings);
        Assert.DoesNotContain(repaired.Diagnostics, item => item.Producer == "analyzer");
    }

    /// <summary>A physical analyzer barrier does not prevent compiler/query progress or matching promotion.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public static async Task AutomaticBlockedWorkSurvivesPromotionAndCoalescesReplacement(bool replace, bool timeout, bool lateFault)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var analyzer = StageExecutionAnalyzer(fixture.Root);
        var file = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        var clock = new AnalyzerDeadlineClock();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new() { CompilationWorkers = 2, EditAnalysis = new() { CandidateLifetimeMilliseconds = int.MaxValue, Analyzers = new() { Enabled = true, DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue } } },
            timeProvider: clock)
        {
            AnalyzerQualification = identity => identity.Assembly == "Threadsmith.SemanticFixtures.Roslyn59",
        };
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
            if (timeout)
            {
                clock.Expire();
                Assert.Equal("timed-out", (await advisory.Task.WaitAsync(ct)).Analyzers.Status);
                Assert.Equal(1, engine.ActiveAdvisoryOperations);
            }

            if (replace || timeout)
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
                Assert.Equal(timeout ? "unavailable" : lateFault ? "failed" : "completed", current.Analyzers.Status);
                Assert.Equal(0, current.Analyzers.ReportableFindings);
                if (lateFault)
                {
                    var quarantined = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, Repairs, Repairs + "\n// after fault", null, ct);
                    Assert.Equal("unavailable", quarantined.Analyzers.Status);
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

    /// <summary>Missing, unqualified, suppressed and faulted execution retain distinct statuses.</summary>
    [Theory]
    [InlineData("unqualified")]
    [InlineData("missing")]
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
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { CandidateLifetimeMilliseconds = int.MaxValue, Analyzers = new() { Enabled = true, DiagnosticIds = scenario == "missing" ? ["MISSING"] : ["TS1170"], BudgetMilliseconds = int.MaxValue } } })
        {
            AnalyzerQualification = identity => scenario != "unqualified" && identity.Assembly == "Threadsmith.SemanticFixtures.Roslyn59",
        };
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var file = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        var after = scenario == "fault" ? "public class AnalyzerFailure {}" : "#pragma warning disable TS1170\npublic class AnalyzerProbe {}";
        var result = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, Violations, after, null, ct);
        Assert.Equal(scenario == "suppressed" ? "completed" : scenario == "fault" ? "failed" : "unavailable", result.Analyzers.Status);
        Assert.DoesNotContain(result.Diagnostics, item => item.Producer == "analyzer");
        if (scenario == "fault")
        {
            result = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, file, after, Violations, null, ct);
            Assert.Equal("unavailable", result.Analyzers.Status);
            Assert.Contains(result.Analyzers.Omissions, omission => omission.Contains("disabled after failure", StringComparison.Ordinal));
        }
    }

    /// <summary>Initial evidence from one document cannot label another document's existing finding introduced.</summary>
    [Fact]
    public static async Task AutomaticInitialOriginsRequireMatchingDocumentCoverage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var second = Path.Combine(fixture.Root, "Contracts", "OtherProbe.cs");
        const string existing = "namespace Other { public class AnalyzerProbe {} }";
        await File.WriteAllTextAsync(second, existing, ct);
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { CandidateLifetimeMilliseconds = int.MaxValue, Analyzers = new() { Enabled = true, DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue } } })
        {
            AnalyzerQualification = identity => identity.Assembly == "Threadsmith.SemanticFixtures.Roslyn59",
        };
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs"), Violations, Repairs, null, ct);
        var result = await ApplyAutomaticCandidateAsync(engine, events, session, workspace, fixture.Root, second, existing, existing + "\n// edit", null, ct);
        Assert.Equal(0, result.Analyzers.NewFindings);
        Assert.Equal("unknown", Assert.Single(result.Diagnostics, item => item.Producer == "analyzer").Origin);
    }

    /// <summary>Evaluated graph replacements honor changed configuration/additional inputs and abandon old origins.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task AutomaticGraphReplacementUsesCurrentInputs(bool additional)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await ConfigureFixtureAsync(fixture.Root);
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        var logger = new AnalyzerTestLogger();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, logger, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { CandidateLifetimeMilliseconds = int.MaxValue, Analyzers = new() { Enabled = true, DiagnosticIds = ["TS1170"], BudgetMilliseconds = int.MaxValue } } })
        {
            AnalyzerQualification = identity => identity.Assembly == "Threadsmith.SemanticFixtures.Roslyn59",
        };
        await engine.LoadAsync(new(session, workspace, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var project = engine.CaptureAdvancedSnapshot().Solution.Projects.Single(item => item.Name == "Contracts");
        var input = additional ? Assert.Single(project.AdditionalDocuments).FilePath! : project.AnalyzerConfigDocuments.Single(item => item.Name == ".editorconfig").FilePath!;
        var previousInput = await File.ReadAllTextAsync(input, ct);
        var inputText = additional ? "disable-probe" : "root = true\n[*.cs]\ndotnet_diagnostic.TS1170.severity = none\n";
        var file = Path.Combine(fixture.Root, "Contracts", "AnalyzerCases.cs");
        const string after = Violations + "\n// config edit";
        var command = new ApplySourceEditCommand(session, RunId.New(), workspace, Guid.NewGuid(), new() { Rationale = "Check refreshed analyzer inputs.", Mutations = [] });
        var snapshots = new MutationEffectSnapshot(MutationSetId.New(), [], [.. AutomaticSnapshot(fixture.Root, file, Violations, after).Endpoints, .. AutomaticSnapshot(fixture.Root, input, previousInput, inputText).Endpoints]);
        Assert.NotNull(await engine.AnalyzeCandidateAsync(command, fixture.Root, snapshots, TimeSpan.Zero, ct));
        await File.WriteAllTextAsync(file, after, ct);
        await File.WriteAllTextAsync(input, inputText, ct);
        engine.ConfirmEditApplied(command.EffectId);
        await engine.RefreshFullAsync(ct);
        var completed = new TaskCompletionSource<SourceEditAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { CheckName: "advisory edit analyzers", Analysis: { Obsolete: false, CommittedGeneration: not null } analysis })
            {
                completed.TrySetResult(analysis);
            }

            return Task.CompletedTask;
        });
        engine.ContinueEditAfterGraphRefresh(new Dictionary<string, string?> { [file] = AnalyzerHash(after), [input] = AnalyzerHash(inputText) });
        var result = await completed.Task.WaitAsync(ct);
        Assert.True(result.Analyzers.Status == "partial", string.Join(Environment.NewLine, logger.Exceptions));
        Assert.Equal(1, result.Analyzers.DocumentsAnalyzed);
        Assert.Equal(0, result.Analyzers.ReportableFindings);
        Assert.Null(result.Analyzers.NewFindings);
    }

    private sealed class AnalyzerTestLogger : ILogger<SemanticEngine>
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

    private sealed class AnalyzerDeadlineClock : TimeProvider
    {
        private readonly ConcurrentBag<DeadlineTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public void Expire()
        {
            foreach (var timer in _timers)
            {
                timer.Fire();
            }
        }

        private sealed class DeadlineTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private int _disposed;

            public DeadlineTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;

            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _callback(_state);
                }
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static MutationEffectSnapshot AutomaticSnapshot(string root, string file, string before, string after) =>
        new(MutationSetId.New(), [], [new(Path.GetRelativePath(root, file), AnalyzerHash(before), AnalyzerHash(after)) { FinalBytes = Encoding.UTF8.GetBytes(after) }]);

    private static async Task<SourceEditAnalysis> ApplyAutomaticCandidateAsync(SemanticEngine engine, DomainEventStream events, SessionId session, WorkspaceId workspace, string root, string file, string before, string after, TaskCompletionSource? promoted, CancellationToken cancellationToken)
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

