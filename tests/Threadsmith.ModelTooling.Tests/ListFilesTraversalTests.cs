namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises pruning through the production enumerable and authorized list tool.</summary>
public static class ListFilesTraversalTests
{
    /// <summary>Only reportable files consume limits or trigger truncation.</summary>
    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(3, 2, true)]
    [InlineData(2, 0, false)]
    [InlineData(3, 0, true)]
    [InlineData(2, 100, false)]
    [InlineData(3, 100, true)]
    public static async Task LimitsCountOnlyVisibleFiles(int visibleFiles, int requestedLimit, bool truncated)
    {
        using var fixture = new Fixture();
        for (var index = 0; index < visibleFiles; index++)
        {
            fixture.Write($"src/file-{index}.cs");
        }

        fixture.Write(".git/nested/ignored.txt");
        fixture.Write("src/bin/nested/ignored.dll");
        fixture.Write("src/obj/nested/ignored.cs");
        var tool = new ListFilesTool(TestPromptLoader.Instance, new ToolLimits { ListFilesDefaultEntries = 2, ListFilesMaxEntries = 2 });
        var result = await tool.ExecuteAsync(new ListFilesInput { MaximumEntries = requestedLimit }, fixture.Context, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Value.Files.Count);
        Assert.All(result.Value.Files, file => Assert.StartsWith("src/file-", file.Path));
        Assert.Equal(truncated, result.Value.IsTruncated);
    }

    /// <summary>Explicit scratchpads retain projection even when their root is named bin.</summary>
    [Theory]
    [InlineData(".scratch")]
    [InlineData("bin")]
    public static async Task ScratchpadProjectionKeepsItsOwnRootAndExclusions(string scratchpadName)
    {
        using var fixture = new Fixture();
        fixture.Write("source.cs");
        fixture.Write($"{scratchpadName}/notes.md");
        fixture.Write($"{scratchpadName}/analysis/result.md");
        fixture.Write($"{scratchpadName}/analysis/bin/ignored.dll");
        fixture.Write($"{scratchpadName}/analysis/obj/ignored.cs");
        fixture.Write($"{scratchpadName}/analysis/.git/ignored.txt");
        var context = fixture.Context with
        {
            Invocation = fixture.Context.Invocation with
            {
                Scratchpad = new ScratchpadSessionCapability
                {
                    IsActive = true,
                    DisabledReason = ScratchpadDisabledReason.None,
                    RootPath = Path.Combine(fixture.Repository, scratchpadName),
                    ModelPath = scratchpadName,
                },
            },
        };
        var tool = new ListFilesTool(TestPromptLoader.Instance);
        var ct = TestContext.Current.CancellationToken;
        var repository = await tool.ExecuteAsync(new ListFilesInput(), context, ct);
        var explicitListing = await tool.ExecuteAsync(new ListFilesInput { Path = scratchpadName }, context, ct);
        var nested = await tool.ExecuteAsync(new ListFilesInput { Path = $"{scratchpadName}/analysis" }, context, ct);
        Assert.Equal("source.cs", Assert.Single(repository.Value.Files).Path);
        Assert.Equal(["analysis/result.md", "notes.md"], explicitListing.Value.Files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal("analysis/result.md", Assert.Single(nested.Value.Files).Path);
    }

    /// <summary>Cancellation still propagates at the real tool traversal boundary.</summary>
    [Fact]
    public static async Task CanceledListingThrowsWhenTraversingIncludedFiles()
    {
        using var fixture = new Fixture();
        fixture.Write("src/nested/visible.cs");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ListFilesTool(TestPromptLoader.Instance).ExecuteAsync(new ListFilesInput(), fixture.Context, cancellation.Token));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Repository = Directory.CreateDirectory(Path.Combine(TestTemporaryPath.PhysicalPath(), "threadsmith-list-traversal-tests-" + Guid.NewGuid().ToString("N"))).FullName;
            Context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
            {
                RepositoryPath = Repository,
                TrustLevel = RepositoryTrustLevel.TrustedRead,
                RequestedBy = "model",
            });
        }

        public string Repository { get; }

        public ToolExecutionContext Context { get; }

        public void Write(string relativePath)
        {
            var path = Path.GetFullPath(relativePath, Repository);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }

        public string Relative(string path) => Path.GetRelativePath(Repository, path).Replace('\\', '/');

        public void Dispose()
        {
            if (!Path.GetFileName(Repository).StartsWith("threadsmith-list-traversal-tests-", StringComparison.Ordinal)
                || !string.Equals(Path.GetDirectoryName(Repository), TestTemporaryPath.PhysicalPath(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected fixture cleanup root.");
            }

            Directory.Delete(Repository, recursive: true);
        }
    }
}
