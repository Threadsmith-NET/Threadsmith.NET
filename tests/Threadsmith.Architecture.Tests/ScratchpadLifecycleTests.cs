namespace Threadsmith.Architecture.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks destructive scratchpad lifecycle boundaries with real filesystem state.</summary>
public sealed class ScratchpadLifecycleTests
{
    /// <summary>Startup creates an in-repository root, clears every existing child, and warns when unignored.</summary>
    [Fact]
    public async Task Startup_CreatesClearsAndWarnsForUnignoredRepositoryScratchpad()
    {
        await using var fixture = await Fixture.CreateAsync(".scratch");
        var scratchpad = Path.Combine(fixture.Repository, ".scratch");
        Directory.CreateDirectory(Path.Combine(scratchpad, "nested"));
        var readOnly = Path.Combine(scratchpad, "nested", ".hidden.log");
        await File.WriteAllTextAsync(readOnly, "stale", TestContext.Current.CancellationToken);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Lifecycle.Current.IsActive);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchpad));
        Assert.Contains(warnings, warning => warning.Contains(".gitignore", StringComparison.Ordinal));
    }

    /// <summary>The dedicated Threadsmith scratchpad may be cleared without touching adjacent state.</summary>
    [Fact]
    public async Task Startup_ThreadsmithScratchpadPreservesConfigurationAndOtherState()
    {
        await using var fixture = await Fixture.CreateAsync("./.threadsmith/.scratchpad");
        var threadsmith = Path.Combine(fixture.Repository, ".threadsmith");
        var scratchpad = Path.Combine(threadsmith, ".scratchpad");
        Directory.CreateDirectory(scratchpad);
        var stale = Path.Combine(scratchpad, "stale.log");
        var state = Path.Combine(threadsmith, "threadsmith.db");
        await File.WriteAllTextAsync(stale, "remove", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(state, "preserve", TestContext.Current.CancellationToken);

        _ = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Lifecycle.Current.IsActive);
        Assert.Equal(".threadsmith/.scratchpad", fixture.Lifecycle.Current.ModelPath);
        Assert.True(Directory.Exists(scratchpad));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(Path.Combine(threadsmith, "config.json")));
        Assert.Equal("preserve", await File.ReadAllTextAsync(state, TestContext.Current.CancellationToken));
    }

    /// <summary>Other Threadsmith state paths cannot become cleanup roots.</summary>
    [Theory]
    [InlineData(".threadsmith")]
    [InlineData(".threadsmith/secrets")]
    [InlineData(".threadsmith/artifacts")]
    public async Task Startup_OtherThreadsmithPathsRemainProtected(string configuredPath)
    {
        await using var fixture = await Fixture.CreateAsync(configuredPath);
        var configuration = Path.Combine(fixture.Repository, ".threadsmith", "config.json");

        var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Lifecycle.Current.IsActive);
        Assert.Contains(warnings, warning => warning.Contains("protected Threadsmith", StringComparison.Ordinal));
        Assert.True(File.Exists(configuration));
    }

    /// <summary>A session boundary removes all transient content before publishing the next capability.</summary>
    [Fact]
    public async Task NewSession_ClearsContentsAndReactivates()
    {
        await using var fixture = await Fixture.CreateAsync(".scratch");
        _ = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);
        var scratchpad = Path.Combine(fixture.Repository, ".scratch");
        await File.WriteAllTextAsync(
            Path.Combine(scratchpad, "iteration.json"),
            "{}",
            TestContext.Current.CancellationToken);
        var previousGeneration = fixture.Lifecycle.Current.Generation;

        await fixture.Lifecycle.BeginNewSessionAsync(TestContext.Current.CancellationToken);

        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchpad));
        Assert.True(fixture.Lifecycle.Current.IsActive);
        Assert.True(fixture.Lifecycle.Current.Generation > previousGeneration);
    }

    /// <summary>Reopening the already-bound repository preserves the active session's scratchpad.</summary>
    [Fact]
    public async Task SameRepositoryBinding_PreservesCapabilityAndContents()
    {
        await using var fixture = await Fixture.CreateAsync(".scratch");
        _ = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);
        var scratchpad = Path.Combine(fixture.Repository, ".scratch");
        var note = Path.Combine(scratchpad, "iteration.log");
        await File.WriteAllTextAsync(note, "current session", TestContext.Current.CancellationToken);
        var generation = fixture.Lifecycle.Current.Generation;

        await fixture.Lifecycle.BindRepositoryAsync(fixture.Repository, TestContext.Current.CancellationToken);

        Assert.True(fixture.Lifecycle.Current.IsActive);
        Assert.Equal(generation, fixture.Lifecycle.Current.Generation);
        Assert.Equal("current session", await File.ReadAllTextAsync(note, TestContext.Current.CancellationToken));
    }

    /// <summary>An existing external root is checked from its filesystem anchor before cleanup.</summary>
    [Fact]
    public async Task ExistingExternalScratchpad_ClearsContents()
    {
        await using var fixture = await Fixture.CreateAsync("../external", userConfigured: true);
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "external")).FullName;
        var stale = Path.Combine(external, "stale.log");
        await File.WriteAllTextAsync(stale, "stale", TestContext.Current.CancellationToken);

        var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(warnings);
        Assert.True(fixture.Lifecycle.Current.IsActive);
        Assert.False(File.Exists(stale));
    }

    /// <summary>A linked external root must never cause cleanup of its target.</summary>
    [Fact]
    public async Task LinkedExternalScratchpad_DoesNotDeleteTargetContents()
    {
        await using var fixture = await Fixture.CreateAsync("../external", userConfigured: true);
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "target")).FullName;
        var protectedFile = Path.Combine(target, "keep.log");
        await File.WriteAllTextAsync(protectedFile, "keep", TestContext.Current.CancellationToken);
        var link = Path.Combine(fixture.Root, "external");
        await CreateDirectoryLinkAsync(link, target);
        try
        {
            var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

            Assert.False(fixture.Lifecycle.Current.IsActive);
            Assert.True(File.Exists(protectedFile));
            Assert.Contains(warnings, warning => warning.Contains("symbolic links", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>Repository configuration cannot target an external directory, even when it already exists.</summary>
    [Fact]
    public async Task RepositoryConfiguration_RejectsExternalDirectory()
    {
        var external = Path.Combine(Path.GetTempPath(), "threadsmith-scratch-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            await using var fixture = await Fixture.CreateAsync(external);

            var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

            Assert.False(fixture.Lifecycle.Current.IsActive);
            Assert.Equal(ScratchpadDisabledReason.InvalidConfiguration, fixture.Lifecycle.Current.DisabledReason);
            Assert.Contains(warnings, warning => warning.Contains("inside the repository", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    /// <summary>Scratchpad is disabled when write_file remains approval-gated.</summary>
    [Fact]
    public async Task ApprovalRequiredWriteFile_DisablesScratchpad()
    {
        await using var fixture = await Fixture.CreateAsync(".scratch", requireWriteApproval: true);

        var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Lifecycle.Current.IsActive);
        Assert.Equal(ScratchpadDisabledReason.WriteFileUnavailable, fixture.Lifecycle.Current.DisabledReason);
        Assert.Contains(warnings, warning => warning.Contains("write_file", StringComparison.Ordinal));
    }

    /// <summary>A linked in-repository ancestor is rejected before a missing child can be created externally.</summary>
    [Fact]
    public async Task MissingDirectory_LinkedAncestorDoesNotCreateOutsideRepository()
    {
        await using var fixture = await Fixture.CreateAsync("linked/scratch");
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "external")).FullName;
        var link = Path.Combine(fixture.Repository, "linked");
        await CreateDirectoryLinkAsync(link, external);
        try
        {
            var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

            Assert.False(fixture.Lifecycle.Current.IsActive);
            Assert.False(Directory.Exists(Path.Combine(external, "scratch")));
            Assert.Contains(warnings, warning => warning.Contains("symbolic links", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start junction creation.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                Assert.Skip("Junction creation is unavailable on this host.");
            }

            return;
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, string repository, ScratchpadLifecycle lifecycle)
        {
            Root = root;
            Repository = repository;
            Lifecycle = lifecycle;
        }

        public string Root { get; }

        public string Repository { get; }

        public ScratchpadLifecycle Lifecycle { get; }

        public static async Task<Fixture> CreateAsync(
            string configuredPath,
            bool requireWriteApproval = false,
            bool userConfigured = false)
        {
            var root = Directory.CreateDirectory(Path.Combine(
                PhysicalTemporaryPath(),
                "threadsmith-scratch-lifecycle-" + Guid.NewGuid().ToString("N"))).FullName;
            var repository = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            var threadsmith = Directory.CreateDirectory(Path.Combine(repository, ".threadsmith")).FullName;
            var machine = Directory.CreateDirectory(Path.Combine(root, "configuration", "machine")).FullName;
            var user = Directory.CreateDirectory(Path.Combine(root, "configuration", "user")).FullName;
            var repositoryConfiguration = Path.Combine(threadsmith, "config.json");
            var configuredFile = userConfigured
                ? Path.Combine(user, "config.json")
                : repositoryConfiguration;
            await File.WriteAllTextAsync(
                configuredFile,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    scratchpad = new { path = configuredPath },
                    tools = requireWriteApproval ? new { requireApproval = new[] { "write_file" } } : null,
                }),
                TestContext.Current.CancellationToken);
            var paths = new ConfigurationPaths
            {
                RepositoryRoot = repository,
                MachineConfiguration = Path.Combine(machine, "config.json"),
                UserConfiguration = Path.Combine(user, "config.json"),
                RepositoryConfigurationDirectory = threadsmith,
                RepositoryConfigurationDirectoryExistedAtStartup = true,
                RepositoryConfiguration = repositoryConfiguration,
                UserProviderCatalog = Path.Combine(user, "providers.json"),
                RepositoryProviderCatalog = Path.Combine(threadsmith, "providers.json"),
                SessionConfiguration = Path.Combine(threadsmith, "session.json"),
                SecretsConfiguration = Path.Combine(threadsmith, "secrets", "config.json"),
            };
            var configuration = ConfigurationBootstrap.Build([], paths);
            var writeConfiguration = new WriteFileConfiguration(configuration, configuration, repository);
            var write = new WriteFileTool(
                writeConfiguration,
                new EmptyConversationStore(),
                TestPromptLoader.Instance);
            var state = new ToolStateManager(
                [write.Definition],
                configuration,
                repositoryConfiguration,
                writeFileConfiguration: writeConfiguration);
            var registry = new ToolRegistry([write], state);
            var processes = new ProcessManager(
                new SecretOutputSanitizer(),
                NullLogger<ProcessManager>.Instance);
            var lifecycle = new ScratchpadLifecycle(
                [],
                configuration,
                paths,
                processes,
                state,
                registry,
                NullLogger<ScratchpadLifecycle>.Instance);
            return new Fixture(root, repository, lifecycle);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)Lifecycle).DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string PhysicalTemporaryPath()
        {
            var path = Path.GetFullPath(Path.GetTempPath());
            var resolved = Path.GetPathRoot(path)!;
            foreach (var segment in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = new DirectoryInfo(Path.Combine(resolved, segment));
                resolved = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
            }

            return Path.TrimEndingDirectorySeparator(resolved);
        }
    }

    private sealed class EmptyConversationStore : IConversationStore
    {
        public Task<ConversationMessage> ArchiveMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ConversationStateSnapshot> GetSnapshotAsync(SessionId sessionId, bool includeBodies = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> RemoveMessageBodiesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ReplaceSummaryAsync(SessionId sessionId, IReadOnlyList<ConversationMemoryItem> items, ConversationSummarySnapshot snapshot, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetModeAsync(SessionId sessionId, ConversationContextMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateMemoryAsync(ConversationMemoryItem item, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
