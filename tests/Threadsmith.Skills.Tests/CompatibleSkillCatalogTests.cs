namespace Threadsmith.Skills.Tests;

using Threadsmith.Core;
using Threadsmith.Skills;
using Xunit;

/// <summary>Checks concurrent refresh and cancellation through the combined catalog.</summary>
public static class CompatibleSkillCatalogTests
{
    /// <summary>Verifies refresh ownership and publication boundaries.</summary>
    [Fact]
    public static async Task RefreshesOverlapAndPublishOnlyAfterBothComplete()
    {
        var token = TestContext.Current.CancellationToken;
        var native = new NativeCatalog();
        var claude = new ClaudeCatalog();
        var progress = new SemanticStartupProgress();
        using var observation = progress.ObserveProcess();
        var catalog = new CompatibleSkillCatalog(native, claude, progress);
        var first = catalog.RefreshAsync(token);
        await Task.WhenAll(native.Started.Task, claude.Started.Task).WaitAsync(token);
        var second = catalog.RefreshAsync(token);
        Assert.Equal(1, native.Calls);
        Assert.Equal(1, claude.Calls);
        claude.Release.SetResult();
        await claude.Finished.Task.WaitAsync(token);
        Assert.False(first.IsCompleted);
        Assert.Equal(0, catalog.Snapshot.Generation);
        Assert.Equal(SemanticStartupPhaseState.Running, observation.Snapshot[0].State);
        Assert.Equal(["skills", "skills.native", "skills.claude"], observation.Snapshot.Select(row => row.Id));
        native.Release.SetResult();
        Assert.Equal(1, (await first).Generation);
        Assert.Equal(2, (await second).Generation);
        Assert.Equal(2, native.Calls);
        Assert.Equal(2, claude.Calls);
        Assert.All(observation.Snapshot, row => Assert.Equal(SemanticStartupPhaseState.Completed, row.State));
    }

    /// <summary>Verifies refresh ownership and publication boundaries.</summary>
    [Fact]
    public static async Task FatalBranchCancelsAndObservesSiblingWithoutPublishing()
    {
        var token = TestContext.Current.CancellationToken;
        var native = new NativeCatalog();
        var claude = new ClaudeCatalog();
        var catalog = new CompatibleSkillCatalog(native, claude);
        var refresh = catalog.RefreshAsync(token);
        await Task.WhenAll(native.Started.Task, claude.Started.Task).WaitAsync(token);
        native.Release.SetException(new InvalidDataException("Invalid package"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await refresh.WaitAsync(token));
        Assert.True(claude.Finished.Task.IsCompleted);
        Assert.Equal(0, catalog.Snapshot.Generation);
    }

    private sealed class NativeCatalog : ISkillCatalog
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public SkillCatalogSnapshot Snapshot { get; } = new() { CreatedAt = DateTimeOffset.MinValue };

        public async Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Snapshot;
        }

        public IReadOnlyList<SkillCatalogCandidate> Search(SkillCatalogQuery query) => [];

        public SkillCatalogCandidate Resolve(string selector) => throw new NotSupportedException();
    }

    private sealed class ClaudeCatalog : IClaudeSkillCompatibilityCatalog
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public IReadOnlyList<ClaudeSkillCandidate> Candidates => [];

        public async Task<IReadOnlyList<ClaudeSkillCandidate>> RefreshAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return Candidates;
            }
            finally
            {
                Finished.TrySetResult();
            }
        }

        public Task<ClaudeSkillSnapshot> ActivateAsync(ClaudeSkillCandidate candidate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
