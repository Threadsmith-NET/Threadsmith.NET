namespace Threadsmith.Milestone2.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Tui;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies the Milestone 2 repository lifecycle and trust boundary.</summary>
public static class RepositoryLifecycleTests
{
    /// <summary>Untrusted inspection discovers safe candidates but blocks trusted-read operations.</summary>
    [Fact]
    public static async Task OpenRepository_UntrustedInspection_BlocksSelectionAndBaseline()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var sessionId = SessionId.New();

        var opened = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            repository.RootPath,
            RepositoryTrustLevel.UntrustedInspection));

        Assert.Equal(RepositoryTrustLevel.UntrustedInspection, opened.Trust.Level);
        Assert.Null(opened.Environment);
        Assert.Contains(repository.SolutionPath, opened.SolutionCandidates);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.Lifecycle.HandleAsync(new SelectSolutionCommand(
                sessionId,
                opened.WorkspaceId,
                repository.SolutionPath)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.Lifecycle.HandleAsync(new RecordBaselineCommand(
                sessionId,
                opened.WorkspaceId)));
    }

    /// <summary>Opening a replacement repository invalidates the session's prior workspace.</summary>
    [Fact]
    public static async Task OpenRepository_SameSession_ReplacesPriorWorkspace()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var sessionId = SessionId.New();

        var first = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));
        var replacement = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));

        Assert.NotEqual(first.WorkspaceId, replacement.WorkspaceId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lifecycle.HandleAsync(new SelectSolutionCommand(
                sessionId,
                first.WorkspaceId,
                repository.SolutionPath)));
    }

    /// <summary>Repository switching reloads policy state and redirects persistent trust to the active repository.</summary>
    [Fact]
    public static async Task OpenRepository_SwitchRepository_RebindsMutationApprovalPolicy()
    {
        await using var firstRepository = await TemporaryRepository.CreateAsync();
        await using var secondRepository = await TemporaryRepository.CreateAsync();
        await File.WriteAllTextAsync(
            firstRepository.ConfigurationPath,
            "{\"mutation\":{\"approvalPolicy\":\"alwaysTrustRepo\"}}");
        await using var harness = await RepositoryHarness.CreateAsync(
            firstRepository.RootPath,
            useExternalDatabase: true);
        var policy = new MutationApprovalPolicyService();
        var lifecycle = new RepositoryLifecycle(
            harness.Events,
            harness.Facts,
            new DotNetEnvironmentResolver(),
            mutationApprovalPolicy: policy);
        var sessionId = SessionId.New();

        _ = await lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            firstRepository.RootPath,
            RepositoryTrustLevel.UntrustedInspection));
        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, policy.CurrentPolicy);

        _ = await lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            secondRepository.RootPath,
            RepositoryTrustLevel.UntrustedInspection));
        Assert.Equal(MutationApprovalPolicy.ReviewAll, policy.CurrentPolicy);
        await policy.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo);

        using JsonDocument firstConfig = JsonDocument.Parse(
            await File.ReadAllTextAsync(firstRepository.ConfigurationPath));
        using JsonDocument secondConfig = JsonDocument.Parse(
            await File.ReadAllTextAsync(secondRepository.ConfigurationPath));
        Assert.Equal(
            "alwaysTrustRepo",
            firstConfig.RootElement.GetProperty("mutation").GetProperty("approvalPolicy").GetString());
        Assert.Equal(
            "alwaysTrustRepo",
            secondConfig.RootElement.GetProperty("mutation").GetProperty("approvalPolicy").GetString());
    }

    /// <summary>Persistent policy refuses a linked repository configuration directory.</summary>
    [Fact]
    public static async Task MutationApprovalPolicy_LinkedConfigurationDirectory_RejectsPersistence()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        Directory.Delete(Path.Combine(repository.RootPath, ".threadsmith"), recursive: true);
        await using var link = await TemporaryDirectoryLink.CreateAsync(
            repository.RootPath,
            ".threadsmith");
        string configurationPath = Path.Combine(link.LinkPath, "config.json");
        var policy = new MutationApprovalPolicyService(
            new ConfigurationBuilder().Build(),
            configurationPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            policy.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo));

        Assert.False(File.Exists(Path.Combine(link.TargetPath, "config.json")));
    }

    /// <summary>Windows reserved-name entries do not prevent discovery of valid solutions.</summary>
    [Fact]
    public static async Task OpenRepository_WindowsReservedNameEntry_SkipsEntryAndFindsSolution()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var repository = await TemporaryRepository.CreateAsync();
        string reservedPath = @"\\?\" + Path.Combine(repository.RootPath, "nul");
        await File.WriteAllTextAsync(reservedPath, "ignored");
        try
        {
            await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);

            var opened = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
                SessionId.New(),
                repository.RootPath,
                RepositoryTrustLevel.TrustedRead));

            Assert.Single(opened.SolutionCandidates);
            Assert.Equal(repository.SolutionPath, opened.SolutionCandidates[0]);
            Assert.NotNull(opened.Environment);
        }
        finally
        {
            File.Delete(reservedPath);
        }
    }

    /// <summary>Trusted read selects a solution, inventories TFMs, captures a safe baseline, and persists facts.</summary>
    [Fact]
    public static async Task RepositoryLifecycle_TrustedRead_ProducesDurableFactsAndBaseline()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var sessionId = SessionId.New();
        await harness.Events.PublishAsync(new SessionCreated(sessionId, DateTimeOffset.UtcNow, "M2"));

        var opened = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));
        var selected = await harness.Lifecycle.HandleAsync(new SelectSolutionCommand(
            sessionId,
            opened.WorkspaceId,
            repository.SolutionPath));
        var baseline = await harness.Lifecycle.HandleAsync(new RecordBaselineCommand(
            sessionId,
            opened.WorkspaceId));

        Assert.NotNull(opened.Environment);
        Assert.False(string.IsNullOrWhiteSpace(opened.Environment.SdkVersion));
        Assert.Equal(new[] { "net10.0", "net9.0" }, selected.TargetFrameworks);
        Assert.Equal(
            new[]
            {
                "src/App/App.csproj",
                "src/App/Program.cs",
                "src/Lib/Lib.csproj",
                "src/Lib/Service.cs",
            },
            baseline.Files.Select(item => item.RelativePath));
        Assert.All(baseline.Files, item => Assert.Equal(64, item.Sha256.Length));
        Assert.DoesNotContain(baseline.Files, item => item.RelativePath.Contains("obj", StringComparison.Ordinal));

        var facts = await harness.Facts.GetAsync(repository.RootPath);
        Assert.NotNull(facts);
        Assert.Equal(RepositoryTrustLevel.TrustedRead, facts.Trust.Level);
        Assert.Equal(repository.SolutionPath, facts.SolutionPath);
        Assert.Equal(selected.TargetFrameworks, facts.TargetFrameworks);
        Assert.Contains(harness.ObservedEvents, item => item is RepositoryOpened);
        Assert.Contains(harness.ObservedEvents, item => item is SolutionLoaded);
        var durableEvents = await harness.EventStore.ReadAsync(sessionId);
        var durableRepository = Assert.IsType<RepositoryOpened>(
            durableEvents.Single(item => item is RepositoryOpened));
        var durableSolution = Assert.IsType<SolutionLoaded>(
            durableEvents.Single(item => item is SolutionLoaded));
        Assert.Equal(opened.WorkspaceId, durableRepository.WorkspaceId);
        Assert.Equal(RepositoryTrustLevel.TrustedRead, durableRepository.TrustLevel);
        Assert.Equal(selected.TargetFrameworks, durableSolution.TargetFrameworks);

        var projection = await harness.Projections.GetAsync<SessionProjection>(
            new ProjectionKey("session", sessionId.Value.ToString("D")));
        Assert.NotNull(projection);
        Assert.Equal(opened.WorkspaceId, projection.WorkspaceId);
        Assert.Equal(repository.RootPath, projection.RepositoryPath);
        Assert.Equal(repository.SolutionPath, projection.SolutionPath);
        Assert.Equal(selected.TargetFrameworks, projection.TargetFrameworks);
    }

    /// <summary>Durable repository keys canonicalize equivalent root path forms.</summary>
    [Fact]
    public static async Task RepositoryFacts_EquivalentRootPaths_ReuseDurableRow()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        _ = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            SessionId.New(),
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));

        var trailingSeparatorFacts = await harness.Facts.GetAsync(
            repository.RootPath + Path.DirectorySeparatorChar);

        Assert.NotNull(trailingSeparatorFacts);
        Assert.Equal(RepositoryTrustLevel.TrustedRead, trailingSeparatorFacts.Trust.Level);
        if (OperatingSystem.IsWindows())
        {
            var devicePathFacts = await harness.Facts.GetAsync(@"\\?\" + repository.RootPath);
            Assert.NotNull(devicePathFacts);
            Assert.Equal(RepositoryTrustLevel.TrustedRead, devicePathFacts.Trust.Level);
        }
    }

    /// <summary>Persisted trust is reused and cannot be silently downgraded on reopen.</summary>
    [Fact]
    public static async Task OpenRepository_PreviouslyTrustedRepository_ReusesTrust()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var first = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            SessionId.New(),
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));
        var secondLifecycle = new RepositoryLifecycle(
            harness.Events,
            harness.Facts,
            new DotNetEnvironmentResolver());

        var reopened = await secondLifecycle.HandleAsync(new OpenRepositoryCommand(
            SessionId.New(),
            repository.RootPath,
            RepositoryTrustLevel.UntrustedInspection));

        Assert.Equal(RepositoryTrustLevel.TrustedRead, first.Trust.Level);
        Assert.Equal(first.Trust.GrantedAt, reopened.Trust.GrantedAt);
        Assert.Equal(RepositoryTrustLevel.TrustedRead, reopened.Trust.Level);
        Assert.NotNull(reopened.Environment);
    }

    /// <summary>Configured roots and selected solutions cannot escape the opened repository.</summary>
    [Fact]
    public static async Task RepositoryLifecycle_PathEscape_IsRejected()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        string escapedConfig = """
            {
              "solution": { "path": "../outside.sln" },
              "editableRoots": [ "src" ]
            }
            """;
        await File.WriteAllTextAsync(repository.ConfigurationPath, escapedConfig);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
                SessionId.New(),
                repository.RootPath,
                RepositoryTrustLevel.TrustedRead)));
    }

    /// <summary>Configured solutions cannot escape through a linked ancestor directory.</summary>
    [Fact]
    public static async Task OpenRepository_ConfiguredSolutionThroughLinkedDirectory_IsRejected()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var linkedDirectory = await TemporaryDirectoryLink.CreateAsync(repository.RootPath);
        await File.WriteAllTextAsync(
            Path.Combine(linkedDirectory.TargetPath, "Outside.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00");
        string escapedConfig = $$"""
            {
              "solution": { "path": "{{linkedDirectory.Name}}/Outside.sln" },
              "editableRoots": [ "src" ]
            }
            """;
        await File.WriteAllTextAsync(repository.ConfigurationPath, escapedConfig);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
                SessionId.New(),
                repository.RootPath,
                RepositoryTrustLevel.TrustedRead)));
    }

    /// <summary>Solution project references cannot escape through a linked ancestor directory.</summary>
    [Fact]
    public static async Task SelectSolution_ProjectThroughLinkedDirectory_IsRejected()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var linkedDirectory = await TemporaryDirectoryLink.CreateAsync(repository.RootPath);
        await File.WriteAllTextAsync(
            Path.Combine(linkedDirectory.TargetPath, "Outside.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string solutionContent = $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Outside", "{{linkedDirectory.Name}}\Outside.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Global
            EndGlobal
            """;
        await File.WriteAllTextAsync(repository.SolutionPath, solutionContent);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var sessionId = SessionId.New();
        var opened = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            harness.Lifecycle.HandleAsync(new SelectSolutionCommand(
                sessionId,
                opened.WorkspaceId,
                repository.SolutionPath)));
    }

    /// <summary>Prohibited-path globs distinguish one path segment from recursive globstars.</summary>
    [Fact]
    public static async Task RecordBaseline_ProhibitedGlobs_UseSegmentAwareSemantics()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        string nestedDirectory = Path.Combine(repository.RootPath, "src", "App", "Nested");
        string generatedDirectory = Path.Combine(nestedDirectory, "generated");
        Directory.CreateDirectory(generatedDirectory);
        await File.WriteAllTextAsync(Path.Combine(repository.RootPath, "src", "App", "Secret.cs"), "blocked");
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "Secret.cs"), "allowed");
        await File.WriteAllTextAsync(Path.Combine(generatedDirectory, "Blocked.cs"), "blocked");
        string configuration = """
            {
              "solution": { "path": "Sample.sln" },
              "editableRoots": [ "src" ],
              "prohibitedPaths": [ "src/*/Secret.cs", "src/**/generated/" ]
            }
            """;
        await File.WriteAllTextAsync(repository.ConfigurationPath, configuration);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var sessionId = SessionId.New();
        var opened = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));

        var baseline = await harness.Lifecycle.HandleAsync(new RecordBaselineCommand(
            sessionId,
            opened.WorkspaceId));

        Assert.DoesNotContain(baseline.Files, item => item.RelativePath == "src/App/Secret.cs");
        Assert.Contains(baseline.Files, item => item.RelativePath == "src/App/Nested/Secret.cs");
        Assert.DoesNotContain(
            baseline.Files,
            item => item.RelativePath == "src/App/Nested/generated/Blocked.cs");
    }

    /// <summary>Solution selection atomically remembers a relative path without replacing unrelated repository configuration.</summary>
    [Fact]
    public static async Task SelectSolution_RemembersRelativePathAndPreservesConfiguration()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await File.WriteAllTextAsync(
            repository.ConfigurationPath,
            """
            {
              "solution": { "path": null },
              "custom": { "preserved": true }
            }
            """);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var sessionId = SessionId.New();
        var opened = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));

        _ = await harness.Lifecycle.HandleAsync(new SelectSolutionCommand(
            sessionId,
            opened.WorkspaceId,
            repository.SolutionPath));

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(repository.ConfigurationPath));
        Assert.Equal("Sample.sln", document.RootElement.GetProperty("solution").GetProperty("path").GetString());
        Assert.True(document.RootElement.GetProperty("custom").GetProperty("preserved").GetBoolean());
    }

    /// <summary>Solution preference persistence preserves existing case-insensitive JSON property spellings.</summary>
    [Fact]
    public static async Task SelectSolution_MixedCaseConfiguration_PreservesKeysAndRemainsLoadable()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await File.WriteAllTextAsync(
            repository.ConfigurationPath,
            """
            {
              "Solution": { "Path": null },
              "Custom": { "Preserved": true }
            }
            """);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var sessionId = SessionId.New();
        var opened = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            sessionId,
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));

        _ = await harness.Lifecycle.HandleAsync(new SelectSolutionCommand(
            sessionId,
            opened.WorkspaceId,
            repository.SolutionPath));

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(repository.ConfigurationPath));
        var solution = Assert.Single(
            document.RootElement.EnumerateObject(),
            property => string.Equals(property.Name, "solution", StringComparison.OrdinalIgnoreCase));
        var path = Assert.Single(
            solution.Value.EnumerateObject(),
            property => string.Equals(property.Name, "path", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Solution", solution.Name);
        Assert.Equal("Path", path.Name);
        Assert.Equal("Sample.sln", path.Value.GetString());
        var reloaded = new ConfigurationBuilder()
            .AddJsonFile(repository.ConfigurationPath)
            .Build();
        Assert.Equal("Sample.sln", reloaded["solution:path"]);
        Assert.True(reloaded.GetValue<bool>("custom:preserved"));
    }

    /// <summary>A missing remembered solution is cleared and normal discovery continues.</summary>
    [Fact]
    public static async Task OpenRepository_StaleRememberedSolution_ClearsPreferenceAndDiscovers()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await File.WriteAllTextAsync(
            repository.ConfigurationPath,
            """
            {
              "solution": { "path": "Moved.sln" },
              "editableRoots": [ "src" ]
            }
            """);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);

        var opened = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            SessionId.New(),
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));

        Assert.Null(opened.Configuration.SolutionPath);
        Assert.Contains(repository.SolutionPath, opened.SolutionCandidates);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(repository.ConfigurationPath));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("solution").GetProperty("path").ValueKind);
        Assert.Equal("src", document.RootElement.GetProperty("editableRoots")[0].GetString());
    }

    /// <summary>Repository initialization is confined, minimal, valid, and idempotent.</summary>
    [Fact]
    public static async Task RepositoryInitializer_CreatesMinimalConfigurationWithoutOverwrite()
    {
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-m2-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var before = await RepositoryInitializer.GetStatusAsync(repositoryPath);
            var created = await RepositoryInitializer.InitializeAsync(repositoryPath);
            var repeated = await RepositoryInitializer.InitializeAsync(repositoryPath);
            var after = await RepositoryInitializer.GetStatusAsync(repositoryPath);

            Assert.True(before.ShouldOfferInitialization);
            Assert.True(created.Created);
            Assert.False(repeated.Created);
            Assert.True(after.HasConfigurationDirectory);
            Assert.False(after.ShouldOfferInitialization);
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(created.ConfigurationPath));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("solution").GetProperty("path").ValueKind);
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("tools").GetProperty("disabled").ValueKind);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    /// <summary>Repository initialization rejects a linked configuration directory before writing.</summary>
    [Fact]
    public static async Task RepositoryInitializer_LinkedConfigurationDirectory_IsRejected()
    {
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-m2-init-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            await using var linkedDirectory = await TemporaryDirectoryLink.CreateAsync(
                repositoryPath,
                ".threadsmith");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                RepositoryInitializer.InitializeAsync(repositoryPath));
            Assert.Empty(Directory.EnumerateFiles(linkedDirectory.TargetPath));
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    /// <summary>A remembered solution bypasses ambiguity while an explicit solution remains the override.</summary>
    [Fact]
    public static async Task TuiRepositoryWorkflow_RememberedSolution_AutoLoadsWithoutPrompt()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        string secondSolutionPath = Path.Combine(repository.RootPath, "Second.sln");
        File.Copy(repository.SolutionPath, secondSolutionPath);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher([new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var controller = new TuiController(new TuiPresenter(dispatcher, harness.Projections));
        await controller.OpenAsync("Remembered solution");
        int solutionPromptCount = 0;

        var remembered = await controller.OpenRepositoryWorkflowAsync(
            repository.RootPath,
            _ => Task.FromResult<RepositoryTrustLevel?>(RepositoryTrustLevel.TrustedRead),
            (_, _) =>
            {
                solutionPromptCount++;
                return Task.FromResult<string?>(secondSolutionPath);
            });

        Assert.Equal(0, solutionPromptCount);
        Assert.True(remembered.UsedRememberedSolution);
        Assert.NotNull(remembered.Solution);
        Assert.Equal(repository.SolutionPath, remembered.Solution.SolutionPath);

        using var output = new StringWriter();
        int exitCode = await new HeadlessShell(dispatcher, harness.Projections, output)
            .InspectRepositoryAsync(
                "Remembered CLI solution",
                repository.RootPath,
                RepositoryTrustLevel.TrustedRead);
        Assert.Equal(0, exitCode);
        Assert.Contains("Loading remembered solution: Sample.sln", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Headless and terminal-independent TUI surfaces use repository commands without a model.</summary>
    [Fact]
    public static async Task RepositorySurfaces_CommandBoundary_DoesNotRequireModel()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        using var output = new StringWriter();

        int exitCode = await new HeadlessShell(
            dispatcher,
            harness.Projections,
            output).InspectRepositoryAsync(
                "CLI",
                repository.RootPath,
                RepositoryTrustLevel.TrustedRead,
                repository.SolutionPath);
        var controller = new TuiController(new TuiPresenter(dispatcher, harness.Projections));
        await controller.OpenAsync("TUI");
        var opened = await controller.OpenRepositoryAsync(
            repository.RootPath,
            RepositoryTrustLevel.UntrustedInspection);
        await controller.SelectSolutionAsync(opened.WorkspaceId, repository.SolutionPath);
        var rendered = await controller.RenderAsync();

        Assert.Equal(0, exitCode);
        Assert.Contains("Baseline files: 4", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(repository.RootPath, rendered.Workspace, StringComparison.Ordinal);
        Assert.Contains(repository.SolutionPath, rendered.Workspace, StringComparison.Ordinal);
    }

    /// <summary>Headless discovery lists ambiguity instead of silently selecting the first solution.</summary>
    [Fact]
    public static async Task HeadlessRepositoryWorkflow_MultipleSolutions_RequiresOption()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await File.WriteAllTextAsync(
            repository.ConfigurationPath,
            "{ \"solution\": { \"path\": null }, \"editableRoots\": [ \"src\" ] }");
        string secondSolutionPath = Path.Combine(repository.RootPath, "Second.sln");
        File.Copy(repository.SolutionPath, secondSolutionPath);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        using var output = new StringWriter();

        int exitCode = await new HeadlessShell(
            dispatcher,
            harness.Projections,
            output).InspectRepositoryAsync(
                "CLI ambiguous solution",
                repository.RootPath,
                RepositoryTrustLevel.TrustedRead);

        Assert.Equal(2, exitCode);
        Assert.Contains("specify --solution", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(repository.SolutionPath, output.ToString(), StringComparison.Ordinal);
        Assert.Contains(secondSolutionPath, output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The TUI workflow reuses trusted-read grants without invoking its trust prompt.</summary>
    [Fact]
    public static async Task TuiRepositoryWorkflow_PersistedTrust_SkipsPrompt()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        _ = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            SessionId.New(),
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var controller = new TuiController(new TuiPresenter(dispatcher, harness.Projections));
        await controller.OpenAsync("TUI persisted trust");
        int trustPromptCount = 0;
        int solutionPromptCount = 0;

        var result = await controller.OpenRepositoryWorkflowAsync(
            repository.RootPath,
            _ =>
            {
                trustPromptCount++;
                return Task.FromResult<RepositoryTrustLevel?>(null);
            },
            (_, _) =>
            {
                solutionPromptCount++;
                return Task.FromResult<string?>(null);
            });

        Assert.Equal(0, trustPromptCount);
        Assert.Equal(0, solutionPromptCount);
        Assert.NotNull(result.Repository);
        Assert.Equal(RepositoryTrustLevel.TrustedRead, result.Repository.Trust.Level);
        Assert.NotNull(result.Solution);
        Assert.Equal(repository.SolutionPath, result.Solution.SolutionPath);
    }

    /// <summary>The TUI workflow accepts compiler-aware trust during the initial repository open.</summary>
    [Fact]
    public static async Task TuiRepositoryWorkflow_InitialTrustedBuild_UsesCompilerTrust()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var controller = new TuiController(new TuiPresenter(dispatcher, harness.Projections));
        await controller.OpenAsync("TUI trusted build");
        int trustPromptCount = 0;

        var result = await controller.OpenRepositoryWorkflowAsync(
            repository.RootPath,
            _ =>
            {
                trustPromptCount++;
                return Task.FromResult<RepositoryTrustLevel?>(RepositoryTrustLevel.TrustedBuild);
            },
            (_, _) => Task.FromResult<string?>(null));

        Assert.Equal(1, trustPromptCount);
        Assert.NotNull(result.Repository);
        Assert.Equal(RepositoryTrustLevel.TrustedBuild, result.Repository.Trust.Level);
        Assert.NotNull(result.Solution);
        Assert.Equal(repository.SolutionPath, result.Solution.SolutionPath);
    }

    /// <summary>The TUI workflow can explicitly upgrade a persisted read grant for compiler discovery.</summary>
    [Fact]
    public static async Task TuiRepositoryWorkflow_PersistedRead_CanUpgradeToTrustedBuild()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        _ = await harness.Lifecycle.HandleAsync(new OpenRepositoryCommand(
            SessionId.New(),
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead));
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var controller = new TuiController(new TuiPresenter(dispatcher, harness.Projections));
        await controller.OpenAsync("TUI trust upgrade");
        int initialTrustPromptCount = 0;
        int upgradePromptCount = 0;

        var result = await controller.OpenRepositoryWorkflowAsync(
            repository.RootPath,
            _ =>
            {
                initialTrustPromptCount++;
                return Task.FromResult<RepositoryTrustLevel?>(null);
            },
            (_, _) => Task.FromResult<string?>(null),
            (persistedTrust, _) =>
            {
                upgradePromptCount++;
                Assert.Equal(RepositoryTrustLevel.TrustedRead, persistedTrust.Level);
                return Task.FromResult<RepositoryTrustLevel?>(RepositoryTrustLevel.TrustedBuild);
            });

        Assert.Equal(0, initialTrustPromptCount);
        Assert.Equal(1, upgradePromptCount);
        Assert.NotNull(result.Repository);
        Assert.Equal(RepositoryTrustLevel.TrustedBuild, result.Repository.Trust.Level);
        Assert.NotNull(result.Solution);
        Assert.Equal(repository.SolutionPath, result.Solution.SolutionPath);
    }

    /// <summary>The TUI workflow requires an explicit candidate when multiple solutions are found.</summary>
    [Fact]
    public static async Task TuiRepositoryWorkflow_MultipleSolutions_UsesExplicitSelection()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await File.WriteAllTextAsync(
            repository.ConfigurationPath,
            "{ \"solution\": { \"path\": null }, \"editableRoots\": [ \"src\" ] }");
        string secondSolutionPath = Path.Combine(repository.RootPath, "Second.sln");
        File.Copy(repository.SolutionPath, secondSolutionPath);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var controller = new TuiController(new TuiPresenter(dispatcher, harness.Projections));
        await controller.OpenAsync("TUI multiple solutions");
        int trustPromptCount = 0;
        int solutionPromptCount = 0;

        var result = await controller.OpenRepositoryWorkflowAsync(
            repository.RootPath,
            _ =>
            {
                trustPromptCount++;
                return Task.FromResult<RepositoryTrustLevel?>(RepositoryTrustLevel.TrustedRead);
            },
            (candidates, _) =>
            {
                solutionPromptCount++;
                return Task.FromResult<string?>(candidates.Single(candidate =>
                    string.Equals(candidate, secondSolutionPath, StringComparison.OrdinalIgnoreCase)));
            });

        Assert.Equal(1, trustPromptCount);
        Assert.Equal(1, solutionPromptCount);
        Assert.NotNull(result.Repository);
        Assert.Equal(2, result.Repository.SolutionCandidates.Count);
        Assert.NotNull(result.Solution);
        Assert.Equal(secondSolutionPath, result.Solution.SolutionPath);
    }

    /// <summary>Command-line startup choices bypass prompts and select the requested solution.</summary>
    [Fact]
    public static async Task TuiRepositoryWorkflow_StartupOptions_BypassPrompts()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        string secondSolutionPath = Path.Combine(repository.RootPath, "Second.sln");
        File.Copy(repository.SolutionPath, secondSolutionPath);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var controller = new TuiController(new TuiPresenter(dispatcher, harness.Projections));
        await controller.OpenAsync("TUI startup options");
        int trustPromptCount = 0;
        int solutionPromptCount = 0;

        var result = await controller.OpenRepositoryWorkflowAsync(
            repository.RootPath,
            _ =>
            {
                trustPromptCount++;
                return Task.FromResult<RepositoryTrustLevel?>(null);
            },
            (_, _) =>
            {
                solutionPromptCount++;
                return Task.FromResult<string?>(null);
            },
            requestedTrust: RepositoryTrustLevel.TrustedRead,
            requestedSolutionPath: secondSolutionPath);

        Assert.Equal(0, trustPromptCount);
        Assert.Equal(0, solutionPromptCount);
        Assert.NotNull(result.Repository);
        Assert.Equal(RepositoryTrustLevel.TrustedRead, result.Repository.Trust.Level);
        Assert.NotNull(result.Solution);
        Assert.Equal(secondSolutionPath, result.Solution.SolutionPath);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(repository.ConfigurationPath));
        Assert.Equal("Second.sln", document.RootElement.GetProperty("solution").GetProperty("path").GetString());
    }

    /// <summary>Interactive startup reports live state and labels the composer with the repository name.</summary>
    [Fact]
    public static async Task ConversationalShell_Startup_ReportsStateAndUsesRepositoryPrompt()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var surface = new RepositoryConsoleSurface(["/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(dispatcher, harness.Projections),
            harness.Events,
            surface);

        await shell.RunAsync(
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead,
            repository.SolutionPath,
            "Test profile (test-model)").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal($"{Path.GetFileName(repository.RootPath)[..40]} > ", surface.Prompt);
        Assert.Contains(
            "Forge better code, not slop.\n\n",
            surface.Output.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("Model: Test profile (test-model)", surface.Output, StringComparison.Ordinal);
        Assert.Contains($"Repository: {repository.RootPath}", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Trust: TrustedRead", surface.Output, StringComparison.Ordinal);
        Assert.Contains($"Solution: {repository.SolutionPath}", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Target frameworks: net10.0", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Semantic confidence: Loading...", surface.TransientStatuses);
        Assert.DoesNotContain("Semantic confidence: Loading...", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Semantic confidence: TextOnly", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Interactive startup reports remembered auto-loading before normal status.</summary>
    [Fact]
    public static async Task ConversationalShell_RememberedSolution_PrintsNotification()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        string secondSolutionPath = Path.Combine(repository.RootPath, "Second.sln");
        File.Copy(repository.SolutionPath, secondSolutionPath);
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher([new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var surface = new RepositoryConsoleSurface(["/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(dispatcher, harness.Projections),
            harness.Events,
            surface);

        await shell.RunAsync(
            repository.RootPath,
            RepositoryTrustLevel.TrustedRead,
            modelStatus: "Test profile (test-model)").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(
            "Loading remembered solution: Sample.sln",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains("(Use --solution to change)", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Interactive startup offers scaffolding when runtime storage creates .threadsmith after eligibility is captured.</summary>
    [Fact]
    public static async Task ConversationalShell_EmptyRepository_OffersInitialization()
    {
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-m2-shell-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            bool configurationDirectoryExistedAtStartup =
                Directory.Exists(Path.Combine(repositoryPath, ".threadsmith"));
            Directory.CreateDirectory(Path.Combine(repositoryPath, ".threadsmith"));
            await using var harness = await RepositoryHarness.CreateAsync(repositoryPath);
            var dispatcher = new CommandDispatcher([new CreateSessionHandler(harness.Events), harness.Lifecycle]);
            var surface = new RepositoryConsoleSurface(["/quit"], [0]);
            var shell = new ConversationalShell(
                new TuiPresenter(dispatcher, harness.Projections),
                harness.Events,
                surface);

            await shell.RunAsync(
                repositoryPath,
                RepositoryTrustLevel.UntrustedInspection,
                modelStatus: "Test profile (test-model)",
                repositoryConfigurationDirectoryExistedAtStartup: configurationDirectoryExistedAtStartup)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("Initialized Threadsmith repository:", surface.Output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(repositoryPath, ".threadsmith", "config.json")));
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    /// <summary>Cancelling the startup trust menu exits before the composer is read.</summary>
    [Fact]
    public static async Task ConversationalShell_StartupCancel_ExitsCleanly()
    {
        await using var repository = await TemporaryRepository.CreateAsync();
        await using var harness = await RepositoryHarness.CreateAsync(repository.RootPath);
        var dispatcher = new CommandDispatcher(
            [new CreateSessionHandler(harness.Events), harness.Lifecycle]);
        var surface = new RepositoryConsoleSurface([]);
        var shell = new ConversationalShell(
            new TuiPresenter(dispatcher, harness.Projections),
            harness.Events,
            surface);

        await shell.RunAsync(
            repository.RootPath,
            modelStatus: "Test profile (test-model)").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("Startup cancelled.", surface.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, surface.Prompt);
        Assert.DoesNotContain(harness.ObservedEvents, item => item is SolutionLoaded);
        Assert.DoesNotContain(harness.ObservedEvents, item => item is TaskIntentRecorded);
    }

    private sealed class RepositoryConsoleSurface : IConsoleSurface
    {
        private readonly Queue<ConsoleInput> _inputs;
        private readonly Queue<int> _selections;
        private readonly StringBuilder _output = new();
        private readonly List<string> _transientStatuses = [];

        public RepositoryConsoleSurface(
            IEnumerable<string> inputs,
            IEnumerable<int>? selections = null)
        {
            _inputs = new Queue<ConsoleInput>(inputs.Select(text => new ConsoleInput(
                true,
                text,
                CancellationToken.None)));
            _selections = new Queue<int>(selections ?? []);
        }

        public string Output => _output.ToString();

        public IReadOnlyList<string> TransientStatuses => _transientStatuses;

        public string Prompt { get; private set; } = string.Empty;

        /// <inheritdoc />
        public Task SetPromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prompt = prompt;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<ConsoleInput> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_inputs.Dequeue());
        }

        /// <inheritdoc />
        public Task<int> SelectAsync(
            string title,
            IReadOnlyList<string> choices,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int selection = _selections.Count > 0
                ? _selections.Dequeue()
                : choices.Count - 1;
            return Task.FromResult(selection);
        }

        /// <inheritdoc />
        public async Task ShowStatusUntilAsync(
            string text,
            Task operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            _transientStatuses.Add(text);
            await operation.WaitAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task WriteAsync(
            string text,
            TuiTextRole role = TuiTextRole.Default,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _output.Append(text);
            return Task.CompletedTask;
        }
    }

    private sealed class RepositoryHarness : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly IDomainEventSubscription _eventSubscription;
        private readonly IDomainEventSubscription _persistenceSubscription;
        private readonly IDomainEventSubscription _projectionSubscription;
        private readonly SemanticEngineRegistry _semanticEngines;
        private readonly SemanticLifecycleObserver _semanticObserver;
        private readonly IDomainEventSubscription _semanticSubscription;

        private RepositoryHarness(
            string databasePath,
            DomainEventStream events,
            IDomainEventSubscription eventSubscription,
            IDomainEventSubscription persistenceSubscription,
            IDomainEventSubscription projectionSubscription,
            SemanticEngineRegistry semanticEngines,
            SemanticLifecycleObserver semanticObserver,
            IDomainEventSubscription semanticSubscription,
            SqliteRepositoryFactsStore facts,
            SqliteEventStore eventStore,
            InMemoryProjectionStore projections,
            RepositoryLifecycle lifecycle,
            List<IDomainEvent> observedEvents)
        {
            _databasePath = databasePath;
            _eventSubscription = eventSubscription;
            _persistenceSubscription = persistenceSubscription;
            _projectionSubscription = projectionSubscription;
            _semanticEngines = semanticEngines;
            _semanticObserver = semanticObserver;
            _semanticSubscription = semanticSubscription;
            Events = events;
            Facts = facts;
            EventStore = eventStore;
            Projections = projections;
            Lifecycle = lifecycle;
            ObservedEvents = observedEvents;
        }

        public DomainEventStream Events { get; }

        public SqliteEventStore EventStore { get; }

        public SqliteRepositoryFactsStore Facts { get; }

        public RepositoryLifecycle Lifecycle { get; }

        public List<IDomainEvent> ObservedEvents { get; }

        public InMemoryProjectionStore Projections { get; }

        public static async Task<RepositoryHarness> CreateAsync(
            string repositoryPath,
            bool useExternalDatabase = false)
        {
            string databasePath = useExternalDatabase
                ? Path.Combine(Path.GetTempPath(), $"threadsmith-m2-facts-{Guid.NewGuid():N}.db")
                : Path.Combine(repositoryPath, ".threadsmith", "facts.db");
            var facts = new SqliteRepositoryFactsStore($"Data Source={databasePath};Pooling=False");
            await facts.InitializeAsync();
            var eventStore = new SqliteEventStore($"Data Source={databasePath};Pooling=False");
            await eventStore.InitializeAsync();
            var events = new DomainEventStream();
            var observedEvents = new List<IDomainEvent>();
            var projections = new InMemoryProjectionStore();
            var eventSubscription = events.Subscribe((domainEvent, _) =>
            {
                observedEvents.Add(domainEvent);
                return Task.CompletedTask;
            });
            var persistenceSubscription = events.Subscribe(eventStore.AppendAsync);
            var projectionSubscription = events.Subscribe(projections.ApplyAsync);
            var semanticEngines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
            var semanticObserver = new SemanticLifecycleObserver(
                semanticEngines,
                events,
                NullLogger<SemanticLifecycleObserver>.Instance);
            var semanticSubscription = events.Subscribe(semanticObserver.ObserveAsync);
            var lifecycle = new RepositoryLifecycle(events, facts, new DotNetEnvironmentResolver());
            return new RepositoryHarness(
                databasePath,
                events,
                eventSubscription,
                persistenceSubscription,
                projectionSubscription,
                semanticEngines,
                semanticObserver,
                semanticSubscription,
                facts,
                eventStore,
                projections,
                lifecycle,
                observedEvents);
        }

        public async ValueTask DisposeAsync()
        {
            await _semanticSubscription.DisposeAsync();
            await _semanticObserver.DisposeAsync();
            await _semanticEngines.DisposeAsync();
            await _eventSubscription.DisposeAsync();
            await _persistenceSubscription.DisposeAsync();
            await _projectionSubscription.DisposeAsync();
            await Events.DisposeAsync();
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
    }

    private sealed class CreateSessionHandler : ICommandHandler<CreateSessionCommand, SessionId>
    {
        private readonly IDomainEventStream _events;

        public CreateSessionHandler(IDomainEventStream events)
        {
            _events = events;
        }

        public async Task<SessionId> HandleAsync(
            CreateSessionCommand command,
            CancellationToken cancellationToken = default)
        {
            var sessionId = SessionId.New();
            await _events.PublishAsync(
                new SessionCreated(sessionId, DateTimeOffset.UtcNow, command.Name),
                cancellationToken);
            return sessionId;
        }
    }

    private sealed class TemporaryDirectoryLink : IAsyncDisposable
    {
        private TemporaryDirectoryLink(string name, string linkPath, string targetPath)
        {
            Name = name;
            LinkPath = linkPath;
            TargetPath = targetPath;
        }

        public string LinkPath { get; }

        public string Name { get; }

        public string TargetPath { get; }

        public static async Task<TemporaryDirectoryLink> CreateAsync(
            string repositoryPath,
            string? requestedName = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
            string name = requestedName ?? $"linked-{Guid.NewGuid():N}";
            if (name.Contains(Path.DirectorySeparatorChar)
                || name.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new ArgumentException("Link names must be one path segment.", nameof(requestedName));
            }

            string linkPath = Path.Combine(repositoryPath, name);
            string targetPath = Path.Combine(Path.GetTempPath(), $"threadsmith-m2-external-{Guid.NewGuid():N}");
            Directory.CreateDirectory(targetPath);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.System),
                                "cmd.exe"),
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        },
                    };
                    process.StartInfo.ArgumentList.Add("/d");
                    process.StartInfo.ArgumentList.Add("/c");
                    process.StartInfo.ArgumentList.Add("mklink");
                    process.StartInfo.ArgumentList.Add("/J");
                    process.StartInfo.ArgumentList.Add(linkPath);
                    process.StartInfo.ArgumentList.Add(targetPath);

                    if (!process.Start())
                    {
                        throw new InvalidOperationException("Unable to start the Windows junction command.");
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                    await process.WaitForExitAsync(cancellationToken);
                    string output = await outputTask;
                    string error = await errorTask;
                    if (process.ExitCode != 0)
                    {
                        throw new IOException(
                            $"Unable to create the Windows test junction. {error}{output}".Trim());
                    }
                }
                else
                {
                    Directory.CreateSymbolicLink(linkPath, targetPath);
                }

                return new TemporaryDirectoryLink(name, linkPath, targetPath);
            }
            catch
            {
                if (Directory.Exists(linkPath))
                {
                    Directory.Delete(linkPath);
                }

                if (Directory.Exists(targetPath))
                {
                    Directory.Delete(targetPath, recursive: true);
                }

                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(LinkPath))
            {
                Directory.Delete(LinkPath);
            }

            string fullTargetPath = Path.GetFullPath(TargetPath);
            string tempPath = Path.GetFullPath(Path.GetTempPath());
            if (!fullTargetPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullTargetPath).StartsWith(
                    "threadsmith-m2-external-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove an unexpected linked test directory.");
            }

            if (Directory.Exists(fullTargetPath))
            {
                Directory.Delete(fullTargetPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryRepository : IAsyncDisposable
    {
        private TemporaryRepository(string rootPath, string solutionPath, string configurationPath)
        {
            RootPath = rootPath;
            SolutionPath = solutionPath;
            ConfigurationPath = configurationPath;
        }

        public string ConfigurationPath { get; }

        public string RootPath { get; }

        public string SolutionPath { get; }

        public static async Task<TemporaryRepository> CreateAsync()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"threadsmith-m2-{Guid.NewGuid():N}");
            string appDirectory = Path.Combine(rootPath, "src", "App");
            string libDirectory = Path.Combine(rootPath, "src", "Lib");
            string configDirectory = Path.Combine(rootPath, ".threadsmith");
            Directory.CreateDirectory(appDirectory);
            Directory.CreateDirectory(libDirectory);
            Directory.CreateDirectory(configDirectory);
            Directory.CreateDirectory(Path.Combine(appDirectory, "obj"));
            string solutionPath = Path.Combine(rootPath, "Sample.sln");
            string configurationPath = Path.Combine(configDirectory, "config.json");
            string solutionContent = """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Global
                EndGlobal
                """;
            string appProjectContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks></PropertyGroup>
                </Project>
                """;
            string libProjectContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """;
            string configurationContent = """
                {
                  "solution": { "path": "Sample.sln" },
                  "editableRoots": [ "src" ],
                  "prohibitedPaths": [ "**/*.env", ".threadsmith/secrets/" ]
                }
                """;
            await File.WriteAllTextAsync(solutionPath, solutionContent);
            await File.WriteAllTextAsync(Path.Combine(appDirectory, "App.csproj"), appProjectContent);
            await File.WriteAllTextAsync(Path.Combine(libDirectory, "Lib.csproj"), libProjectContent);
            await File.WriteAllTextAsync(Path.Combine(appDirectory, "Program.cs"), "public class Program { }");
            await File.WriteAllTextAsync(Path.Combine(libDirectory, "Service.cs"), "public class Service { }");
            await File.WriteAllTextAsync(Path.Combine(appDirectory, "obj", "Generated.cs"), "generated");
            await File.WriteAllTextAsync(configurationPath, configurationContent);
            return new TemporaryRepository(rootPath, solutionPath, configurationPath);
        }

        public ValueTask DisposeAsync()
        {
            string fullPath = Path.GetFullPath(RootPath);
            string tempPath = Path.GetFullPath(Path.GetTempPath());
            if (!fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith("threadsmith-m2-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove an unexpected test directory.");
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
