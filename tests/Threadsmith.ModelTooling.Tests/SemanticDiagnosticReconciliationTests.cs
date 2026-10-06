namespace Threadsmith.ModelTooling.Tests;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Threadsmith.Validation;
using Xunit;

/// <summary>Exercises disk reconciliation through the production validation pipeline and workspace registry.</summary>
public static class SemanticDiagnosticReconciliationTests
{
    /// <summary>Unreported same-size edits remain visible and overlays never replace the loaded solution.</summary>
    [Fact]
    public static async Task ValidationReconcilesUnreportedEditsAndKeepsLoadedSnapshot()
    {
        using var fixture = new RepositoryFixture(3, 32);
        await using var events = new DomainEventStream();
        using var measurement = new ReconciliationMeasurement();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
        var (pipeline, request, mutation) = await fixture.LoadAsync(events, registry);
        var engine = registry.GetEngine(request.Baseline.WorkspaceId);
        var snapshot = engine.CaptureAdvancedSnapshot();
        measurement.Reset();
        var baseline = await pipeline.CaptureSemanticBaselineAsync(request, mutation, TestContext.Current.CancellationToken);
        Assert.Equal(3, measurement.Reads);
        Assert.Equal(fixture.TotalCharacters, measurement.Characters);
        Assert.Equal(0, measurement.Texts);

        // No watcher is involved. Timestamp and file length are deliberately preserved.
        var path = Path.Combine(fixture.Root, "File0.cs");
        var timestamp = File.GetLastWriteTimeUtc(path);
        var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, original.Replace("int Value;", "int Value ", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, timestamp);
        measurement.Reset();
        var result = await pipeline.ValidateAsync(request, baseline, mutation, true, true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, measurement.Reads);
        Assert.Equal(fixture.TotalCharacters, measurement.Characters);
        Assert.Equal(1, measurement.Texts);
        Assert.Contains(result.Diagnostics, item => item.Code == "CS1002" && item.File == "File0.cs");
        Assert.Same(snapshot.Solution, engine.CaptureAdvancedSnapshot().Solution);

        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        measurement.Reset();
        var restored = await pipeline.CaptureSemanticBaselineAsync(request, mutation, TestContext.Current.CancellationToken);
        Assert.Equal(baseline.Diagnostics.Select(item => item.Id), restored.Diagnostics.Select(item => item.Id));
        Assert.Equal(3, measurement.Reads);
        Assert.Equal(0, measurement.Texts);
    }

    /// <summary>Linked paths read once while project filtering and local overlay updates remain intact.</summary>
    [Fact]
    public static async Task LinkedDocumentsReconcileOnceAcrossSelectedProjects()
    {
        using var fixture = new RepositoryFixture(3, 32, linkedProject: true);
        await using var events = new DomainEventStream();
        using var measurement = new ReconciliationMeasurement();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
        var (_, request, _) = await fixture.LoadAsync(events, registry);
        var engine = registry.GetEngine(request.Baseline.WorkspaceId);
        var path = Path.Combine(fixture.Root, "File0.cs");
        Assert.Equal(2, engine.CaptureAdvancedSnapshot().Solution.GetDocumentIdsWithFilePath(path).Length);
        var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, original.Replace("int Value;", "int Value ", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        measurement.Reset();
        var all = await engine.GetDiagnosticsAsync([], [path, "File0.cs", path], TestContext.Current.CancellationToken);
        Assert.Equal(3, measurement.Reads);
        Assert.Equal(1, measurement.Texts);
        Assert.Equal(2, all.Count(item => item.Code == "CS1002" && item.File == "File0.cs"));

        measurement.Reset();
        var scoped = await engine.GetDiagnosticsAsync([Path.Combine(fixture.Root, "App.csproj")], [], TestContext.Current.CancellationToken);
        Assert.Single(scoped, item => item.Code == "CS1002" && item.Project == "App");
        Assert.DoesNotContain(scoped, item => item.Project == "Linked");
        Assert.Equal(3, measurement.Reads);
    }

    /// <summary>Local diagnostics do not wait for a replacement generation blocked by an active-run boundary.</summary>
    [Fact]
    public static async Task ValidationCompletesWhileRefreshPublicationIsBlocked()
    {
        using var fixture = new RepositoryFixture(3, 32);
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
        var (pipeline, request, mutation) = await fixture.LoadAsync(events, registry);
        var gate = new BlockingPublicationGate();
        await using var refresh = new SemanticRefreshCoordinator(
            new RegistrySemanticRefreshBackend(registry),
            events,
            NullLogger<SemanticRefreshCoordinator>.Instance,
            publicationGate: gate,
            watchFileSystem: false);
        await refresh.BindAsync(registry.GetLoadRequest(request.Baseline.WorkspaceId), TestContext.Current.CancellationToken);
        var pending = refresh.ForceRefreshAsync(request.SessionId, TestContext.Current.CancellationToken);
        try
        {
            await gate.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var path = Path.Combine(fixture.Root, "File0.cs");
            var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(path, original.Replace("int Value;", "int Value ", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            var capture = await pipeline.CaptureSemanticBaselineAsync(request, mutation, TestContext.Current.CancellationToken);
            Assert.Contains(capture.Diagnostics, item => item.Code == "CS1002" && item.File == "File0.cs");
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            gate.Release.TrySetResult();
            await pending;
        }
    }

    /// <summary>Reports warmed end-to-end allocations without an elapsed-time or allocation CI threshold.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Performance")]
    public static async Task MeasureRepeatedValidationAllocations()
    {
        using var fixture = new RepositoryFixture(24, 96_000);
        await using var events = new DomainEventStream();
        using var measurement = new ReconciliationMeasurement();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
        var (pipeline, request, mutation) = await fixture.LoadAsync(events, registry);
        await pipeline.CaptureSemanticBaselineAsync(request, mutation, TestContext.Current.CancellationToken);
        measurement.Reset();
        const int iterations = 5;
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            await pipeline.CaptureSemanticBaselineAsync(request, mutation, TestContext.Current.CancellationToken);
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Unchanged baseline: reads/op={measurement.Reads / iterations}, characters/op={measurement.Characters / iterations}, texts/op={measurement.Texts / iterations}, allocated bytes/op={allocated / iterations}");
        Assert.Equal(24 * iterations, measurement.Reads);
        Assert.Equal(fixture.TotalCharacters * iterations, measurement.Characters);

        var path = Path.Combine(fixture.Root, "File0.cs");
        var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, content.Replace("Value;", "Other;", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await pipeline.CaptureSemanticBaselineAsync(request, mutation, TestContext.Current.CancellationToken);
        measurement.Reset();
        before = GC.GetTotalAllocatedBytes(precise: true);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            await pipeline.CaptureSemanticBaselineAsync(request, mutation, TestContext.Current.CancellationToken);
        }

        allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"One changed file: reads/op={measurement.Reads / iterations}, characters/op={measurement.Characters / iterations}, texts/op={measurement.Texts / iterations}, allocated bytes/op={allocated / iterations}");
        Assert.Equal(24 * iterations, measurement.Reads);
        Assert.Equal(iterations, measurement.Texts);
    }

    private sealed class BlockingPublicationGate : ISemanticRefreshPublicationGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TResult> PublishAsync<TResult>(SessionId sessionId, WorkspaceId workspaceId, Func<CancellationToken, Task<TResult>> publication, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await publication(cancellationToken);
        }
    }

    private sealed class ReconciliationMeasurement : IDisposable
    {
        private static readonly AsyncLocal<ReconciliationMeasurement?> Current = new();
        private readonly MeterListener _listener = new();

        public ReconciliationMeasurement()
        {
            Current.Value = this;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Threadsmith.SemanticLoad" && instrument.Name.StartsWith("threadsmith.semantic.diagnostics.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (Current.Value != this)
                {
                    return;
                }

                switch (instrument.Name)
                {
                    case "threadsmith.semantic.diagnostics.file_reads": Reads += value; break;
                    case "threadsmith.semantic.diagnostics.characters_read": Characters += value; break;
                    case "threadsmith.semantic.diagnostics.texts_created": Texts += value; break;
                }
            });
            _listener.Start();
        }

        public long Reads { get; private set; }

        public long Characters { get; private set; }

        public long Texts { get; private set; }

        public void Reset() => Reads = Characters = Texts = 0;

        public void Dispose()
        {
            _listener.Dispose();
            Current.Value = null;
        }
    }

    private sealed class RepositoryFixture : IDisposable
    {
        public RepositoryFixture(int files, int commentLength, bool linkedProject = false)
        {
            Root = Path.Combine(Path.GetTempPath(), $"threadsmith-diagnostic-reconciliation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="File*.cs" /></ItemGroup>
                </Project>
                """);
            for (var index = 0; index < files; index++)
            {
                var text = $"/*{new string('x', commentLength)}*/\nclass File{index} {{ int Value; }}\n";
                File.WriteAllText(Path.Combine(Root, $"File{index}.cs"), text);
                TotalCharacters += text.Length;
            }

            if (linkedProject)
            {
                var linked = Directory.CreateDirectory(Path.Combine(Root, "Linked")).FullName;
                File.WriteAllText(Path.Combine(linked, "Linked.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                        <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                        <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                      </PropertyGroup>
                      <ItemGroup><Compile Include="../File0.cs" /></ItemGroup>
                    </Project>
                    """);
                var project = Path.Combine(Root, "App.csproj");
                File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "<ItemGroup><ProjectReference Include=\"Linked/Linked.csproj\" /></ItemGroup></Project>", StringComparison.Ordinal));
            }
        }

        public string Root { get; }

        public long TotalCharacters { get; }

        public async Task<(ValidationPipeline Pipeline, BuildValidationRequest Request, MutationSet Mutation)> LoadAsync(
            IDomainEventStream events,
            SemanticEngineRegistry registry)
        {
            var session = SessionId.New();
            var workspace = WorkspaceId.New();
            var project = Path.Combine(Root, "App.csproj");
            await registry.LoadAsync(new(session, workspace, Root, project, RepositoryTrustLevel.TrustedBuild), TestContext.Current.CancellationToken);
            await registry.GetEngine(workspace).WaitForWarmAsync(TestContext.Current.CancellationToken);
            var baseline = new WorkspaceBaseline(workspace, Root, DateTimeOffset.UtcNow, [], TrustLevel: RepositoryTrustLevel.TrustedBuild);
            var request = new BuildValidationRequest
            {
                SessionId = session,
                RunId = RunId.New(),
                Baseline = baseline,
                Confidence = registry.GetConfidence(workspace),
                Stages = [MutationValidationStage.Semantic],
                Projects = [new AffectedProject("App", project, ["net10.0"], registry.GetConfidence(workspace), true)],
                AffectedPaths = ["unrelated.cs"],
            };
            var mutation = new MutationSet
            {
                MutationSetId = MutationSetId.New(),
                SessionId = session,
                RunId = request.RunId,
                WorkspaceId = workspace,
                BaselineCapturedAt = baseline.CapturedAt,
                Mutations = [],
                Rationale = "Exercise semantic reconciliation.",
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
            return (pipeline, request, mutation);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
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
