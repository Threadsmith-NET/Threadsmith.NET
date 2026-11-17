namespace Threadsmith.ModelTooling.Integration.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

public abstract partial class SourceEditAnalysisTests
{
}

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
    [InlineData(0, true)]
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

/// <summary>Exercises one independently owned semantic or model contract.</summary>
public sealed class SourceEditAnalysisTestsCompilerReuseProjectBoundDoesNotNarrowAffectedCoverage : SourceEditAnalysisTests
{
    /// <summary>Unlimited reporting and carried scope do not make compiler reuse unbounded.</summary>
    [Fact]
    public static async Task CompilerReuseProjectBoundDoesNotNarrowAffectedCoverage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TestSemanticSolution();
        var solutionPath = Path.Combine(fixture.Root, "SmallDotNetSolution.sln");
        var solutionText = await File.ReadAllTextAsync(solutionPath, ct);
        var origins = new List<(int CachedProjects, int StagedProjects, int Fingerprints)>();
        var projectEntries = new System.Text.StringBuilder();
        for (var index = 0; index < SemanticEngine.MaximumCompilerCachedProjects; index++)
        {
            var name = $"Dependent{index}";
            var directory = Path.Combine(fixture.Root, name);
            Directory.CreateDirectory(directory);
            const string projectSource = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Contracts/Contracts.csproj" /></ItemGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".csproj"), projectSource, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "Source.cs"), $"public class {name} {{ }}", ct);
            projectEntries.AppendLine($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{name}/{name}.csproj\", \"{{{Guid.NewGuid()}}}\"\nEndProject");
        }

        await File.WriteAllTextAsync(solutionPath, solutionText.Replace("\nGlobal", "\n" + projectEntries + "Global", StringComparison.Ordinal), ct);
        // Keep one independently warmed project outside the broad candidate's dependency scope.
        var appProjectPath = Path.Combine(fixture.Root, "App/App.csproj");
        var appProject = await File.ReadAllTextAsync(appProjectPath, ct);
        await File.WriteAllTextAsync(appProjectPath, appProject.Replace("<ProjectReference Include=\"..\\Contracts\\Contracts.csproj\" />", string.Empty, StringComparison.Ordinal), ct);
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, "Contracts/Services.cs"), "\nclass InitialRetentionError { MissingInitial value; }", ct);
        var observations = new List<(int CachedProjects, int StagedProjects, int EvidenceSets, int Findings)>();
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, resourceLimits: new() { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow))
        {
            EditCompilerRetentionObserver = observations.Add,
            EditCompilerOriginRetentionObserver = origins.Add,
        };
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, ct);
        await engine.WaitForWarmAsync(ct);
        const string appRelative = "App/Program.cs";
        var appOriginal = await File.ReadAllTextAsync(Path.Combine(fixture.Root, appRelative), ct);
        var warmCommand = Command(request.SessionId, RunId.New(), request.WorkspaceId);
        var warm = await AnalyzeCompletedAsync(engine, events, warmCommand, fixture.Root, Snapshot(appRelative, appOriginal, appOriginal + "\nclass WarmRetention { }"), ct);
        Assert.True(warm.CompilerCompletedSuccessfully);
        Assert.Equal(1, warm.ProjectsInScope);
        Assert.Equal(1, observations[^1].CachedProjects);
        engine.DiscardEditCandidate(warmCommand.EffectId);
        observations.Clear();
        const string relative = "Contracts/Services.cs";
        var original = await File.ReadAllTextAsync(Path.Combine(fixture.Root, relative), ct);
        var broadCommand = Command(request.SessionId, warmCommand.RunId, request.WorkspaceId);
        var broadText = original + "\nclass BroadRetention { MissingIntroduced value; }";
        var broadSnapshot = Snapshot(relative, original, broadText);
        var broad = await AnalyzeCompletedAsync(engine, events, broadCommand, fixture.Root, broadSnapshot, ct);
        Assert.True(broad.CompilerCompletedSuccessfully);
        Assert.Equal(SemanticEngine.MaximumCompilerCachedProjects + 1, broad.ProjectsInScope);
        Assert.Equal(broad.ProjectsInScope, broad.ProjectsAnalyzed);
        Assert.Equal(0, broad.ProjectsOmitted);
        Assert.All(observations, item => Assert.InRange(item.StagedProjects, 0, SemanticEngine.MaximumCompilerCachedProjects));
        Assert.Equal(SemanticEngine.MaximumCompilerCachedProjects, observations[^1].CachedProjects);
        Assert.All(origins, item =>
        {
            Assert.InRange(item.CachedProjects, 0, SemanticEngine.MaximumCompilerCachedProjects);
            Assert.InRange(item.StagedProjects, 0, SemanticEngine.MaximumCompilerCachedProjects);
            Assert.InRange(item.Fingerprints, 0, 2 * SemanticEngine.MaximumCompilerCachedProjects * SemanticEngine.MaximumCompilerCachedFindingsPerSlot);
        });
        Assert.Equal(SemanticEngine.MaximumCompilerCachedProjects, origins[^1].CachedProjects);
        // Accepted projects outside origin capacity must not rebase introduced findings as initial.
        engine.ConfirmEditApplied(broadCommand.EffectId);
        await engine.RefreshDocumentsAsync([new(Path.Combine(fixture.Root, relative), broadText, broadSnapshot.Endpoints[0].AfterSha256!)], ct);
        var passes = engine.EditAnalysisStatistics.DiagnosticPasses;
        var retryCommand = Command(request.SessionId, warmCommand.RunId, request.WorkspaceId);
        var retry = await AnalyzeCompletedAsync(engine, events, retryCommand, fixture.Root, Snapshot(relative, broadText, broadText + "\nclass BroadRetryRetention { }"), ct);
        Assert.True(retry.CompilerCompletedSuccessfully);
        Assert.Equal(OrderCompilerDiagnostics(broad.Diagnostics.Select(item => item with { Origin = "unknown" }).ToArray()), OrderCompilerDiagnostics(retry.Diagnostics.Select(item => item with { Origin = "unknown" }).ToArray()));
        Assert.Contains(retry.Diagnostics, item => item.Code == "CS0246" && item.Origin == "unknown");
        Assert.Contains(retry.Omissions, item => item.Contains("origin labels are unknown", StringComparison.Ordinal));
        Assert.Equal(0, retry.NewErrors);
        Assert.Equal(broad.ProjectsInScope, retry.ProjectsAnalyzed);
        // Exactly one of the 33 baseline projects was outside the 32-entry reuse cache.
        Assert.Equal(passes + retry.ProjectsInScope + 1, engine.EditAnalysisStatistics.DiagnosticPasses);
        Assert.Equal(SemanticEngine.MaximumCompilerCachedProjects, observations[^1].CachedProjects);
        engine.DiscardEditCandidate(retryCommand.EffectId);
    }
}
