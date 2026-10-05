namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Behavioral coverage for MemoryReconciliationTests.</summary>
public static class MemoryReconciliationTests
{
    private static readonly RepositoryMemoryOptions Reconcile = new()
    {
        ReconciliationEnabled = true,

        MaxRepoMemoriesInContext = 0,
    };

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Theory]
    [InlineData(RepositoryMemoryOrigin.Manual, RepositoryMemoryType.Situational)]
    [InlineData(RepositoryMemoryOrigin.Manual, RepositoryMemoryType.StandingPreference)]
    [InlineData(RepositoryMemoryOrigin.Model, RepositoryMemoryType.Situational)]
    [InlineData(RepositoryMemoryOrigin.Model, RepositoryMemoryType.StandingPreference)]
    public static async Task Add_returns_both_collision_types_without_writes_then_updates_same_identity(
        RepositoryMemoryOrigin origin, RepositoryMemoryType type)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var first = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "Cancellation convention") with { MemoryType = type });
        var second = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "Cancellation preference") with
        { MemoryType = type == RepositoryMemoryType.Situational ? RepositoryMemoryType.StandingPreference : RepositoryMemoryType.Situational });
        var before = await store.GetSnapshotAsync(MemoryTestData.Repository, []);
        using var search = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
        var calls = generator.Calls;

        var blocked = await service.ExecuteAsync(MemoryTestData.Operation("add", "Refined cancellation convention") with { Origin = origin, MemoryType = type, Options = Reconcile });

        Assert.Equal("reconciliationRequired", blocked.Outcome);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Completed, blocked.SearchDetails?.Reranker);
        Assert.Null(blocked.Entry);
        Assert.Empty(blocked.EvictedIds);
        Assert.Equal(2, blocked.Entries.Count);
        Assert.Contains(blocked.Entries, entry => entry.Id == first.Id && entry.Revision == 1 && entry.Text == "Cancellation convention");
        Assert.Contains(blocked.Entries, entry => entry.Id == second.Id);
        Assert.Equal(before.Revision, (await store.GetSnapshotAsync(MemoryTestData.Repository, [])).Revision);
        Assert.Equal(calls + 1, generator.Calls); // Complete proposed-text vector is reused by search.
        Assert.All(blocked.Entries, entry => Assert.Equal(0, entry.InclusionCount));

        var replacement = await service.ExecuteAsync(MemoryTestData.Operation("update", "Refined cancellation convention", first.Id) with
        { ExpectedRevision = 1, Origin = origin, Options = Reconcile });
        Assert.Equal("updated", replacement.Outcome);
        Assert.Equal(first.Id, replacement.Id);
        Assert.Equal(type, replacement.Entry!.MemoryType);
        Assert.Equal(2, replacement.Entry.Revision);
        Assert.Equal(2, (await store.GetSnapshotAsync(MemoryTestData.Repository, [])).Entries.Count);
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Fact]
    public static async Task Confirmations_are_revision_specific_and_preserve_distinct_entries()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var saved = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "Existing convention"));
        using var search = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, new MemoryScoreEncoder());
        var service = new RepositoryMemoryService(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, new SecretOutputSanitizer(), search: search);
        var proposal = MemoryTestData.Operation("add", "Related distinct requirement") with { Options = Reconcile };
        var collision = await service.ExecuteAsync(proposal);
        var old = Assert.Single(collision.Entries);
        await baseline.ExecuteAsync(MemoryTestData.Operation("update", "Changed convention", old.Id));

        var stale = await service.ExecuteAsync(proposal with { ConfirmDistinctFrom = [new(old.Id, old.Revision)] });
        var current = Assert.Single(stale.Entries);
        Assert.Equal("reconciliationRequired", stale.Outcome);
        Assert.Equal(old.Revision + 1, current.Revision);
        var committed = await service.ExecuteAsync(proposal with { ConfirmDistinctFrom = [new(current.Id, current.Revision)] });
        Assert.Equal("added", committed.Outcome);
        Assert.NotEqual(saved.Id, committed.Id);
        var entries = (await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Id == old.Id && entry.Text == "Changed convention" && entry.Revision == current.Revision);
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Theory]
    [InlineData(-100, "reconciliationRequired")]
    [InlineData(0, "reconciliationRequired")]
    [InlineData(1.01, "reconciliationRequired")]
    public static async Task Discovered_candidates_require_review_regardless_of_absolute_score(double score, string expected)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        await baseline.ExecuteAsync(MemoryTestData.Operation("add", "existing"));
        var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        using var search = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder { Score = (_, _) => new(score, 12, false) });
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
        Assert.Equal(expected, (await service.ExecuteAsync(MemoryTestData.Operation("add", "new") with { Options = Reconcile })).Outcome);
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Incomplete_or_cancelled_comparison_never_evicts(bool cancel)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var original = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "original"));
        var encoder = new MemoryScoreEncoder { Score = (_, _) => cancel ? throw new OperationCanceledException() : new(10, 256, true) };
        using var search = new HybridRepositoryMemoryRetriever(store, generator, encoder);
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
        var operation = MemoryTestData.Operation("add", "replacement") with { Options = Reconcile with { MaxNumberOfRepoMemories = 1 } };

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(operation));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(operation));
        }

        Assert.Equal(original.Id, Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries).Id);
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Fact]
    public static async Task Concurrent_new_collision_is_seen_after_transaction_fence_retries()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        using var inner = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var interleaved = new InterleavedSearch(inner, async count =>
        {
            if (count == 1)
            {
                await baseline.ExecuteAsync(MemoryTestData.Operation("add", "Concurrent convention"));
            }
        });
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: interleaved);

        var result = await service.ExecuteAsync(MemoryTestData.Operation("add", "Proposed convention") with { Options = Reconcile });

        Assert.Equal(2, interleaved.Calls);
        Assert.Equal("reconciliationRequired", result.Outcome);
        Assert.Equal("Concurrent convention", Assert.Single(result.Entries).Text);
        Assert.Equal("Concurrent convention", Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries).Text);
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Fact]
    public static async Task Repeated_concurrent_change_returns_conflict_without_inserting()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var original = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "original"));
        var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        using var inner = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder { Score = (_, _) => new(0, 12, false) });
        var interleaved = new InterleavedSearch(inner, async count =>
            _ = await baseline.ExecuteAsync(MemoryTestData.Operation("update", "Concurrent version " + count, original.Id)));
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: interleaved);

        var result = await service.ExecuteAsync(MemoryTestData.Operation("add", "proposal") with { Options = Reconcile with { ReconciliationSemanticMinimum = 1 } });

        Assert.Equal("conflict", result.Outcome);
        Assert.Equal(2, interleaved.Calls);
        Assert.Equal("Concurrent version 2", Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries).Text);
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Fact]
    public static async Task Metadata_roundtrips_preserves_vectors_and_fences_stale_updates()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var added = await service.ExecuteAsync(MemoryTestData.Operation("add", "stable text") with { Concepts = [" Cancellation ", "ＣＡＮＣＥＬＬＡＴＩＯＮ", "\U00020000", "\uFA0E"], Kind = ManagedRepositoryMemoryKind.Constraint });
        var first = Assert.IsType<RepositoryMemoryEntry>(added.Entry);
        var loaded = Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries);
        Assert.Equal(MemoryConcepts.Normalize(first.Concepts), loaded.Concepts);
        var unchanged = await service.ExecuteAsync(MemoryTestData.Operation("update", first.Text, first.Id) with { Concepts = loaded.Concepts });
        Assert.Equal("unchanged", unchanged.Outcome);
        var cleared = await service.ExecuteAsync(MemoryTestData.Operation("update", first.Text, first.Id) with { Concepts = [], ExpectedRevision = first.Revision });
        Assert.Empty(cleared.Entry!.Concepts);
        Assert.Equal(first.Kind, cleared.Entry.Kind);
        Assert.Equal(first.Embedding.ToArray(), cleared.Entry.Embedding.ToArray());
        Assert.Equal(cleared.Entry.Revision, cleared.Entry.EmbeddingRevision);
        Assert.Equal(1, generator.Calls);
        var stale = await service.ExecuteAsync(MemoryTestData.Operation("update", "obsolete replacement", first.Id) with { ExpectedRevision = first.Revision });
        Assert.Equal("conflict", stale.Outcome);
        Assert.Equal(1, generator.Calls);
        var edited = await service.ExecuteAsync(MemoryTestData.Operation("update", "revised text", first.Id));
        Assert.Equal(first.Kind, edited.Entry!.Kind);
        Assert.Empty(edited.Entry.Concepts);
    }

    private sealed class InterleavedSearch : IRepositoryMemorySearch
    {
        private readonly IRepositoryMemorySearch _inner;
        private readonly Func<int, Task> _afterRead;

        public InterleavedSearch(IRepositoryMemorySearch inner, Func<int, Task> afterRead)
        {
            _inner = inner;
            _afterRead = afterRead;
        }

        public int Calls { get; private set; }

        public async Task<RepositoryMemoryRetrievalResult> SearchAsync(RepositoryMemorySearchRequest request, CancellationToken cancellationToken = default)
        {
            var result = await _inner.SearchAsync(request, cancellationToken);
            await _afterRead(++Calls);
            return result;
        }
    }
}

internal sealed class MemoryScoreEncoder : ITextCrossEncoder
{
    public TextCrossEncoderModelDescriptor Model { get; } = new("memory-behavior", 256, 64);

    public Func<string, string, TextCrossEncoderScore> Score { get; set; } = (_, _) => new(2, 12, false);

    public List<string> Documents { get; } = [];

    public Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Documents.AddRange(documents);
        return Task.FromResult<IReadOnlyList<TextCrossEncoderScore>>(documents.Select(text => Score(query, text)).ToArray());
    }
}
