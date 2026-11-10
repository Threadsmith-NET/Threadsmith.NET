namespace Threadsmith.ModelTooling.Tests;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public static partial class SourceEditAnalysisTests
{
    /// <summary>Only fully represented candidates can turn scoped compiler findings into pre-write rejection.</summary>
    [Theory]
    [InlineData("create", false)]
    [InlineData("delete", false)]
    [InlineData("graph", false)]
    [InlineData("replace", true)]
    public static async Task PreEditAdmissionRequiresAllCandidateInputs(string operation, bool inputsComplete)
    {
        // Arrange: freeze production deadlines so completion never depends on runner speed.
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        const string removedPath = "Contracts/Removed.cs";
        const string removed = "public sealed class Removed { public MissingType Value; }";
        if (operation is "delete" or "graph")
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, removedPath), removed, ct);
        }

        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } },
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        var changed = original + (operation == "create"
            ? "\npublic sealed class Consumer { public NewType Value = new(); }\n"
            : operation == "replace"
                ? "\npublic sealed class Consumer { public MissingType Value; }\n"
                : "\npublic sealed class Consumer { }\n");
        var snapshot = Snapshot(relative, original, changed);
        if (operation == "create")
        {
            const string added = "public sealed class NewType { }";
            snapshot = snapshot with
            {
                Endpoints = [.. snapshot.Endpoints, new("Contracts/NewType.cs", null, Hash(added)) { FinalBytes = Encoding.UTF8.GetBytes(added) }],
            };
        }
        else if (operation == "delete")
        {
            snapshot = snapshot with { Endpoints = [.. snapshot.Endpoints, new(removedPath, Hash(removed), null)] };
        }
        else if (operation == "graph")
        {
            const string projectPath = "Contracts/Contracts.csproj";
            var project = await File.ReadAllTextAsync(Path.Combine(fixture.Root, projectPath), ct);
            var replacement = project.Replace("</Project>", "<ItemGroup><Compile Remove=\"Removed.cs\" /></ItemGroup></Project>", StringComparison.Ordinal);
            var graphEdit = Snapshot(projectPath, project, replacement);
            snapshot = snapshot with { Endpoints = [.. snapshot.Endpoints, .. graphEdit.Endpoints] };
        }

        var completed = new TaskCompletionSource<SemanticCheckCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted { CheckName: "advisory edit analysis" } check && check.Analysis?.EffectId == command.EffectId)
            {
                completed.TrySetResult(check);
            }

            return Task.CompletedTask;
        });

        // Act: incomplete inputs return advisory evidence without waiting for background completion.
        var immediate = await engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(immediate);
        Assert.Equal(inputsComplete, immediate.CandidateInputsComplete);
        if (inputsComplete)
        {
            Assert.True(immediate.CompilerCompletedSuccessfully);
        }

        var result = (await completed.Task.WaitAsync(ct)).Analysis;

        // Assert: incomplete snapshots retain findings, but cannot reject the grouped edit.
        Assert.NotNull(result);
        Assert.False(result.Pending);
        Assert.True(result.CompilerCompletedSuccessfully);
        Assert.Equal(inputsComplete, result.CandidateInputsComplete);
        Assert.True(result.ProjectsAnalyzed > 0);
        Assert.Contains(result.Diagnostics, item => item.Code == "CS0246");
        Assert.Equal(DiagnosticSeverity.Error, SourceEditAnalysisPolicy.GetCompilerFindingSeverity(result));
    }

    /// <summary>Known-incomplete edits do not observe the blocking window while compiler work is queued.</summary>
    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("graph")]
    public static async Task IncompleteCandidatesReturnWhileCompilerQueueIsBlocked(string operation)
    {
        // Arrange: no deadline can expire until explicitly advanced, and the compiler queue is held.
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } },
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, fixture.Root, Path.Combine(fixture.Root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        var snapshot = Snapshot(relative, original, original + "\npublic class Broken {\n");
        if (operation == "create")
        {
            const string added = "public class Added { }";
            snapshot = snapshot with { Endpoints = [.. snapshot.Endpoints, new("Contracts/Added.cs", null, Hash(added)) { FinalBytes = Encoding.UTF8.GetBytes(added) }] };
        }
        else if (operation == "delete")
        {
            snapshot = snapshot with { Endpoints = [new(relative, Hash(original), null)] };
        }
        else
        {
            const string projectPath = "Contracts/Contracts.csproj";
            var project = await File.ReadAllTextAsync(Path.Combine(fixture.Root, projectPath), ct);
            var graph = Snapshot(projectPath, project, project + "\n");
            snapshot = snapshot with { Endpoints = [.. snapshot.Endpoints, .. graph.Endpoints] };
        }

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

            // Act: return without advancing the five-second observation timer or releasing the queue.
            var result = await engine.AnalyzeCandidateAsync(command, fixture.Root, snapshot, TimeSpan.FromSeconds(5), ct);

            // Assert: admission returned but the same background compiler work remains queued.
            Assert.NotNull(result);
            Assert.False(result.CandidateInputsComplete);
            Assert.False(result.CompilerCompletedSuccessfully);
            Assert.True(result.Pending);
            Assert.False(completed.Task.IsCompleted);
            release.TrySetResult();
            await blocker;
            var feedback = (await completed.Task.WaitAsync(ct)).Analysis;
            Assert.NotNull(feedback);
            Assert.True(feedback.CompilerCompletedSuccessfully);
            Assert.False(feedback.Pending);
            if (operation != "delete")
            {
                Assert.True(feedback.SyntaxErrors > 0);
            }
        }
        finally
        {
            release.TrySetResult();
            await blocker;
        }
    }

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
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new() { EditAnalysis = new() { CandidateLifetimeMilliseconds = 1000, Analyzers = new() { Enabled = false } } },
            timeProvider: clock)
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

    /// <summary>Observation expiry leaves the existing compiler operation alive for exact committed reuse.</summary>
    [Fact]
    public static async Task PreEditObservationDeadlineRetainsCandidateForCommittedFeedback()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        await using var events = new DomainEventStream();
        var clock = new ObservedTimeProvider();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } },
            timeProvider: clock);
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
