namespace Threadsmith.ModelTooling.Tests;

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
    /// <summary>Full refresh returns usable current coverage and releases ordinary background warming.</summary>
    [Fact]
    public static async Task FullRefreshUsesStartupReadinessAndWarmsRemainingProjects()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
        await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        var generation = engine.CaptureAdvancedSnapshot().Generation;
        observed.Clear();

        var refreshed = await engine.RefreshFullAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SemanticConfidenceLevel.PartialCompilation, refreshed.Confidence);
        Assert.True(engine.CaptureAdvancedSnapshot().Generation > generation);
        Assert.DoesNotContain(observed, item => item is SemanticLoadCompleted);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, engine.Confidence);
        Assert.Equal(engine.Projects.Count, engine.CaptureAdvancedSnapshot().CompiledProjects.Count);
    }

    /// <summary>Production admits one operation while test bounds reject unreviewed fan-out.</summary>
    [Fact]
    public static void ProductionLimitsAreConservativeAndRejectFanOut()
    {
        var limits = new SemanticPreparationLimits();
        Assert.Equal(1, limits.Workers);
        Assert.Equal(1, limits.Frontier);
        Assert.Throws<ArgumentOutOfRangeException>(() => (limits with { Workers = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (limits with { Workers = 3 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (limits with { Frontier = 2 }).Validate());
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
            new(),
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

    /// <summary>Two-worker admission is exact and late obsolete work has no publication authority.</summary>
    [Fact]
    public static async Task WorkerBoundIsExactAndObsoleteResultsCannotPublish()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, 3);
        var starts = solution.ProjectIds.ToDictionary(id => id, _ => Signal());
        var releases = solution.ProjectIds.ToDictionary(id => id, _ => Signal());
        var published = new ConcurrentQueue<ProjectId>();
        var coordinator = new SemanticCompilationCoordinator(
            solution,
            new() { Workers = 2 },
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
            await Task.WhenAll(starts[solution.ProjectIds[0]].Task, starts[solution.ProjectIds[1]].Task)
                .WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(starts[solution.ProjectIds[2]].Task.IsCompleted);
            Assert.Equal(2, coordinator.Statistics.MaximumRunning);
            coordinator.Abort();
            var outcomes = await coordinator.Completion.WaitAsync(TestContext.Current.CancellationToken);
            Assert.All(outcomes, result => Assert.True(result.Obsolete));
            foreach (var release in releases.Values)
            {
                release.TrySetResult();
            }

            await coordinator.DisposeAsync();
            Assert.Empty(published);
            Assert.False(starts[solution.ProjectIds[2]].Task.IsCompleted);
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

    /// <summary>Real Roslyn warming starts only after the lifecycle owner acknowledges initial publication.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public static async Task BindingRequiresSuccessfulInitialPublicationBeforeWarming(bool publicationSucceeded, bool refreshBeforePublication)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var request = new SemanticLoadRequest(
            SessionId.New(),
            WorkspaceId.New(),
            root,
            Path.Combine(root, "SmallDotNetSolution.sln"),
            RepositoryTrustLevel.TrustedBuild);

        var result = await registry.LoadForBindingAsync(request, TestContext.Current.CancellationToken);
        var engine = registry.GetEngine(request.WorkspaceId);
        Assert.Equal(SemanticConfidenceLevel.PartialCompilation, result.Confidence);
        Assert.Single(engine.CaptureAdvancedSnapshot().CompiledProjects);
        Assert.Empty(observed);
        if (refreshBeforePublication)
        {
            var snapshot = engine.CaptureAdvancedSnapshot();
            var document = snapshot.Solution.GetProject(Assert.Single(snapshot.CompiledProjects))!.Documents.First();
            var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
            await engine.RefreshDocumentsAsync(
                [new SemanticDocumentRefresh(document.FilePath!, text + "\n// Incremental edit before lifecycle publication.\n", "fixture")],
                TestContext.Current.CancellationToken);
        }

        var readiness = engine.CaptureCodeExploreReadinessSnapshot();
        var demand = engine.EnsureProjectsPreparedAsync(readiness.Solution!, readiness.CompiledProjects, "publication-test", true, TestContext.Current.CancellationToken);
        Assert.False(demand.IsCompleted);
        var warm = engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        Assert.False(warm.IsCompleted);

        if (publicationSucceeded)
        {
            await events.PublishAsync(new SemanticConfidenceChanged(request.SessionId, DateTimeOffset.UtcNow, result.Confidence.ToString()), TestContext.Current.CancellationToken);
            await events.PublishAsync(new SemanticLoadCompleted(request.SessionId, DateTimeOffset.UtcNow, request.WorkspaceId, result.Confidence.ToString()), TestContext.Current.CancellationToken);
        }

        await registry.CompleteInitialPublicationAsync(result, publicationSucceeded, TestContext.Current.CancellationToken);
        await warm;
        if (publicationSucceeded)
        {
            await demand;
            Assert.Equal(SemanticConfidenceLevel.FullSemantic, engine.Confidence);
            Assert.Single(observed.OfType<SemanticLoadCompleted>());
            Assert.Equal(
                new[] { nameof(SemanticConfidenceLevel.PartialCompilation), nameof(SemanticConfidenceLevel.FullSemantic) },
                observed.OfType<SemanticConfidenceChanged>().Select(domainEvent => domainEvent.Confidence));
        }
        else
        {
#pragma warning disable VSTHRD003 // The test owns the demand waiting for lifecycle acknowledgement.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => demand);
#pragma warning restore VSTHRD003
            Assert.Equal(SemanticConfidenceLevel.PartialCompilation, engine.Confidence);
            Assert.Single(engine.CaptureAdvancedSnapshot().CompiledProjects);
            Assert.Empty(observed);
        }
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
            new(),
            new HashSet<ProjectId>(),
            async (id, token) =>
            {
                order.Enqueue(id == solution.ProjectIds[0] ? "first" : "warm");
                if (id == solution.ProjectIds[0])
                {
                    started.SetResult();
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
        Assert.False(operation.IsCompleted);
        release.SetResult();
        Assert.Equal(42, await operation);
        await coordinator.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("first,validation,warm", string.Join(',', order));
        Assert.Equal(1, coordinator.Statistics.MaximumRunning);
    }

    /// <summary>A failed background event preserves proven coverage and emits one recovery diagnostic.</summary>
    [Fact]
    public static async Task WarmPublicationFailurePreservesCompilationAndReportsRecovery()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
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
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
        await engine.LoadAsync(new(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild), TestContext.Current.CancellationToken);
        await diagnostic.Task.WaitAsync(TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, engine.Confidence);
        Assert.Equal(1, completions);
        Assert.Equal(engine.Projects.Count, engine.CaptureAdvancedSnapshot().CompiledProjects.Count);
    }

    /// <summary>Workspace aliases receive progressive confidence through the existing binding authority.</summary>
    [Fact]
    public static async Task WarmConfidenceFansOutToSharedWorkspaceSessions()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
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
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        await using var refresh = new SemanticRefreshCoordinator(registry, events, NullLogger<SemanticRefreshCoordinator>.Instance);
        var request = new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild);
        var generation = await refresh.BeginBindingAsync(request, TestContext.Current.CancellationToken);
        var result = await registry.LoadForBindingAsync(request, TestContext.Current.CancellationToken);
        await refresh.CompleteBindingAsync(request, generation, TestContext.Current.CancellationToken);
        var alias = request with { SessionId = SessionId.New() };
        var aliasBinding = await refresh.BeginBindingForLifecycleAsync(alias, TestContext.Current.CancellationToken);
        Assert.True(aliasBinding.ReusedWorkspaceBinding);
        await registry.CompleteInitialPublicationAsync(result, true, TestContext.Current.CancellationToken);
        await registry.GetEngine(request.WorkspaceId).WaitForWarmAsync(TestContext.Current.CancellationToken);
        Assert.Contains(observed, item => item.SessionId == request.SessionId && item.Confidence == nameof(SemanticConfidenceLevel.FullSemantic));
        Assert.Contains(observed, item => item.SessionId == alias.SessionId && item.Confidence == nameof(SemanticConfidenceLevel.FullSemantic));
    }

    /// <summary>A reused lifecycle pair and warm confidence updates share one publication order.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ReusedLifecyclePublicationIsOrderedWithWarming(bool warmBeforeReuse)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        await using var refresh = new SemanticRefreshCoordinator(registry, events, NullLogger<SemanticRefreshCoordinator>.Instance);
        var request = new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild);
        var generation = await refresh.BeginBindingAsync(request, cancellationToken);
        var result = await registry.LoadForBindingAsync(request, cancellationToken);
        await refresh.CompleteBindingAsync(request, generation, cancellationToken);
        var engine = registry.GetEngine(request.WorkspaceId);
        Assert.Equal(SemanticConfidenceLevel.PartialCompilation, result.Confidence);
        var alias = SessionId.New();
        var publishing = Signal();
        var release = Signal();
        var completed = Signal();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe(async (domainEvent, token) =>
        {
            if (domainEvent.SessionId != alias)
            {
                return;
            }

            observed.Enqueue(domainEvent);
            if (domainEvent is SemanticConfidenceChanged)
            {
                publishing.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            if (domainEvent is SemanticLoadCompleted)
            {
                completed.TrySetResult();
            }
        });
        await using var observer = new SemanticLifecycleObserver(registry, refresh, events, NullLogger<SemanticLifecycleObserver>.Instance);
        try
        {
            if (warmBeforeReuse)
            {
                await registry.CompleteInitialPublicationAsync(result, true, cancellationToken);
                await engine.WaitForWarmAsync(cancellationToken);
            }

            await observer.ObserveAsync(new RepositoryOpened(alias, DateTimeOffset.UtcNow, root, request.WorkspaceId, RepositoryTrustLevel.TrustedBuild), cancellationToken);
            await observer.ObserveAsync(new SolutionLoaded(alias, DateTimeOffset.UtcNow, request.SolutionPath), cancellationToken);
            await publishing.Task.WaitAsync(cancellationToken);
            if (!warmBeforeReuse)
            {
                // Warming cannot pass its publication gate while the reused pair is in flight.
                var acknowledgement = registry.CompleteInitialPublicationAsync(result, true, cancellationToken);
                Assert.False(acknowledgement.IsCompleted);
                release.TrySetResult();
                await acknowledgement;
            }

            release.TrySetResult();
            await completed.Task.WaitAsync(cancellationToken);
            await engine.WaitForWarmAsync(cancellationToken);
            var confidence = observed.OfType<SemanticConfidenceChanged>().Select(item => item.Confidence).ToArray();
            string[] expectedConfidence = warmBeforeReuse
                ? [nameof(SemanticConfidenceLevel.FullSemantic)]
                : [nameof(SemanticConfidenceLevel.PartialCompilation), nameof(SemanticConfidenceLevel.FullSemantic)];
            Assert.Equal(expectedConfidence, confidence);
            var completion = Assert.Single(observed.OfType<SemanticLoadCompleted>());
            Assert.Equal(warmBeforeReuse ? nameof(SemanticConfidenceLevel.FullSemantic) : nameof(SemanticConfidenceLevel.PartialCompilation), completion.Confidence);
            Assert.Equal(SemanticConfidenceLevel.FullSemantic, engine.Confidence);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>Detaching the last binding aborts its unpublished warm work before another workspace opens.</summary>
    [Fact]
    public static async Task LastOwnerUnbindStopsPreparationAndCannotPromoteReboundSession()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
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
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
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
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
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
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
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
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
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
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
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
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var queries = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance);
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

    /// <summary>Rejected incremental input does not abandon the still-useful original coordinator.</summary>
    [Fact]
    public static async Task InvalidIncrementalInputPreservesPreparationOwner()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var request = new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild);
        var result = await registry.LoadForBindingAsync(request, TestContext.Current.CancellationToken);
        var engine = registry.GetEngine(request.WorkspaceId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RefreshDocumentsAsync([new SemanticDocumentRefresh(Path.Combine(root, "Unknown.cs"), "class Unknown {}", "fixture")], TestContext.Current.CancellationToken));
        await registry.CompleteInitialPublicationAsync(result, true, TestContext.Current.CancellationToken);
        await engine.WaitForWarmAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, engine.Confidence);
        var snapshot = engine.CaptureAdvancedSnapshot();
        Assert.Equal(snapshot.Solution.ProjectIds.Count, snapshot.CompiledProjects.Count);
    }

    /// <summary>Source publication leaves affected compilations lazy until requested.</summary>
    [Fact]
    public static async Task IncrementalRefreshPublishesTextWithoutCompilingAffectedProjects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var request = new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild);
        await registry.LoadAsync(request, cancellationToken);
        var engine = registry.GetEngine(request.WorkspaceId);
        await engine.WaitForWarmAsync(cancellationToken);
        var before = engine.CaptureAdvancedSnapshot();
        var document = before.Solution.Projects.SelectMany(project => project.Documents)
            .First(document => document.FilePath is not null && !document.FilePath.Contains("obj", StringComparison.Ordinal));
        var updatedText = (await document.GetTextAsync(cancellationToken)) + "\npublic class RefreshAddedType { }\n";

        await engine.RefreshDocumentsAsync([new(document.FilePath!, updatedText, "updated")], cancellationToken);

        var after = engine.CaptureAdvancedSnapshot();
        Assert.Equal(updatedText, (await after.Solution.GetDocument(document.Id)!.GetTextAsync(cancellationToken)).ToString());
        Assert.False(after.Solution.GetProject(document.Project.Id)!.TryGetCompilation(out _));
        Assert.Equal(before.CompiledProjects.Count, after.CompiledProjects.Count);
        var compilation = await after.Solution.GetProject(document.Project.Id)!.GetCompilationAsync(cancellationToken);
        Assert.Single(compilation!.GetSymbolsWithName("RefreshAddedType"));
    }

    /// <summary>Batch screening parses proposed text without joining compiler readiness.</summary>
    [Theory]
    [InlineData("public class Proposal { public void M() { Missing.NewApi(); } }", false)]
    [InlineData("public class Proposal { public void M( { }", true)]
    public static async Task SyntaxScreeningDoesNotWaitForCompilerPublication(string text, bool hasSyntaxError)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "semantic", "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var request = new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), root, Path.Combine(root, "SmallDotNetSolution.sln"), RepositoryTrustLevel.TrustedBuild);
        var loaded = await registry.LoadForBindingAsync(request, cancellationToken);
        var engine = registry.GetEngine(request.WorkspaceId);
        var baseline = new WorkspaceBaseline(request.WorkspaceId, root, DateTimeOffset.UtcNow, []);
        var mutations = new MutationSet
        {
            MutationSetId = MutationSetId.New(), SessionId = request.SessionId, RunId = RunId.New(),
            WorkspaceId = request.WorkspaceId, BaselineCapturedAt = baseline.CapturedAt,
            Rationale = "Screen a batch without joining deferred compilation.", Mutations = [],
        };

        var result = await engine.AnalyzePreMutationAsync(
            new()
            {
                SessionId = request.SessionId, WorkspaceId = request.WorkspaceId,
                Baseline = baseline, MutationSet = mutations, IncludeCompilation = false,
                OverlayFiles = [new() { RelativePath = "Proposal.cs", Text = text }],
            },
            cancellationToken);

        Assert.Equal(hasSyntaxError ? PreMutationGateDecision.RepairableDiagnostics : PreMutationGateDecision.PassedCheapGates, result.Decision);
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(PreMutationDiagnosticSource.Syntax, diagnostic.Source));
        Assert.Single(observed.OfType<SemanticCheckStarted>());
        await registry.CompleteInitialPublicationAsync(loaded, false, cancellationToken);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        public bool IsPathAllowed(string path) => true;

        public Task<CodeExploreSourceText> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cheap candidate discovery must not read source through the projection reader.");
        }
    }
}
