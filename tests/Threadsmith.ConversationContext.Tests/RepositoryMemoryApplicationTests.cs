namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies manual repository-memory commands preserve atomic update outcomes.</summary>
public static class RepositoryMemoryApplicationTests
{
    /// <summary>Conflicting updates do not return the current entry as a successful replacement.</summary>
    [Fact]
    public static async Task UpdateAsync_ConflictOutcomeSurfacesAnActionableRetryMessage()
    {
        var memoryId = RepositoryMemoryId.New();
        var currentEntry = CreateEntry(memoryId, "written by another session");
        var memories = new ConflictMemoryService(currentEntry);
        var application = new RepositoryMemoryApplication(memories, new TestOptionsProvider());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(
            new UpdateRepositoryMemoryCommand(SessionId.New(), "test:repository", memoryId, "my replacement")));

        Assert.Contains(memoryId.Value.ToString("D"), exception.Message, StringComparison.Ordinal);
        Assert.Contains("Inspect", exception.Message, StringComparison.Ordinal);
        Assert.Contains("retry", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("update", memories.Request?.Action);
        Assert.Equal("my replacement", memories.Request?.Text);
    }

    private static RepositoryMemoryEntry CreateEntry(RepositoryMemoryId id, string text) => new()
    {
        Id = id,
        RepositoryIdentity = "test:repository",
        Text = text,
        ContentHash = "test-content-hash",
        Origin = RepositoryMemoryOrigin.Manual,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class ConflictMemoryService : IManagedRepositoryMemoryService
    {
        private readonly RepositoryMemoryEntry _currentEntry;

        internal ConflictMemoryService(RepositoryMemoryEntry currentEntry) => _currentEntry = currentEntry;

        internal RepositoryMemoryOperationRequest? Request { get; private set; }

        public Task<RepositoryMemoryOperationResult> ExecuteAsync(
            RepositoryMemoryOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(new RepositoryMemoryOperationResult("conflict", _currentEntry.Id, _currentEntry, [], []));
        }

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(
            string repositoryIdentity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RepositoryMemoryReadSnapshot(repositoryIdentity, 1, [_currentEntry], [], []));

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(
            string repositoryIdentity,
            RepositoryMemoryOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RepositoryMemoryId>>([]);

        public Task RecordInclusionsAsync(
            string repositoryIdentity,
            RunId runId,
            IReadOnlyList<RepositoryMemoryInclusion> inclusions,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestOptionsProvider : IRepositoryMemoryOptionsProvider
    {
        public RepositoryMemoryOptions Capture(string repositoryIdentity) => new();
    }
}
