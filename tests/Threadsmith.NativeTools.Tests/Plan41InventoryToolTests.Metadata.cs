namespace Threadsmith.NativeTools.Tests;

using Threadsmith.Core;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class Plan41InventoryToolTests
{
    /// <summary>Request metadata allowances cannot enlarge the host's capture ceiling.</summary>
    [Theory]
    [InlineData("a")]
    [InlineData("ü")]
    public async Task MetadataLogPreservesConfiguredHostCaptureCeiling(string character)
    {
        await using var repository = await TestRepository.CreateAsync();
        await repository.RunGitAsync("-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", string.Concat(Enumerable.Repeat(character, 6000)));
        var limits = new GitResourceLimits { MaximumCapturedCharacters = 512 };

        var result = await new GitQueryService(limits).LogAsync(repository.Path, new() { MaximumMetadataBytes = 16384 }, TestContext.Current.CancellationToken);

        Assert.True(result.IsTruncated);
        Assert.Empty(result.Commits);
        Assert.InRange(result.AcquiredMetadataBytes, 1, limits.MaximumCapturedCharacters + 1);
    }

    /// <summary>Ordinary parent ancestry fits an explicitly small accepted metadata budget.</summary>
    [Fact]
    public async Task CommitDiffWith128ByteBudgetCollectsOrdinaryParentChanges()
    {
        await using var repository = await TestRepository.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "a"), "new", token);
        await repository.RunGitAsync("add", "a");
        await repository.RunGitAsync("-c", "commit.gpgSign=false", "commit", "-m", "parented");
        var commit = (await repository.RunGitAsync("rev-parse", "HEAD")).Trim();

        var result = await new GitQueryService().DiffAsync(repository.Path, new() { Mode = GitComparisonMode.Commit, BaseRevision = commit, IncludePatch = false, MaximumMetadataBytes = 128 }, token);

        Assert.Contains(result.Entries, entry => entry.Path == "a");
        Assert.False(result.IsTruncated);
        Assert.Empty(result.Patch);
        Assert.InRange(result.AcquiredMetadataBytes, 1, 128);
    }

    /// <summary>Insufficient ancestry capture is a truncation result, not malformed valid Git output.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(80)]
    public async Task TinyCommitDiffBudgetReportsTruncation(int maximumBytes)
    {
        await using var repository = await TestRepository.CreateAsync();
        await repository.RunGitAsync("-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "parented");

        var result = await new GitQueryService().DiffAsync(repository.Path, new() { Mode = GitComparisonMode.Commit, BaseRevision = "HEAD", IncludePatch = false, MaximumMetadataBytes = maximumBytes }, TestContext.Current.CancellationToken);

        Assert.True(result.IsTruncated);
        Assert.Empty(result.Entries);
        Assert.InRange(result.AcquiredMetadataBytes, 0, maximumBytes);
    }

    /// <summary>Git ceilings come from configured host limits, including allowances above the former fixed ceiling.</summary>
    [Fact]
    public async Task MetadataAndHistoryCursorUseConfiguredCeilings()
    {
        await using var repository = await TestRepository.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var service = new GitQueryService(new() { MaximumMetadataBytes = 100000, MaximumHistoryOffset = 1 });

        var result = await service.LogAsync(repository.Path, new() { MaximumMetadataBytes = 80000, Offset = 1 }, token);

        Assert.Empty(result.Commits);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.LogAsync(repository.Path, new() { MaximumMetadataBytes = 100001 }, token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.LogAsync(repository.Path, new() { Offset = 2 }, token));
    }
}
