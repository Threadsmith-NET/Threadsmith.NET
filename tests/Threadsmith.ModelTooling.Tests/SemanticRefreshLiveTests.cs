namespace Threadsmith.ModelTooling.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Threadsmith.Validation;
using Xunit;

/// <summary>Collects real repository refresh and diagnostic timings without changing source files.</summary>
public static class SemanticRefreshLiveTests
{
    /// <summary>Replays the incident's seven document refreshes through the production Roslyn engine.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public static async Task IncidentDocuments_RefreshAndPostMutationDiagnostics()
    {
        var repository = Environment.GetEnvironmentVariable("THREADSMITH_REFRESH_LIVE_REPOSITORY");
        var selection = Environment.GetEnvironmentVariable("THREADSMITH_REFRESH_LIVE_SOLUTION");
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(selection))
        {
            Assert.Skip("Set THREADSMITH_REFRESH_LIVE_REPOSITORY and THREADSMITH_REFRESH_LIVE_SOLUTION to measure the incident repository.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var sessionId = SessionId.New();
        var workspaceId = WorkspaceId.New();
        var started = Stopwatch.GetTimestamp();
        var loaded = await registry.LoadAsync(
            new(sessionId, workspaceId, repository, selection, RepositoryTrustLevel.TrustedBuild),
            cancellationToken);
        var engine = registry.GetEngine(workspaceId);
        WriteTiming("load-ready", started);
        Assert.True(loaded.Confidence >= SemanticConfidenceLevel.PartialCompilation);
        started = Stopwatch.GetTimestamp();
        await engine.WaitForWarmAsync(cancellationToken);
        WriteTiming("warm", started);

        string[] filenames =
        [
            "AgenticMcpAgentService.cs", "KernelRequest_Transcript_Tests.cs",
            "ModelInvocationPerformanceTranscript_Flush_Tests.cs", "AnthropicLLMService.cs",
            "KernelRequest.cs", "ModelInvocationPerformanceLogEntry.cs",
            "ModelInvocationPerformanceTranscript.cs",
        ];
        var snapshot = engine.CaptureAdvancedSnapshot();
        var documents = snapshot.Solution.Projects.SelectMany(project => project.Documents)
            .Where(document => filenames.Contains(Path.GetFileName(document.FilePath), StringComparer.Ordinal))
            .GroupBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        Assert.Equal(filenames.Length, documents.Length);
        var refreshes = new List<SemanticDocumentRefresh>();
        var originals = new List<SemanticDocumentRefresh>();
        foreach (var document in documents)
        {
            var text = await document.GetTextAsync(cancellationToken);
            originals.Add(new(document.FilePath!, text.ToString(), "live-original"));
            refreshes.Add(new(document.FilePath!, text + "\n// Threadsmith live refresh measurement.\n", "live-measurement"));
        }

        var baseline = new WorkspaceBaseline(workspaceId, repository, DateTimeOffset.UtcNow, []);
        var runId = RunId.New();
        var paths = documents.Select(document => Path.GetRelativePath(repository, document.FilePath!).Replace('\\', '/')).ToArray();
        var affected = AffectedProjectCalculator.Calculate(repository, paths, engine.Projects);
        var request = new BuildValidationRequest
        {
            SessionId = sessionId,
            RunId = runId,
            Baseline = baseline,
            Projects = affected.Projects,
            AffectedPaths = paths,
            ProjectInventory = engine.Projects,
            Confidence = engine.Confidence,
            Stages = [MutationValidationStage.Semantic],
        };
        var mutations = new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = sessionId,
            RunId = runId,
            WorkspaceId = workspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
            Rationale = "Measure live semantic validation without writing repository source.",
            Mutations = paths.Select(path => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.ReplaceText,
                RelativePath = path,
            }).ToArray(),
        };
        var processes = new UnusedProcessManager();
        var pipeline = new ValidationPipeline(
            new BuildExecutor(events, new DiagnosticNormalizer(), NullLogger<BuildExecutor>.Instance),
            new DiagnosticClassifier(),
            new DiagnosticCorrelator(),
            new AcceptanceGate(),
            new TestValidationPipeline(new TestDiscoverer(processes), new TestRunner(processes, events), events),
            events,
            registry);

        for (var iteration = 1; iteration <= 3; iteration++)
        {
            started = Stopwatch.GetTimestamp();
            var capture = await pipeline.CaptureSemanticBaselineAsync(request, mutations, cancellationToken);
            WriteTiming($"baseline-{iteration} projects={affected.Projects.Count} diagnostics={capture.Diagnostics.Count}", started);
            var generation = engine.CaptureAdvancedSnapshot().Generation;
            started = Stopwatch.GetTimestamp();
            await engine.RefreshDocumentsAsync(refreshes, cancellationToken);
            WriteTiming($"incremental-{iteration} files={refreshes.Count}", started);
            Assert.True(engine.CaptureAdvancedSnapshot().Generation > generation);
            started = Stopwatch.GetTimestamp();
            var validation = await pipeline.ValidateAsync(request, capture, mutations, true, true, cancellationToken: cancellationToken);
            WriteTiming($"post-mutation-{iteration} diagnostics={validation.Diagnostics.Count} gate={validation.Gate.Status}", started);
            Assert.True(validation.Build.Succeeded);
            Assert.Empty(validation.Build.Targets);
            Assert.Equal(AcceptanceGateStatus.Passed, validation.Gate.Status);
            started = Stopwatch.GetTimestamp();
            await engine.WaitForWarmAsync(cancellationToken);
            WriteTiming($"warm-after-{iteration}", started);
        }

        await engine.RefreshDocumentsAsync(originals, cancellationToken);
        await engine.WaitForWarmAsync(cancellationToken);
        for (var iteration = 1; iteration <= 2; iteration++)
        {
            started = Stopwatch.GetTimestamp();
            var capture = await pipeline.CaptureSemanticBaselineAsync(request, mutations, cancellationToken);
            WriteTiming($"unchanged-diagnostics-{iteration} projects={affected.Projects.Count} diagnostics={capture.Diagnostics.Count}", started);
        }

        started = Stopwatch.GetTimestamp();
        await engine.RefreshFullAsync(cancellationToken);
        WriteTiming("full-refresh-ready", started);
        started = Stopwatch.GetTimestamp();
        await engine.WaitForWarmAsync(cancellationToken);
        WriteTiming("full-refresh-warm", started);
    }

    private static void WriteTiming(string phase, long started)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"LIVE-REFRESH {phase} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
    }

    private sealed class UnusedProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Semantic-only validation must not start a process.");
        }
    }
}
