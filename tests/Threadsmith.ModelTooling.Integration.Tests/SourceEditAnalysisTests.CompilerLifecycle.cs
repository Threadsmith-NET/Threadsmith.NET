namespace Threadsmith.ModelTooling.Integration.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsIncompleteCompilerOperationsCannotSeedEvidence : SourceEditAnalysisTests
{
    /// <summary>Failed, discarded and superseded operations cannot publish computed evidence late.</summary>
    [Theory]
    [InlineData("failure", 1)]
    [InlineData("supersede", 1)]
    [InlineData("discard", 2)]
    public static async Task IncompleteCompilerOperationsCannotSeedEvidence(string termination, int blockedProject)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid blockedEffect = default;
        var observedProjects = 0;
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow))
        {
            EditProjectAnalysisObserver = async (effect, _) =>
            {
                if (effect != blockedEffect || Interlocked.Increment(ref observedProjects) != blockedProject)
                {
                    return;
                }

                entered.TrySetResult();
                // Deliberately ignore operation cancellation to exercise late actual completion.
                await release.Task.WaitAsync(ct);
                if (termination == "failure")
                {
                    throw new InvalidOperationException("Injected compiler operation failure.");
                }
            },
        };
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        var firstCommand = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        blockedEffect = firstCommand.EffectId;
        var first = AnalyzeCompletedAsync(engine, events, firstCommand, fixture.Root, Snapshot(relative, original, original + "\npublic class BlockedCandidate { }"), ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            var passes = engine.EditAnalysisStatistics.DiagnosticPasses;
            Assert.Equal(2L * blockedProject, passes);
            if (termination == "discard")
            {
                engine.DiscardEditCandidate(firstCommand.EffectId);
            }

            Task<SourceEditAnalysis>? second = null;
            var secondCommand = Command(request.SessionId, firstCommand.RunId, request.WorkspaceId);
            var secondSnapshot = Snapshot(relative, original, original + "\npublic class NextCandidate { }");
            if (termination == "supersede")
            {
                second = AnalyzeCompletedAsync(engine, events, secondCommand, fixture.Root, secondSnapshot, ct);
            }

            release.TrySetResult();
            var terminated = await first;
            Assert.False(terminated.CompilerCompletedSuccessfully);
            Assert.Equal(blockedProject - 1, terminated.ProjectsAnalyzed);
            engine.DiscardEditCandidate(firstCommand.EffectId);
            var next = await (second ?? AnalyzeCompletedAsync(engine, events, secondCommand, fixture.Root, secondSnapshot, ct));
            Assert.True(next.CompilerCompletedSuccessfully);
            Assert.Equal(passes + (2L * next.ProjectsInScope), engine.EditAnalysisStatistics.DiagnosticPasses);
            engine.DiscardEditCandidate(secondCommand.EffectId);
        }
        finally
        {
            release.TrySetResult();
        }
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsDependencyRefreshInvalidatesCompilerBaseline : SourceEditAnalysisTests
{
    /// <summary>Changed dependencies invalidate evidence even when the dependent project's baseline text is unchanged.</summary>
    [Fact]
    public static async Task DependencyRefreshInvalidatesCompilerBaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "App/Program.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        var command = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var candidate = original + "\npublic class AppCandidate { }";
        var first = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, Snapshot(relative, original, candidate), ct);
        Assert.True(first.CompilerCompletedSuccessfully);
        engine.DiscardEditCandidate(command.EffectId);
        var dependency = Path.Combine(fixture.Root, "Contracts/Services.cs");
        var changed = await File.ReadAllTextAsync(dependency, ct) + "\npublic class ExternalDependencyChange { }";
        await engine.RefreshDocumentsAsync([new(dependency, changed, Hash(changed))], ct);
        var passes = engine.EditAnalysisStatistics.DiagnosticPasses;
        var nextCommand = Command(request.SessionId, command.RunId, request.WorkspaceId);
        var next = await AnalyzeCompletedAsync(engine, events, nextCommand, fixture.Root, Snapshot(relative, original, candidate), ct);
        Assert.True(next.CompilerCompletedSuccessfully);
        Assert.Equal(passes + (2L * next.ProjectsInScope), engine.EditAnalysisStatistics.DiagnosticPasses);
        engine.DiscardEditCandidate(nextCommand.EffectId);
    }
}
