namespace Threadsmith.NativeTools.Tests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies Plan 41 closed Git and .NET inventory behavior.</summary>
public sealed partial class Plan41InventoryToolTests
{
    /// <summary>Verifies git_diff validates mode-specific revision requirements before execution.</summary>
    [Fact]
    public void GitDiffTool_MissingModeRevision_IsActionableValidationFailure()
    {
        ITool tool = new GitDiffTool(new UnusedGitQueryService(), TestPromptLoader.Instance);

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
        ITool tool = new GitLogTool(new UnusedGitQueryService(), TestPromptLoader.Instance);

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

    private static ToolExecutionContext CreateExecutionContext(
        string repositoryPath,
        IReadOnlyList<string>? prohibitedPaths = null,
        WorkspaceId? workspaceId = null)
    {
        return new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            CreateInvocationContext(
                repositoryPath,
                prohibitedPaths,
                allowedExecutables: ["git"],
                workspaceId: workspaceId));
    }

    private static ToolInvocationContext CreateInvocationContext(
        string repositoryPath,
        IReadOnlyList<string>? prohibitedPaths = null,
        IReadOnlyList<string>? allowedExecutables = null,
        WorkspaceId? workspaceId = null)
    {
        return new ToolInvocationContext
        {
            RepositoryPath = repositoryPath,
            WorkspaceId = workspaceId,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            ApprovedRoots = ["."],
            ProhibitedPaths = prohibitedPaths ?? [],
            AllowedExecutables = allowedExecutables ?? [],
            RequestedBy = "plan-41-tests",
        };
    }

    private sealed class StubDotNetInventoryService : IDotNetInventoryService
    {
        private readonly DotNetInventoryResult _result;

        public StubDotNetInventoryService(DotNetInventoryResult result)
        {
            _result = result;
        }

        public IReadOnlyList<string> GetResourcePaths(DotNetInventoryRequest request)
        {
            return [];
        }

        public Task<DotNetInventoryResult> GetInventoryAsync(
            DotNetInventoryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
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
            // Git resolves platform temp-directory aliases (for example /var on macOS).
            return new TestRepository((await repository.RunGitAsync("rev-parse", "--show-toplevel")).Trim());
        }

        public static async Task<TestRepository> CreateDotNetAsync()
        {
            var repository = await CreateAsync();
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(repository.Path, "Directory.Packages.props"),
                "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Example.Package\" Version=\"1.2.3\" /><PackageVersion Include=\"Unused.Pin\" Version=\"2.0.0\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(repository.Path, "Inventory.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup></Project>");
            return repository;
        }

        public async Task<string> RunGitAsync(params string[] arguments)
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
            var cancellationToken = TestContext.Current.CancellationToken;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(process.ExitCode == 0, error);
            return output;
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
