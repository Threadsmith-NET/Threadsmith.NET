namespace Threadsmith.ModelTooling.Integration.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsCompilerReuseCannotEvictOriginsUnderConfiguredProjectLimit : SourceEditAnalysisTests
{
    /// <summary>Optional reuse cannot evict origins retained for an oversized project after an accepted edit.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public static async Task CompilerReuseCannotEvictOriginsUnderConfiguredProjectLimit(int cachedProjects)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        const string appRelative = "App/Program.cs";
        const string contractsRelative = "Contracts/Services.cs";
        var appPath = Path.Combine(fixture.Root, appRelative);
        var contractsPath = Path.Combine(fixture.Root, contractsRelative);
        const string initialErrors = "class InitialAppError { MissingInitial value; }";
        var oversizedCandidateErrors = string.Join("\n", Enumerable.Range(0, SemanticEngine.MaximumCompilerCachedFindingsPerSlot + 1).Select(index => $"class CandidateAppError{index} {{ MissingCandidate{index} value; }}"));
        await File.AppendAllTextAsync(appPath, "\n" + initialErrors, ct);
        var appOriginal = await File.ReadAllTextAsync(appPath, ct);
        var contractsOriginal = await File.ReadAllTextAsync(contractsPath, ct);
        var observations = new List<(int CachedProjects, int StagedProjects, int EvidenceSets, int Findings)>();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { MaximumCachedProjects = cachedProjects, Analyzers = new() { Enabled = false } } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow))
        {
            EditCompilerRetentionObserver = observations.Add,
        };
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        var contractsText = contractsOriginal + "\nclass SmallCompilerCandidate { MissingContracts value; }";
        var appText = appOriginal + "\n" + oversizedCandidateErrors;
        var snapshot = Snapshot(contractsRelative, contractsOriginal, contractsText);
        snapshot = snapshot with
        {
            Endpoints = [.. snapshot.Endpoints, .. Snapshot(appRelative, appOriginal, appText).Endpoints],
        };
        var command = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var first = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, snapshot, ct);
        Assert.True(first.CompilerCompletedSuccessfully);
        Assert.Equal(2, first.ProjectsAnalyzed);
        Assert.False(first.FindingRetentionTruncated);
        Assert.Contains(first.Diagnostics, item => item.Project == "App" && item.Code == "CS0246" && item.Origin == "initial");
        Assert.Contains(first.Diagnostics, item => item.Project == "App" && item.Code == "CS0246" && item.Origin == "introduced");
        Assert.Contains(observations, item => item.StagedProjects == 1);
        // Accept both endpoints, then reanalyze the same dependency scope from the promoted snapshot.
        engine.ConfirmEditApplied(command.EffectId);
        await engine.RefreshDocumentsAsync([new(contractsPath, contractsText, snapshot.Endpoints[0].AfterSha256!), new(appPath, appText, snapshot.Endpoints[1].AfterSha256!)], ct);
        var promoted = engine.GetLatestEditAnalysis(command.SessionId, command.RunId, command.EffectId);
        Assert.NotNull(promoted);
        Assert.True(promoted.CandidateReused);
        var nextCommand = Command(request.SessionId, command.RunId, request.WorkspaceId);
        var next = await AnalyzeCompletedAsync(engine, events, nextCommand, fixture.Root, Snapshot(contractsRelative, contractsText, contractsText + "\nclass CompilerRetentionFollowup { }"), ct);
        Assert.True(next.CompilerCompletedSuccessfully);
        Assert.Equal(2, next.ProjectsAnalyzed);
        Assert.True(next.ErrorComparisonAvailable);
        Assert.Equal(0, next.NewErrors);
        Assert.Equal(0, next.ResolvedErrors);
        Assert.Equal(OrderCompilerDiagnostics(first.Diagnostics.Where(item => item.Project == "App").ToArray()), OrderCompilerDiagnostics(next.Diagnostics.Where(item => item.Project == "App").ToArray()));
        Assert.DoesNotContain(next.Diagnostics, item => item.Project == "App" && item.Origin == "unknown");
        Assert.Equal(cachedProjects - 1, observations[^1].CachedProjects);
        engine.DiscardEditCandidate(nextCommand.EffectId);
    }
}

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsCompilerOriginAdmissionPreservesTruthAtTheFingerprintBoundary : SourceEditAnalysisTests
{
    /// <summary>Complete maps, including empty maps, are admitted only within the exact fingerprint bound.</summary>
    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public static async Task CompilerOriginAdmissionPreservesTruthAtTheFingerprintBoundary(int initialCount, bool admitted)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        const string relative = "App/Program.cs";
        var path = Path.Combine(fixture.Root, relative);
        var original = string.Join("\n", Enumerable.Range(0, initialCount).Select(index => $"public class Initial{index} {{ public Missing{index} Method() => default; }}"));
        await File.WriteAllTextAsync(path, original, ct);
        var origins = new List<(int CachedProjects, int StagedProjects, int Fingerprints)>();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow))
        {
            EditCompilerOriginRetentionObserver = origins.Add,
        };
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        var command = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var accepted = original + "\npublic class AcceptedBoundary { }";
        var snapshot = Snapshot(relative, original, accepted);
        var first = await AnalyzeCompletedAsync(engine, events, command, fixture.Root, snapshot, ct);
        Assert.True(first.CompilerCompletedSuccessfully);
        Assert.Equal(1, first.ProjectsAnalyzed);
        Assert.Equal(initialCount, first.Diagnostics.Count(item => item.Code == "CS0246"));
        Assert.Equal(admitted ? (1, 0, initialCount) : (0, 0, 0), origins[^1]);
        Assert.False(first.FindingRetentionTruncated);
        engine.ConfirmEditApplied(command.EffectId);
        await engine.RefreshDocumentsAsync([new(path, accepted, snapshot.Endpoints[0].AfterSha256!)], ct);
        var followupCommand = Command(request.SessionId, command.RunId, request.WorkspaceId);
        var followup = await AnalyzeCompletedAsync(engine, events, followupCommand, fixture.Root, Snapshot(relative, accepted, accepted + "\npublic class IntroducedBoundary { public MissingNew Method() => default; }"), ct);
        Assert.True(followup.CompilerCompletedSuccessfully);
        Assert.True(followup.ErrorComparisonAvailable);
        Assert.Equal(1, followup.NewErrors);
        Assert.Equal(0, followup.ResolvedErrors);
        Assert.Contains(followup.Diagnostics, item => item.Code == "CS0246" && item.Origin == (admitted ? "introduced" : "unknown"));
        if (!admitted)
        {
            Assert.All(followup.Diagnostics, item => Assert.Equal("unknown", item.Origin));
            Assert.Equal((0, 0, 0), origins[^1]);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"initial fingerprints={initialCount}; retained={origins[^1]}");
        engine.DiscardEditCandidate(followupCommand.EffectId);
    }
}
