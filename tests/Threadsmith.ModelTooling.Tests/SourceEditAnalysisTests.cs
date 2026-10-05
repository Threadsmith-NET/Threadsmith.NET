namespace Threadsmith.ModelTooling.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises advisory candidates against a real evaluated multi-project solution.</summary>
public static class SourceEditAnalysisTests
{
    /// <summary>Temporary errors remain introduced across subsequent edits, and exact promotion reuses completed diagnostics.</summary>
    [Fact]
    public static async Task CandidatePromotionRetainsOriginAndReusesDiagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        var run = RunId.New();
        await engine.LoadAsync(new(session, workspace, root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        const string relative = "Contracts/Services.cs";
        var path = Path.Combine(root, relative);
        var original = await File.ReadAllTextAsync(path, ct);
        var broken = original + "\npublic sealed class EditError { public MissingType Value; }\n";
        var first = Command(session, run, workspace);
        var firstSnapshot = Snapshot(relative, original, broken);
        var analysis = await engine.AnalyzeCandidateAsync(first, root, firstSnapshot, TimeSpan.FromSeconds(20), ct);
        Assert.False(analysis.Pending);
        Assert.True(analysis.ProjectsInScope >= 2);
        Assert.Contains(analysis.Diagnostics, item => item.Code == "CS0246" && item.Origin == "introduced");
        Assert.Null(engine.GetLatestEditAnalysis(session, run, first.EffectId));
        var passes = engine.EditAnalysisStatistics.DiagnosticPasses;
        engine.ConfirmEditApplied(first.EffectId);
        await engine.RefreshDocumentsAsync([new(path, broken, firstSnapshot.Endpoints[0].AfterSha256!)], ct);
        var promoted = engine.GetLatestEditAnalysis(session, run, first.EffectId)!;
        Assert.NotNull(promoted.CommittedGeneration);
        Assert.False(promoted.Pending);
        Assert.Equal(passes, engine.EditAnalysisStatistics.DiagnosticPasses);
        Assert.Equal(1, engine.EditAnalysisStatistics.CandidatePromotions);

        var second = Command(session, run, workspace);
        var extended = broken + "\npublic sealed class AnotherEdit { }\n";
        var secondSnapshot = Snapshot(relative, broken, extended);
        var secondAnalysis = await engine.AnalyzeCandidateAsync(second, root, secondSnapshot, TimeSpan.FromSeconds(20), ct);
        Assert.Contains(secondAnalysis.Diagnostics, item => item.Code == "CS0246" && item.Origin == "introduced");
        Assert.Equal(0, secondAnalysis.NewErrors);
        engine.ConfirmEditApplied(second.EffectId);
        await engine.RefreshDocumentsAsync([new(path, extended, secondSnapshot.Endpoints[0].AfterSha256!)], ct);

        var repair = Command(session, run, workspace);
        var repaired = extended.Replace("MissingType", "int", StringComparison.Ordinal);
        var repairAnalysis = await engine.AnalyzeCandidateAsync(repair, root, Snapshot(relative, extended, repaired), TimeSpan.FromSeconds(20), ct);
        Assert.True(repairAnalysis.ResolvedErrors > 0);
        Assert.DoesNotContain(repairAnalysis.Diagnostics, item => item.Code == "CS0246");
        Assert.Equal(1, repairAnalysis.CurrentErrors);
        Assert.Collection(repairAnalysis.Diagnostics, item =>
        {
            Assert.Equal("CS8805", item.Code);
            Assert.Equal("initial", item.Origin);
        });
    }

    /// <summary>An unrelated external document in the same refresh batch prevents candidate replacement from dropping it.</summary>
    [Fact]
    public static async Task ExtraRefreshInputPreventsCandidatePromotion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var path = Path.Combine(root, "Contracts", "Services.cs");
        var otherPath = Path.Combine(root, "App", "Program.cs");
        var original = await File.ReadAllTextAsync(path, ct);
        var changed = original + "\npublic sealed class CandidateAdded { }\n";
        var external = await File.ReadAllTextAsync(otherPath, ct) + "\npublic sealed class ExternalAdded { }\n";
        var snapshot = Snapshot("Contracts/Services.cs", original, changed);
        _ = await engine.AnalyzeCandidateAsync(command, root, snapshot, TimeSpan.FromSeconds(20), ct);
        engine.ConfirmEditApplied(command.EffectId);
        await engine.RefreshDocumentsAsync([new(path, changed, snapshot.Endpoints[0].AfterSha256!), new(otherPath, external, Hash(external))], ct);
        Assert.Equal(0, engine.EditAnalysisStatistics.CandidatePromotions);
        Assert.True(engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId)!.Obsolete);
        Assert.NotEmpty(await engine.FindSymbolsAsync("ExternalAdded", ct));
        Assert.NotEmpty(await engine.FindSymbolsAsync("CandidateAdded", ct));
    }

    /// <summary>Pending work survives matching promotion through the existing queue and cannot be delivered to another run.</summary>
    [Fact]
    public static async Task PendingCandidateContinuesAfterMatchingPublication()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, cancellationBackstop: TimeSpan.FromMilliseconds(1));
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted check && check.RunId == command.RunId && check.CheckName == "advisory edit analysis")
            {
                completed.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var blocker = engine.RunSnapshotOperationAsync(
            engine.CaptureMutationSnapshot().Solution,
            async _ =>
            {
                started.SetResult();
                await release.Task.WaitAsync(ct);
                return 0;
            },
            ct);
        try
        {
            await started.Task.WaitAsync(ct);
            var path = Path.Combine(root, "Contracts", "Services.cs");
            var original = await File.ReadAllTextAsync(path, ct);
            var changed = original + "\npublic sealed class DeferredError { public MissingType Value; }\n";
            var snapshot = Snapshot("Contracts/Services.cs", original, changed);
            var pending = await engine.AnalyzeCandidateAsync(command, root, snapshot, TimeSpan.Zero, ct);
            Assert.True(pending.Pending);
            engine.ConfirmEditApplied(command.EffectId);
            await engine.RefreshDocumentsAsync([new(path, changed, snapshot.Endpoints[0].AfterSha256!)], ct);
            await completed.Task.WaitAsync(ct);
            var latest = engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId)!;
            Assert.False(latest.Pending);
            Assert.NotNull(latest.CommittedGeneration);
            Assert.Contains(latest.Diagnostics, item => item.Code == "CS0246");
            Assert.Null(engine.GetLatestEditAnalysis(command.SessionId, RunId.New(), command.EffectId));
        }
        finally
        {
            release.TrySetResult();
#pragma warning disable VSTHRD003 // The test owns the deliberately superseded blocking operation.
            await Assert.ThrowsAsync<InvalidOperationException>(() => blocker);
#pragma warning restore VSTHRD003
        }
    }

    /// <summary>Existing-project aggregation preserves syntax findings from a newly created, not-yet-evaluated file.</summary>
    [Fact]
    public static async Task MixedMembershipCandidateRetainsNewFileSyntaxErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
        await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var original = await File.ReadAllTextAsync(Path.Combine(root, "Contracts", "Services.cs"), ct);
        var existing = Snapshot("Contracts/Services.cs", original, original + "\npublic class MixedEdit { }\n");
        const string added = "public class Broken {";
        var snapshot = existing with
        {
            Endpoints = [.. existing.Endpoints, new("Contracts/New.cs", null, Hash(added)) { FinalBytes = Encoding.UTF8.GetBytes(added) }],
        };
        var result = await engine.AnalyzeCandidateAsync(command, root, snapshot, TimeSpan.FromSeconds(20), ct);
        Assert.False(result.Pending);
        Assert.True(result.ProjectsAnalyzed > 0);
        Assert.Contains(result.Diagnostics, item => item.Code == "CS1513" && item.File == "Contracts/New.cs");
        Assert.True(result.CurrentErrors >= 2);
    }

    /// <summary>A graph refresh before write confirmation cannot redefine introduced errors as initial on the next edit.</summary>
    [Fact]
    public static async Task GraphRefreshBeforeConfirmationPreservesUnknownInitialOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-edit-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "Example.csproj");
            var path = Path.Combine(root, "Example.cs");
            const string original = "class Example { }";
            const string broken = "class Example { MissingType Value; }";
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", ct);
            await File.WriteAllTextAsync(path, original, ct);
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
            var command = Command(SessionId.New(), RunId.New(), WorkspaceId.New());
            await engine.LoadAsync(new(command.SessionId, command.WorkspaceId, root, project, RepositoryTrustLevel.TrustedBuild), ct);
            _ = await engine.AnalyzeCandidateAsync(command, root, Snapshot("Example.cs", original, broken), TimeSpan.Zero, ct);
            await File.WriteAllTextAsync(path, broken, ct);
            await engine.RefreshFullAsync(ct);
            engine.ContinueEditAfterGraphRefresh(new Dictionary<string, string?> { [path] = Hash(broken) });
            Assert.Null(engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId));
            engine.ConfirmEditApplied(command.EffectId);
            Assert.NotNull(engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId)!.CommittedGeneration);
            var next = Command(command.SessionId, command.RunId, command.WorkspaceId);
            var result = await engine.AnalyzeCandidateAsync(next, root, Snapshot("Example.cs", broken, broken + "\nclass Another { }"), TimeSpan.FromSeconds(20), ct);
            Assert.Contains(result.Diagnostics, item => item.Code == "CS0246" && item.Origin == "unknown");
            Assert.DoesNotContain(result.Diagnostics, item => item.Code == "CS0246" && item.Origin == "initial");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ApplySourceEditCommand Command(SessionId sessionId, RunId runId, WorkspaceId workspaceId)
    {
        return new(sessionId, runId, workspaceId, Guid.NewGuid(), new() { Rationale = "Exercise exact writer receipt analysis.", Mutations = [] });
    }

    private static MutationEffectSnapshot Snapshot(string path, string before, string after)
    {
        return new(MutationSetId.New(), [], [new(path, Hash(before), Hash(after)) { FinalBytes = Encoding.UTF8.GetBytes(after) }]);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
