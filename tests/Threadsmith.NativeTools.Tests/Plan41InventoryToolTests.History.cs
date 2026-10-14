namespace Threadsmith.NativeTools.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class Plan41InventoryToolTests
{
    /// <summary>History includes merge parents, empty commits, and a stable starting revision.</summary>
    [Fact]
    public async Task ChronologicalHistoryIncludesMergedAndEmptyCommitsAndPinsHead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync();
        await repository.RunGitAsync("checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "feature.txt"), "feature", ct);
        await repository.RunGitAsync("add", ".");
        await repository.RunGitAsync("commit", "-m", "feature\n\nWhy this pattern exists.");
        await repository.RunGitAsync("checkout", "main");
        await repository.RunGitAsync("commit", "--allow-empty", "-m", "empty change");
        await repository.RunGitAsync("merge", "--no-ff", "feature", "-m", "integrate feature");
        var pinned = (await repository.RunGitAsync("rev-parse", "HEAD")).Trim();
        await using var reader = await new GitQueryService().OpenHistoryAsync(repository.Path, ct);
        await repository.RunGitAsync("commit", "--allow-empty", "-m", "after pin");
        var commits = new List<GitCommitSummary>();
        GitLogResult page;
        do
        {
            page = await reader.ReadPageAsync(1, 4096, ct);
            Assert.Equal(pinned, page.HistoryRevision);
            commits.AddRange(page.Commits);
        }
        while (page.NextCollectionPage is not null);

        Assert.Equal(4, commits.Count);
        Assert.Equal("initial commit\n", commits[0].Message);
        Assert.Equal(pinned, commits[^1].Commit);
        Assert.Contains("feature.txt", commits[^1].ChangedPaths);
        Assert.Contains(commits, commit => commit.Message == "feature\n\nWhy this pattern exists.\n");
        Assert.Empty(Assert.Single(commits, commit => commit.Message == "empty change\n").ChangedPaths);
        var positions = commits.Select((commit, index) => (commit.Commit, index)).ToDictionary(item => item.Commit, item => item.index);
        Assert.All(commits, commit => Assert.All(commit.Parents, parent => Assert.True(positions[parent] < positions[commit.Commit])));
    }

    /// <summary>Depth selects newest commits before reversing; shallow boundaries cannot masquerade as roots.</summary>
    [Fact]
    public async Task ChronologicalDepthSelectsNewestCommitsAndValidatesShallowBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync();
        var boundary = (await repository.RunGitAsync("rev-parse", "HEAD")).Trim();
        for (var index = 1; index <= 4; index++)
        {
            await repository.RunGitAsync("commit", "--allow-empty", "-m", $"step {index}");
        }

        await File.WriteAllTextAsync(Path.Combine(repository.Path, ".git", "shallow"), boundary + "\n", ct);
        var service = new GitQueryService();
        await using (var reader = await service.OpenHistoryAsync(repository.Path, 2, ct))
        {
            var page = await reader.ReadPageAsync(16, 4096, ct);
            Assert.Equal(["step 3\n", "step 4\n"], page.Commits.Select(commit => commit.Message));
            Assert.Equal(2, page.HistoryDepth);
            Assert.Null(page.NextCollectionPage);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenHistoryAsync(repository.Path, ct));
        await using var incomplete = await service.OpenHistoryAsync(repository.Path, 5, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => incomplete.ReadPageAsync(16, 4096, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.OpenHistoryAsync(repository.Path, 0, ct));
    }

    /// <summary>Paging preserves messages and paths, including serialized supplementary Unicode.</summary>
    [Fact]
    public async Task ChronologicalHistorySplitsLargeMessagesAndPathListsWithoutDroppingContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync();
        var paths = Enumerable.Range(0, 160).Select(index => $"file-{index:D3}-{new string('x', 45)}.txt").ToArray();
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(Path.Combine(repository.Path, path), "content", ct);
        }

        var message = new string('m', 1943) + "\U0001F600" + new string('m', 10000);
        await repository.RunGitAsync("add", ".");
        await repository.RunGitAsync("commit", "-m", message);
        var hash = (await repository.RunGitAsync("rev-parse", "HEAD")).Trim();
        await using var reader = await new GitQueryService().OpenHistoryAsync(repository.Path, ct);
        var segments = new List<GitCommitSummary>();
        GitLogResult page;
        do
        {
            page = await reader.ReadPageAsync(1, 4096, ct);
            page = JsonSerializer.Deserialize<GitLogResult>(JsonSerializer.Serialize(page))!;
            segments.AddRange(page.Commits.Where(commit => commit.Commit == hash));
            Assert.True(page.Commits.Sum(commit => (commit.Message?.Length ?? 0) + commit.ChangedPaths.Sum(path => path.Length)) <= 4096);
        }
        while (page.NextCollectionPage is not null);

        Assert.True(segments.Count > 2);
        Assert.False(segments[0].IsContinuation);
        Assert.All(segments.Skip(1), segment => Assert.True(segment.IsContinuation));
        Assert.False(segments[^1].PathsContinue);
        Assert.Equal(message + "\n", string.Concat(segments.Select(segment => segment.Message)));
        Assert.Equal(paths.Order(StringComparer.Ordinal), segments.SelectMany(segment => segment.ChangedPaths).Order(StringComparer.Ordinal));
    }

    /// <summary>Repositories without history and cancelled traversals have explicit outcomes.</summary>
    [Fact]
    public async Task ChronologicalHistoryHandlesUnbornAndNonGitRepositoriesAndCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync();
        await repository.RunGitAsync("checkout", "--orphan", "unborn");
        await using (var reader = await new GitQueryService().OpenHistoryAsync(repository.Path, ct))
        {
            var page = await reader.ReadPageAsync(16, 4096, ct);
            Assert.Empty(page.Commits);
            Assert.Null(page.NextCollectionPage);
            Assert.True(page.IsGitRepository);
        }

        var plain = Directory.CreateDirectory(Path.Combine(repository.Path, "plain")).FullName;
        await using (var reader = await new GitQueryService().OpenHistoryAsync(plain, ct))
        {
            Assert.False((await reader.ReadPageAsync(16, 4096, ct)).IsGitRepository);
        }

        await repository.RunGitAsync("checkout", "main");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using (var reader = await new GitQueryService().OpenHistoryAsync(repository.Path, cancellation.Token))
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadPageAsync(16, 4096, cancellation.Token));
        }
    }
}
