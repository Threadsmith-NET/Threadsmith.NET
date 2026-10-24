namespace Threadsmith.Architecture.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks destructive scratchpad lifecycle boundaries with real filesystem state.</summary>
public sealed class ScratchpadLifecycleTests
{
    /// <summary>The managed location works from existing project settings without folder grants or a permission file.</summary>
    [Fact]
    public async Task ManagedScratchpad_ProjectPreferenceNeedsNoPermissionSetup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(true);
        var adjacent = Path.Combine(fixture.Repository, ".threadsmith", "keep.txt");
        await File.WriteAllTextAsync(adjacent, "keep", ct);
        _ = await fixture.Lifecycle.StartAsync(ct);
        Assert.True(fixture.Lifecycle.Current.IsActive);
        Assert.Equal(".threadsmith/.scratchpad", fixture.Lifecycle.Current.ModelPath);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "configuration", "user", "repositories")));
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([fixture.Tool]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);
        var invocation = new ToolInvocationContext
        {
            RepositoryPath = fixture.Repository,
            TrustLevel = RepositoryTrustLevel.UntrustedInspection,
            RequestedBy = "model",
            Scratchpad = fixture.Lifecycle.Current,
        };
        var result = await pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "write_file",
                ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new WriteFileInput(fixture.Lifecycle.Current.ModelPath + "/temporary.txt", "temporary")),
                Context = invocation,
            },
            ct);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("temporary", await File.ReadAllTextAsync(Path.Combine(fixture.Lifecycle.Current.RootPath!, "temporary.txt"), ct));
        _ = await fixture.Lifecycle.BeginNewSessionAsync(ct);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Lifecycle.Current.RootPath!));
        Assert.Equal("keep", await File.ReadAllTextAsync(adjacent, ct));
    }

    /// <summary>Absent and false settings leave existing contents untouched.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task ManagedScratchpad_DisabledRemainsOptional(bool? enabled)
    {
        await using var fixture = await Fixture.CreateAsync(enabled);
        if (enabled is null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Repository, ".threadsmith", "config.json"),
                "{}",
                TestContext.Current.CancellationToken);
        }

        _ = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(fixture.Scratchpad));
        var scratchpad = Directory.CreateDirectory(fixture.Scratchpad).FullName;
        var keep = Path.Combine(scratchpad, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep", TestContext.Current.CancellationToken);
        _ = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ScratchpadDisabledReason.NotConfigured, fixture.Lifecycle.Current.DisabledReason);
        Assert.True(File.Exists(keep));
    }

    /// <summary>A link at the managed root cannot redirect cleanup to other files.</summary>
    [Fact]
    public async Task ManagedScratchpad_LinkCannotExpandCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(true);
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "external")).FullName;
        var keep = Path.Combine(external, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep", ct);
        var link = Path.Combine(fixture.Repository, ".threadsmith", ".scratchpad");
        await CreateDirectoryLinkAsync(link, external);
        try
        {
            _ = await fixture.Lifecycle.StartAsync(ct);
            Assert.False(fixture.Lifecycle.Current.IsActive);
            Assert.Equal("keep", await File.ReadAllTextAsync(keep, ct));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>Repository, session, and CLI flags retain ordinary precedence.</summary>
    [Theory]
    [InlineData("repository", true)]
    [InlineData("session", false)]
    [InlineData("cli", true)]
    public async Task ConfiguredScratchpad_PreservesSourcePrecedence(string source, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(
            false,
            userConfigured: true,
            arguments: source == "cli" ? ["--set:scratchpad:enabled=true"] : []);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Repository, ".threadsmith", "config.json"),
            """{"scratchpad":{"enabled":true}}""",
            ct);
        if (source is "session" or "cli")
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Repository, ".threadsmith", "session.json"),
                """{"scratchpad":{"enabled":false}}""",
                ct);
        }

        _ = await fixture.Lifecycle.StartAsync(ct);
        Assert.Equal(expected, fixture.Lifecycle.Current.IsActive);
        Assert.Equal(expected ? ".threadsmith/.scratchpad" : null, fixture.Lifecycle.Current.ModelPath);
    }

    /// <summary>Invalid boolean values fail closed before directory creation.</summary>
    [Fact]
    public async Task InvalidFlag_DoesNotCreateScratchpad()
    {
        await using var fixture = await Fixture.CreateAsync(true, arguments: ["--set:scratchpad:enabled=somewhere"]);
        var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(fixture.Lifecycle.Current.IsActive);
        Assert.False(Directory.Exists(fixture.Scratchpad));
        Assert.Contains(warnings, warning => warning.Contains("true or false", StringComparison.Ordinal));
    }

    /// <summary>Only a positive repository gitignore rule permits startup cleanup.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("negated")]
    [InlineData("exclude")]
    public async Task Startup_RequiresRepositoryGitIgnoreBeforeCleanup(string rule)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(true, ignored: false);
        Directory.CreateDirectory(fixture.Scratchpad);
        var keep = Path.Combine(fixture.Scratchpad, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep", ct);
        if (rule == "negated")
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, ".gitignore"), ".threadsmith/.scratchpad/\n!.threadsmith/.scratchpad/\n", ct);
        }
        else if (rule == "exclude")
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, ".git", "info", "exclude"), ".threadsmith/.scratchpad/\n", ct);
        }

        var warnings = await fixture.Lifecycle.StartAsync(ct);
        Assert.False(fixture.Lifecycle.Current.IsActive);
        Assert.True(File.Exists(keep));
        Assert.Contains(warnings, warning => warning.Contains(".gitignore", StringComparison.Ordinal));
    }

    /// <summary>The dedicated Threadsmith scratchpad may be cleared without touching adjacent state.</summary>
    [Fact]
    public async Task Startup_ThreadsmithScratchpadPreservesConfigurationAndOtherState()
    {
        await using var fixture = await Fixture.CreateAsync(true);
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

    /// <summary>Cleanup removes nested read-only contents and links without following link targets.</summary>
    [Fact]
    public async Task Startup_ClearsNestedContentsWithoutFollowingChildLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(true);
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Scratchpad, "nested")).FullName;
        var stale = Path.Combine(nested, ".hidden.txt");
        await File.WriteAllTextAsync(stale, "stale", ct);
        File.SetAttributes(stale, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "external")).FullName;
        var keep = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep", ct);
        var link = Path.Combine(fixture.Scratchpad, "linked");
        await CreateDirectoryLinkAsync(link, target);
        try
        {
            var warnings = await fixture.Lifecycle.StartAsync(ct);
            Assert.Empty(warnings);
            Assert.True(fixture.Lifecycle.Current.IsActive);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Scratchpad));
            Assert.Equal("keep", await File.ReadAllTextAsync(keep, ct));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    /// <summary>Disabling at the next session still clears the previously owned contents.</summary>
    [Fact]
    public async Task NewSession_DisablingClearsOldContentsAndRevokesCapability()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(true);
        _ = await fixture.Lifecycle.StartAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.Scratchpad, "stale.txt"), "stale", ct);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Repository, ".threadsmith", "session.json"),
            """{"scratchpad":{"enabled":false}}""",
            ct);

        _ = await fixture.Lifecycle.BeginNewSessionAsync(ct);

        Assert.Equal(ScratchpadDisabledReason.NotConfigured, fixture.Lifecycle.Current.DisabledReason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Scratchpad));
    }

    /// <summary>Cleanup rechecks links even after a valid capability was activated.</summary>
    [Fact]
    public async Task NewSession_RejectsReplacedRootWithoutDeletingTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(true);
        _ = await fixture.Lifecycle.StartAsync(ct);
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "external")).FullName;
        var keep = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep", ct);
        Directory.Delete(fixture.Scratchpad);
        await CreateDirectoryLinkAsync(fixture.Scratchpad, target);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Lifecycle.BeginNewSessionAsync(ct));
            Assert.False(fixture.Lifecycle.Current.IsActive);
            Assert.Equal("keep", await File.ReadAllTextAsync(keep, ct));
        }
        finally
        {
            Directory.Delete(fixture.Scratchpad);
            Directory.CreateDirectory(fixture.Scratchpad);
        }
    }

    /// <summary>Repository transitions clean the old fixed root and advertise the destination's fixed root.</summary>
    [Fact]
    public async Task RepositorySwitch_RebindsFixedRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var original = await Fixture.CreateAsync(true);
        await using var destination = await Fixture.CreateAsync(true);
        _ = await original.Lifecycle.StartAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(original.Scratchpad, "stale.txt"), "stale", ct);

        await original.Lifecycle.BindRepositoryAsync(destination.Repository, ct);
        _ = await original.Lifecycle.StartAsync(ct);

        Assert.Empty(Directory.EnumerateFileSystemEntries(original.Scratchpad));
        Assert.True(original.Lifecycle.Current.IsActive);
        Assert.Equal(destination.Scratchpad, original.Lifecycle.Current.RootPath);
        Assert.Equal(".threadsmith/.scratchpad", original.Lifecycle.Current.ModelPath);
        await original.Lifecycle.RestoreRepositoryAsync(original.Repository, ct);
        Assert.Equal(original.Scratchpad, original.Lifecycle.Current.RootPath);
    }

    /// <summary>Legacy path configuration cannot enable or redirect cleanup, from any source.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LegacyPath_NeverCleansSelectedDirectory(bool enabled, bool userConfigured)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(enabled, userConfigured: userConfigured, legacyPath: "../external");
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "external")).FullName;
        var keep = Path.Combine(external, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep", ct);

        var warnings = await fixture.Lifecycle.StartAsync(ct);
        Assert.Equal(enabled, fixture.Lifecycle.Current.IsActive);
        if (enabled)
        {
            Assert.Equal(fixture.Scratchpad, fixture.Lifecycle.Current.RootPath);
        }

        Assert.Empty(warnings);
        Assert.Equal("keep", await File.ReadAllTextAsync(keep, ct));
    }

    /// <summary>A session boundary removes all transient content before publishing the next capability.</summary>
    [Fact]
    public async Task NewSession_ClearsContentsAndReactivates()
    {
        await using var fixture = await Fixture.CreateAsync(true);
        _ = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);
        var scratchpad = fixture.Scratchpad;
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
        await using var fixture = await Fixture.CreateAsync(true);
        _ = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);
        var scratchpad = fixture.Scratchpad;
        var note = Path.Combine(scratchpad, "iteration.log");
        await File.WriteAllTextAsync(note, "current session", TestContext.Current.CancellationToken);
        var generation = fixture.Lifecycle.Current.Generation;

        await fixture.Lifecycle.BindRepositoryAsync(fixture.Repository, TestContext.Current.CancellationToken);

        Assert.True(fixture.Lifecycle.Current.IsActive);
        Assert.Equal(generation, fixture.Lifecycle.Current.Generation);
        Assert.Equal("current session", await File.ReadAllTextAsync(note, TestContext.Current.CancellationToken));
    }

    /// <summary>Scratchpad is disabled when write_file remains approval-gated.</summary>
    [Fact]
    public async Task ApprovalRequiredWriteFile_DisablesScratchpad()
    {
        await using var fixture = await Fixture.CreateAsync(true, requireWriteApproval: true, userConfigured: true);

        var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Lifecycle.Current.IsActive);
        Assert.Equal(ScratchpadDisabledReason.WriteFileUnavailable, fixture.Lifecycle.Current.DisabledReason);
        Assert.Contains(warnings, warning => warning.Contains("write_file", StringComparison.Ordinal));
    }

    /// <summary>A linked in-repository ancestor is rejected before a missing child can be created externally.</summary>
    [Fact]
    public async Task MissingDirectory_LinkedAncestorDoesNotCreateOutsideRepository()
    {
        await using var fixture = await Fixture.CreateAsync(true, userConfigured: true);
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "external")).FullName;
        var link = Path.Combine(fixture.Repository, ".threadsmith");
        var original = Path.Combine(fixture.Repository, "original-threadsmith");
        Directory.Move(link, original);
        await CreateDirectoryLinkAsync(link, external);
        try
        {
            var warnings = await fixture.Lifecycle.StartAsync(TestContext.Current.CancellationToken);

            Assert.False(fixture.Lifecycle.Current.IsActive);
            Assert.False(Directory.Exists(Path.Combine(external, ".scratchpad")));
            Assert.Contains(warnings, warning => warning.Contains("symbolic links", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Move(original, link);
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
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
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
        private Fixture(string root, string repository, ScratchpadLifecycle lifecycle, WriteFileTool tool)
        {
            Root = root;
            Repository = repository;
            Lifecycle = lifecycle;
            Tool = tool;
        }

        public string Root { get; }

        public string Repository { get; }

        public string Scratchpad => Path.Combine(Repository, ".threadsmith", ".scratchpad");

        public ScratchpadLifecycle Lifecycle { get; }

        public WriteFileTool Tool { get; }

        public static async Task<Fixture> CreateAsync(
            bool? enabled,
            bool requireWriteApproval = false,
            bool userConfigured = false,
            string[]? arguments = null,
            bool ignored = true,
            string? legacyPath = null)
        {
            var root = Directory.CreateDirectory(Path.Combine(
                PhysicalTemporaryPath(),
                "threadsmith-scratch-lifecycle-" + Guid.NewGuid().ToString("N"))).FullName;
            var repository = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            var threadsmith = Directory.CreateDirectory(Path.Combine(repository, ".threadsmith")).FullName;
            var machine = Directory.CreateDirectory(Path.Combine(root, "configuration", "machine")).FullName;
            var user = Directory.CreateDirectory(Path.Combine(root, "configuration", "user")).FullName;
            var repositoryConfiguration = Path.Combine(threadsmith, "config.json");
            await File.WriteAllTextAsync(repositoryConfiguration, "{}", TestContext.Current.CancellationToken);
            var configuredFile = userConfigured
                ? Path.Combine(user, "config.json")
                : repositoryConfiguration;
            await File.WriteAllTextAsync(
                configuredFile,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    scratchpad = new { enabled, path = legacyPath },
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
            var configuration = ConfigurationBootstrap.Build(arguments ?? [], paths);
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
            var git = await processes.RunAsync(
                new ProcessExecutionRequest
                {
                    ToolInvocationId = ToolInvocationId.New(),
                    RunId = RunId.New(),
                    FileName = "git",
                    Arguments = ["init", "--quiet"],
                    WorkingDirectory = repository,
                    Origin = ProcessRequestOrigin.Host,
                },
                TestContext.Current.CancellationToken);
            Assert.Equal(0, git.ExitCode);
            if (ignored)
            {
                await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), ".threadsmith/.scratchpad/\n", TestContext.Current.CancellationToken);
            }

            var lifecycle = new ScratchpadLifecycle(
                arguments ?? [],
                configuration,
                paths,
                processes,
                state,
                registry,
                NullLogger<ScratchpadLifecycle>.Instance);
            return new Fixture(root, repository, lifecycle, write);
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
        {
            throw new NotSupportedException();
        }

        public Task<ConversationStateSnapshot> GetSnapshotAsync(SessionId sessionId, bool includeBodies = true, ConversationHistoryWindow? historyWindow = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> RemoveMessageBodiesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ReplaceSummaryAsync(SessionId sessionId, IReadOnlyList<ConversationMemoryItem> items, ConversationSummarySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SetModeAsync(SessionId sessionId, ConversationContextMode mode, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateMemoryAsync(ConversationMemoryItem item, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
