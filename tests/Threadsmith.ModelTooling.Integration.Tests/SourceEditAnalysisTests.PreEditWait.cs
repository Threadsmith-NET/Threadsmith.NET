namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsPartialCompilerTerminationIsNotSuccessfulCompletion : SourceEditAnalysisTests
{
    /// <summary>Compiler cancellation or failure preserves partial diagnostics without admission eligibility.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task PartialCompilerTerminationIsNotSuccessfulCompletion(bool cancel)
    {
        // Arrange: stop after syntax findings have been published, before project compilation.
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        var clock = new ObservedTimeProvider();
        var syntaxPublished = new TaskCompletionSource<SourceEditAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { CandidateLifetimeMilliseconds = 1000, Analyzers = new() { Enabled = false } } }, timeProvider: clock)
        {
            EditSyntaxAnalysisObserver = async (partial, token) =>
            {
                syntaxPublished.TrySetResult(partial);
                await release.Task.WaitAsync(token);
                throw new InvalidOperationException("Injected compiler failure after partial findings.");
            },
        };
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        var snapshot = Snapshot(relative, original, original + "\npublic class Broken {\n");
        var completed = new TaskCompletionSource<SemanticCheckCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { CheckName: "advisory edit analysis" } check && check.Analysis?.EffectId == command.EffectId)
            {
                completed.TrySetResult(check);
            }

            return Task.CompletedTask;
        });
        // Act: terminate remaining analysis inside the observation window, after observing partial findings.
        var observing = engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.FromSeconds(5), ct);
        try
        {
            var partial = await syntaxPublished.Task.WaitAsync(ct);
            Assert.True(partial.SyntaxErrors > 0);
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), ct);
            if (cancel)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            else
            {
                release.TrySetResult();
            }

            var result = await observing;
            var terminal = await completed.Task.WaitAsync(ct);
            // Assert: terminal partial evidence remains actionable but cannot reject admission.
            Assert.Equal(cancel ? SemanticCheckOutcome.Cancelled : SemanticCheckOutcome.Degraded, terminal.Outcome);
            Assert.NotNull(result);
            Assert.True(result.CandidateInputsComplete);
            Assert.False(result.Pending);
            Assert.False(result.CompilerCompletedSuccessfully);
            Assert.True(result.SyntaxErrors > 0);
            Assert.Contains(result.Diagnostics, item => item.Code == "CS1513");
            Assert.Equal(DiagnosticSeverity.Error, SourceEditAnalysisPolicy.GetCompilerFindingSeverity(result));
        }
        finally
        {
            release.TrySetResult();
            await observing;
        }
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsPreEditObservationDeadlineRetainsCandidateForCommittedFeedback : SourceEditAnalysisTests
{
    /// <summary>Observation expiry leaves the existing compiler operation alive for exact committed reuse.</summary>
    [Fact]
    public static async Task PreEditObservationDeadlineRetainsCandidateForCommittedFeedback()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        var clock = new ObservedTimeProvider();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }, timeProvider: clock);
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<SemanticCheckCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { CheckName: "advisory edit analysis" } check && check.Analysis?.EffectId == command.EffectId)
            {
                completed.TrySetResult(check);
            }

            return Task.CompletedTask;
        });
        var blocker = engine.RunSnapshotOperationAsync(
            engine.CaptureMutationSnapshot().Solution,
            async _ =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return 0;
        },
            ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            const string relative = "Contracts/Services.cs";
            var path = Path.Combine(fixture.Root, relative);
            var original = await File.ReadAllTextAsync(path, ct);
            var changed = original + "\npublic sealed class DeferredError { public MissingType Value; }\n";
            var snapshot = Snapshot(relative, original, changed);
            var observing = engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.FromSeconds(5), ct);
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), ct);
            Assert.False(observing.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(5));
            var pending = await observing;
            Assert.NotNull(pending);
            Assert.True(pending.Pending);
            Assert.False(completed.Task.IsCompleted);
            release.TrySetResult();
            await blocker;
            var feedback = await completed.Task.WaitAsync(ct);
            Assert.False(feedback.Analysis!.Pending);
            Assert.Contains(feedback.Analysis.Diagnostics, item => item.Code == "CS0246");
            engine.ConfirmEditApplied(command.EffectId);
            await engine.RefreshDocumentsAsync([new(path, changed, snapshot.Endpoints[0].AfterSha256!)], ct);
            var committed = engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId);
            Assert.NotNull(committed);
            Assert.True(committed.CandidateReused);
            Assert.NotNull(committed.CommittedGeneration);
            Assert.Contains(committed.Diagnostics, item => item.Code == "CS0246");
        }
        finally
        {
            release.TrySetResult();
            await blocker;
        }
    }
}
