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

    /// <summary>Manual commands route to the stored partition without trusting a caller-supplied owner.</summary>
    [Theory]
    [InlineData(RepositoryMemoryOwnership.Ordinary)]
    [InlineData(RepositoryMemoryOwnership.Intelligence)]
    public static async Task Manual_mutations_resolve_stored_ownership(RepositoryMemoryOwnership ownership)
    {
        var entry = CreateEntry(RepositoryMemoryId.New(), "Original") with { Ownership = ownership };
        var service = new ConflictMemoryService(entry);
        var application = new RepositoryMemoryApplication(service, new TestOptionsProvider());
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(
            new UpdateRepositoryMemoryCommand(SessionId.New(), entry.RepositoryIdentity, entry.Id, "Changed") { ExpectedRevision = 1 }, ct));
        Assert.Equal(ownership, service.Request?.Ownership);
        Assert.Equal(1, service.Request?.ExpectedRevision);
        Assert.Equal(RepositoryMemoryOrigin.Manual, service.Request?.Origin);
        await application.HandleAsync(new ForgetRepositoryMemoryCommand(SessionId.New(), entry.RepositoryIdentity, entry.Id), ct);
        Assert.Equal("remove", service.Request?.Action);
        Assert.Equal(ownership, service.Request?.Ownership);
    }

    /// <summary>Real storage and service validation allow manual editing and deletion in every tab without losing metadata.</summary>
    [Theory]
    [InlineData(RepositoryMemoryType.StandingPreference, RepositoryMemoryOwnership.Ordinary)]
    [InlineData(RepositoryMemoryType.Situational, RepositoryMemoryOwnership.Ordinary)]
    [InlineData(RepositoryMemoryType.Situational, RepositoryMemoryOwnership.Intelligence)]
    public static async Task Manual_mutations_preserve_partition_and_metadata_in_storage(RepositoryMemoryType type, RepositoryMemoryOwnership ownership)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var service = MemoryTestData.CreateService(fixture, new TestMemoryEmbeddingGenerator());
        var added = await service.ExecuteAsync(
            MemoryTestData.Operation("add", "Original memory") with
        {
            Ownership = ownership,
            Options = new RepositoryMemoryOptions { Intelligence = new RepositoryIntelligenceOptions { ReconciliationEnabled = false } },
            MemoryType = type,
            Kind = ManagedRepositoryMemoryKind.Constraint,
            Concepts = ["cancellation"],
            Supports = ownership == RepositoryMemoryOwnership.Intelligence
                ? [new RepositoryMemorySupport { Path = "source.cs", ContentHash = new string('a', 64), ObservedAt = DateTimeOffset.UtcNow }]
                : [],
        },
            ct);
        var entry = Assert.IsType<RepositoryMemoryEntry>(added.Entry);
        var application = new RepositoryMemoryApplication(service, new TestOptionsProvider());
        var updated = await application.HandleAsync(
            new UpdateRepositoryMemoryCommand(SessionId.New(), entry.RepositoryIdentity, entry.Id, "Edited memory")
        {
            ExpectedRevision = entry.Revision,
        },
            ct);
        Assert.Equal(entry.Id, updated.Id);
        Assert.Equal(entry.Revision + 1, updated.Revision);
        Assert.Equal(ownership, updated.Ownership);
        Assert.Equal(type, updated.MemoryType);
        Assert.Equal(entry.Supports, updated.Supports);
        Assert.Equal(entry.Concepts, updated.Concepts);
        Assert.Equal(entry.Kind, updated.Kind);
        Assert.Equal("Edited memory", (await service.GetEntryAsync(entry.RepositoryIdentity, entry.Id, ct))?.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(
            new UpdateRepositoryMemoryCommand(SessionId.New(), entry.RepositoryIdentity, entry.Id, "Stale edit") { ExpectedRevision = entry.Revision }, ct));
        Assert.True(await application.HandleAsync(new ForgetRepositoryMemoryCommand(SessionId.New(), entry.RepositoryIdentity, entry.Id), ct));
        Assert.Null(await service.GetEntryAsync(entry.RepositoryIdentity, entry.Id, ct));
    }

    private static RepositoryMemoryEntry CreateEntry(RepositoryMemoryId id, string text)
    {
        return new()
        {
            Id = id,
            RepositoryIdentity = "test:repository",
            Text = text,
            ContentHash = "test-content-hash",
            Origin = RepositoryMemoryOrigin.Manual,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private sealed class ConflictMemoryService : IManagedRepositoryMemoryService
    {
        private readonly RepositoryMemoryEntry _currentEntry;

        internal ConflictMemoryService(RepositoryMemoryEntry currentEntry)
        {
            _currentEntry = currentEntry;
        }

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
        {
            return Task.FromResult(new RepositoryMemoryReadSnapshot(repositoryIdentity, 1, [_currentEntry], [], []));
        }

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(
            string repositoryIdentity,
            RepositoryMemoryOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RepositoryMemoryId>>([]);
        }

        public Task RecordInclusionsAsync(
            string repositoryIdentity,
            RunId runId,
            IReadOnlyList<RepositoryMemoryInclusion> inclusions,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestOptionsProvider : IRepositoryMemoryOptionsProvider
    {
        public RepositoryMemoryOptions CaptureCurrent()
        {
            return new();
        }

        public RepositoryMemoryOptions Capture(string repositoryIdentity)
        {
            return new();
        }
    }
}
