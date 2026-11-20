namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies external semantic refresh coordination, fencing, and recovery.</summary>
public static class SemanticRefreshCoordinatorTests
{
    /// <summary>Snapshot and reconciliation passes overlap only the configured number of reads and join before returning.</summary>
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(4, false, false)]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    public static async Task FileReadPassHonorsLimitAndJoins(int limit, bool reconcile, bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        for (var index = 0; index < limit; index++)
        {
            var path = Path.Combine(repository.Root, reconcile ? $"Extra{index}.cs" : $"Extra{index}.props");
            await File.WriteAllTextAsync(path, reconcile ? $"class Extra{index} {{ }}" : "<Project />", ct);
            if (reconcile)
            {
                backend.AddSourceDocument(repository.WorkspaceId, path);
            }
        }

        var reader = new TestSemanticFileSnapshotReader();
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            fileSnapshotReader: reader,
            semanticResourceLimits: new SemanticResourceLimits { MaximumConcurrentFileReads = limit });
        var request = repository.CreateRequest();
        var generation = reconcile ? await coordinator.BeginBindingAsync(request, ct) : 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var started = 0;
        var cancelledReads = 0;
        reader.BeforeRead = async (path, token) =>
        {
            if (reconcile && !Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var concurrent = Interlocked.Increment(ref active);
            try
            {
                Assert.InRange(concurrent, 1, limit);
                if (Interlocked.Increment(ref started) == limit)
                {
                    entered.TrySetResult();
                }

                await release.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Interlocked.Increment(ref cancelledReads);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var operation = reconcile
            ? coordinator.CompleteBindingAsync(request, generation, cancellation.Token)
            : coordinator.BeginBindingAsync(request, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(ct);
            Assert.Equal(limit, Volatile.Read(ref active));
            Assert.False(operation.IsCompleted);
            Assert.False(coordinator.IsCurrent(repository.SessionId));
        }
        finally
        {
            try
            {
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation.WaitAsync(ct));
                }
                else
                {
                    release.TrySetResult();
                    await operation;
                }
            }
            finally
            {
                release.TrySetResult();
            }
        }

        Assert.Equal(0, Volatile.Read(ref active));
        if (cancel)
        {
            Assert.Equal(limit, Volatile.Read(ref cancelledReads));
        }
        else
        {
            Assert.True(Volatile.Read(ref started) > limit);
        }

        if (reconcile && !cancel)
        {
            Assert.True(coordinator.IsCurrent(repository.SessionId));
        }
    }

    /// <summary>Actual binding boundaries produce timings without replacing freshness checks.</summary>
    [Fact]
    public static async Task StartupProgressTracksSnapshotAndMonitoringReconciliationBoundaries()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var progress = new SemanticStartupProgress();
        using var observation = progress.Observe(repository.SessionId);
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events, startupProgress: progress);
        var request = repository.CreateRequest();
        var generation = await coordinator.BeginBindingAsync(request, TestContext.Current.CancellationToken);
        var captured = Assert.Single(observation.Snapshot);
        Assert.Equal(SemanticStartupPhase.CaptureSnapshots, captured.Phase);
        Assert.Equal(SemanticStartupPhaseState.Completed, captured.State);
        Assert.False(coordinator.IsCurrent(repository.SessionId));

        await coordinator.CompleteBindingAsync(request, generation, TestContext.Current.CancellationToken);
        Assert.Equal(
            [
            SemanticStartupPhase.CaptureSnapshots,
            SemanticStartupPhase.StartMonitoring,
            SemanticStartupPhase.ReadDocuments,
            SemanticStartupPhase.ReconcileSnapshots,
            ],
            observation.Snapshot.Select(item => item.Phase));
        Assert.All(observation.Snapshot, item => Assert.Equal(SemanticStartupPhaseState.Completed, item.State));
        Assert.Equal(captured.Elapsed, observation.Snapshot[0].Elapsed);
        Assert.True(coordinator.IsCurrent(repository.SessionId));
    }

    /// <summary>Derived build outputs stay irrelevant across binding, reload, watcher topology, and admission.</summary>
    [Fact]
    public static async Task GeneratedBuildOutputsNeverCreateRefreshWorkOrWatcherRoots()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var generatedDirectory = Directory.CreateDirectory(Path.Combine(repository.Root, "src", "App", "obj", "Debug", "net10.0")).FullName;
        string[] names = ["App.GeneratedMSBuildEditorConfig.editorconfig", "App.GlobalUsings.g.cs", "App.AssemblyInfo.cs", ".NETCoreApp,Version=v10.0.AssemblyAttributes.cs", "View.g.i.cs", "TemporaryGeneratedFile_fixture.cs"];
        var paths = names.Select(name => Path.Combine(generatedDirectory, name)).ToArray();
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, "initial", TestContext.Current.CancellationToken);
            backend.AddAnalyzerConfigDocument(repository.WorkspaceId, path);
            backend.AddAdditionalDocument(repository.WorkspaceId, path);
            backend.AddSourceDocument(repository.WorkspaceId, path);
        }

        var watcherRoots = new List<string>();
        FileSystemWatcher CreateWatcher(string path)
        {
            watcherRoots.Add(path);
            return new FileSystemWatcher(path);
        }

        await using var coordinator = CreateCoordinator(backend, events, watchFileSystem: true, watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest(), TestContext.Current.CancellationToken);
        var baseline = await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, TestContext.Current.CancellationToken);
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, "changed", TestContext.Current.CancellationToken);
            foreach (var kind in Enum.GetValues<SemanticFileChangeKind>())
            {
                await coordinator.ObserveChangeAsync(new(repository.SessionId, path, kind), TestContext.Current.CancellationToken);
            }
        }

        string[] ordinaryBuildOutputs = ["obj/project.assets.json", "obj/App.csproj.nuget.g.props", "obj/App.csproj.nuget.g.targets", "obj/App.csproj.FileListAbsolute.txt", "obj/App.AssemblyReference.cache", "bin/Debug/App.dll", "bin/Debug/App.pdb", "TestResults/run.trx"];
        foreach (var relative in ordinaryBuildOutputs)
        {
            await coordinator.ObserveChangeAsync(new(repository.SessionId, Path.Combine(repository.Root, relative), SemanticFileChangeKind.Created), TestContext.Current.CancellationToken);
        }

        var clean = await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, TestContext.Current.CancellationToken);
        Assert.False(clean.WasRefreshed);
        Assert.Equal(baseline.DirtyVersion, clean.DirtyVersion);
        Assert.Equal(baseline.AppliedVersion, clean.AppliedVersion);
        Assert.True(coordinator.IsCurrent(repository.SessionId));
        Assert.Equal(0, backend.RefreshCount);
        Assert.DoesNotContain(observed, item => item is SemanticRefreshStarted or SemanticRefreshCompleted or SemanticRefreshFailed);
        Assert.DoesNotContain(generatedDirectory, watcherRoots);

        var barrier = backend.BlockNextRefresh();
        var manual = coordinator.ForceRefreshAsync(repository.SessionId, TestContext.Current.CancellationToken);
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(paths[0], "rewritten by full evaluation", TestContext.Current.CancellationToken);
        barrier.Release.TrySetResult();
        await manual;
        Assert.True(coordinator.IsCurrent(repository.SessionId));
        Assert.Equal(1, backend.RefreshCount);
        Assert.DoesNotContain(generatedDirectory, watcherRoots);
        Assert.Single(observed.OfType<SemanticRefreshStarted>());
        Assert.Single(observed.OfType<SemanticRefreshCompleted>());
    }

    /// <summary>The semantic refresh resource seam preserves every production guardrail value.</summary>
    [Fact]
    public static void SemanticRefreshResourceLimits_Production_PreservesExactDefaults()
    {
        var limits = SemanticRefreshResourceLimits.Production;

        Assert.Equal(1024, limits.MaximumPendingPaths);
        Assert.Equal(1024, limits.MaximumRecentHostEchoIdentities);
        Assert.Equal(256, limits.MaximumSafeReasonLength);
    }

    /// <summary>Invalid semantic refresh resource limits fail before a coordinator is created.</summary>
    [Fact]
    public static void SemanticRefreshResourceLimits_InvalidValues_FailFast()
    {
        Func<SemanticRefreshResourceLimits>[] invalidConstructions =
        [
            () => new SemanticRefreshResourceLimits(maximumPendingPaths: 0),
            () => new SemanticRefreshResourceLimits(maximumRecentHostEchoIdentities: 0),
            () => new SemanticRefreshResourceLimits(maximumSafeReasonLength: 0),
        ];

        Assert.All(
            invalidConstructions,
            construction => Assert.Throws<ArgumentOutOfRangeException>(() => _ = construction()));
    }

    /// <summary>Pending initial binding blocks admission and releases it with a bounded failure.</summary>
    [Fact]
    public static async Task BeginBindingAsync_PendingLoadBlocksAdmissionAndFailureReleasesWithError()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        var generation = await coordinator.BeginBindingAsync(repository.CreateRequest());

        var ensure = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        Assert.False(ensure.IsCompleted);
        await coordinator.FailBindingAsync(
            repository.SessionId,
            generation,
            "Synthetic initial-load failure.",
            CancellationToken.None);

#pragma warning disable VSTHRD003 // The assertion intentionally observes a task started above.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ensure);
#pragma warning restore VSTHRD003
        Assert.Equal("Synthetic initial-load failure.", exception.Message);
    }

    /// <summary>Watcher recovery waits for initial loading before forcing a full refresh.</summary>
    [Fact]
    public static async Task BeginBindingAsync_WatcherRecoveryWaitsForInitialLoadThenForcesFullRefresh()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        var request = repository.CreateRequest();
        var generation = await coordinator.BeginBindingAsync(request);

        coordinator.ObserveFileSystemWatcherError(repository.SessionId);
        Assert.Equal(0, backend.RefreshCount);
        await coordinator.CompleteBindingAsync(request, generation, CancellationToken.None);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        Assert.Equal(SemanticRefreshReason.Recovery, result.Reason);
        Assert.Equal(1, backend.RefreshCount);
    }

    /// <summary>An empty directory created during failed-watcher handoff gains coverage without extra compiler work.</summary>
    [Fact]
    public static async Task ObserveFileSystemWatcherError_EmptyDirectoryDuringHandoffGainsCoverage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var completion = new TaskCompletionSource<SemanticRefreshCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticRefreshCompleted completed && completed.Reason == SemanticRefreshReason.ExternalChange)
            {
                completion.TrySetResult(completed);
            }

            return Task.CompletedTask;
        });
        var folder = Path.Combine(repository.Root, "handoff");
        var watchers = new ConcurrentDictionary<string, FileSystemWatcher>(PathComparer);
        var createDuringHandoff = false;
        FileSystemWatcher CreateWatcher(string path)
        {
            if (createDuringHandoff)
            {
                createDuringHandoff = false;
                Directory.CreateDirectory(folder);
            }

            var watcher = new FileSystemWatcher(path);
            watchers[path] = watcher;
            return watcher;
        }

        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events, watchFileSystem: true, watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest(), ct);
        foreach (var watcher in watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
        }

        // The old watcher cannot deliver the create, and the replacement roots have already been captured.
        createDuringHandoff = true;
        coordinator.ObserveFileSystemWatcherError(repository.SessionId);
        await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct);
        Assert.True(watchers.TryGetValue(folder, out var installed));
        Assert.True(installed.EnableRaisingEvents);
        Assert.Equal(1, backend.RefreshCount);
        Assert.True(coordinator.IsCurrent(repository.SessionId));

        // Exercise native monitoring; injecting a notification would hide missing coverage.
        await File.WriteAllTextAsync(Path.Combine(folder, "New.cs"), "class New { }", ct);
        Assert.Equal(SemanticRefreshMode.Full, (await completion.Task.WaitAsync(ct)).Mode);
    }

    /// <summary>Watcher recovery rebuilds monitoring before accepting later physical edits.</summary>
    [Fact]
    public static async Task ObserveFileSystemWatcherError_RestartsProductionWatcher()
    {
        using var repository = new TemporaryRepository(useNestedSourcePath: true);
        await using var events = new DomainEventStream();
        var externalCompletion = new TaskCompletionSource<SemanticRefreshCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticRefreshCompleted completed
                && completed.Reason == SemanticRefreshReason.ExternalChange)
            {
                externalCompletion.TrySetResult(completed);
            }

            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events, watchFileSystem: true);
        await coordinator.BindAsync(repository.CreateRequest());

        coordinator.ObserveFileSystemWatcherError(repository.SessionId);
        var recovery = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        Assert.Equal(SemanticRefreshReason.Recovery, recovery.Reason);
        Assert.Equal(SemanticRefreshMode.Full, recovery.Mode);

        await File.WriteAllTextAsync(repository.SourcePath, "public class AfterRecovery { }");
        var result = await externalCompletion.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SemanticRefreshReason.ExternalChange, result.Reason);
        Assert.Equal(SemanticRefreshMode.Incremental, result.Mode);
        Assert.Equal(2, backend.RefreshCount);
    }

    /// <summary>A watcher-rebuild failure publishes one correlated bounded refresh failure.</summary>
    [Fact]
    public static async Task ObserveFileSystemWatcherError_RebuildFailurePublishesLifecycleFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe(async (domainEvent, token) =>
        {
            observed.Enqueue(domainEvent);
            if (domainEvent is SemanticRefreshStarted)
            {
                recoveryStarted.TrySetResult();
                await releaseRecovery.Task.WaitAsync(token);
            }
        });
        var watcherCreationCount = 0;
        FileSystemWatcher CreateWatcher(string path)
        {
            if (Interlocked.Increment(ref watcherCreationCount) > 1)
            {
                throw new IOException("Synthetic watcher rebuild failure.");
            }

            return new FileSystemWatcher(path);
        }

        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            watchFileSystem: true,
            watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest(), cancellationToken);

        coordinator.ObserveFileSystemWatcherError(repository.SessionId);
        Task<SemanticRefreshResult> admission;
        try
        {
            await recoveryStarted.Task.WaitAsync(cancellationToken);
            // Join the active recovery before allowing watcher creation to fail. A later
            // admission intentionally retries failed work and would emit a second lifecycle.
            admission = coordinator.EnsureCurrentAsync(
                repository.SessionId,
                SemanticRefreshReason.UserAdmission,
                cancellationToken);
            Assert.False(admission.IsCompleted);
        }
        finally
        {
            releaseRecovery.TrySetResult();
        }

#pragma warning disable VSTHRD003 // The assertion intentionally observes the shared refresh task.
        await Assert.ThrowsAsync<InvalidOperationException>(() => admission);
#pragma warning restore VSTHRD003

        var started = Assert.Single(observed.OfType<SemanticRefreshStarted>());
        var failed = Assert.Single(observed.OfType<SemanticRefreshFailed>());
        Assert.Equal(SemanticRefreshReason.Recovery, started.Reason);
        Assert.Equal(started.RefreshId, failed.RefreshId);
        Assert.Equal(SemanticRefreshFailureKind.Infrastructure, failed.FailureKind);
        Assert.Equal(0, backend.RefreshCount);
    }

    /// <summary>A full reload adds and removes exact watcher roots for loaded ignored-directory documents.</summary>
    [Fact]
    public static async Task ForceRefreshAsync_ReconcilesExplicitIgnoredDirectoryWatcherRoots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var activeWatchers = new ConcurrentDictionary<FileSystemWatcher, string>();
        FileSystemWatcher CreateWatcher(string path)
        {
            var watcher = new FileSystemWatcher(path);
            activeWatchers.TryAdd(watcher, path);
            watcher.Disposed += (_, _) => activeWatchers.TryRemove(watcher, out _);
            return watcher;
        }

        var externalCompletion = new TaskCompletionSource<SemanticRefreshCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticRefreshCompleted completed
                && completed.Reason == SemanticRefreshReason.ExternalChange)
            {
                externalCompletion.TrySetResult(completed);
            }

            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            watchFileSystem: true,
            watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest(), cancellationToken);
        var ignoredDirectory = Path.Combine(repository.Root, "obj");
        Directory.CreateDirectory(ignoredDirectory);
        var additionalPath = Path.Combine(ignoredDirectory, "loaded.json");
        await File.WriteAllTextAsync(additionalPath, "initial", cancellationToken);
        backend.AddAdditionalDocument(repository.WorkspaceId, additionalPath);

        await coordinator.ForceRefreshAsync(repository.SessionId, cancellationToken);
        var explicitWatcher = Assert.Single(activeWatchers, pair => pair.Value == ignoredDirectory).Key;
        Assert.True(explicitWatcher.EnableRaisingEvents);
        await File.WriteAllTextAsync(additionalPath, "changed", cancellationToken);
        var external = await externalCompletion.Task.WaitAsync(cancellationToken);
        Assert.Equal(SemanticRefreshMode.Full, external.Mode);

        backend.RemoveAdditionalDocument(repository.WorkspaceId, additionalPath);
        await coordinator.ForceRefreshAsync(repository.SessionId, cancellationToken);

        Assert.True(coordinator.IsCurrent(repository.SessionId));
        Assert.DoesNotContain(ignoredDirectory, activeWatchers.Values);
    }

    /// <summary>The shared semantic refresh path policy recognizes nested generated-output paths.</summary>
    [Fact]
    public static void SemanticRefreshPathPolicy_IgnoresNestedBuildOutputPaths()
    {
        using var repository = new TemporaryRepository();
        var generatedSourcePath = Path.Combine(
            repository.Root,
            "container",
            "source",
            "AI.Inference.Fusion.Tests",
            "obj",
            "Debug",
            "net8.0",
            "Generated.g.cs");

        Assert.True(SemanticRefreshPathPolicy.IsIgnoredPath(
            repository.Root,
            generatedSourcePath));
        Assert.True(SemanticRefreshPathPolicy.IsIgnoredGeneratedSourceDocument(
            repository.Root,
            generatedSourcePath));

        var assemblyInfoPath = Path.Combine(
            repository.Root,
            "container",
            "source",
            "AI.Inference.Fusion.Tests",
            "obj",
            "Debug",
            "net8.0",
            "AI.Inference.Fusion.Tests.AssemblyInfo.cs");
        var assemblyAttributesPath = Path.Combine(
            repository.Root,
            "container",
            "source",
            "AI.Inference.Fusion.Tests",
            "obj",
            "Debug",
            "net8.0",
            ".NETCoreApp,Version=v10.0.AssemblyAttributes.cs");
        var sourceArtifactPath = Path.Combine(
            repository.Root,
            "src",
            "Artifacts",
            "ArtifactFactory.cs");

        Assert.True(SemanticRefreshPathPolicy.IsIgnoredGeneratedSourceDocument(
            repository.Root,
            assemblyInfoPath));
        Assert.True(SemanticRefreshPathPolicy.IsIgnoredGeneratedSourceDocument(
            repository.Root,
            assemblyAttributesPath));
        Assert.False(SemanticRefreshPathPolicy.IsIgnoredGeneratedSourceDocument(
            repository.Root,
            sourceArtifactPath));
    }

    /// <summary>Ignored build and editor churn during watcher handoff causes no follow-up refresh.</summary>
    [Fact]
    public static async Task ObserveFileSystemWatcherError_IgnoresBuildAndTemporaryChurnDuringRestart()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var watcherCreationCount = 0;
        FileSystemWatcher CreateWatcher(string path)
        {
            if (Interlocked.Increment(ref watcherCreationCount) == 2)
            {
                var ignoredDirectory = Directory.CreateDirectory(Path.Combine(repository.Root, "obj"));
                File.WriteAllText(Path.Combine(ignoredDirectory.FullName, "Generated.cs"), "ignored");
                File.WriteAllText(Path.Combine(repository.Root, "editor.tmp"), "ignored");
            }

            return new FileSystemWatcher(path);
        }

        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            watchFileSystem: true,
            watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest());

        coordinator.ObserveFileSystemWatcherError(repository.SessionId);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshReason.Recovery, result.Reason);
        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        Assert.True(coordinator.IsCurrent(repository.SessionId));
        Assert.Equal(1, backend.RefreshCount);
    }

    /// <summary>A post-full watcher failure keeps the workspace dirty until recovery succeeds.</summary>
    [Fact]
    public static async Task ForceRefreshAsync_PostFullWatcherFailureRequiresSuccessfulRecovery()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var watcherCreationCount = 0;
        var failRebuilds = 1;
        FileSystemWatcher CreateWatcher(string path)
        {
            if (Interlocked.Increment(ref watcherCreationCount) > 1
                && Volatile.Read(ref failRebuilds) != 0)
            {
                throw new IOException("Synthetic post-refresh watcher rebuild failure.");
            }

            return new FileSystemWatcher(path);
        }

        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            watchFileSystem: true,
            watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest());

#pragma warning disable VSTHRD003 // The assertion intentionally observes the shared refresh task.
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ForceRefreshAsync(
            repository.SessionId));
#pragma warning restore VSTHRD003
        Assert.False(coordinator.IsCurrent(repository.SessionId));
        Assert.Contains(observed, domainEvent => domainEvent is SemanticRefreshFailed failed
            && failed.Reason == SemanticRefreshReason.Manual
            && failed.FailureKind == SemanticRefreshFailureKind.Infrastructure);

        Volatile.Write(ref failRebuilds, 0);
        var recovery = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshReason.Recovery, recovery.Reason);
        Assert.Equal(SemanticRefreshMode.Full, recovery.Mode);
        Assert.True(coordinator.IsCurrent(repository.SessionId));
        Assert.Equal(2, backend.RefreshCount);
    }

    /// <summary>An unknown source owner retains the authoritative reload path.</summary>
    [Fact]
    public static async Task UnresolvedProjectAdditionUsesFullRefresh()
    {
        var token = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest(), token);
        var observed = new ConcurrentQueue<IDomainEvent>();
        var completion = new TaskCompletionSource<SemanticRefreshCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            if (item is SemanticRefreshCompleted refresh)
            {
                completion.TrySetResult(refresh);
            }

            return Task.CompletedTask;
        });
        var barrier = backend.BlockNextRefresh();
        var path = Path.Combine(repository.Root, "Added.cs");
        await File.WriteAllTextAsync(path, "class Added {}", token);
        await coordinator.ObserveChangeAsync(new(repository.SessionId, path, SemanticFileChangeKind.Created), token);
        SemanticRefreshId refreshId;
        try
        {
            await barrier.Started.Task.WaitAsync(token);
            var started = Assert.Single(observed.OfType<SemanticRefreshStarted>());
            var changed = Assert.Single(observed.OfType<SemanticRefreshModeChanged>());
            Assert.Equal(SemanticRefreshMode.Incremental, started.Mode);
            Assert.Equal(SemanticRefreshMode.Full, changed.Mode);
            Assert.Equal(started.RefreshId, changed.RefreshId);
            Assert.Equal(started.WorkspaceId, changed.WorkspaceId);
            Assert.False(completion.Task.IsCompleted);
            refreshId = changed.RefreshId;
        }
        finally
        {
            barrier.Release.TrySetResult();
        }

        await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, token);
        var completed = await completion.Task.WaitAsync(token);
        Assert.Equal(SemanticRefreshMode.Full, completed.Mode);
        Assert.Equal(refreshId, completed.RefreshId);
    }

    /// <summary>Concurrent waiters share refresh work while cancellation remains waiter-local.</summary>
    [Fact]
    public static async Task EnsureCurrentAsync_ConcurrentWaitersShareOneRefreshAndCancellationIsIsolated()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var barrier = backend.BlockNextRefresh();
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await File.WriteAllTextAsync(repository.SourcePath, "public class Changed { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        using var cancelledWaiter = new CancellationTokenSource();

        var first = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission,
            cancelledWaiter.Token);
        var second = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancelledWaiter.CancelAsync();
#pragma warning disable VSTHRD003 // The assertion intentionally observes a task started above.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
#pragma warning restore VSTHRD003
        barrier.Release.TrySetResult();
        var result = await second;

        Assert.Equal(1, backend.RefreshCount);
        Assert.True(result.WasRefreshed);
        Assert.Equal(result.DirtyVersion, result.AppliedVersion);
    }

    /// <summary>A refresh start describes its initial attempt while completion aggregates a correlated escalated follow-up.</summary>
    [Fact]
    public static async Task EnsureCurrentAsync_HostIncrementalStartEscalatesToCorrelatedExternalFullCompletion()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var firstBarrier = backend.BlockNextRefresh();
        var secondBarrier = backend.BlockNextRefresh();
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await File.WriteAllTextAsync(repository.SourcePath, "public class HostChange { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange() with
        {
            Source = SemanticRefreshReason.HostMutation,
        });
        var ensure = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await firstBarrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(repository.SolutionPath, "external graph change");
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            repository.SolutionPath,
            SemanticFileChangeKind.Changed,
            SemanticRefreshReason.ExternalChange));
        firstBarrier.Release.TrySetResult();
        await secondBarrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(observed.OfType<SemanticRefreshCompleted>());
        secondBarrier.Release.TrySetResult();
        var result = await ensure;

        var started = Assert.Single(observed.OfType<SemanticRefreshStarted>());
        var completed = Assert.Single(observed.OfType<SemanticRefreshCompleted>());
        Assert.Equal(SemanticRefreshReason.HostMutation, started.Reason);
        Assert.Equal(SemanticRefreshMode.Incremental, started.Mode);
        Assert.Equal(SemanticRefreshReason.ExternalChange, completed.Reason);
        Assert.Equal(SemanticRefreshMode.Full, completed.Mode);
        Assert.Equal(started.RefreshId, completed.RefreshId);
        Assert.Equal(result.RefreshId, completed.RefreshId);
    }

    /// <summary>A failed refresh retains dirty state and can recover on a later admission.</summary>
    [Fact]
    public static async Task EnsureCurrentAsync_FailureRetainsDirtyStateForRecovery()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath)
        {
            FailRefreshes = true,
        };
        var barrier = backend.BlockNextRefresh();
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await File.WriteAllTextAsync(repository.SourcePath, "public class BrokenRefresh { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var failed = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        barrier.Release.TrySetResult();

#pragma warning disable VSTHRD003 // The assertion intentionally observes a task started above.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
#pragma warning restore VSTHRD003
        Assert.Equal("Semantic refresh could not establish current state.", exception.Message);
        Assert.Contains(observed, domainEvent => domainEvent is SemanticRefreshFailed failure
            && failure.AppliedVersion < failure.DirtyVersion);

        backend.FailRefreshes = false;
        var recovered = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.Recovery);
        Assert.Equal(recovered.DirtyVersion, recovered.AppliedVersion);
        Assert.True(recovered.WasRefreshed);
    }

    /// <summary>An exact expected write identity is attributed to the host mutation.</summary>
    [Fact]
    public static async Task RegisterExpectedWritesAsync_AttributesWatcherEchoToHostMutation()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var completed = new TaskCompletionSource<SemanticRefreshCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticRefreshCompleted refresh)
            {
                completed.TrySetResult(refresh);
            }

            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var reader = new TestSemanticFileSnapshotReader();
        await using var coordinator = CreateCoordinator(backend, events, reader);
        await coordinator.BindAsync(repository.CreateRequest());
        const string replacement = "public class HostOwned { }";
        var registration = await coordinator.RegisterExpectedWritesAsync(
            repository.SessionId,
            repository.WorkspaceId,
            MutationSetId.New(),
            [new SemanticHostWriteExpectation("Source.cs", Hash(replacement), true)]);

        await File.WriteAllTextAsync(repository.SourcePath, replacement);
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.BeforeRead = async (_, token) =>
        {
            reading.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var completion = coordinator.CompleteExpectedWritesAsync(
            Assert.IsType<SemanticHostMutationRegistration>(registration),
            ["Source.cs"]);
        try
        {
            await reading.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, coordinator.GetActiveHostMutationCount(repository.SessionId));
            Assert.Equal(0, backend.RefreshCount);
        }
        finally
        {
            release.TrySetResult();
            await completion;
        }

        _ = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshReason.HostMutation, (await completed.Task.WaitAsync(TestContext.Current.CancellationToken)).Reason);
        Assert.Equal(1, backend.RefreshCount);
        Assert.Equal(0, coordinator.GetActiveHostMutationCount(repository.SessionId));
    }

    /// <summary>A mismatched expected write identity remains externally attributed.</summary>
    [Fact]
    public static async Task RegisterExpectedWritesAsync_MismatchedIdentityRemainsExternal()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        var registration = await coordinator.RegisterExpectedWritesAsync(
            repository.SessionId,
            repository.WorkspaceId,
            MutationSetId.New(),
            [new SemanticHostWriteExpectation("Source.cs", Hash("expected"), true)]);

        await File.WriteAllTextAsync(repository.SourcePath, "external");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        await coordinator.CompleteExpectedWritesAsync(
            Assert.IsType<SemanticHostMutationRegistration>(registration),
            ["Source.cs"]);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshReason.ExternalChange, result.Reason);
        Assert.Equal(1, backend.RefreshCount);
    }

    /// <summary>Stale and duplicate mutation completion cannot release a newer active registration.</summary>
    [Fact]
    public static async Task CompleteExpectedWritesAsync_TracksExactMutationRegistrationIdentity()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        var first = Assert.IsType<SemanticHostMutationRegistration>(
            await coordinator.RegisterExpectedWritesAsync(
                repository.SessionId,
                repository.WorkspaceId,
                MutationSetId.New(),
                []));
        var second = Assert.IsType<SemanticHostMutationRegistration>(
            await coordinator.RegisterExpectedWritesAsync(
                repository.SessionId,
                repository.WorkspaceId,
                MutationSetId.New(),
                []));

        await coordinator.CompleteExpectedWritesAsync(
            first with { MutationSetId = MutationSetId.New() },
            []);
        Assert.Equal(2, coordinator.GetActiveHostMutationCount(repository.SessionId));
        await coordinator.CompleteExpectedWritesAsync(first, []);
        await coordinator.CompleteExpectedWritesAsync(first, []);
        Assert.Equal(1, coordinator.GetActiveHostMutationCount(repository.SessionId));
        await coordinator.CompleteExpectedWritesAsync(second, []);

        Assert.Equal(0, coordinator.GetActiveHostMutationCount(repository.SessionId));
    }

    /// <summary>An unstable snapshot retains dirty state and reports its failure kind.</summary>
    [Fact]
    public static async Task EnsureCurrentAsync_UnstableSnapshotRetainsDirtyVersionAndReportsFailure()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var reader = new TestSemanticFileSnapshotReader();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events, reader);
        await coordinator.BindAsync(repository.CreateRequest());
        reader.ReturnUnstable = true;
        await File.WriteAllTextAsync(repository.SourcePath, "public class Unstable { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());

        var failed = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
#pragma warning disable VSTHRD003 // The assertion intentionally observes a task started above.
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
#pragma warning restore VSTHRD003
        var failure = Assert.IsType<SemanticRefreshFailed>(
            observed.Single(item => item is SemanticRefreshFailed));
        Assert.Equal(SemanticRefreshFailureKind.UnstableSnapshot, failure.FailureKind);
        Assert.True(failure.AppliedVersion < failure.DirtyVersion);
        Assert.Equal(0, backend.RefreshCount);

        reader.ReturnUnstable = false;
        var recovered = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.Recovery);
        Assert.Equal(recovered.DirtyVersion, recovered.AppliedVersion);
    }

    /// <summary>An obsolete binding generation cannot replace a newer pending binding.</summary>
    [Fact]
    public static async Task CompleteBindingAsync_ObsoleteGenerationCannotReplaceNewerPendingBinding()
    {
        using var firstRepository = new TemporaryRepository();
        using var secondRepository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(
            firstRepository.WorkspaceId,
            firstRepository.SourcePath);
        backend.AddWorkspace(secondRepository.WorkspaceId, secondRepository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        var firstRequest = firstRepository.CreateRequest();
        var firstGeneration = await coordinator.BeginBindingAsync(firstRequest);
        var secondRequest = secondRepository.CreateRequest() with
        {
            SessionId = firstRepository.SessionId,
        };
        var secondGeneration = await coordinator.BeginBindingAsync(secondRequest);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.CompleteBindingAsync(
                firstRequest,
                firstGeneration,
                CancellationToken.None));
        var ensure = coordinator.EnsureCurrentAsync(
            firstRepository.SessionId,
            SemanticRefreshReason.UserAdmission);
        Assert.False(ensure.IsCompleted);
        await coordinator.CompleteBindingAsync(
            secondRequest,
            secondGeneration,
            CancellationToken.None);

        var result = await ensure;
        Assert.Equal(secondRepository.WorkspaceId, result.WorkspaceId);
    }

    /// <summary>New source membership missed by initial evaluation fences admission on full recovery.</summary>
    [Theory]
    [InlineData("Added.cs")]
    [InlineData("new/nested/Added.CS")]
    public static async Task BeginBindingAsync_SourceAddedDuringInitialLoadRequiresFullRecovery(string relativePath)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath)
        {
            InventoryAvailable = false,
        };
        await using var coordinator = CreateCoordinator(backend, events);
        var request = repository.CreateRequest();
        var generation = await coordinator.BeginBindingAsync(request, cancellationToken);

        // Simulate a compile item appearing after evaluation, with no watcher notification.
        var addedPath = Path.Combine(repository.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(addedPath)!);
        await File.WriteAllTextAsync(addedPath, "public class Added { }", cancellationToken);
        backend.InventoryAvailable = true;
        var barrier = backend.BlockNextRefresh();
        await coordinator.CompleteBindingAsync(request, generation, cancellationToken);
        var admission = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission,
            cancellationToken);
        await barrier.Started.Task.WaitAsync(cancellationToken);

        Assert.False(admission.IsCompleted);
        Assert.False(coordinator.IsCurrent(repository.SessionId));
        Assert.Equal(SemanticRefreshMode.Full, Assert.Single(backend.Modes));
        barrier.Release.TrySetResult();
        var result = await admission;
        Assert.True(result.WasRefreshed);
        Assert.True(coordinator.IsCurrent(repository.SessionId));
        Assert.Equal(1, backend.RefreshCount);
    }

    /// <summary>A pending additional-document change is reconciled after inventory loads.</summary>
    [Fact]
    public static async Task BeginBindingAsync_AdditionalDocumentChangedWithEmptyPendingInventoryIsReconciled()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var additionalPath = Path.Combine(repository.Root, "config.json");
        await File.WriteAllTextAsync(additionalPath, "{ \"value\": 1 }");
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        backend.AddAdditionalDocument(repository.WorkspaceId, additionalPath);
        backend.InventoryAvailable = false;
        await using var coordinator = CreateCoordinator(backend, events);
        var request = repository.CreateRequest();
        var generation = await coordinator.BeginBindingAsync(request);

        await File.WriteAllTextAsync(additionalPath, "{ \"value\": 2 }");
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            additionalPath,
            SemanticFileChangeKind.Changed));
        backend.InventoryAvailable = true;
        await coordinator.CompleteBindingAsync(request, generation, CancellationToken.None);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        Assert.True(result.WasRefreshed);
    }

    /// <summary>Unrelated lifecycle bursts reuse a compiler ancestor index, which follows inventory replacement.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_UnrelatedLifecycleBurstDoesNotEnumerateInventoryRepeatedly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var sources = new EnumerationProbeSet([repository.SourcePath]);
        var additional = new EnumerationProbeSet([Path.Combine(repository.Root, "obj", "settings.json")]);
        var configs = new EnumerationProbeSet([Path.Combine(repository.Root, "config", ".editorconfig")]);
        var references = new EnumerationProbeSet([Path.Combine(repository.Root, "bin", "reference.dll")]);
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath)
        {
            InventoryOverride = new SemanticRefreshInventory(sources, additional, configs, references),
        };
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest(), ct);
        await coordinator.ObserveChangeAsync(new(repository.SessionId, Path.Combine(repository.Root, "warmup.md"), SemanticFileChangeKind.Created), ct);
        var enumerations = new[] { sources, additional, configs, references }.Select(set => set.EnumerationCount).ToArray();
        Assert.All(enumerations, count => Assert.True(count > 0));

        foreach (var kind in new[] { SemanticFileChangeKind.Created, SemanticFileChangeKind.Deleted, SemanticFileChangeKind.Renamed, SemanticFileChangeKind.Uncertain })
        {
            for (var i = 0; i < 100; i++)
            {
                await coordinator.ObserveChangeAsync(new(repository.SessionId, Path.Combine(repository.Root, $"report-{i}.json"), kind), ct);
            }
        }

        Assert.Equal(enumerations, new[] { sources, additional, configs, references }.Select(set => set.EnumerationCount).ToArray());
        Assert.False((await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct)).WasRefreshed);

        // Missing directories still carry compiler membership, including beneath ignored roots.
        foreach (var name in new[] { "obj", "config", "bin" })
        {
            await coordinator.ObserveChangeAsync(new(repository.SessionId, Path.Combine(repository.Root, name), SemanticFileChangeKind.Deleted), ct);
            Assert.True((await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct)).WasRefreshed);
        }

        var replacement = Path.Combine(repository.Root, "replacement", "data.json");
        backend.InventoryOverride = new SemanticRefreshInventory(
            new HashSet<string>(PathComparer),
            new HashSet<string>([replacement], PathComparer),
            new HashSet<string>(PathComparer),
            new HashSet<string>(PathComparer));
        await coordinator.ObserveChangeAsync(new(repository.SessionId, Path.GetDirectoryName(replacement)!, SemanticFileChangeKind.Deleted), ct);
        Assert.True((await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct)).WasRefreshed);
        await coordinator.ObserveChangeAsync(new(repository.SessionId, Path.Combine(repository.Root, "obj"), SemanticFileChangeKind.Deleted), ct);
        Assert.False((await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct)).WasRefreshed);
    }

    /// <summary>An unrelated new directory gains watcher coverage without semantic work.</summary>
    [Fact]
    public static async Task UnrelatedDirectoryMaintainsWatchersWithoutRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var watched = new ConcurrentQueue<string>();
        FileSystemWatcher CreateWatcher(string path)
        {
            watched.Enqueue(path);
            return new FileSystemWatcher(path);
        }

        await using var coordinator = CreateCoordinator(backend, events, watchFileSystem: true, watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest(), ct);
        var folder = Directory.CreateDirectory(Path.Combine(repository.Root, "reports")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.md"), "not compiler input", ct);
        await coordinator.ObserveChangeAsync(new(repository.SessionId, folder, SemanticFileChangeKind.Created), ct);
        Assert.False((await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct)).WasRefreshed);
        Assert.Contains(folder, watched);
        Assert.Equal(0, backend.RefreshCount);

        var source = Path.Combine(folder, "New.cs");
        await File.WriteAllTextAsync(source, "class New { }", ct);
        await coordinator.ObserveChangeAsync(new(repository.SessionId, source, SemanticFileChangeKind.Created), ct);
        var result = await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct);
        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
    }

    /// <summary>A removed watcher root repairs monitoring without a compiler recovery cycle.</summary>
    [Theory]
    [InlineData("reports")]
    [InlineData("reports.cs")]
    public static async Task DeletedReportDirectoryWatcherErrorDoesNotRefresh(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var folder = Directory.CreateDirectory(Path.Combine(repository.Root, name)).FullName;
        var watchers = new ConcurrentDictionary<string, ErrorProbeWatcher>();
        FileSystemWatcher CreateWatcher(string path)
        {
            var watcher = new ErrorProbeWatcher(path);
            watchers[path] = watcher;
            return watcher;
        }

        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events, watchFileSystem: true, watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest(), ct);
        PauseNativeNotifications(watchers.Values);
        var deletedWatcher = watchers[folder];
        Directory.Delete(folder);
        deletedWatcher.RaiseError();
        var result = await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct);
        Assert.False(result.WasRefreshed);
        Assert.Equal(0, backend.RefreshCount);
        Assert.True(coordinator.IsCurrent(repository.SessionId));

        await File.WriteAllTextAsync(repository.SourcePath, "public class StillWatched { }", ct);
        await coordinator.ObserveChangeAsync(new(repository.SessionId, repository.SourcePath, SemanticFileChangeKind.Changed), ct);
        Assert.True((await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct)).WasRefreshed);

        watchers[repository.Root].RaiseError();
        result = await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct);
        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshReason.Recovery, result.Reason);
        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
    }

    /// <summary>Losing an explicit compiler input beneath an ignored watcher root still refreshes.</summary>
    [Fact]
    public static async Task DeletedRegisteredInputDirectoryWatcherErrorRefreshes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var folder = Directory.CreateDirectory(Path.Combine(repository.Root, "obj")).FullName;
        var additional = Path.Combine(folder, "settings.json");
        await File.WriteAllTextAsync(additional, "{}", ct);
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        backend.AddAdditionalDocument(repository.WorkspaceId, additional);
        var watchers = new ConcurrentDictionary<string, ErrorProbeWatcher>();
        FileSystemWatcher CreateWatcher(string path)
        {
            var watcher = new ErrorProbeWatcher(path);
            watchers[path] = watcher;
            return watcher;
        }

        await using var coordinator = CreateCoordinator(backend, events, watchFileSystem: true, watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest(), ct);
        PauseNativeNotifications(watchers.Values);
        var removedWatcher = watchers[folder];
        File.Delete(additional);
        Directory.Delete(folder);
        removedWatcher.RaiseError();
        var result = await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, ct);
        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
    }

    /// <summary>A manual full reload discards unobserved graph hashes before a later reversion.</summary>
    [Fact]
    public static async Task ForceRefreshAsync_DiscardsStaleGraphControlIdentity()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            repository.SolutionPath,
            SemanticFileChangeKind.Changed));
        await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await File.WriteAllTextAsync(repository.SolutionPath, "missed graph state");

        await coordinator.ForceRefreshAsync(repository.SessionId);
        await File.WriteAllTextAsync(repository.SolutionPath, string.Empty);
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            repository.SolutionPath,
            SemanticFileChangeKind.Changed));
        var reverted = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(reverted.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Full, reverted.Mode);
        Assert.Equal(3, backend.RefreshCount);
    }

    /// <summary>Real sharing failures use the bounded retry delay for both text and binary inputs.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task BeginBindingAsync_RetriesSharingFailures(bool binary, bool releaseLock)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var path = binary ? Path.Combine(repository.Root, "Library.dll") : repository.SolutionPath;
        if (binary)
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
            backend.AddFullReloadInput(repository.WorkspaceId, path);
        }

        await using var exclusiveLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var delays = 0;
        var timeProvider = new CallbackTimeProvider(() =>
        {
            delays++;
            if (releaseLock)
            {
                exclusiveLock.Dispose();
            }
        });
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            resourceLimits: new SemanticRefreshResourceLimits(maximumStableFileReadAttempts: 3),
            timeProvider: timeProvider);

        if (releaseLock)
        {
            await coordinator.BindAsync(repository.CreateRequest(), cancellationToken);
            Assert.True(coordinator.IsCurrent(repository.SessionId));
            Assert.Equal(1, delays);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                coordinator.BeginBindingAsync(repository.CreateRequest(), cancellationToken));
            Assert.Equal(2, delays);
            Assert.Equal(0, backend.RefreshCount);
        }
    }

    /// <summary>Post-full reconciliation detects a disk change even when no second watcher hint arrives.</summary>
    [Fact]
    public static async Task ForceRefreshAsync_ReconcilesDiskChangeObservedOnlyDuringFullLoad()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var barrier = backend.BlockNextRefresh();
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());

        var force = coordinator.ForceRefreshAsync(repository.SessionId);
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(repository.SourcePath, "public class ChangedDuringFull { }");
        barrier.Release.TrySetResult();
        var result = await force;

        Assert.True(result.WasRefreshed);
        Assert.Equal(result.DirtyVersion, result.AppliedVersion);
        Assert.Equal(2, backend.RefreshCount);
    }

    /// <summary>A manual full reload follows up when graph and reference inputs change during loading without watcher delivery.</summary>
    [Fact]
    public static async Task ForceRefreshAsync_ReconcilesAllAuthoritativeInputsChangedDuringFullLoad()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var projectPath = Path.Combine(repository.Root, "Repository.csproj");
        var referencePath = Path.Combine(repository.Root, "Library.dll");
        await File.WriteAllTextAsync(projectPath, "<Project />");
        await File.WriteAllBytesAsync(referencePath, [1, 2, 3]);
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        backend.AddFullReloadInput(repository.WorkspaceId, referencePath);
        var barrier = backend.BlockNextRefresh();
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());

        var force = coordinator.ForceRefreshAsync(repository.SessionId);
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllBytesAsync(referencePath, [3, 2, 1]);
        barrier.Release.TrySetResult();
        var result = await force;

        Assert.True(result.WasRefreshed);
        Assert.Equal(result.DirtyVersion, result.AppliedVersion);
        Assert.Equal(2, backend.RefreshCount);
    }

    /// <summary>Initial binding seeds graph and reference identities so unchanged watcher echoes are no-ops.</summary>
    [Fact]
    public static async Task BindAsync_SeedsAuthoritativeInputIdentitiesForFirstEchoSuppression()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var referencePath = Path.Combine(repository.Root, "Library.dll");
        await File.WriteAllBytesAsync(referencePath, [1, 2, 3]);
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        backend.AddFullReloadInput(repository.WorkspaceId, referencePath);
        await using var coordinator = CreateCoordinator(backend, events);
        var request = repository.CreateRequest();
        var generation = await coordinator.BeginBindingAsync(request);
        await coordinator.CompleteBindingAsync(request, generation, CancellationToken.None);

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            repository.SolutionPath,
            SemanticFileChangeKind.Changed));
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            referencePath,
            SemanticFileChangeKind.Changed));
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.False(result.WasRefreshed);
        Assert.Equal(0, backend.RefreshCount);
    }

    /// <summary>An unsafe path transition is not read and leaves the workspace dirty.</summary>
    [Fact]
    public static async Task EnsureCurrentAsync_UnsafePathRevalidationDoesNotReadAndRetainsDirtyState()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var reader = new TestSemanticFileSnapshotReader();
        var validator = new TestSemanticPathSafetyValidator();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events, reader, validator);
        await coordinator.BindAsync(repository.CreateRequest());
        var readsAfterBinding = reader.ReadCount;
        validator.IsSafeValue = false;
        await File.WriteAllTextAsync(repository.SourcePath, "public class Unsafe { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission));

        Assert.Equal(readsAfterBinding, reader.ReadCount);
        Assert.False(coordinator.IsCurrent(repository.SessionId));
        Assert.Equal(0, backend.RefreshCount);
    }

    /// <summary>An old workspace write registration cannot affect a rebound workspace.</summary>
    [Fact]
    public static async Task CompleteExpectedWritesAsync_OldWorkspaceRegistrationCannotAffectRebind()
    {
        using var firstRepository = new TemporaryRepository();
        using var secondRepository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(
            firstRepository.WorkspaceId,
            firstRepository.SourcePath);
        backend.AddWorkspace(secondRepository.WorkspaceId, secondRepository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(firstRepository.CreateRequest());
        var registration = await coordinator.RegisterExpectedWritesAsync(
            firstRepository.SessionId,
            firstRepository.WorkspaceId,
            MutationSetId.New(),
            [new SemanticHostWriteExpectation("Source.cs", Hash("host"), true)]);
        var rebound = secondRepository.CreateRequest() with
        {
            SessionId = firstRepository.SessionId,
        };
        await coordinator.BindAsync(rebound);

        await coordinator.CompleteExpectedWritesAsync(
            Assert.IsType<SemanticHostMutationRegistration>(registration),
            ["Source.cs"]);
        var result = await coordinator.EnsureCurrentAsync(
            firstRepository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(secondRepository.WorkspaceId, result.WorkspaceId);
        Assert.False(result.WasRefreshed);
        Assert.Equal(0, backend.RefreshCount);
    }

    /// <summary>Refresh inventory excludes external and prohibited additional or analyzer-config inputs.</summary>
    [Fact]
    public static async Task SemanticEngine_SemanticRefreshInventoryIsRepositoryConfined()
    {
        var container = Path.Combine(
            Path.GetTempPath(),
            "threadsmith-semantic-confinement",
            Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(container, "repository");
        var prohibitedPath = Path.Combine(repositoryPath, "secret");
        Directory.CreateDirectory(prohibitedPath);
        try
        {
            var projectPath = Path.Combine(repositoryPath, "App.csproj");
            var visibleAdditional = Path.Combine(repositoryPath, "visible.json");
            var visibleConfig = Path.Combine(repositoryPath, ".editorconfig");
            var outputDirectory = Directory.CreateDirectory(Path.Combine(repositoryPath, "obj")).FullName;
            var generatedConfig = Path.Combine(outputDirectory, "App.GeneratedMSBuildEditorConfig.editorconfig");
            var customConfig = Path.Combine(outputDirectory, "custom.editorconfig");
            await File.WriteAllTextAsync(generatedConfig, "root = true", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(customConfig, "root = true", TestContext.Current.CancellationToken);
            var externalAdditional = Path.Combine(container, "outside.json");
            var externalConfig = Path.Combine(container, ".editorconfig");
            var prohibitedAdditional = Path.Combine(prohibitedPath, "hidden.json");
            var prohibitedConfig = Path.Combine(prohibitedPath, ".editorconfig");

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <AdditionalFiles Include="visible.json" />
                    <AdditionalFiles Include="..\outside.json" />
                    <AdditionalFiles Include="secret\hidden.json" />
                    <EditorConfigFiles Include=".editorconfig" />
                    <EditorConfigFiles Include="obj/App.GeneratedMSBuildEditorConfig.editorconfig" />
                    <EditorConfigFiles Include="obj/custom.editorconfig" />
                    <EditorConfigFiles Include="..\.editorconfig" />
                    <EditorConfigFiles Include="secret\.editorconfig" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "Source.cs"), "public class Source { }");
            await File.WriteAllTextAsync(visibleAdditional, "visible");
            await File.WriteAllTextAsync(visibleConfig, "root = true");
            await File.WriteAllTextAsync(externalAdditional, "outside");
            await File.WriteAllTextAsync(externalConfig, "root = true");
            await File.WriteAllTextAsync(prohibitedAdditional, "hidden");
            await File.WriteAllTextAsync(prohibitedConfig, "root = true");
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(
                events,
                NullLogger<SemanticEngine>.Instance,
                TestPromptLoader.Instance,
                timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            await engine.LoadAsync(new SemanticLoadRequest(
                SessionId.New(),
                WorkspaceId.New(),
                repositoryPath,
                projectPath,
                RepositoryTrustLevel.TrustedBuild,
                ["secret/"]));

            var inventory = engine.GetRefreshInventory();
            var loaded = await engine.GetLoadedDocumentsAsync();

            Assert.Contains(visibleAdditional, inventory.AdditionalDocuments);
            Assert.Contains(visibleConfig, inventory.AnalyzerConfigDocuments);
            Assert.Contains(customConfig, inventory.AnalyzerConfigDocuments);
            Assert.DoesNotContain(generatedConfig, inventory.AnalyzerConfigDocuments);
            Assert.DoesNotContain(loaded, document => document.Path == generatedConfig);
            Assert.Contains(loaded, document => document.Path == customConfig);
            Assert.Contains(engine.CaptureAdvancedSnapshot().Solution.Projects.SelectMany(project => project.AnalyzerConfigDocuments), document => document.FilePath == generatedConfig);
            Assert.DoesNotContain(externalAdditional, inventory.AdditionalDocuments);
            Assert.DoesNotContain(externalConfig, inventory.AnalyzerConfigDocuments);
            Assert.DoesNotContain(prohibitedAdditional, inventory.AdditionalDocuments);
            Assert.DoesNotContain(prohibitedConfig, inventory.AnalyzerConfigDocuments);
            Assert.DoesNotContain(loaded, document => document.Path.StartsWith(
                container,
                StringComparison.OrdinalIgnoreCase)
                && !document.Path.StartsWith(repositoryPath, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(loaded, document => document.Path.StartsWith(
                prohibitedPath,
                StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    /// <summary>Refresh inventory rejects an additional file reached through an external directory link.</summary>
    [Fact]
    public static async Task SemanticEngine_SemanticRefreshInventoryRejectsLinkedDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var container = Directory.CreateTempSubdirectory("threadsmith-semantic-linked-");
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(container.FullName, "repository")).FullName;
            var external = Directory.CreateDirectory(Path.Combine(container.FullName, "external")).FullName;
            await File.WriteAllTextAsync(Path.Combine(external, "linked.json"), "outside", cancellationToken);
            var link = Path.Combine(repository, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows()
                && (exception is PlatformNotSupportedException
                    || (exception.HResult & 0xffff) is 1 or 50 or 1314))
            {
                Assert.Skip($"Windows symbolic-link capability is unavailable: {exception.GetType().Name} (0x{exception.HResult:X8}).");
            }

            var linkedFile = Path.Combine(link, "linked.json");
            Assert.True(File.Exists(linkedFile));
            var visibleFile = Path.Combine(repository, "visible.json");
            await File.WriteAllTextAsync(visibleFile, "visible", cancellationToken);
            var project = Path.Combine(repository, "App.csproj");
            await File.WriteAllTextAsync(
                project,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <AdditionalFiles Include="visible.json" />
                    <AdditionalFiles Include="linked/linked.json" />
                  </ItemGroup>
                </Project>
                """,
                cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repository, "Source.cs"), "public class Source { }", cancellationToken);
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            await engine.LoadAsync(
                new SemanticLoadRequest(SessionId.New(), WorkspaceId.New(), repository, project, RepositoryTrustLevel.TrustedBuild),
                cancellationToken);

            var inventory = engine.GetRefreshInventory();
            Assert.Contains(visibleFile, inventory.AdditionalDocuments);
            Assert.DoesNotContain(linkedFile, inventory.AdditionalDocuments);
            Assert.DoesNotContain(await engine.GetLoadedDocumentsAsync(cancellationToken), document => document.Path == linkedFile);
            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(external, "linked.json"), cancellationToken));
        }
        finally
        {
            container.Delete(recursive: true);
        }
    }

    /// <summary>Workspace aliases share refresh work and each alias receives refresh lifecycle events.</summary>
    [Fact]
    public static async Task BeginBindingAsync_SharedWorkspaceAliasUsesOneRefreshAuthority()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        var aliasSessionId = SessionId.New();
        var alias = await coordinator.BeginBindingForLifecycleAsync(
            repository.CreateRequest() with { SessionId = aliasSessionId },
            CancellationToken.None);
        Assert.True(alias.ReusedWorkspaceBinding);

        await File.WriteAllTextAsync(repository.SourcePath, "public class SharedAlias { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange() with { SessionId = aliasSessionId });
        var results = await Task.WhenAll(
            coordinator.EnsureCurrentAsync(
                repository.SessionId,
                SemanticRefreshReason.UserAdmission),
            coordinator.EnsureCurrentAsync(
                aliasSessionId,
                SemanticRefreshReason.UserAdmission));

        Assert.Equal(1, backend.RefreshCount);
        Assert.All(results, result => Assert.Equal(repository.WorkspaceId, result.WorkspaceId));
        Assert.Contains(observed, domainEvent => domainEvent is SemanticRefreshStarted started
            && started.SessionId == aliasSessionId);
        Assert.Contains(observed, domainEvent => domainEvent is SemanticRefreshCompleted completed
            && completed.SessionId == aliasSessionId);
        await coordinator.UnbindAsync(aliasSessionId);
        Assert.True(coordinator.TryGetWorkspaceId(repository.SessionId, out var workspaceId));
        Assert.Equal(repository.WorkspaceId, workspaceId);
        Assert.False(coordinator.TryGetWorkspaceId(aliasSessionId, out _));
    }

    /// <summary>Lifecycle loading reuses a workspace authority when a cloned session selects the same solution.</summary>
    [Fact]
    public static async Task SemanticLifecycle_BindingAsync_SharedWorkspaceSkipsDuplicateLoad()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var completions = new ConcurrentDictionary<SessionId, TaskCompletionSource>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticLoadCompleted completed
                && completions.TryGetValue(completed.SessionId, out var completion))
            {
                completion.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        var loader = new TestSemanticLifecycleLoader();
        await using var observer = new SemanticLifecycleObserver(
            loader,
            coordinator,
            events,
            NullLogger<SemanticLifecycleObserver>.Instance);
        var firstCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completions[repository.SessionId] = firstCompletion;
        await observer.ObserveAsync(new RepositoryOpened(
            repository.SessionId,
            DateTimeOffset.UtcNow,
            repository.Root,
            repository.WorkspaceId));
        await observer.ObserveAsync(new SolutionLoaded(
            repository.SessionId,
            DateTimeOffset.UtcNow,
            repository.SolutionPath));
        var firstLoad = await loader.WaitForStartAsync(repository.SessionId);
        firstLoad.Release.TrySetResult();
        await firstCompletion.Task.WaitAsync(TestContext.Current.CancellationToken);
        var aliasSessionId = SessionId.New();
        var aliasCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completions[aliasSessionId] = aliasCompletion;

        await observer.ObserveAsync(new RepositoryOpened(
            aliasSessionId,
            DateTimeOffset.UtcNow,
            repository.Root,
            repository.WorkspaceId));
        await observer.ObserveAsync(new SolutionLoaded(
            aliasSessionId,
            DateTimeOffset.UtcNow,
            repository.SolutionPath));
        await aliasCompletion.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, loader.LoadCount);
        Assert.True(coordinator.TryGetWorkspaceId(aliasSessionId, out var workspaceId));
        Assert.Equal(repository.WorkspaceId, workspaceId);
        await coordinator.UnbindAsync(aliasSessionId);
        Assert.True(coordinator.IsCurrent(repository.SessionId));
    }

    /// <summary>A non-equivalent alias rebind publishes only after the workspace publication gate opens.</summary>
    [Fact]
    public static async Task SemanticLifecycle_AliasRebindWaitsAtWorkspacePublicationBoundary()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var aliasSessionId = SessionId.New();
        var alternateSolutionPath = Path.Combine(repository.Root, "Alternate.sln");
        await File.WriteAllTextAsync(alternateSolutionPath, string.Empty);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticLoadCompleted completed
                && completed.SessionId == aliasSessionId)
            {
                completion.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var publicationGate = new TestSemanticRefreshPublicationGate();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            publicationGate: publicationGate);
        await coordinator.BindAsync(repository.CreateRequest());
        var loader = new TestSemanticLifecycleLoader();
        await using var observer = new SemanticLifecycleObserver(
            loader,
            coordinator,
            events,
            NullLogger<SemanticLifecycleObserver>.Instance);

        await observer.ObserveAsync(new RepositoryOpened(
            aliasSessionId,
            DateTimeOffset.UtcNow,
            repository.Root,
            repository.WorkspaceId));
        await observer.ObserveAsync(new SolutionLoaded(
            aliasSessionId,
            DateTimeOffset.UtcNow,
            alternateSolutionPath));
        await publicationGate.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, loader.LoadCount);
        Assert.False(coordinator.TryAdmitCurrent(
            repository.SessionId,
            repository.WorkspaceId,
            () => true));
        publicationGate.Release.TrySetResult();
        var load = await loader.WaitForStartAsync(aliasSessionId);
        load.Release.TrySetResult();
        await completion.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(coordinator.IsCurrent(aliasSessionId));
    }

    /// <summary>Loading a second session does not cancel another session's active load.</summary>
    [Fact]
    public static async Task SemanticLifecycle_DifferentSessionDoesNotCancelActiveLoad()
    {
        using var firstRepository = new TemporaryRepository();
        using var secondRepository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var loader = new TestSemanticLifecycleLoader();
        await using var observer = new SemanticLifecycleObserver(
            loader,
            refreshCoordinator: null,
            events,
            NullLogger<SemanticLifecycleObserver>.Instance);
        await observer.ObserveAsync(new RepositoryOpened(
            firstRepository.SessionId,
            DateTimeOffset.UtcNow,
            firstRepository.Root,
            firstRepository.WorkspaceId));
        await observer.ObserveAsync(new RepositoryOpened(
            secondRepository.SessionId,
            DateTimeOffset.UtcNow,
            secondRepository.Root,
            secondRepository.WorkspaceId));
        await observer.ObserveAsync(new SolutionLoaded(
            firstRepository.SessionId,
            DateTimeOffset.UtcNow,
            firstRepository.SolutionPath,
            firstRepository.WorkspaceId));
        var firstLoad = await loader.WaitForStartAsync(firstRepository.SessionId);

        await observer.ObserveAsync(new SolutionLoaded(
            secondRepository.SessionId,
            DateTimeOffset.UtcNow,
            secondRepository.SolutionPath,
            secondRepository.WorkspaceId));
        Assert.False(firstLoad.CancellationToken.IsCancellationRequested);
        firstLoad.Release.TrySetResult();
        var secondLoad = await loader.WaitForStartAsync(secondRepository.SessionId);
        secondLoad.Release.TrySetResult();
    }

    /// <summary>Rebinding cancels obsolete work and rejects late changes from it.</summary>
    [Fact]
    public static async Task BindAsync_RebindCancelsOldWorkAndRejectsLateChanges()
    {
        using var firstRepository = new TemporaryRepository();
        using var secondRepository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(
            firstRepository.WorkspaceId,
            firstRepository.SourcePath);
        backend.AddWorkspace(secondRepository.WorkspaceId, secondRepository.SourcePath);
        var barrier = backend.BlockNextRefresh();
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(firstRepository.CreateRequest());
        await File.WriteAllTextAsync(firstRepository.SourcePath, "public class Old { }");
        await coordinator.ObserveChangeAsync(firstRepository.CreateChange());
        var oldEnsure = coordinator.EnsureCurrentAsync(
            firstRepository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        while (observed.TryDequeue(out _))
        {
        }

        var reboundRequest = secondRepository.CreateRequest() with
        {
            SessionId = firstRepository.SessionId,
        };
        await coordinator.BindAsync(reboundRequest);
#pragma warning disable VSTHRD003 // The assertion intentionally observes a task started above.
        await Assert.ThrowsAnyAsync<Exception>(() => oldEnsure);
#pragma warning restore VSTHRD003
        await coordinator.ObserveChangeAsync(firstRepository.CreateChange());
        var current = await coordinator.EnsureCurrentAsync(
            firstRepository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(secondRepository.WorkspaceId, current.WorkspaceId);
        Assert.Equal(1, backend.RefreshCount);
        Assert.DoesNotContain(observed, domainEvent => domainEvent switch
        {
            SemanticRefreshStarted started => started.WorkspaceId == firstRepository.WorkspaceId,
            SemanticRefreshCompleted completed => completed.WorkspaceId == firstRepository.WorkspaceId,
            SemanticRefreshFailed failed => failed.WorkspaceId == firstRepository.WorkspaceId,
            _ => false,
        });
    }

    /// <summary>Last-owner cleanup captures its preparation before another binding adopts the same workspace.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task UnbindAsync_OverlappingRebindCannotRetireReplacementPreparation(bool cancelWaiter)
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var barrier = backend.BlockNextRefresh(honorCancellation: false);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await File.WriteAllTextAsync(repository.SourcePath, "public class Retiring { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var ensure = coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission);
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var unbind = coordinator.UnbindAsync(repository.SessionId, cancellation.Token);
        try
        {
            Assert.False(unbind.IsCompleted);
            if (cancelWaiter)
            {
                await cancellation.CancelAsync();
            }

            backend.PreparationOwnerVersion = 2;
            var replacement = repository.CreateRequest() with { SessionId = SessionId.New() };
            await coordinator.BeginBindingAsync(replacement, TestContext.Current.CancellationToken);
            barrier.Release.TrySetResult();
            await unbind;
#pragma warning disable VSTHRD003 // The assertion observes the refresh operation started above.
            await Assert.ThrowsAnyAsync<Exception>(() => ensure);
#pragma warning restore VSTHRD003
            Assert.Equal(new[] { 1 }, backend.RetiredPreparationOwners);
        }
        finally
        {
            barrier.Release.TrySetResult();
        }
    }

    /// <summary>A change already inside inventory lookup is ignored when shutdown disposes its backend.</summary>
    [Fact]
    public static async Task DisposeAsync_InFlightInventoryDisposal_DoesNotEscapeWatcherBoundary()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        var barrier = backend.BlockNextInventoryWithDisposal();

        var observation = Task.Run(async () =>
            await coordinator.ObserveChangeAsync(repository.CreateChange()));
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await coordinator.DisposeAsync();
        barrier.Release.Set();
        await observation.WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An active backend disposal remains visible when no shutdown boundary explains it.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_ActiveInventoryDisposal_IsNotSuppressed()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        var barrier = backend.BlockNextInventoryWithDisposal();

        var observation = Task.Run(async () =>
            await coordinator.ObserveChangeAsync(repository.CreateChange()));
        await barrier.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        barrier.Release.Set();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await observation.WaitAsync(TestContext.Current.CancellationToken));
    }

    private static SemanticRefreshCoordinator CreateCoordinator(
        ISemanticRefreshBackend backend,
        IDomainEventStream events,
        ISemanticFileSnapshotReader? fileSnapshotReader = null,
        ISemanticPathSafetyValidator? pathSafetyValidator = null,
        ISemanticRefreshPublicationGate? publicationGate = null,
        TimeSpan? recentHostEchoLifetime = null,
        bool watchFileSystem = false,
        Func<string, FileSystemWatcher>? watcherFactory = null,
        SemanticRefreshResourceLimits? resourceLimits = null,
        TimeProvider? timeProvider = null,
        SemanticStartupProgress? startupProgress = null,
        SemanticResourceLimits? semanticResourceLimits = null)
    {
        return new SemanticRefreshCoordinator(
            backend,
            events,
            NullLogger<SemanticRefreshCoordinator>.Instance,
            publicationGate,
            timeProvider: timeProvider,
            settleInterval: TimeSpan.FromMilliseconds(10),
            maximumBurstWindow: TimeSpan.FromMilliseconds(50),
            watchFileSystem: watchFileSystem,
            fileSnapshotReader: fileSnapshotReader,
            pathSafetyValidator: pathSafetyValidator,
            recentHostEchoLifetime: recentHostEchoLifetime,
            watcherFactory: watcherFactory,
            resourceLimits: resourceLimits,
            startupProgress: startupProgress,
            semanticResourceLimits: semanticResourceLimits);
    }

    private static string Hash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class EnumerationProbeSet(IEnumerable<string> paths) : HashSet<string>(paths, PathComparer), IReadOnlySet<string>
    {
        public int EnumerationCount { get; private set; }

        IEnumerator<string> IEnumerable<string>.GetEnumerator()
        {
            EnumerationCount++;
            return GetEnumerator();
        }
    }

    private static void PauseNativeNotifications(IEnumerable<ErrorProbeWatcher> watchers)
    {
        // These probes own error timing. Physical edit/restart tests cover native delivery.
        // Pause before deleting paths so native errors cannot race the explicit RaiseError.
        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
        }
    }

    private sealed class ErrorProbeWatcher(string path) : FileSystemWatcher(path)
    {
        public void RaiseError()
        {
            OnError(new ErrorEventArgs(new IOException("Synthetic native watcher failure.")));
        }
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository(bool useNestedSourcePath = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "threadsmith-semantic-refresh", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var sourceDirectory = useNestedSourcePath
                ? Directory.CreateDirectory(Path.Combine(Root, "src", "Threadsmith.Cli")).FullName
                : Root;
            SourcePath = Path.Combine(sourceDirectory, "Source.cs");
            SolutionPath = Path.Combine(Root, "Repository.sln");
            File.WriteAllText(SourcePath, "public class Initial { }");
            File.WriteAllText(SolutionPath, string.Empty);
        }

        public string Root { get; }

        public SessionId SessionId { get; } = SessionId.New();

        public string SolutionPath { get; }

        public string SourcePath { get; }

        public WorkspaceId WorkspaceId { get; } = WorkspaceId.New();

        public SemanticFileChange CreateChange()
        {
            return new SemanticFileChange(
                SessionId,
                SourcePath,
                SemanticFileChangeKind.Changed);
        }

        public SemanticLoadRequest CreateRequest()
        {
            return new SemanticLoadRequest(
                SessionId,
                WorkspaceId,
                Root,
                SolutionPath,
                RepositoryTrustLevel.TrustedBuild);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestSemanticRefreshBackend : ISemanticRefreshBackend
    {
        private readonly Lock _gate = new();
        private readonly List<SemanticRefreshMode> _modes = [];
        private readonly Dictionary<WorkspaceId, HashSet<string>> _sourcePaths = [];
        private readonly Dictionary<WorkspaceId, HashSet<string>> _additionalPaths = [];
        private readonly Dictionary<WorkspaceId, HashSet<string>> _analyzerConfigPaths = [];
        private readonly Queue<RefreshBarrier> _barriers = [];
        private readonly Dictionary<WorkspaceId, Dictionary<string, string>> _loadedTexts = [];
        private readonly Dictionary<WorkspaceId, HashSet<string>> _fullReloadInputPaths = [];
        private readonly ConcurrentQueue<int> _retiredPreparationOwners = new();
        private InventoryBarrier? _inventoryBarrier;
        private int _refreshCount;

        public TestSemanticRefreshBackend(WorkspaceId workspaceId, string sourcePath)
        {
            AddWorkspace(workspaceId, sourcePath);
        }

        public bool FailRefreshes { get; set; }

        public bool InventoryAvailable { get; set; } = true;

        public SemanticRefreshInventory? InventoryOverride { get; set; }

        public int PreparationOwnerVersion { get; set; } = 1;

        public IReadOnlyList<int> RetiredPreparationOwners => _retiredPreparationOwners.ToArray();

        public Task RetirePreparationAsync(WorkspaceId workspaceId, bool retainCurrentPreparation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!retainCurrentPreparation)
            {
                _retiredPreparationOwners.Enqueue(PreparationOwnerVersion);
            }

            return Task.CompletedTask;
        }

        public IReadOnlyList<SemanticRefreshMode> Modes
        {
            get
            {
                lock (_gate)
                {
                    return _modes.ToArray();
                }
            }
        }

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public void AddWorkspace(WorkspaceId workspaceId, string sourcePath)
        {
            _sourcePaths.Add(workspaceId, new HashSet<string>([sourcePath], PathComparer));
            _additionalPaths.Add(workspaceId, new HashSet<string>(PathComparer));
            _analyzerConfigPaths.Add(workspaceId, new HashSet<string>(PathComparer));
            _fullReloadInputPaths.Add(workspaceId, new HashSet<string>(PathComparer));
            _loadedTexts.Add(workspaceId, new Dictionary<string, string>(PathComparer)
            {
                [sourcePath] = File.ReadAllText(sourcePath),
            });
        }

        public void AddSourceDocument(WorkspaceId workspaceId, string path)
        {
            _sourcePaths[workspaceId].Add(path);
            _loadedTexts[workspaceId][path] = File.ReadAllText(path);
        }

        public void AddAdditionalDocument(WorkspaceId workspaceId, string path)
        {
            _additionalPaths[workspaceId].Add(path);
            _loadedTexts[workspaceId][path] = File.ReadAllText(path);
        }

        public void RemoveAdditionalDocument(WorkspaceId workspaceId, string path)
        {
            _additionalPaths[workspaceId].Remove(path);
        }

        public void AddAnalyzerConfigDocument(WorkspaceId workspaceId, string path)
        {
            _analyzerConfigPaths[workspaceId].Add(path);
            _loadedTexts[workspaceId][path] = File.ReadAllText(path);
        }

        public void AddFullReloadInput(WorkspaceId workspaceId, string path)
        {
            _fullReloadInputPaths[workspaceId].Add(path);
        }

        public RefreshBarrier BlockNextRefresh(bool honorCancellation = true)
        {
            var barrier = new RefreshBarrier(honorCancellation);
            lock (_gate)
            {
                _barriers.Enqueue(barrier);
            }

            return barrier;
        }

        public InventoryBarrier BlockNextInventoryWithDisposal()
        {
            var barrier = new InventoryBarrier();
            lock (_gate)
            {
                _inventoryBarrier = barrier;
            }

            return barrier;
        }

        public void SetLoadedDocumentText(WorkspaceId workspaceId, string path, string text)
        {
            _loadedTexts[workspaceId][path] = text;
        }

        public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId)
        {
            return SemanticConfidenceLevel.FullSemantic;
        }

        public SemanticRefreshInventory GetRefreshInventory(WorkspaceId workspaceId)
        {
            InventoryBarrier? barrier;
            lock (_gate)
            {
                barrier = _inventoryBarrier;
                _inventoryBarrier = null;
            }

            if (barrier is not null)
            {
                barrier.Started.TrySetResult();
                barrier.Release.Wait(TestContext.Current.CancellationToken);

                throw new ObjectDisposedException(nameof(SemanticEngineRegistry));
            }

            if (InventoryOverride is not null)
            {
                return InventoryOverride;
            }

            if (!InventoryAvailable)
            {
                return new SemanticRefreshInventory(
                    new HashSet<string>(PathComparer),
                    new HashSet<string>(PathComparer),
                    new HashSet<string>(PathComparer),
                    new HashSet<string>(PathComparer));
            }

            return new SemanticRefreshInventory(
                new HashSet<string>(_sourcePaths[workspaceId], PathComparer),
                new HashSet<string>(_additionalPaths[workspaceId], PathComparer),
                new HashSet<string>(_analyzerConfigPaths[workspaceId], PathComparer),
                new HashSet<string>(_fullReloadInputPaths[workspaceId], PathComparer));
        }

        public Task<IReadOnlyList<SemanticDocumentRefresh>> GetLoadedDocumentsAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SemanticDocumentRefresh> documents = [.. _loadedTexts[workspaceId]
                .Select(item => new SemanticDocumentRefresh(
                    item.Key,
                    item.Value,
                    Hash(item.Value)))];
            return Task.FromResult(documents);
        }

        public Task<SemanticLoadResult> RefreshIncrementalAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<SemanticDocumentRefresh> documents,
            CancellationToken cancellationToken)
        {
            return RefreshAsync(workspaceId, SemanticRefreshMode.Incremental, cancellationToken);
        }

        public Task<SemanticLoadResult> RefreshFullAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken)
        {
            return RefreshAsync(workspaceId, SemanticRefreshMode.Full, cancellationToken);
        }

        private static string Hash(string text)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private async Task<SemanticLoadResult> RefreshAsync(
            WorkspaceId workspaceId,
            SemanticRefreshMode mode,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refreshCount);
            Dictionary<string, string>? fullLoadSnapshot = null;
            lock (_gate)
            {
                _modes.Add(mode);
                if (mode == SemanticRefreshMode.Full)
                {
                    var textDocumentPaths = new HashSet<string>(
                        _sourcePaths[workspaceId],
                        PathComparer);
                    textDocumentPaths.UnionWith(_additionalPaths[workspaceId]);
                    textDocumentPaths.UnionWith(_analyzerConfigPaths[workspaceId]);
                    fullLoadSnapshot = textDocumentPaths
                        .Where(File.Exists)
                        .ToDictionary(
                            path => path,
                            File.ReadAllText,
                            PathComparer);
                }
            }

            RefreshBarrier? barrier;
            lock (_gate)
            {
                barrier = _barriers.Count > 0 ? _barriers.Dequeue() : null;
            }

            if (barrier is not null)
            {
                barrier.Started.TrySetResult();
                if (barrier.HonorCancellation)
                {
                    await barrier.Release.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await barrier.Release.Task.WaitAsync(TestContext.Current.CancellationToken);
                }
            }

            if (FailRefreshes)
            {
                throw new InvalidOperationException("Synthetic refresh failure.");
            }

            if (fullLoadSnapshot is not null)
            {
                lock (_gate)
                {
                    _loadedTexts[workspaceId].Clear();
                    foreach (var document in fullLoadSnapshot)
                    {
                        _loadedTexts[workspaceId][document.Key] = document.Value;
                    }
                }
            }

            return new SemanticLoadResult(
                workspaceId,
                SemanticConfidenceLevel.FullSemantic,
                [],
                []);
        }
    }

    private sealed class TestSemanticFileSnapshotReader : ISemanticFileSnapshotReader
    {
        private readonly ConcurrentQueue<string> _readPaths = new();
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public IReadOnlyCollection<string> ReadPaths => _readPaths.ToArray();

        public bool ReturnUnstable { get; set; }

        public Func<string, CancellationToken, Task>? BeforeRead { get; set; }

        public async Task<SemanticFileSnapshot> ReadAsync(
            string path,
            bool readAsBinary,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            _readPaths.Enqueue(Path.GetFullPath(path));
            if (BeforeRead is { } beforeRead)
            {
                await beforeRead(path, cancellationToken);
            }

            if (ReturnUnstable)
            {
                return new SemanticFileSnapshot(
                    File.Exists(path),
                    IsStable: false,
                    Text: null,
                    Identity: "unstable");
            }

            if (!File.Exists(path))
            {
                return new SemanticFileSnapshot(
                    Exists: false,
                    IsStable: true,
                    Text: null,
                    Identity: "missing");
            }

            if (readAsBinary)
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                return new SemanticFileSnapshot(
                    Exists: true,
                    IsStable: true,
                    Text: null,
                    Convert.ToHexString(SHA256.HashData(bytes)));
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken);
            return new SemanticFileSnapshot(
                Exists: true,
                IsStable: true,
                text,
                Hash(text));
        }
    }

    private sealed class TestSemanticPathSafetyValidator : ISemanticPathSafetyValidator
    {
        public bool IsSafeValue { get; set; } = true;

        public bool IsSafe(string repositoryPath, string path)
        {
            return IsSafeValue;
        }
    }

    private sealed class TestSemanticRefreshPublicationGate : ISemanticRefreshPublicationGate
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TResult> PublishAsync<TResult>(
            SessionId sessionId,
            WorkspaceId workspaceId,
            Func<CancellationToken, Task<TResult>> publication,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await publication(cancellationToken);
        }
    }

    private sealed class TestSemanticLifecycleLoader : ISemanticLifecycleLoader
    {
        private readonly ConcurrentDictionary<SessionId, TaskCompletionSource<LoadBarrier>> _loadStarts = new();
        private int _loadCount;

        public int LoadCount => Volatile.Read(ref _loadCount);

        public async Task<SemanticLoadResult> LoadForBindingAsync(
            SemanticLoadRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loadCount);
            var started = _loadStarts.GetOrAdd(
                request.SessionId,
                static _ => new TaskCompletionSource<LoadBarrier>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            var load = new LoadBarrier(cancellationToken);
            started.TrySetResult(load);
            await load.Release.Task.WaitAsync(cancellationToken);
            return new SemanticLoadResult(
                request.WorkspaceId,
                SemanticConfidenceLevel.FullSemantic,
                [],
                []);
        }

        public Task<LoadBarrier> WaitForStartAsync(SessionId sessionId)
        {
            var started = _loadStarts.GetOrAdd(
                sessionId,
                static _ => new TaskCompletionSource<LoadBarrier>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            return started.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class CallbackTimeProvider : FakeTimeProvider
    {
        private readonly Action _onDelay;

        public CallbackTimeProvider(Action onDelay)
        {
            _onDelay = onDelay;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _onDelay();
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Advance(dueTime);
            return timer;
        }
    }

    private sealed record LoadBarrier(CancellationToken CancellationToken)
    {
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RefreshBarrier(bool HonorCancellation)
    {
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class InventoryBarrier
    {
        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
