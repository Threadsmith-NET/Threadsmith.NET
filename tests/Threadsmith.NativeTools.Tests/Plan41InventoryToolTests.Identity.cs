namespace Threadsmith.NativeTools.Tests;

using System.Diagnostics;
using Threadsmith.Core;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class Plan41InventoryToolTests
{
    /// <summary>Logical root aliases work for ordinary and linked worktrees without admitting subdirectories or escaping paths.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitQueries_LogicalRootAlias_PreservesWorktreeBoundary(bool linkedWorktree)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync();
        var alias = Path.Combine(repository.Path, "root-alias");
        var root = repository.Path;
        if (linkedWorktree)
        {
            root = Path.Combine(repository.Path, "linked-worktree");
            await repository.RunGitAsync("worktree", "add", "--detach", root, "HEAD");
        }

        try
        {
            await CreateDirectoryAliasAsync(alias, repository.Path, ct);
            var logicalRoot = linkedWorktree ? Path.Combine(alias, "linked-worktree") : alias;
            var service = new GitQueryService();
            var physical = await service.ShowAsync(root, new GitShowRequest { Revision = "HEAD", Path = "tracked.txt" }, ct);
            var logical = await service.ShowAsync(logicalRoot, new GitShowRequest { Revision = "HEAD", Path = "tracked.txt" }, ct);
            Assert.Equal("initial\n", physical.Content);
            Assert.Equal(physical.Content, logical.Content);
            Assert.NotEmpty((await service.LogAsync(logicalRoot, new GitLogRequest(), ct)).Commits);
            if (OperatingSystem.IsWindows())
            {
                var differentCase = await service.ShowAsync(logicalRoot.ToUpperInvariant(), new GitShowRequest { Revision = "HEAD", Path = "tracked.txt" }, ct);
                Assert.Equal(physical.Content, differentCase.Content);
            }

            var subdirectory = Directory.CreateDirectory(Path.Combine(logicalRoot, "src")).FullName;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DiffAsync(subdirectory, new GitDiffRequest(), ct));
            await Assert.ThrowsAsync<ArgumentException>(() => service.ShowAsync(logicalRoot, new GitShowRequest { Revision = "HEAD", Path = "../outside.txt" }, ct));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.DiffAsync(Path.Combine(logicalRoot, "missing"), new GitDiffRequest(), ct));
        }
        finally
        {
            // Remove the alias itself before recursive fixture cleanup, never its target.
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }
        }
    }

    private static async Task CreateDirectoryAliasAsync(string alias, string target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", Path.GetFullPath(alias), Path.GetFullPath(target) })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error).WaitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while termination was being requested.
            }

            // Cleanup owns termination and pipe draining even after the test token is cancelled.
            await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), output, error)
                .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            throw;
        }

        Assert.True(process.ExitCode == 0, await output + await error);
    }
}
