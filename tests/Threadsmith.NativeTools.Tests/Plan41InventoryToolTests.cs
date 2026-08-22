namespace Threadsmith.NativeTools.Tests;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies Plan 41 closed Git and .NET inventory behavior.</summary>
public sealed class Plan41InventoryToolTests
{
    /// <summary>Verifies history, working-tree, blame, show, and branch comparison normalization.</summary>
    [Fact]
    public async Task GitQueries_LocalRepository_ReturnBoundedNormalizedResults()
    {
        // Arrange
        await using var repository = await TestRepository.CreateAsync();
        var service = new GitQueryService();
        await repository.RunGitAsync("checkout", "-b", "feature");
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "tracked.txt"), "feature\n");
        await repository.RunGitAsync("add", "tracked.txt");
        await repository.RunGitAsync("commit", "-m", "feature commit");
        await repository.RunGitAsync("checkout", "main");
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "tracked.txt"), "working\n");

        // Act
        var diff = await service.DiffAsync(
            repository.Path,
            new GitDiffRequest { Mode = GitComparisonMode.WorkingTree });
        var log = await service.LogAsync(repository.Path, new GitLogRequest());
        var show = await service.ShowAsync(
            repository.Path,
            new GitShowRequest { Revision = "HEAD", Path = "tracked.txt" });
        var blame = await service.BlameAsync(
            repository.Path,
            new GitBlameRequest { Path = "tracked.txt" });
        var comparison = await service.CompareBranchesAsync(
            repository.Path,
            new GitBranchComparisonRequest { BaseRevision = "main", TargetRevision = "feature" });

        // Assert
        Assert.Contains(diff.Entries, entry => entry.Path == "tracked.txt");
        Assert.NotEmpty(log.Commits);
        Assert.Equal(GitObjectKind.Blob, show.Kind);
        Assert.Contains("initial", show.Content, StringComparison.Ordinal);
        Assert.NotEmpty(blame.Lines);
        Assert.Equal(1, comparison.Ahead);
        Assert.Equal(0, comparison.Behind);
        Assert.Contains(comparison.ChangedPaths, entry => entry.Path == "tracked.txt");
    }

    /// <summary>Verifies Git queries cannot discover a worktree rooted above the opened directory.</summary>
    [Fact]
    public async Task GitQueries_OpenedSubdirectoryOfRepository_IsRejected()
    {
        await using var repository = await TestRepository.CreateAsync();
        var subdirectory = Path.Combine(repository.Path, "src");
        Directory.CreateDirectory(subdirectory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GitQueryService().DiffAsync(
            subdirectory,
            new GitDiffRequest()));
    }

    /// <summary>Verifies unsafe revision and path tokens fail before Git execution.</summary>
    [Fact]
    public async Task GitQueries_UnsafeInputs_AreRejected()
    {
        await using var repository = await TestRepository.CreateAsync();
        var service = new GitQueryService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.ShowAsync(
            repository.Path,
            new GitShowRequest { Revision = "HEAD:secret/token.txt" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.LogAsync(
            repository.Path,
            new GitLogRequest { Revision = "--upload-pack=evil" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.LogAsync(
            repository.Path,
            new GitLogRequest { Revision = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.BlameAsync(
            repository.Path,
            new GitBlameRequest { Path = "tracked.txt", Revision = string.Empty }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.BlameAsync(
            repository.Path,
            new GitBlameRequest { Path = "../outside.txt" }));
        var missingRevision = await Assert.ThrowsAsync<ArgumentException>(() => service.DiffAsync(
            repository.Path,
            new GitDiffRequest { Mode = GitComparisonMode.Commit }));
        Assert.Contains("BaseRevision", missingRevision.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required", missingRevision.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies strict nullable Git defaults are normalized before local Git execution.</summary>
    [Fact]
    public async Task GitQueries_NullDefaultableFields_UseDocumentedDefaults()
    {
        await using var repository = await TestRepository.CreateAsync();
        var service = new GitQueryService();

        var log = await service.LogAsync(
            repository.Path,
            new GitLogRequest { Revision = null, MaximumCommits = null });
        var diff = await service.DiffAsync(
            repository.Path,
            new GitDiffRequest
            {
                Mode = null,
                MaximumEntries = null,
                MaximumPatchCharacters = null,
            });

        Assert.NotEmpty(log.Commits);
        Assert.Equal(GitComparisonMode.WorkingTree, diff.Mode);
    }

    /// <summary>Verifies git_blame provenance reports the effective revision for explicit null input.</summary>
    [Fact]
    public async Task GitBlameTool_NullRevision_ReportsHeadProvenance()
    {
        await using var repository = await TestRepository.CreateAsync();
        var tool = new GitBlameTool(new GitQueryService());

        var execution = await tool.ExecuteAsync(
            new GitBlameRequest { Path = "tracked.txt", Revision = null },
            CreateExecutionContext(repository.Path));

        var source = Assert.Single(execution.Sources);
        Assert.Equal("git-blame", source.Kind);
        Assert.Equal("tracked.txt", source.Identifier);
        Assert.Equal("HEAD", source.Range);
    }

    /// <summary>Verifies git_diff validates mode-specific revision requirements before execution.</summary>
    [Fact]
    public void GitDiffTool_MissingModeRevision_IsActionableValidationFailure()
    {
        ITool tool = new GitDiffTool(new UnusedGitQueryService());

        var exception = Assert.Throws<ToolArgumentValidationException>(() =>
            tool.DeserializeInput("{\"mode\":\"Commit\",\"baseRevision\":null}"));

        Assert.Contains("BaseRevision", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("commit mode", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WorkingTree", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Staged", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies git_log accepts strict nullable defaults and rejects unsafe revisions clearly.</summary>
    [Fact]
    public void GitLogTool_DefaultsAndUnsafeRevision_AreActionableValidationResults()
    {
        ITool tool = new GitLogTool(new UnusedGitQueryService());

        var input = Assert.IsType<GitLogRequest>(tool.DeserializeInput(
            "{\"revision\":null,\"maximumCommits\":null}"));
        Assert.Null(input.Revision);
        Assert.Null(input.MaximumCommits);

        var exception = Assert.Throws<ToolArgumentValidationException>(() =>
            tool.DeserializeInput("{\"revision\":\"--upload-pack=evil\"}"));
        Assert.Contains("HEAD", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bounded non-option", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ToolArgumentValidationException>(() =>
            tool.DeserializeInput("{\"revision\":\"   \"}"));
    }

    /// <summary>Verifies literal pathspecs do not expand valid filename metacharacters.</summary>
    [Fact]
    public async Task GitDiff_LiteralPathFilter_DoesNotExpandPathspecMagic()
    {
        // Arrange
        await using var repository = await TestRepository.CreateAsync();
        var literal = Path.Combine(repository.Path, "literal[1].txt");
        var matchedByMagic = Path.Combine(repository.Path, "literal1.txt");
        await File.WriteAllTextAsync(literal, "initial\n");
        await File.WriteAllTextAsync(matchedByMagic, "initial\n");
        await repository.RunGitAsync("add", "literal[1].txt", "literal1.txt");
        await repository.RunGitAsync("commit", "-m", "add literal paths");
        await File.AppendAllTextAsync(literal, "literal\n");
        await File.AppendAllTextAsync(matchedByMagic, "magic\n");

        // Act
        var result = await new GitQueryService().DiffAsync(
            repository.Path,
            new GitDiffRequest { Path = "literal[1].txt" });

        // Assert
        var entry = Assert.Single(result.Entries);
        Assert.Equal("literal[1].txt", entry.Path);
        Assert.DoesNotContain("literal1.txt", result.Patch, StringComparison.Ordinal);
    }

    /// <summary>Verifies root commits compare against an empty tree.</summary>
    [Fact]
    public async Task GitDiff_RootCommit_ReturnsInitialChanges()
    {
        await using var repository = await TestRepository.CreateAsync();

        var result = await new GitQueryService().DiffAsync(
            repository.Path,
            new GitDiffRequest { Mode = GitComparisonMode.Commit, BaseRevision = "HEAD" });

        var entry = Assert.Single(result.Entries);
        Assert.Equal("tracked.txt", entry.Path);
        Assert.Contains("initial", result.Patch, StringComparison.Ordinal);
    }

    /// <summary>Verifies merge commits retain first-parent comparison behavior.</summary>
    [Fact]
    public async Task GitDiff_MergeCommit_ComparesFirstParent()
    {
        await using var repository = await TestRepository.CreateAsync();
        await repository.RunGitAsync("checkout", "-b", "feature");
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "tracked.txt"), "feature\n");
        await repository.RunGitAsync("add", "tracked.txt");
        await repository.RunGitAsync("commit", "-m", "feature");
        await repository.RunGitAsync("checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "main.txt"), "main\n");
        await repository.RunGitAsync("add", "main.txt");
        await repository.RunGitAsync("commit", "-m", "main");
        await repository.RunGitAsync("merge", "--no-ff", "feature", "-m", "merge feature");

        var result = await new GitQueryService().DiffAsync(
            repository.Path,
            new GitDiffRequest { Mode = GitComparisonMode.Commit, BaseRevision = "HEAD" });

        var entry = Assert.Single(result.Entries);
        Assert.Equal("tracked.txt", entry.Path);
        Assert.DoesNotContain("main.txt", result.Patch, StringComparison.Ordinal);
    }

    /// <summary>Verifies invalid UTF-8 blobs are classified before text decoding.</summary>
    [Fact]
    public async Task GitShow_InvalidUtf8Blob_ReturnsBinaryWithoutCorruptedText()
    {
        await using var repository = await TestRepository.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(repository.Path, "binary.dat"), [0xFF, 0xFE, 0xFD, 0xFC]);
        await repository.RunGitAsync("add", "binary.dat");
        await repository.RunGitAsync("commit", "-m", "add binary");

        var result = await new GitQueryService().ShowAsync(
            repository.Path,
            new GitShowRequest { Revision = "HEAD", Path = "binary.dat" });

        Assert.True(result.IsBinary);
        Assert.Empty(result.Content);
    }

    /// <summary>Verifies the process capture bound remains visible in normalized results.</summary>
    [Fact]
    public async Task GitShow_ProcessOutputExceedsCaptureBound_ReportsTruncation()
    {
        await using var repository = await TestRepository.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "large.txt"), new string('x', 600 * 1024));
        await repository.RunGitAsync("add", "large.txt");
        await repository.RunGitAsync("commit", "-m", "add large text");

        var result = await new GitQueryService().ShowAsync(
            repository.Path,
            new GitShowRequest
            {
                Revision = "HEAD",
                Path = "large.txt",
                MaximumCharacters = 512 * 1024,
            });

        Assert.True(result.IsTruncated);
        Assert.Equal(512 * 1024, result.Content.Length);
    }

    /// <summary>Verifies NUL-delimited status parsing preserves non-ASCII rename paths.</summary>
    [Fact]
    public async Task GitDiff_RenameWithNonAsciiPath_NormalizesExactPaths()
    {
        await using var repository = await TestRepository.CreateAsync();
        const string previous = "café-old.txt";
        const string current = "café-new.txt";
        await File.WriteAllTextAsync(Path.Combine(repository.Path, previous), "rename content\n");
        await repository.RunGitAsync("add", previous);
        await repository.RunGitAsync("commit", "-m", "add unusual path");
        await repository.RunGitAsync("mv", previous, current);

        var result = await new GitQueryService().DiffAsync(
            repository.Path,
            new GitDiffRequest { Mode = GitComparisonMode.Staged });

        var rename = Assert.Single(result.Entries, entry => entry.Status.StartsWith('R'));
        Assert.Equal(previous, rename.PreviousPath);
        Assert.Equal(current, rename.Path);
    }

    /// <summary>Verifies recursive Git output cannot return prohibited descendants.</summary>
    [Fact]
    public async Task GitTools_ProhibitedDescendants_AreFilteredBeforeReturn()
    {
        await using var repository = await TestRepository.CreateAsync();
        Directory.CreateDirectory(Path.Combine(repository.Path, "secret"));
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "secret", "token.txt"), "old secret\n");
        await repository.RunGitAsync("add", "secret/token.txt");
        await repository.RunGitAsync("commit", "-m", "add secret");
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "tracked.txt"), "public\n");
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "secret", "token.txt"), "new secret\n");
        var context = CreateExecutionContext(
            repository.Path,
            prohibitedPaths: ["secret/**"]);
        var tool = new GitDiffTool(new GitQueryService());

        var execution = await tool.ExecuteAsync(
            new GitDiffRequest(),
            context);

        Assert.Contains(execution.Value.Entries, entry => entry.Path == "tracked.txt");
        Assert.DoesNotContain(execution.Value.Entries, entry => entry.Path.StartsWith("secret/", StringComparison.Ordinal));
        Assert.Empty(execution.Value.Patch);
        Assert.True(execution.IsTruncated);
    }

    /// <summary>Verifies commit-show patches and branch paths honor descendant policy.</summary>
    [Fact]
    public async Task GitCommitAndBranchTools_ProhibitedDescendants_DoNotEnterEvidence()
    {
        await using var repository = await TestRepository.CreateAsync();
        Directory.CreateDirectory(Path.Combine(repository.Path, "secret"));
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "secret", "token.txt"), "old secret\n");
        await repository.RunGitAsync("add", "secret/token.txt");
        await repository.RunGitAsync("commit", "-m", "add private fixture");
        await repository.RunGitAsync("checkout", "-b", "feature");
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "tracked.txt"), "public feature\n");
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "secret", "token.txt"), "prohibited payload\n");
        await repository.RunGitAsync("add", "tracked.txt", "secret/token.txt");
        await repository.RunGitAsync("commit", "-m", "feature changes");
        await repository.RunGitAsync("checkout", "main");
        var context = CreateExecutionContext(
            repository.Path,
            prohibitedPaths: ["secret/**"]);

        var show = await new GitShowTool(new GitQueryService()).ExecuteAsync(
            new GitShowRequest { Revision = "feature" },
            context);
        var comparison = await new GitBranchComparisonTool(
            new GitQueryService()).ExecuteAsync(
                new GitBranchComparisonRequest { BaseRevision = "main", TargetRevision = "feature" },
                context);

        Assert.DoesNotContain("prohibited payload", show.Value.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("diff --git ", show.Value.Content, StringComparison.Ordinal);
        Assert.True(show.IsTruncated);
        var changedPath = Assert.Single(comparison.Value.ChangedPaths);
        Assert.Equal("tracked.txt", changedPath.Path);
        Assert.True(comparison.IsTruncated);
    }

    /// <summary>Verifies central version attribution and test-project classification.</summary>
    [Fact]
    public async Task DotNetInventory_LoadedWorkspace_ReportsCentralPackagesAndTestProjects()
    {
        // Arrange
        await using var repository = await TestRepository.CreateDotNetAsync();
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        var workspaceId = WorkspaceId.New();
        var load = await registry.LoadAsync(new SemanticLoadRequest(
            SessionId.New(),
            workspaceId,
            repository.Path,
            Path.Combine(repository.Path, "Inventory.Tests.csproj"),
            RepositoryTrustLevel.TrustedRead));
        var service = new DotNetInventoryService(registry, new GitQueryService());

        // Act
        var result = await service.GetInventoryAsync(new DotNetInventoryRequest
        {
            WorkspaceId = workspaceId,
            RepositoryPath = repository.Path,
            SelectedSolutionPath = "caller-controlled.sln",
        });

        // Assert
        var project = Assert.Single(result.Solution.Projects);
        Assert.True(project.IsTestProject);
        var package = Assert.Single(
            project.PackageReferences,
            item => item.Id == "Example.Package");
        Assert.Equal("1.2.3", package.Version);
        Assert.Equal(PackageVersionSource.Central, package.VersionSource);
        Assert.Equal(load.Confidence, result.Confidence);
        Assert.NotNull(result.RepositoryRevision);
        Assert.Equal("Inventory.Tests.csproj", result.Solution.Path);
        var resources = service.GetResourcePaths(new DotNetInventoryRequest
        {
            WorkspaceId = workspaceId,
            RepositoryPath = repository.Path,
            SelectedSolutionPath = "caller-controlled.sln",
        });
        Assert.Contains(resources, path => path.EndsWith("Inventory.Tests.csproj", StringComparison.Ordinal));
        Assert.Contains(resources, path => path.EndsWith("Directory.Packages.props", StringComparison.Ordinal));

        var inventoryTool = new DotNetInventoryTool(service);
        var policy = new DefaultPolicyEngine();
        var decision = policy.Evaluate(
            inventoryTool,
            new DotNetInventoryRequest
            {
                WorkspaceId = workspaceId,
                RepositoryPath = repository.Path,
                SelectedSolutionPath = "caller-controlled.sln",
            },
            CreateInvocationContext(
                repository.Path,
                prohibitedPaths: ["Directory.Packages.props"],
                allowedExecutables: ["git"]));
        Assert.False(decision.IsAllowed);
    }

    /// <summary>Verifies every Plan 41 operation has a distinct typed schema.</summary>
    [Fact]
    public void ToolDefinitions_AreDistinctBoundedReadOnlyContracts()
    {
        var git = new GitQueryService();
        ITool[] tools =
        [
            new GitDiffTool(git),
            new GitLogTool(git),
            new GitShowTool(git),
            new GitBlameTool(git),
            new GitBranchComparisonTool(git),
        ];

        Assert.Equal(5, tools.Select(tool => tool.Definition.Id).Distinct().Count());
        Assert.All(tools, tool => Assert.Equal(ToolSideEffect.ReadOnly, tool.Definition.SideEffect));
        Assert.All(tools, tool => Assert.InRange(tool.Definition.MaximumOutputBytes, 1, 512 * 1024));
        Assert.All(tools, tool => Assert.Equal("git", tool.GetExecutable(tool switch
        {
            GitDiffTool => new GitDiffRequest(),
            GitLogTool => new GitLogRequest(),
            GitShowTool => new GitShowRequest { Revision = "HEAD" },
            GitBlameTool => new GitBlameRequest { Path = "tracked.txt" },
            GitBranchComparisonTool => new GitBranchComparisonRequest
            {
                BaseRevision = "main",
                TargetRevision = "feature",
            },
            _ => throw new InvalidOperationException("Unexpected Git tool."),
        })));

        var denied = new DefaultPolicyEngine().Evaluate(
            tools[0],
            new GitDiffRequest(),
            CreateInvocationContext(Path.GetTempPath()));
        Assert.False(denied.IsAllowed);
        Assert.Contains("not allow-listed", denied.Reason, StringComparison.Ordinal);
    }

    private static ToolExecutionContext CreateExecutionContext(
        string repositoryPath,
        IReadOnlyList<string>? prohibitedPaths = null)
    {
        return new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            CreateInvocationContext(
                repositoryPath,
                prohibitedPaths,
                allowedExecutables: ["git"]));
    }

    private static ToolInvocationContext CreateInvocationContext(
        string repositoryPath,
        IReadOnlyList<string>? prohibitedPaths = null,
        IReadOnlyList<string>? allowedExecutables = null)
    {
        return new ToolInvocationContext
        {
            RepositoryPath = repositoryPath,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            ApprovedRoots = ["."],
            ProhibitedPaths = prohibitedPaths ?? [],
            AllowedExecutables = allowedExecutables ?? [],
            RequestedBy = "plan-41-tests",
        };
    }

    private sealed class UnusedGitQueryService : IGitQueryService
    {
        public Task<string?> GetCurrentBranchAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The validation test must not query Git.");
        }

        public Task<string?> GetRevisionAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The validation test must not query Git.");
        }

        public Task<GitDiffResult> DiffAsync(
            string repositoryPath,
            GitDiffRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The validation test must not query Git.");
        }

        public Task<GitLogResult> LogAsync(
            string repositoryPath,
            GitLogRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The validation test must not query Git.");
        }

        public Task<GitShowResult> ShowAsync(
            string repositoryPath,
            GitShowRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The validation test must not query Git.");
        }

        public Task<GitBlameResult> BlameAsync(
            string repositoryPath,
            GitBlameRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The validation test must not query Git.");
        }

        public Task<GitBranchComparisonResult> CompareBranchesAsync(
            string repositoryPath,
            GitBranchComparisonRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The validation test must not query Git.");
        }
    }

    private sealed class TestRepository : IAsyncDisposable
    {
        private TestRepository(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static async Task<TestRepository> CreateAsync()
        {
            var repository = new TestRepository(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "threadsmith-plan41-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(repository.Path);
            await repository.RunGitAsync("init", "-b", "main");
            await repository.RunGitAsync("config", "user.email", "test@example.invalid");
            await repository.RunGitAsync("config", "user.name", "Threadsmith Tests");
            await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "tracked.txt"), "initial\n");
            await repository.RunGitAsync("add", "tracked.txt");
            await repository.RunGitAsync("commit", "-m", "initial commit");
            return repository;
        }

        public static async Task<TestRepository> CreateDotNetAsync()
        {
            var repository = await CreateAsync();
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(repository.Path, "Directory.Packages.props"),
                "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Example.Package\" Version=\"1.2.3\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(repository.Path, "Inventory.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup></Project>");
            return repository;
        }

        public async Task RunGitAsync(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = Path,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
