namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
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

    /// <summary>Derived editor-config recognition covers build roots and both separator spellings.</summary>
    [Theory]
    [InlineData("src/App/obj/Debug/net10.0/App.GeneratedMSBuildEditorConfig.editorconfig", true)]
    [InlineData("src/App/OBJ/Debug/App.GENERATEDMSBUILDEDITORCONFIG.EDITORCONFIG", true)]
    [InlineData("artifacts/obj/App/App.GeneratedMSBuildEditorConfig.editorconfig", true)]
    [InlineData("bin/App.GeneratedMSBuildEditorConfig.editorconfig", true)]
    [InlineData("src/App.GeneratedMSBuildEditorConfig.editorconfig", false)]
    [InlineData("obj/custom.editorconfig", false)]
    [InlineData("obj/.globalconfig", false)]
    public static void GeneratedEditorConfigPolicyIsSpecific(string relativePath, bool expected)
    {
        using var repository = new TemporaryRepository();
        var path = Path.Combine(repository.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(expected, SemanticRefreshPathPolicy.IsIgnoredGeneratedEditorConfig(repository.Root, path));
        Assert.Equal(expected, SemanticRefreshPathPolicy.IsIgnoredGeneratedEditorConfig(repository.Root, path.Replace('\\', '/')));
    }

    /// <summary>Explicit user-owned configuration beneath obj remains watched and refreshes.</summary>
    [Theory]
    [InlineData("obj/custom.editorconfig")]
    [InlineData("obj/.globalconfig")]
    [InlineData("src/App.GeneratedMSBuildEditorConfig.editorconfig")]
    public static async Task UserOwnedAnalyzerConfigStillRefreshes(string relativePath)
    {
        using var repository = new TemporaryRepository();
        var path = Path.Combine(repository.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Directory.CreateDirectory(Path.GetDirectoryName(path)!).FullName;
        await File.WriteAllTextAsync(path, "root = true", TestContext.Current.CancellationToken);
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        backend.AddAnalyzerConfigDocument(repository.WorkspaceId, path);
        var watcherRoots = new List<string>();
        FileSystemWatcher CreateWatcher(string root)
        {
            watcherRoots.Add(root);
            return new FileSystemWatcher(root);
        }

        await using var coordinator = CreateCoordinator(backend, events, watchFileSystem: true, watcherFactory: CreateWatcher);
        await coordinator.BindAsync(repository.CreateRequest(), TestContext.Current.CancellationToken);
        Assert.Contains(directory, watcherRoots);
        await File.WriteAllTextAsync(path, "root = false", TestContext.Current.CancellationToken);
        await coordinator.ObserveChangeAsync(new(repository.SessionId, path, SemanticFileChangeKind.Changed), TestContext.Current.CancellationToken);
        var refreshed = await coordinator.EnsureCurrentAsync(repository.SessionId, SemanticRefreshReason.UserAdmission, TestContext.Current.CancellationToken);
        Assert.True(refreshed.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Full, refreshed.Mode);
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

    /// <summary>A production watcher observes a physical source edit without an injected hint.</summary>
    [Fact]
    public static async Task BindAsync_ProductionWatcherDetectsPhysicalSourceEdit()
    {
        using var repository = new TemporaryRepository(useNestedSourcePath: true);
        await using var events = new DomainEventStream();
        var completed = new TaskCompletionSource<SemanticRefreshCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticRefreshCompleted refreshCompleted)
            {
                completed.TrySetResult(refreshCompleted);
            }

            return Task.CompletedTask;
        });
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events, watchFileSystem: true);
        await coordinator.BindAsync(repository.CreateRequest());

        await File.WriteAllTextAsync(repository.SourcePath, "public class Watched { }");
        var observed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SemanticRefreshReason.ExternalChange, observed.Reason);
        Assert.Equal(SemanticRefreshMode.Incremental, observed.Mode);
        Assert.Equal(1, observed.ChangedFileCount);
        Assert.Equal([Path.GetRelativePath(repository.Root, repository.SourcePath).Replace('\\', '/')], observed.TriggerPaths);
        Assert.Equal(1, backend.RefreshCount);
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
        var result = await externalCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SemanticRefreshReason.ExternalChange, result.Reason);
        Assert.Equal(SemanticRefreshMode.Incremental, result.Mode);
        Assert.Equal(2, backend.RefreshCount);
    }

    /// <summary>A watcher-rebuild failure publishes one correlated bounded refresh failure.</summary>
    [Fact]
    public static async Task ObserveFileSystemWatcherError_RebuildFailurePublishesLifecycleFailure()
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
        await coordinator.BindAsync(repository.CreateRequest());

        coordinator.ObserveFileSystemWatcherError(repository.SessionId);
#pragma warning disable VSTHRD003 // The assertion intentionally observes the shared refresh task.
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission));
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

    /// <summary>Generated C# source beneath build-output directories does not cause semantic refresh churn.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_IgnoresGeneratedSourceDocumentUnderBuildOutput()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var generatedDirectory = Path.Combine(
            repository.Root,
            "src",
            "Example",
            "obj",
            "Debug",
            "net10.0");
        Directory.CreateDirectory(generatedDirectory);
        var generatedSourcePath = Path.Combine(generatedDirectory, "Generated.g.cs");
        await File.WriteAllTextAsync(generatedSourcePath, "public class Generated { }");
        backend.AddSourceDocument(repository.WorkspaceId, generatedSourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());

        await File.WriteAllTextAsync(generatedSourcePath, "public class GeneratedChanged { }");
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            generatedSourcePath,
            SemanticFileChangeKind.Changed));
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.False(result.WasRefreshed);
        Assert.True(coordinator.IsCurrent(repository.SessionId));
        Assert.Equal(0, backend.RefreshCount);
    }

    /// <summary>A loaded source document under an artifact-named source folder still refreshes.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_SourceDocumentUnderArtifactsRefreshes()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var sourceDirectory = Path.Combine(repository.Root, "src", "Artifacts");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "ArtifactFactory.cs");
        await File.WriteAllTextAsync(sourcePath, "public class ArtifactFactory { }");
        backend.AddSourceDocument(repository.WorkspaceId, sourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());

        await File.WriteAllTextAsync(sourcePath, "public class ArtifactFactoryChanged { }");
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            sourcePath,
            SemanticFileChangeKind.Changed));
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Incremental, result.Mode);
        Assert.Equal(1, backend.RefreshCount);
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

    /// <summary>An atomic replacement of an existing loaded source remains incremental.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_ExistingLoadedSourceCreatedHintIsIncremental()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await File.WriteAllTextAsync(repository.SourcePath, "public class AtomicallyReplaced { }");

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            repository.SourcePath,
            SemanticFileChangeKind.Created));
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Incremental, result.Mode);
        Assert.Equal(1, backend.RefreshCount);
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
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>A change observed during refresh converges before admission is released.</summary>
    [Fact]
    public static async Task EnsureCurrentAsync_ChangeDuringRefreshConvergesBeforeRelease()
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
        await File.WriteAllTextAsync(repository.SourcePath, "public class First { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var ensure = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await firstBarrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await File.WriteAllTextAsync(repository.SourcePath, "public class Second { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        Assert.Empty(observed.OfType<SemanticRefreshCompleted>());
        firstBarrier.Release.TrySetResult();
        await secondBarrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(observed.OfType<SemanticRefreshCompleted>());
        secondBarrier.Release.TrySetResult();
        var result = await ensure;

        Assert.Equal(2, backend.RefreshCount);
        Assert.Equal(2, result.DirtyVersion);
        Assert.Equal(result.DirtyVersion, result.AppliedVersion);
        Assert.Single(observed.OfType<SemanticRefreshStarted>());
        var completed = Assert.Single(observed.OfType<SemanticRefreshCompleted>());
        Assert.Equal(completed.DirtyVersion, completed.AppliedVersion);
        Assert.Equal(result.RefreshId, completed.RefreshId);
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
        await firstBarrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await File.WriteAllTextAsync(repository.SolutionPath, "external graph change");
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            repository.SolutionPath,
            SemanticFileChangeKind.Changed,
            SemanticRefreshReason.ExternalChange));
        firstBarrier.Release.TrySetResult();
        await secondBarrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>A manual request joining incremental work adds one shared full refresh.</summary>
    [Fact]
    public static async Task ForceRefreshAsync_JoiningIncrementalAddsOneSharedFullCycle()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var barrier = backend.BlockNextRefresh();
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await File.WriteAllTextAsync(repository.SourcePath, "public class Changed { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var admission = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstForce = coordinator.ForceRefreshAsync(repository.SessionId);
        var secondForce = coordinator.ForceRefreshAsync(repository.SessionId);
        barrier.Release.TrySetResult();
        await admission;
        var results = await Task.WhenAll(firstForce, secondForce);

        Assert.Equal(
            [SemanticRefreshMode.Incremental, SemanticRefreshMode.Full],
            backend.Modes);
        Assert.Equal(results[0].RefreshId, results[1].RefreshId);
        Assert.All(results, result => Assert.Equal(SemanticRefreshReason.Manual, result.Reason));
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
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>An unchanged loaded document notification is suppressed as a no-op.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_UnchangedLoadedDocumentSuppressesRefresh()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());

        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(0, backend.RefreshCount);
        Assert.False(result.WasRefreshed);
        Assert.Equal(result.DirtyVersion, result.AppliedVersion);
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
        reader.BeforeRead = async token =>
        {
            reading.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var completion = coordinator.CompleteExpectedWritesAsync(
            Assert.IsType<SemanticHostMutationRegistration>(registration),
            ["Source.cs"]);
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
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

        Assert.Equal(SemanticRefreshReason.HostMutation, (await completed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Reason);
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

    /// <summary>A host-created wildcard candidate forces membership reevaluation without a watcher event.</summary>
    [Fact]
    public static async Task CompleteExpectedWritesAsync_HostCreatedCustomFileForcesFullRefresh()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        const string content = "semantic input";
        var createdPath = Path.Combine(repository.Root, "generated.input");
        var registration = Assert.IsType<SemanticHostMutationRegistration>(
            await coordinator.RegisterExpectedWritesAsync(
                repository.SessionId,
                repository.WorkspaceId,
                MutationSetId.New(),
                [new SemanticHostWriteExpectation(
                    "generated.input",
                    Hash(content),
                    AllowMissingTransition: false,
                    ExistedBefore: false)]));

        await File.WriteAllTextAsync(createdPath, content);
        await coordinator.CompleteExpectedWritesAsync(registration, ["generated.input"]);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        Assert.Equal(SemanticRefreshReason.HostMutation, result.Reason);
    }

    /// <summary>A delayed create echo survives an intervening full refresh without hiding later content.</summary>
    [Fact]
    public static async Task CompleteExpectedWritesAsync_DelayedCreatedEchoSuppressesOnlyMatchingIdentity()
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
        const string hostContent = "host semantic input";
        var createdPath = Path.Combine(repository.Root, "generated.input");
        var registration = Assert.IsType<SemanticHostMutationRegistration>(
            await coordinator.RegisterExpectedWritesAsync(
                repository.SessionId,
                repository.WorkspaceId,
                MutationSetId.New(),
                [new SemanticHostWriteExpectation(
                    "generated.input",
                    Hash(hostContent),
                    AllowMissingTransition: false,
                    ExistedBefore: false)]));

        await File.WriteAllTextAsync(createdPath, hostContent);
        await coordinator.CompleteExpectedWritesAsync(registration, ["generated.input"]);
        var hostResult = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        Assert.Equal(SemanticRefreshReason.HostMutation, hostResult.Reason);
        Assert.Equal(1, backend.RefreshCount);

        var unrelatedFull = await coordinator.ForceRefreshAsync(repository.SessionId);
        Assert.Equal(SemanticRefreshMode.Full, unrelatedFull.Mode);
        Assert.Equal(2, backend.RefreshCount);

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            createdPath,
            SemanticFileChangeKind.Created,
            SemanticRefreshReason.ExternalChange));
        var echoResult = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        Assert.False(echoResult.WasRefreshed);
        Assert.Equal(2, backend.RefreshCount);

        while (observed.TryDequeue(out _))
        {
        }

        await File.WriteAllTextAsync(createdPath, "different external content");
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            createdPath,
            SemanticFileChangeKind.Created,
            SemanticRefreshReason.ExternalChange));
        var externalResult = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(externalResult.WasRefreshed);
        Assert.Equal(SemanticRefreshReason.ExternalChange, externalResult.Reason);
        Assert.Equal(3, backend.RefreshCount);
        Assert.Contains(
            observed,
            domainEvent => domainEvent is SemanticRefreshStarted started
                && started.Reason == SemanticRefreshReason.ExternalChange);
    }

    /// <summary>A delayed delete echo survives an intervening full refresh without hiding recreation.</summary>
    [Fact]
    public static async Task CompleteExpectedWritesAsync_DelayedDeletedEchoSuppressesOnlyMatchingTombstone()
    {
        using var repository = new TemporaryRepository();
        var deletedPath = Path.Combine(repository.Root, "removed.input");
        await File.WriteAllTextAsync(deletedPath, "original semantic input");
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
        var registration = Assert.IsType<SemanticHostMutationRegistration>(
            await coordinator.RegisterExpectedWritesAsync(
                repository.SessionId,
                repository.WorkspaceId,
                MutationSetId.New(),
                [new SemanticHostWriteExpectation(
                    "removed.input",
                    "missing",
                    AllowMissingTransition: true,
                    ExistedBefore: true)]));

        File.Delete(deletedPath);
        await coordinator.CompleteExpectedWritesAsync(registration, ["removed.input"]);
        var hostResult = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        Assert.Equal(SemanticRefreshReason.HostMutation, hostResult.Reason);
        Assert.Equal(1, backend.RefreshCount);

        var unrelatedFull = await coordinator.ForceRefreshAsync(repository.SessionId);
        Assert.Equal(SemanticRefreshMode.Full, unrelatedFull.Mode);
        Assert.Equal(2, backend.RefreshCount);

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            deletedPath,
            SemanticFileChangeKind.Deleted,
            SemanticRefreshReason.ExternalChange));
        var echoResult = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        Assert.False(echoResult.WasRefreshed);
        Assert.Equal(2, backend.RefreshCount);

        while (observed.TryDequeue(out _))
        {
        }

        await File.WriteAllTextAsync(deletedPath, "external recreation");
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            deletedPath,
            SemanticFileChangeKind.Created,
            SemanticRefreshReason.ExternalChange));
        var externalResult = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(externalResult.WasRefreshed);
        Assert.Equal(SemanticRefreshReason.ExternalChange, externalResult.Reason);
        Assert.Equal(3, backend.RefreshCount);
        Assert.Contains(
            observed,
            domainEvent => domainEvent is SemanticRefreshStarted started
                && started.Reason == SemanticRefreshReason.ExternalChange);
    }

    /// <summary>A recent host identity expires instead of suppressing later indistinguishable edits forever.</summary>
    [Fact]
    public static async Task CompleteExpectedWritesAsync_RecentHostEchoIdentityExpires()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var timeProvider = new AdjustableTimeProvider();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            recentHostEchoLifetime: TimeSpan.FromMilliseconds(20),
            timeProvider: timeProvider);
        await coordinator.BindAsync(repository.CreateRequest());
        const string hostContent = "expiring host semantic input";
        var createdPath = Path.Combine(repository.Root, "expiring.input");
        var registration = Assert.IsType<SemanticHostMutationRegistration>(
            await coordinator.RegisterExpectedWritesAsync(
                repository.SessionId,
                repository.WorkspaceId,
                MutationSetId.New(),
                [new SemanticHostWriteExpectation(
                    "expiring.input",
                    Hash(hostContent),
                    AllowMissingTransition: false,
                    ExistedBefore: false)]));

        await File.WriteAllTextAsync(createdPath, hostContent);
        await coordinator.CompleteExpectedWritesAsync(registration, ["expiring.input"]);
        await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await coordinator.ForceRefreshAsync(repository.SessionId);
        timeProvider.Advance(TimeSpan.FromMilliseconds(20));

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            createdPath,
            SemanticFileChangeKind.Created,
            SemanticRefreshReason.ExternalChange));
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshReason.ExternalChange, result.Reason);
        Assert.Equal(3, backend.RefreshCount);
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

    /// <summary>A graph change during initial loading is retained for a full refresh.</summary>
    [Fact]
    public static async Task BeginBindingAsync_GraphChangeDuringInitialLoadIsRetainedForFullRefresh()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        var request = repository.CreateRequest();
        var generation = await coordinator.BeginBindingAsync(request);

        await File.AppendAllTextAsync(repository.SolutionPath, "changed");
        await coordinator.CompleteBindingAsync(request, generation, CancellationToken.None);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        Assert.True(result.WasRefreshed);
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

    /// <summary>Unchanged membership and ignored build output do not request startup recovery.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("obj/Generated.g.cs")]
    [InlineData("bin/Generated.cs")]
    [InlineData(".git/Excluded.cs")]
    [InlineData("notes.txt")]
    public static async Task BeginBindingAsync_UnchangedSourceMembershipDoesNotRefresh(string? relativePath)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        var request = repository.CreateRequest();
        var generation = await coordinator.BeginBindingAsync(request, cancellationToken);
        if (relativePath is not null)
        {
            var path = Path.Combine(repository.Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "ignored", cancellationToken);
        }

        await coordinator.CompleteBindingAsync(request, generation, cancellationToken);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission,
            cancellationToken);

        Assert.False(result.WasRefreshed);
        Assert.Equal(0, backend.RefreshCount);
        Assert.True(coordinator.IsCurrent(repository.SessionId));
    }

    /// <summary>A reference change during initial loading is detected without a watcher hint.</summary>
    [Fact]
    public static async Task BeginBindingAsync_ReferenceChangeDuringInitialLoadIsRetainedForFullRefresh()
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

        await File.WriteAllBytesAsync(referencePath, [3, 2, 1]);
        await coordinator.CompleteBindingAsync(request, generation, CancellationToken.None);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        Assert.True(result.WasRefreshed);
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

    /// <summary>Additional, analyzer, graph, reference, and membership inputs force full refresh.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_AdditionalGlobalConfigAndMembershipChangesAreFull()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var additionalPath = Path.Combine(repository.Root, "settings.json");
        var analyzerConfigPath = Path.Combine(repository.Root, "analysis.rules");
        var globalConfigPath = Path.Combine(repository.Root, ".globalconfig");
        var analyzerReferencePath = Path.Combine(repository.Root, "Analyzer.dll");
        var directoryPath = Path.Combine(repository.Root, "Generated");
        await File.WriteAllTextAsync(additionalPath, "{}");
        await File.WriteAllTextAsync(analyzerConfigPath, "root = true");
        await File.WriteAllTextAsync(globalConfigPath, "is_global = true");
        await File.WriteAllTextAsync(analyzerReferencePath, "synthetic analyzer");
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        backend.AddAdditionalDocument(repository.WorkspaceId, additionalPath);
        backend.AddAnalyzerConfigDocument(repository.WorkspaceId, analyzerConfigPath);
        backend.AddFullReloadInput(repository.WorkspaceId, analyzerReferencePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());

        foreach (var change in new[]
        {
            new SemanticFileChange(
                repository.SessionId,
                additionalPath,
                SemanticFileChangeKind.Changed),
            new SemanticFileChange(
                repository.SessionId,
                analyzerConfigPath,
                SemanticFileChangeKind.Changed),
            new SemanticFileChange(
                repository.SessionId,
                globalConfigPath,
                SemanticFileChangeKind.Changed),
            new SemanticFileChange(
                repository.SessionId,
                analyzerReferencePath,
                SemanticFileChangeKind.Changed),
            new SemanticFileChange(
                repository.SessionId,
                directoryPath,
                SemanticFileChangeKind.Created),
        })
        {
            if (change.Path == directoryPath)
            {
                Directory.CreateDirectory(directoryPath);
            }
            else
            {
                await File.AppendAllTextAsync(change.Path, Environment.NewLine + "changed");
            }

            await coordinator.ObserveChangeAsync(change);
            var result = await coordinator.EnsureCurrentAsync(
                repository.SessionId,
                SemanticRefreshReason.UserAdmission);
            Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        }

        Assert.Equal(
            [
                SemanticRefreshMode.Full,
                SemanticRefreshMode.Full,
                SemanticRefreshMode.Full,
                SemanticRefreshMode.Full,
                SemanticRefreshMode.Full,
            ],
            backend.Modes);
    }

    /// <summary>A custom-extension lifecycle change remains conservatively full-refresh eligible.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_CustomExtensionLifecycleChangeForcesFullRefresh()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        var notesPath = Path.Combine(repository.Root, "notes.md");
        await File.WriteAllTextAsync(notesPath, "not semantic input");

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            notesPath,
            SemanticFileChangeKind.Created));
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.True(result.WasRefreshed);
        Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        Assert.Equal(1, backend.RefreshCount);
    }

    /// <summary>A binary full-reload input uses its raw-byte content identity.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_BinaryFullReloadInputUsesRawIdentity()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var analyzerPath = Path.Combine(repository.Root, "Analyzer.dll");
        await File.WriteAllBytesAsync(analyzerPath, [0xFF]);
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        backend.AddFullReloadInput(repository.WorkspaceId, analyzerPath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            analyzerPath,
            SemanticFileChangeKind.Changed));
        var first = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await File.WriteAllBytesAsync(analyzerPath, [0xFE]);
        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            analyzerPath,
            SemanticFileChangeKind.Changed));
        var second = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshMode.Full, first.Mode);
        Assert.Equal(SemanticRefreshMode.Full, second.Mode);
        Assert.Equal(2, backend.RefreshCount);
    }

    /// <summary>A full refresh replaces source identities so reverting to a pre-refresh hash is not suppressed.</summary>
    [Fact]
    public static async Task ForceRefreshAsync_RebuildsLoadedDocumentIdentities()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        const string changed = "public class ChangedOutsideWatcher { }";
        await File.WriteAllTextAsync(repository.SourcePath, changed);
        backend.SetLoadedDocumentText(repository.WorkspaceId, repository.SourcePath, changed);

        var forced = await coordinator.ForceRefreshAsync(repository.SessionId);
        await File.WriteAllTextAsync(repository.SourcePath, "public class Initial { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var reverted = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.Equal(SemanticRefreshMode.Full, forced.Mode);
        Assert.True(reverted.WasRefreshed);
        Assert.Equal(2, backend.RefreshCount);
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

    /// <summary>Changes completed while publication waits are included in the first reload's baseline.</summary>
    [Fact]
    public static async Task ForceRefreshAsync_CapturesBaselineAfterPublicationAdmission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        var publicationGate = new TestSemanticRefreshPublicationGate();
        await using var coordinator = CreateCoordinator(backend, events, publicationGate: publicationGate);
        await coordinator.BindAsync(repository.CreateRequest(), cancellationToken);

        var force = coordinator.ForceRefreshAsync(repository.SessionId, cancellationToken);
        await publicationGate.Entered.Task.WaitAsync(cancellationToken);
        await File.WriteAllTextAsync(repository.SolutionPath, "updated during active run", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "Added.cs"), "class Added { }", cancellationToken);
        publicationGate.Release.TrySetResult();
        var result = await force;

        Assert.True(result.WasRefreshed);
        Assert.Equal(result.DirtyVersion, result.AppliedVersion);
        Assert.Equal(1, backend.RefreshCount);
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
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>A build-output reference remains excluded from semantic refresh.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_BuildOutputReferenceIsIgnored()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var outputDirectory = Path.Combine(repository.Root, "bin");
        Directory.CreateDirectory(outputDirectory);
        var analyzerPath = Path.Combine(outputDirectory, "Analyzer.dll");
        await File.WriteAllBytesAsync(analyzerPath, [1, 2, 3]);
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        backend.AddFullReloadInput(repository.WorkspaceId, analyzerPath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            analyzerPath,
            SemanticFileChangeKind.Changed));
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.False(result.WasRefreshed);
        Assert.Equal(0, backend.RefreshCount);
    }

    /// <summary>Known generated and local-tool directories do not dirty semantic state.</summary>
    [Theory]
    [InlineData(".codegraph")]
    [InlineData(".idea")]
    [InlineData(".inbox")]
    [InlineData(".vs")]
    [InlineData(".vscode")]
    [InlineData("artifacts")]
    [InlineData("node_modules")]
    [InlineData("TestResults")]
    public static async Task ObserveChangeAsync_KnownGeneratedDirectoryIsIgnored(string directoryName)
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        var ignoredDirectory = Path.Combine(repository.Root, directoryName);
        Directory.CreateDirectory(ignoredDirectory);
        var graphPath = Path.Combine(ignoredDirectory, "Directory.Build.props");
        await File.WriteAllTextAsync(graphPath, "<Project />");

        await coordinator.ObserveChangeAsync(new SemanticFileChange(
            repository.SessionId,
            graphPath,
            SemanticFileChangeKind.Created));
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.False(result.WasRefreshed);
        Assert.Equal(0, backend.RefreshCount);
    }

    /// <summary>Ignore rules apply inside the repository and not to similarly named ancestor directories.</summary>
    [Fact]
    public static async Task ObserveChangeAsync_RepositoryUnderObjAncestorRetainsGraphInputs()
    {
        var container = Path.Combine(
            Path.GetTempPath(),
            "threadsmith-semantic-refresh",
            Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(container, "obj", "repository");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            var sourcePath = Path.Combine(repositoryPath, "Source.cs");
            var solutionPath = Path.Combine(repositoryPath, "Repository.sln");
            var projectPath = Path.Combine(repositoryPath, "Repository.csproj");
            await File.WriteAllTextAsync(sourcePath, "public class Initial { }");
            await File.WriteAllTextAsync(solutionPath, string.Empty);
            await File.WriteAllTextAsync(projectPath, "<Project />");
            await using var events = new DomainEventStream();
            var backend = new TestSemanticRefreshBackend(workspaceId, sourcePath);
            await using var coordinator = CreateCoordinator(backend, events);
            await coordinator.BindAsync(new SemanticLoadRequest(
                sessionId,
                workspaceId,
                repositoryPath,
                solutionPath,
                RepositoryTrustLevel.TrustedBuild));

            await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await coordinator.ObserveChangeAsync(new SemanticFileChange(
                sessionId,
                projectPath,
                SemanticFileChangeKind.Changed));
            var result = await coordinator.EnsureCurrentAsync(
                sessionId,
                SemanticRefreshReason.UserAdmission);

            Assert.True(result.WasRefreshed);
            Assert.Equal(SemanticRefreshMode.Full, result.Mode);
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
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

    /// <summary>Ignored artifact caches and temporary files stay outside the startup snapshot.</summary>
    [Fact]
    public static async Task BeginBindingAsync_IgnoredArtifactCachesAreExcludedFromStartupSnapshot()
    {
        using var repository = new TemporaryRepository();
        await using var events = new DomainEventStream();
        var inboxDirectory = Path.Combine(repository.Root, ".inbox");
        Directory.CreateDirectory(inboxDirectory);
        var ignoredPaths = new[]
        {
            Path.Combine(inboxDirectory, "Cached.dll"),
            Path.Combine(repository.Root, "artifacts", "Release.dll"),
            Path.Combine(repository.Root, "~Analyzer.dll"),
            Path.Combine(repository.Root, "~Directory.Build.props"),
        };

        var artifactsDirectory = Path.Combine(repository.Root, "artifacts");
        Directory.CreateDirectory(artifactsDirectory);
        foreach (var ignoredPath in ignoredPaths)
        {
            await File.WriteAllBytesAsync(ignoredPath, [1]);
        }

        var fileSnapshotReader = new TestSemanticFileSnapshotReader();
        var backend = new TestSemanticRefreshBackend(repository.WorkspaceId, repository.SourcePath);
        await using var coordinator = CreateCoordinator(
            backend,
            events,
            fileSnapshotReader: fileSnapshotReader);
        var request = repository.CreateRequest();

        var generation = await coordinator.BeginBindingAsync(request);
        await coordinator.CompleteBindingAsync(request, generation, CancellationToken.None);
        var result = await coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);

        Assert.False(result.WasRefreshed);
        Assert.Equal(0, backend.RefreshCount);
        Assert.DoesNotContain(
            fileSnapshotReader.ReadPaths,
            path => ignoredPaths.Contains(path, StringComparer.OrdinalIgnoreCase));
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
            var externalDirectory = Path.Combine(container, "external");
            var linkedDirectory = Path.Combine(repositoryPath, "linked");
            var linkedAdditional = Path.Combine(linkedDirectory, "linked.json");
            var prohibitedAdditional = Path.Combine(prohibitedPath, "hidden.json");
            var prohibitedConfig = Path.Combine(prohibitedPath, ".editorconfig");
            Directory.CreateDirectory(externalDirectory);
            await File.WriteAllTextAsync(Path.Combine(externalDirectory, "linked.json"), "linked");
            var linkCreated = false;
            try
            {
                Directory.CreateSymbolicLink(linkedDirectory, externalDirectory);
                linkCreated = true;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                // Symlink creation is privilege- and platform-dependent; confinement remains covered above.
            }

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <AdditionalFiles Include="visible.json" />
                    <AdditionalFiles Include="..\outside.json" />
                    <AdditionalFiles Include="secret\hidden.json" />
                    <AdditionalFiles Include="linked\linked.json" />
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
                NullLogger<SemanticEngine>.Instance);
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
            if (linkCreated)
            {
                Assert.DoesNotContain(linkedAdditional, inventory.AdditionalDocuments);
            }

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
        await firstCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        await aliasCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
        await publicationGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, loader.LoadCount);
        Assert.False(coordinator.TryAdmitCurrent(
            repository.SessionId,
            repository.WorkspaceId,
            () => true));
        publicationGate.Release.TrySetResult();
        var load = await loader.WaitForStartAsync(aliasSessionId);
        load.Release.TrySetResult();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>Unbinding the last alias fences an active refresh before asynchronous disposal completes.</summary>
    [Fact]
    public static async Task UnbindAsync_LastAliasSuppressesObsoleteRefreshEvents()
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
        var barrier = backend.BlockNextRefresh();
        await using var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await File.WriteAllTextAsync(repository.SourcePath, "public class Obsolete { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var ensure = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        while (observed.TryDequeue(out _))
        {
        }

        await coordinator.UnbindAsync(repository.SessionId);
#pragma warning disable VSTHRD003 // The assertion intentionally observes a task started above.
        await Assert.ThrowsAnyAsync<Exception>(() => ensure);
#pragma warning restore VSTHRD003
        Assert.Empty(observed.OfType<SemanticRefreshStarted>());
        Assert.Empty(observed.OfType<SemanticRefreshCompleted>());
        Assert.Empty(observed.OfType<SemanticRefreshFailed>());
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

    /// <summary>Coordinator shutdown detaches every alias before a non-cooperative refresh can finish.</summary>
    [Fact]
    public static async Task DisposeAsync_ActiveRefreshSuppressesObsoleteTerminalEvents()
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
        var barrier = backend.BlockNextRefresh(honorCancellation: false);
        var coordinator = CreateCoordinator(backend, events);
        await coordinator.BindAsync(repository.CreateRequest());
        await File.WriteAllTextAsync(repository.SourcePath, "public class ShuttingDown { }");
        await coordinator.ObserveChangeAsync(repository.CreateChange());
        var ensure = coordinator.EnsureCurrentAsync(
            repository.SessionId,
            SemanticRefreshReason.UserAdmission);
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        while (observed.TryDequeue(out _))
        {
        }

        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.False(coordinator.TryGetWorkspaceId(repository.SessionId, out _));
        barrier.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning disable VSTHRD003 // The assertion intentionally observes a task started above.
        await Assert.ThrowsAnyAsync<Exception>(() => ensure);
#pragma warning restore VSTHRD003

        Assert.Empty(observed.OfType<SemanticRefreshStarted>());
        Assert.Empty(observed.OfType<SemanticRefreshCompleted>());
        Assert.Empty(observed.OfType<SemanticRefreshFailed>());
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
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.DisposeAsync();
        barrier.Release.Set();
        await observation.WaitAsync(TimeSpan.FromSeconds(5));
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
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        barrier.Release.Set();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await observation.WaitAsync(TimeSpan.FromSeconds(5)));
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
        TimeProvider? timeProvider = null)
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
            resourceLimits: resourceLimits);
    }

    private static string Hash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
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
                if (!barrier.Release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Synthetic inventory disposal was not released.");
                }

                throw new ObjectDisposedException(nameof(SemanticEngineRegistry));
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

        private static StringComparer PathComparer => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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
                    await barrier.Release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
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

        public Func<CancellationToken, Task>? BeforeRead { get; set; }

        public async Task<SemanticFileSnapshot> ReadAsync(
            string path,
            bool readAsBinary,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            _readPaths.Enqueue(Path.GetFullPath(path));
            if (BeforeRead is { } beforeRead)
            {
                await beforeRead(cancellationToken);
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
            return started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class CallbackTimeProvider : TimeProvider
    {
        private readonly Action _onDelay;

        public CallbackTimeProvider(Action onDelay)
        {
            _onDelay = onDelay;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _onDelay();
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private long _utcOffsetTicks;

        public override DateTimeOffset GetUtcNow()
        {
            return TimeProvider.System.GetUtcNow().AddTicks(Volatile.Read(ref _utcOffsetTicks));
        }

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _utcOffsetTicks, elapsed.Ticks);
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
