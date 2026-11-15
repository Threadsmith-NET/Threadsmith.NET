namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises pruning through the production enumerable and authorized list tool.</summary>
public static class ListFilesTraversalTests
{
    /// <summary>Excluded descendants never reach predicates; similarly spelled directories remain visible.</summary>
    [Theory]
    [InlineData(".git", false)]
    [InlineData("bin", false)]
    [InlineData("obj", false)]
    [InlineData(".GIT", true)]
    [InlineData("Bin", true)]
    [InlineData("OBJ", true)]
    [InlineData("binary", true)]
    [InlineData("objects", true)]
    [InlineData("node_modules", true)]
    public static async Task EnumerationPrunesOnlyExactLowercaseNames(string directoryName, bool shouldDescend)
    {
        // Each spelling has its own tree, including on case-insensitive filesystems.
        using var fixture = new Fixture();
        fixture.Write("src/visible.cs");
        var candidate = $"src/{directoryName}";
        var descendant = $"{candidate}/descendant";
        fixture.Write($"{descendant}/hidden.cs");
        var enumeration = ListFilesTool.EnumerateFiles(fixture.Repository);
        var include = enumeration.ShouldIncludePredicate;
        var recurse = enumeration.ShouldRecursePredicate;
        Assert.NotNull(include);
        Assert.NotNull(recurse);
        var includedEntries = new List<string>();
        var recursionEntries = new List<string>();
        enumeration.ShouldIncludePredicate = (ref entry) =>
        {
            includedEntries.Add(fixture.Relative(entry.ToFullPath()));
            return include(ref entry);
        };
        enumeration.ShouldRecursePredicate = (ref entry) =>
        {
            recursionEntries.Add(fixture.Relative(entry.ToFullPath()));
            return recurse(ref entry);
        };

        var paths = enumeration.Select(fixture.Relative).ToArray();
        Assert.Contains(candidate, recursionEntries);
        Assert.Contains("src/visible.cs", paths);
        if (shouldDescend)
        {
            Assert.Contains(descendant, recursionEntries);
            Assert.Contains($"{descendant}/hidden.cs", includedEntries);
        }
        else
        {
            Assert.DoesNotContain(recursionEntries, path => path.StartsWith(candidate + "/", StringComparison.Ordinal));
            Assert.DoesNotContain(includedEntries, path => path.StartsWith(candidate + "/", StringComparison.Ordinal));
        }

        var result = await new ListFilesTool(TestPromptLoader.Instance).ExecuteAsync(new ListFilesInput(), fixture.Context, TestContext.Current.CancellationToken);
        string[] expected = shouldDescend ? ["src/visible.cs", $"{descendant}/hidden.cs"] : ["src/visible.cs"];
        Assert.Equal(expected.OrderBy(path => path, StringComparer.Ordinal), result.Value.Files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.False(result.Value.IsTruncated);
    }

    /// <summary>Explicit repository roots beneath excluded segments retain their empty projection.</summary>
    [Theory]
    [InlineData(".git")]
    [InlineData(".git/nested")]
    [InlineData("bin")]
    [InlineData("bin/nested")]
    [InlineData("obj")]
    [InlineData("obj/nested")]
    [InlineData("src/bin/nested")]
    public static async Task ExplicitExcludedRootsRemainEmpty(string path)
    {
        using var fixture = new Fixture();
        fixture.Write($"{path}/file.cs");
        var result = await new ListFilesTool(TestPromptLoader.Instance).ExecuteAsync(new ListFilesInput { Path = path }, fixture.Context, TestContext.Current.CancellationToken);
        Assert.Empty(result.Value.Files);
        Assert.False(result.Value.IsTruncated);
    }

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

    /// <summary>Reports raw traversal work on one built repository; elapsed time is evidence, never a correctness gate.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Performance")]
    public static void MeasureBuiltRepositoryTraversal()
    {
        var root = Environment.GetEnvironmentVariable("THREADSMITH_LIST_TRAVERSAL_REPOSITORY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), "Set THREADSMITH_LIST_TRAVERSAL_REPOSITORY to an existing built repository.");
        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine("Raw file enumeration plus the existing segment filter, limit 10000; excludes authorization, metadata and scratchpad projection. One warmup and seven warm samples per variant; no cold-cache claim.");
        foreach (var pruned in new[] { false, true })
        {
            var durations = new List<double>();
            var visits = new List<int>();
            var returned = 0;
            for (var sample = 0; sample < 8; sample++)
            {
                var count = 0;
                var enumeration = pruned ? ListFilesTool.EnumerateFiles(root)
                    : new System.IO.Enumeration.FileSystemEnumerable<string>(root, static (ref entry) => entry.ToFullPath(), new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    });
                var include = enumeration.ShouldIncludePredicate;
                enumeration.ShouldIncludePredicate = (ref entry) =>
                {
                    count++;
                    return include is null ? !entry.IsDirectory : include(ref entry);
                };
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                returned = 0;
                foreach (var path in enumeration)
                {
                    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (relative.Split('/').Any(segment => segment is ".git" or "bin" or "obj"))
                    {
                        continue;
                    }

                    if (++returned == 10000)
                    {
                        break;
                    }
                }

                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (sample > 0)
                {
                    durations.Add(elapsed);
                    visits.Add(count);
                }
            }

            durations.Sort();
            output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"pruned={pruned}; returned={returned}; visits={string.Join(',', visits)}; median={durations[3]:F3}ms; range={durations[0]:F3}..{durations[^1]:F3}ms"));
        }
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
