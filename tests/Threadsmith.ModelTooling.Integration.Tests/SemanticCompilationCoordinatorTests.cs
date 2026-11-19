namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Proves preparation ownership, ordering, cancellation, and the real lifecycle barrier.</summary>
public static class SemanticCompilationCoordinatorTests
{
    /// <summary>Production defaults map from configuration and reject invalid resource relationships.</summary>
    [Fact]
    public static void ProductionLimitsUseConfiguredWorkersAndValidateBounds()
    {
        var resources = new SemanticResourceLimits();
        var limits = new SemanticPreparationLimits(resources);
        Assert.Equal(2, resources.CompilationWorkers);
        Assert.Equal(1, resources.InitialPreparationFrontier);
        Assert.Equal(32, resources.MaximumQueuedCompilerOperations);
        Assert.Equal(resources.CompilationWorkers, limits.Workers);
        Assert.Equal(resources.InitialPreparationFrontier, limits.Frontier);
        Assert.Equal(resources.MaximumQueuedCompilerOperations, limits.MaximumQueuedOperations);
        limits.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (limits with { Workers = 1 }).Validate());
        new SemanticPreparationLimits(resources with { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }) { Workers = 1 }.Validate();
        (limits with { Workers = 3, Frontier = 3 }).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (limits with { Workers = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (limits with { Workers = int.MaxValue }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (limits with { Frontier = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (limits with { Frontier = 3 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (resources with { MaximumQueuedCompilerOperations = 0 }).Validate());
    }

    /// <summary>One waiter can leave shared preparation without cancelling another caller.</summary>
    [Fact]
    public static async Task ConcurrentWaitersJoinAndCancellationOnlyStopsOneWaiter()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 1);
        var id = solution.ProjectIds[0];
        var started = Signal();
        var release = Signal();
        var calls = 0;
        await using var coordinator = new SemanticCompilationCoordinator(
            solution,
            new(),
            new HashSet<ProjectId>(),
            async (project, token) =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task.WaitAsync(token);
                return new(project, true);
            },
            static (_, _, _) => Task.CompletedTask);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = coordinator.PrepareAsync(id, true, cancelled.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = coordinator.PrepareAsync(id, true, TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
#pragma warning disable VSTHRD003 // This test owns the first caller's cancellation and completion.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
#pragma warning restore VSTHRD003
        Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.True((await second).Succeeded);
        Assert.Equal(1, calls);
        Assert.True((await coordinator.PrepareAsync(id, true, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(1, calls);
    }

    /// <summary>Queued demand passes warm work while an existing preparation finishes normally.</summary>
    [Fact]
    public static async Task DemandPromotionRunsBeforeQueuedWarmWork()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 3);
        var starts = solution.ProjectIds.ToDictionary(id => id, _ => Signal());
        var releases = solution.ProjectIds.ToDictionary(id => id, _ => Signal());
        var order = new ConcurrentQueue<ProjectId>();
        await using var coordinator = new SemanticCompilationCoordinator(
            solution,
            new(new SemanticResourceLimits { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }) { Workers = 1 },
            new HashSet<ProjectId>(),
            async (id, token) =>
            {
                order.Enqueue(id);
                starts[id].SetResult();
                await releases[id].Task.WaitAsync(token);
                return new(id, true);
            },
            static (_, _, _) => Task.CompletedTask);
        var ids = solution.ProjectIds;
        coordinator.Warm([ids[0]]);
        await starts[ids[0]].Task.WaitAsync(TestContext.Current.CancellationToken);
        coordinator.Warm([ids[1], ids[2]]);
        var demand = coordinator.PrepareAsync(ids[2], true, TestContext.Current.CancellationToken);
        releases[ids[0]].SetResult();
        await starts[ids[2]].Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(starts[ids[1]].Task.IsCompleted);
        releases[ids[2]].SetResult();
        await demand;
        await starts[ids[1]].Task.WaitAsync(TestContext.Current.CancellationToken);
        releases[ids[1]].SetResult();
        await coordinator.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { ids[0], ids[2], ids[1] }, order);
        Assert.Equal(1, coordinator.Statistics.MaximumRunning);
        Assert.Equal(1, coordinator.Statistics.Promotions);
    }

    /// <summary>Configured admission is exact and late obsolete work has no publication authority.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public static async Task WorkerBoundIsExactAndObsoleteResultsCannotPublish(int workers)
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, workers + 1);
        var starts = solution.ProjectIds.ToDictionary(id => id, _ => Signal());
        var releases = solution.ProjectIds.ToDictionary(id => id, _ => Signal());
        var published = new ConcurrentQueue<ProjectId>();
        var coordinator = new SemanticCompilationCoordinator(
            solution,
            new(new SemanticResourceLimits { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }) { Workers = workers },
            new HashSet<ProjectId>(),
            async (id, _) =>
            {
                starts[id].SetResult();
                await releases[id].Task.WaitAsync(TestContext.Current.CancellationToken);
                return new(id, true);
            },
            (_, result, _) =>
            {
                published.Enqueue(result.ProjectId);
                return Task.CompletedTask;
            });
        try
        {
            coordinator.Warm(solution.ProjectIds);
            foreach (var id in solution.ProjectIds.Take(workers))
            {
                await starts[id].Task.WaitAsync(TestContext.Current.CancellationToken);
            }

            Assert.False(starts[solution.ProjectIds[workers]].Task.IsCompleted);
            Assert.Equal(workers, coordinator.Statistics.MaximumRunning);
            coordinator.Abort();
            var outcomes = await coordinator.Completion.WaitAsync(TestContext.Current.CancellationToken);
            Assert.All(outcomes, result => Assert.True(result.Obsolete));
            foreach (var release in releases.Values)
            {
                release.TrySetResult();
            }

            await coordinator.DisposeAsync();
            Assert.Empty(published);
            Assert.False(starts[solution.ProjectIds[workers]].Task.IsCompleted);
        }
        finally
        {
            foreach (var release in releases.Values)
            {
                release.TrySetResult();
            }

            await coordinator.DisposeAsync();
        }
    }

    /// <summary>Repeated demand cannot retry a failed project in the same generation.</summary>
    [Fact]
    public static async Task FailureIsTerminalForTheGeneration()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 1);
        var calls = 0;
        await using var coordinator = new SemanticCompilationCoordinator(
            solution,
            new(),
            new HashSet<ProjectId>(),
            (id, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new SemanticPreparationOutcome(id, false, Failure: "Fixture failure"));
            },
            static (_, _, _) => Task.CompletedTask);
        Assert.False((await coordinator.PrepareAsync(solution.ProjectIds[0], true, TestContext.Current.CancellationToken)).Succeeded);
        Assert.False((await coordinator.PrepareAsync(solution.ProjectIds[0], true, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(1, calls);
    }

    /// <summary>Transient validation and project preparation share capacity and foreground priority.</summary>
    [Fact]
    public static async Task ForegroundCompilationSharesWorkerCapacityWithWarming()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 2);
        var started = Signal();
        var release = Signal();
        var order = new ConcurrentQueue<string>();
        await using var coordinator = new SemanticCompilationCoordinator(
            solution,
            new(new SemanticResourceLimits { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }) { Workers = 1 },
            new HashSet<ProjectId>(),
            async (id, token) =>
            {
                order.Enqueue(id == solution.ProjectIds[0] ? "first" : "warm");
                if (id == solution.ProjectIds[0])
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                }

                return new(id, true);
            },
            static (_, _, _) => Task.CompletedTask);
        coordinator.Warm(solution.ProjectIds);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var operation = coordinator.RunAsync(
            _ =>
        {
            order.Enqueue("validation");
            return Task.FromResult(42);
        },
            TestContext.Current.CancellationToken);
        release.SetResult();
        Assert.Equal(42, await operation);
        await coordinator.Completion.WaitAsync(TestContext.Current.CancellationToken);
        var observedOrder = order.ToArray();
        Assert.Equal("first", observedOrder[0]);
        Assert.Equal("validation", observedOrder[1]);
        Assert.Single(observedOrder, item => item == "warm");
        Assert.All(observedOrder[2..], item => Assert.Contains(item, new[] { "first", "warm" }));
        Assert.Equal(1, coordinator.Statistics.MaximumRunning);
    }

    /// <summary>Generation retirement reports obsolete running and queued operations without cancelling their callers.</summary>
    [Fact]
    public static async Task SupersededForegroundOperationsAreNotCallerCancellation()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 1);
        var started = Signal();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var coordinator = new SemanticCompilationCoordinator(
            solution,
            new(new SemanticResourceLimits { EditAnalysis = new() { Analyzers = new() { Enabled = false } } }) { Workers = 1 },
            solution.ProjectIds.ToHashSet(),
            static (id, _) => Task.FromResult(new SemanticPreparationOutcome(id, true)),
            static (_, _, _) => Task.CompletedTask);
        var running = coordinator.RunAsync(
            async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            },
            cancellationToken);
        await started.Task.WaitAsync(cancellationToken);
        var queued = coordinator.RunAsync(_ => Task.FromResult(2), cancellationToken);

        coordinator.Abort();

#pragma warning disable VSTHRD003 // Both tasks were admitted by this test before retiring their owner.
        await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
#pragma warning restore VSTHRD003
        Assert.False(cancellationToken.IsCancellationRequested);
    }

    /// <summary>Waiter cancellation preserves the shared compiler owner for subsequent work.</summary>
    [Fact]
    public static async Task ForegroundWaiterCancellationPreservesGeneration()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 1);
        var started = Signal();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var coordinator = new SemanticCompilationCoordinator(
            solution,
            new(),
            solution.ProjectIds.ToHashSet(),
            static (id, _) => Task.FromResult(new SemanticPreparationOutcome(id, true)),
            static (_, _, _) => Task.CompletedTask);
        var running = coordinator.RunAsync(
            async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            },
            cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

#pragma warning disable VSTHRD003 // This test started the operation before cancelling its own waiter.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
#pragma warning restore VSTHRD003
        Assert.Equal(2, await coordinator.RunAsync(_ => Task.FromResult(2), TestContext.Current.CancellationToken));
    }

    /// <summary>A failed background event preserves proven coverage and emits one recovery diagnostic.</summary>
    [Fact]
    public static async Task WarmPublicationFailurePreservesCompilationAndReportsRecovery()
    {
        using var fixture = new TestSemanticSolution();
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        var diagnostic = Signal();
        var completions = 0;
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticConfidenceChanged { Confidence: nameof(SemanticConfidenceLevel.FullSemantic) })
            {
                throw new InvalidOperationException("Fixture delivery failure");
            }

            if (domainEvent is DiagnosticObserved { Code: "TSSEMWARM" })
            {
                diagnostic.SetResult();
            }

            if (domainEvent is SemanticLoadCompleted)
            {
                Interlocked.Increment(ref completions);
            }

            return Task.CompletedTask;
        });
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), TestContext.Current.CancellationToken);
        await diagnostic.Task.WaitAsync(TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, engine.Confidence);
        Assert.Equal(1, completions);
        Assert.Equal(engine.Projects.Count, engine.CaptureAdvancedSnapshot().CompiledProjects.Count);
    }

    /// <summary>Detaching the last binding aborts its unpublished warm work before another workspace opens.</summary>
    [Fact]
    public static async Task LastOwnerUnbindStopsPreparationAndCannotPromoteReboundSession()
    {
        using var fixture = new TestSemanticSolution();
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<SemanticConfidenceChanged>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticConfidenceChanged confidence)
            {
                observed.Enqueue(confidence);
            }

            return Task.CompletedTask;
        });
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var refresh = new SemanticRefreshCoordinator(registry, events, NullLogger<SemanticRefreshCoordinator>.Instance);
        var request = new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild);
        var generation = await refresh.BeginBindingAsync(request, TestContext.Current.CancellationToken);
        var result = await registry.LoadForBindingAsync(request, TestContext.Current.CancellationToken);
        await refresh.CompleteBindingAsync(request, generation, TestContext.Current.CancellationToken);
        var oldEngine = registry.GetEngine(request.WorkspaceId);
        var warm = oldEngine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        Assert.False(warm.IsCompleted);
        await refresh.UnbindAsync(request.SessionId, TestContext.Current.CancellationToken);
        await warm;
        var replacement = request with { WorkspaceId = WorkspaceId.New() };
        await refresh.BeginBindingAsync(replacement, TestContext.Current.CancellationToken);
        await registry.CompleteInitialPublicationAsync(result, true, TestContext.Current.CancellationToken);
        Assert.Single(oldEngine.CaptureAdvancedSnapshot().CompiledProjects);
        Assert.Equal(SemanticConfidenceLevel.PartialCompilation, oldEngine.Confidence);
        Assert.DoesNotContain(observed, item => item.Confidence == nameof(SemanticConfidenceLevel.FullSemantic));
    }

    /// <summary>Transient diagnostics preserve the evaluated solution identity and warm owner.</summary>
    [Fact]
    public static async Task DiagnosticOverlayCannotDetachBackgroundPreparation()
    {
        using var fixture = new TestSemanticSolution();
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), TestContext.Current.CancellationToken);
        var before = engine.CaptureAdvancedSnapshot();
        var projectPath = before.Solution.Projects.First(project => before.CompiledProjects.Contains(project.Id)).FilePath
            ?? throw new InvalidOperationException("Fixture project has no path.");
        await engine.GetDiagnosticsAsync([projectPath], [], TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        var after = engine.CaptureAdvancedSnapshot();
        Assert.Same(before.Solution, after.Solution);
        Assert.Equal(after.Solution.ProjectIds.Count, after.CompiledProjects.Count);
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, after.Confidence);
    }

    /// <summary>Coverage invalidation fences a receipt even when immutable source identity is retained.</summary>
    [Fact]
    public static async Task InvalidationRejectsPreviouslyPreparedReceipt()
    {
        using var fixture = new TestSemanticSolution();
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), TestContext.Current.CancellationToken);
        var receipt = await engine.EnsurePreparedAsync(null, "symbols", true, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Fixture preparation is unavailable.");
        var before = engine.CaptureCodeExploreReadinessSnapshot();
        engine.QueueInvalidation(Path.Combine(root, "App", "Program.cs"));
        Assert.Equal(SemanticConfidenceLevel.ProjectGraphOnly, await engine.ApplyInvalidationsAsync(TestContext.Current.CancellationToken));
        Assert.Same(receipt.Solution, engine.CaptureCodeExploreReadinessSnapshot().Solution);
        Assert.True(engine.CaptureCodeExploreReadinessSnapshot().SourceGeneration > before.SourceGeneration);
        Assert.Throws<InvalidOperationException>(() => engine.CaptureAdvancedSnapshot(receipt));
        Assert.Throws<InvalidOperationException>(() => engine.CaptureMutationSnapshot(receipt));
    }

    /// <summary>Cancellation while warm publication owns the gate retains pending invalidation and its preparation owner.</summary>
    [Fact]
    public static async Task CancelledInvalidationWaitPreservesPendingPathsAndWarmOwner()
    {
        using var fixture = new TestSemanticSolution();
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        var publishing = Signal();
        var release = Signal();
        await using var subscription = events.Subscribe(async (domainEvent, token) =>
        {
            if (domainEvent is SemanticConfidenceChanged { Confidence: nameof(SemanticConfidenceLevel.FullSemantic) })
            {
                publishing.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        });
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        try
        {
            await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), TestContext.Current.CancellationToken);
            await publishing.Task.WaitAsync(TestContext.Current.CancellationToken);
            engine.QueueInvalidation(Path.Combine(root, "App", "Program.cs"));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var invalidation = engine.ApplyInvalidationsAsync(cancellation.Token);
            Assert.False(invalidation.IsCompleted);
            await cancellation.CancelAsync();
#pragma warning disable VSTHRD003 // The assertion observes the invalidation operation started above.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invalidation);
#pragma warning restore VSTHRD003
            release.TrySetResult();
            await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
            Assert.NotEmpty(await engine.FindSymbolsAsync("UpperValue", TestContext.Current.CancellationToken));
            Assert.Equal(SemanticConfidenceLevel.ProjectGraphOnly, await engine.ApplyInvalidationsAsync(TestContext.Current.CancellationToken));
            Assert.Empty(engine.CaptureCodeExploreReadinessSnapshot().CompiledProjects);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>Ordinary IDs/paths retain owner scope despite an unknown lexical identifier in prose.</summary>
    [Theory]
    [InlineData("M:Workspace.Owner.Calculate(System.Int32)", true, 2, false)]
    [InlineData("App/Owner.cs", false, 2, false)]
    [InlineData("Owner.cs", false, 3, false)]
    [InlineData("App/Owner.cs", false, 2, true)]
    [InlineData("Owner.cs", false, 3, true)]
    public static async Task CheapCandidatesPreserveMethodIdentityAndFilenameAmbiguity(string anchor, bool symbol, int expectedCount, bool queryOnly)
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 3);
        var ids = solution.ProjectIds;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "threadsmith-candidate-fixture"));
        solution = solution.AddDocument(DocumentId.CreateNewId(ids[0]), "Base.cs", "namespace Workspace; public class Base {}", filePath: Path.Combine(root, "Core", "Base.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(ids[1]), "Owner.cs", "namespace Workspace; public class Owner { public void Calculate(int value) {} }", filePath: Path.Combine(root, "App", "Owner.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(ids[2]), "Owner.cs", "namespace Workspace; public class Unrelated {}", filePath: Path.Combine(root, "Other", "Owner.cs"));
        solution = solution.AddProjectReference(ids[1], new ProjectReference(ids[0]));
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var queries = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var readiness = new CodeExploreReadinessSnapshot(solution, new HashSet<ProjectId>(), SemanticConfidenceLevel.PartialCompilation, root, Path.Combine(root, "Fixture.sln"), 1, 1);
        var request = new CodeExploreRequest
        {
            Query = queryOnly ? anchor : "explain UnknownIdentifier",
            SymbolIds = symbol ? [anchor] : [],
            PathAnchors = symbol || queryOnly ? [] : [new CodeExplorePathAnchor { Path = anchor }],
        };
        var candidates = await queries.DiscoverCodeExploreCandidatesAsync(readiness, request, new CandidateSourceReader(), TestContext.Current.CancellationToken);
        Assert.Equal(expectedCount, candidates.Count);
        Assert.Contains(ids[0], candidates);
        Assert.Contains(ids[1], candidates);
        if (expectedCount == 2)
        {
            Assert.DoesNotContain(ids[2], candidates);
        }
    }

    /// <summary>Source publication leaves affected compilations lazy until requested.</summary>
    [Fact]
    public static async Task IncrementalRefreshPublishesTextWithoutCompilingAffectedProjects()
    {
        using var fixture = new TestSemanticSolution();
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var request = new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild);
        await registry.LoadAsync(request, cancellationToken);
        var engine = registry.GetEngine(request.WorkspaceId);
        await engine.WaitForWarmAsync(cancellationToken);
        var before = engine.CaptureAdvancedSnapshot();
        var mutationSnapshot = engine.CaptureMutationSnapshot();
        engine.EnsureMutationSnapshotCurrent(mutationSnapshot);
        var document = before.Solution.Projects.SelectMany(project => project.Documents)
            .First(document => document.FilePath?.Contains("obj", StringComparison.Ordinal) == false);
        var updatedText = (await document.GetTextAsync(cancellationToken)) + "\npublic class RefreshAddedType { }\n";

        await engine.RefreshDocumentsAsync([new(document.FilePath!, updatedText, "updated")], cancellationToken);

        var after = engine.CaptureAdvancedSnapshot();
        Assert.False(engine.IsCurrentGeneration(before.Generation));
        Assert.Throws<InvalidOperationException>(() => engine.EnsureMutationSnapshotCurrent(mutationSnapshot));
        engine.EnsureMutationSnapshotCurrent(engine.CaptureMutationSnapshot());
        Assert.Equal(updatedText, (await after.Solution.GetDocument(document.Id)!.GetTextAsync(cancellationToken)).ToString());
        Assert.False(after.Solution.GetProject(document.Project.Id)!.TryGetCompilation(out _));
        Assert.Equal(before.CompiledProjects.Count, after.CompiledProjects.Count);
        var compilation = await after.Solution.GetProject(document.Project.Id)!.GetCompilationAsync(cancellationToken);
        Assert.Single(compilation!.GetSymbolsWithName("RefreshAddedType"));
    }

    /// <summary>A query deadline retains completed partial output without treating it as caller cancellation.</summary>
    [Fact]
    public static async Task QueryDeadlinePreservesCompletedPartialResult()
    {
        using var fixture = new TestSemanticSolution();
        var ct = TestContext.Current.CancellationToken;
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var snapshot = engine.CaptureMutationSnapshot();
        var started = Signal();
        using var deadline = new CancellationTokenSource();
        var operation = engine.RunSnapshotQueryAsync(
            snapshot.Solution,
            async token =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    return 17;
                }

                return 0;
            },
            deadline.Token,
            ct);
        await started.Task.WaitAsync(ct);
        await deadline.CancelAsync();
#pragma warning disable VSTHRD003 // The test owns both the query and its independently controlled deadline.
        Assert.Equal(17, await operation);
#pragma warning restore VSTHRD003
        Assert.False(ct.IsCancellationRequested);
    }

    /// <summary>Blocking or throwing compiler callbacks cannot hold a caller beyond bounded abandonment.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompilerCancellationCallbacksRetainResourcesWithoutBlockingCaller(bool throws)
    {
        using var fixture = new TestSemanticSolution();
        var ct = TestContext.Current.CancellationToken;
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        var disposed = Signal();
        var clock = new ObservedTimeProvider();
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TestPromptLoader.Instance,
            cancellationBackstop: TimeSpan.FromMilliseconds(1),
            timeProvider: clock)
        {
            WorkspaceDisposalObserver = _ => disposed.TrySetResult(),
        };
        await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var snapshot = engine.CaptureMutationSnapshot();
        var started = Signal();
        var callbackEntered = Signal();
        using var release = new ManualResetEventSlim();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var operation = engine.RunSnapshotOperationAsync(
            snapshot.Solution,
            async token =>
            {
                await using var registration = token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    release.Wait(ct);
                    if (throws)
                    {
                        throw new InvalidOperationException("Deliberate compiler callback failure.");
                    }
                });
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            caller.Token);
        Exception? primaryFailure = null;
        try
        {
            await started.Task.WaitAsync(ct);
            await caller.CancelAsync();
            await callbackEntered.Task.WaitAsync(ct);
            await clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(1), ct);
            clock.Advance(TimeSpan.FromMilliseconds(1));
#pragma warning disable VSTHRD003 // This test owns the cancelled compiler task.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
#pragma warning restore VSTHRD003
            await engine.DisposeAsync();
            Assert.False(disposed.Task.IsCompleted);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            release.Set();
            try
            {
                await engine.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
                await disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                primaryFailure.Data["WorkspaceCleanupFailure"] = cleanupFailure;
            }
        }

        await disposed.Task.WaitAsync(ct);
    }

    /// <summary>A query whose deadline already expired cannot interrupt useful warming.</summary>
    [Fact]
    public static async Task ExpiredQueryDoesNotInterruptWarming()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 1);
        var started = Signal();
        var release = Signal();
        var interruptions = 0;
        await using var coordinator = new SemanticCompilationCoordinator(
            solution,
            new(),
            new HashSet<ProjectId>(),
            async (id, token) =>
            {
                await using var registration = token.Register(() => Interlocked.Increment(ref interruptions));
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return new(id, true);
            },
            static (_, _, _) => Task.CompletedTask);
        coordinator.Warm(solution.ProjectIds);
        try
        {
            await started.Task.WaitAsync(ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunQueryAsync(_ => Task.FromResult(1), new(true), ct));
            Assert.Equal(0, Volatile.Read(ref interruptions));
        }
        finally
        {
            release.TrySetResult();
        }

        await coordinator.Completion.WaitAsync(ct);
    }

    /// <summary>Saturated abandoned work defers fresh preparation instead of permanently failing its project.</summary>
    [Fact]
    public static async Task SaturationThenReleaseAllowsFreshPreparation()
    {
        using var fixture = new TestSemanticSolution();
        var ct = TestContext.Current.CancellationToken;
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        var clock = new ObservedTimeProvider();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, cancellationBackstop: TimeSpan.FromMilliseconds(1), resourceLimits: new SemanticResourceLimits { CompilationWorkers = 1, EditAnalysis = new() { Analyzers = new() { Enabled = false } } }, timeProvider: clock);
        await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), ct);
        await engine.WaitForWarmAsync(ct);
        var snapshot = engine.CaptureMutationSnapshot();
        var release = Signal();
        try
        {
            for (var index = 0; index < 2; index++)
            {
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var started = Signal();
                var operation = engine.RunMutationOperationAsync(
                    snapshot,
                    async _ =>
                    {
                        started.SetResult();
                        await release.Task.WaitAsync(ct);
                        return 42;
                    },
                    caller.Token);
                await started.Task.WaitAsync(ct);
                await caller.CancelAsync();
                await clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(1), ct);
                clock.Advance(TimeSpan.FromMilliseconds(1));
#pragma warning disable VSTHRD003 // The test owns each deliberately abandoned operation.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
#pragma warning restore VSTHRD003
            }

            var document = snapshot.Solution.Projects.SelectMany(project => project.Documents)
                .First(document => document.FilePath?.Contains("obj", StringComparison.Ordinal) == false);
            var text = (await document.GetTextAsync(ct)) + "\npublic class AfterSaturation { }\n";
            await engine.RefreshDocumentsAsync([new(document.FilePath!, text, "saturated-refresh")], ct);
            var query = engine.FindSymbolsAsync("AfterSaturation", ct);
            Assert.False(query.IsCompleted);
            release.SetResult();
#pragma warning disable VSTHRD003 // The query awaits the capacity released by this test.
            Assert.NotEmpty(await query);
#pragma warning restore VSTHRD003
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>Disposal invalidates admitted query and mutation generations even if source never changed.</summary>
    [Fact]
    public static async Task DisposalInvalidatesCapturedSemanticInputs()
    {
        using var fixture = new TestSemanticSolution();
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = fixture.Root;
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await engine.LoadAsync(
            new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild),
            cancellationToken);
        var query = engine.CaptureAdvancedSnapshot();
        var mutation = engine.CaptureMutationSnapshot();

        await engine.DisposeAsync();

        Assert.False(engine.IsCurrentGeneration(query.Generation));
        Assert.Throws<InvalidOperationException>(() => engine.EnsureMutationSnapshotCurrent(mutation));
        Assert.Throws<InvalidOperationException>(() => engine.CaptureMutationSnapshot());
    }

    private static TaskCompletionSource Signal()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Solution CreateSolution(AdhocWorkspace workspace, int count)
    {
        var solution = workspace.CurrentSolution;
        for (var index = 0; index < count; index++)
        {
            solution = solution.AddProject(ProjectId.CreateNewId(), "Project" + index, "Assembly" + index, LanguageNames.CSharp);
        }

        return solution;
    }

    private sealed class CandidateSourceReader : ICodeExploreSourceReader
    {
        public bool IsPathAllowed(string path)
        {
            return true;
        }

        public Task<CodeExploreSourceText> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cheap candidate discovery must not read source through the projection reader.");
        }
    }
}
