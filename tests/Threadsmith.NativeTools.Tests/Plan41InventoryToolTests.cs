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
    /// <summary>Verifies Git queries cannot discover a worktree rooted above the opened directory.</summary>
    [Fact]
    public async Task GitQueries_OpenedSubdirectoryOfRepository_IsRejected()
    {
        await using var repository = await TestRepository.CreateAsync();
        var subdirectory = Path.Combine(repository.Path, "src");
        Directory.CreateDirectory(subdirectory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)).DiffAsync(
            subdirectory,
            new GitDiffRequest()));
    }

    /// <summary>Verifies unsafe revision and path tokens fail before Git execution.</summary>
    [Fact]
    public async Task GitQueries_UnsafeInputs_AreRejected()
    {
        await using var repository = await TestRepository.CreateAsync();
        var service = new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));

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

    /// <summary>Byte capture ends before a partial UTF-8 scalar without misclassifying text as binary.</summary>
    [Theory]
    [InlineData("é")]
    [InlineData("€")]
    [InlineData("😀")]
    public async Task GitShow_CaptureSplitsUtf8Scalar_ReturnsValidTruncatedPrefix(string scalar)
    {
        await using var repository = await TestRepository.CreateAsync();
        var prefix = new string('x', 511);
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "unicode.txt"), prefix + scalar + "tail");
        await repository.RunGitAsync("add", "unicode.txt");
        await repository.RunGitAsync("commit", "-m", "add unicode");

        var result = await new GitQueryService(new GitResourceLimits { MaximumCapturedCharacters = 512 }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)).ShowAsync(
            repository.Path,
            new GitShowRequest { Revision = "HEAD", Path = "unicode.txt" });

        Assert.False(result.IsBinary);
        Assert.True(result.IsTruncated);
        Assert.Equal(prefix, result.Content);
    }

    /// <summary>Verifies invalid UTF-8 blobs are classified before text decoding.</summary>
    [Fact]
    public async Task GitShow_InvalidUtf8Blob_ReturnsBinaryWithoutCorruptedText()
    {
        await using var repository = await TestRepository.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(repository.Path, "binary.dat"), [0xFF, 0xFE, 0xFD, 0xFC]);
        await repository.RunGitAsync("add", "binary.dat");
        await repository.RunGitAsync("commit", "-m", "add binary");

        var result = await new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)).ShowAsync(
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

        var result = await new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)).ShowAsync(
            repository.Path,
            new GitShowRequest
            {
                Revision = "HEAD",
                Path = "large.txt",
            });

        Assert.True(result.IsTruncated);
        Assert.Equal(131072, result.Content.Length);
    }

    /// <summary>Verifies recursive Git output cannot return prohibited descendants.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitTools_ProhibitedDescendants_AreFilteredBeforeReturn(bool includePatch)
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
        var tool = new GitDiffTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance);

        var execution = await tool.ExecuteAsync(
            new GitDiffInput { IncludePatch = includePatch },
            context);

        Assert.Contains(execution.Value.Entries, entry => entry.Path == "tracked.txt");
        Assert.DoesNotContain(execution.Value.Entries, entry => entry.Path.StartsWith("secret/", StringComparison.Ordinal));
        Assert.Empty(execution.Value.Patch);
        Assert.Equal(includePatch, execution.IsTruncated);
        Assert.NotNull(execution.ModelResultContent);
        Assert.Contains("tracked.txt", execution.ModelResultContent, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", execution.ModelResultContent, StringComparison.OrdinalIgnoreCase);
        using var projection = JsonDocument.Parse(execution.ModelResultContent);
        Assert.NotEmpty(projection.RootElement.GetProperty("changedPaths").EnumerateArray());
        Assert.Equal(1, projection.RootElement.GetProperty("omittedPaths").GetInt32());
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

        var show = await new GitShowTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance).ExecuteAsync(
            new GitShowInput { Revision = "feature" },
            context);
        var comparison = await new GitBranchComparisonTool(
            new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)),
            TestPromptLoader.Instance).ExecuteAsync(
                new GitBranchComparisonRequest { BaseRevision = "main", TargetRevision = "feature" },
                context);

        Assert.DoesNotContain("prohibited payload", show.Value.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("diff --git ", show.Value.Content, StringComparison.Ordinal);
        Assert.True(show.IsTruncated);
        var changedPath = Assert.Single(comparison.Value.ChangedPaths);
        Assert.Equal("tracked.txt", changedPath.Path);
        Assert.True(comparison.IsTruncated);
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
