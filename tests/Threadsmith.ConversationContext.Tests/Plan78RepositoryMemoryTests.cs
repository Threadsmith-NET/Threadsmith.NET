namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Replacement repository-memory contracts superseding Plan 78's automatic governor behavior.</summary>
public static class Plan78RepositoryMemoryTests
{
    /// <summary>Normalized exact retries preserve case, interior whitespace, identity, and usage.</summary>
    [Fact]
    public static async Task Explicit_writes_normalize_only_outer_whitespace_and_newlines()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);

        var first = await service.ExecuteAsync(MemoryTestData.Operation("add", "  Keep  Case\r\nNext line  "));
        var duplicate = await service.ExecuteAsync(MemoryTestData.Operation("add", "Keep  Case\nNext line"));
        var unchanged = await service.ExecuteAsync(MemoryTestData.Operation("update", "Keep  Case\nNext line", first.Id));
        var list = await service.ExecuteAsync(MemoryTestData.Operation("list"));

        Assert.Equal("added", first.Outcome);
        Assert.Equal("duplicate", duplicate.Outcome);
        Assert.Equal("unchanged", unchanged.Outcome);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(first.Id, unchanged.Id);
        Assert.Equal("Keep  Case\nNext line", Assert.Single(list.Entries).Text);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(0, list.Entries[0].InclusionCount);
    }

    /// <summary>Unicode line boundaries share one stored text and never cause duplicate inference.</summary>
    [Theory]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public static async Task Unicode_line_endings_normalize_before_embedding_and_duplicate_detection(string newline)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator
        {
            Generate = text =>
            {
                Assert.Equal("first\nsecond", text);
                return new TextEmbeddingResult(new float[] { 1, 0, 0 }, 3, false);
            },
        };
        var service = MemoryTestData.CreateService(fixture, generator);

        var first = await service.ExecuteAsync(MemoryTestData.Operation("add", $"first{newline}second"));
        var duplicate = await service.ExecuteAsync(MemoryTestData.Operation("add", "first\nsecond"));
        var unchanged = await service.ExecuteAsync(MemoryTestData.Operation("update", $"first{newline}second", first.Id));

        Assert.Equal("added", first.Outcome);
        Assert.Equal("duplicate", duplicate.Outcome);
        Assert.Equal("unchanged", unchanged.Outcome);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal("first\nsecond", Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries).Text);
        Assert.Equal(1, generator.Calls);
    }

    /// <summary>Only the closed action-specific argument shapes reach persistence or inference.</summary>
    [Theory]
    [InlineData("unknown", false, false)]
    [InlineData("add", true, true)]
    [InlineData("add", false, false)]
    [InlineData("update", false, true)]
    [InlineData("update", true, false)]
    [InlineData("remove", true, true)]
    [InlineData("remove", false, false)]
    [InlineData("list", false, true)]
    [InlineData("list", true, false)]
    public static async Task Invalid_action_arguments_fail_without_embedding(string action, bool id, bool text)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(
            MemoryTestData.Operation(action, text ? "text" : null, id ? RepositoryMemoryId.New() : null)));

        Assert.Equal(0, generator.Calls);
        Assert.Empty((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries);
    }

    /// <summary>Unknown updates and duplicate-target conflicts never create or merge entries.</summary>
    [Fact]
    public static async Task Update_unknown_or_duplicate_is_a_nonmutating_outcome()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var first = await service.ExecuteAsync(MemoryTestData.Operation("add", "first note"));
        var second = await service.ExecuteAsync(MemoryTestData.Operation("add", "second note"));

        var unknown = await service.ExecuteAsync(MemoryTestData.Operation("update", "new note", RepositoryMemoryId.New()));
        var duplicate = await service.ExecuteAsync(MemoryTestData.Operation("update", "first note", second.Id));

        Assert.Equal("notFound", unknown.Outcome);
        Assert.Equal("duplicate", duplicate.Outcome);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(2, (await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries.Count);
        Assert.Equal(2, generator.Calls);
    }

    /// <summary>Character overflow is rejected before an unavailable encoder can weaken the bound.</summary>
    [Fact]
    public static async Task Character_overflow_fails_before_generation()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator { Generate = _ => throw new InvalidOperationException("offline") };
        var service = MemoryTestData.CreateService(fixture, generator);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(
            MemoryTestData.Operation("add", new string('a', 2_001))));

        Assert.Contains("2,000", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, generator.Calls);
    }

    /// <summary>Complete sequence overflow and silent encoder truncation cannot evict existing content.</summary>
    [Theory]
    [InlineData(257, false)]
    [InlineData(256, true)]
    public static async Task Incomplete_embeddings_fail_before_capacity_eviction(int tokenCount, bool truncated)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var options = new RepositoryMemoryOptions { MaxNumberOfRepoMemories = 1 };
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "existing") with { Options = options });
        generator.Generate = _ => new TextEmbeddingResult(new float[] { 1, 0, 0 }, tokenCount, truncated);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(
            MemoryTestData.Operation("add", "replacement") with { Options = options }));

        Assert.Equal(saved.Id, Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries).Id);
    }

    /// <summary>Finite complete embeddings at the exact boundary are accepted.</summary>
    [Fact]
    public static async Task Exact_token_boundary_is_accepted()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator
        {
            Generate = _ => new TextEmbeddingResult(new float[] { 1, 0, 0 }, 256, false),
        };
        var service = MemoryTestData.CreateService(fixture, generator);

        var result = await service.ExecuteAsync(MemoryTestData.Operation("add", "boundary"));

        Assert.Equal("added", result.Outcome);
        Assert.NotNull(result.Entry);
    }

    /// <summary>Embedding failure, invalid output, and cancellation leave the previous content intact.</summary>
    [Theory]
    [InlineData("failure")]
    [InlineData("invalid")]
    [InlineData("cancelled")]
    public static async Task Failed_embedding_never_changes_content(string failure)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "existing"));
        generator.Generate = _ => failure switch
        {
            "failure" => throw new InvalidOperationException("offline"),
            "cancelled" => throw new OperationCanceledException(),
            _ => new TextEmbeddingResult(new float[] { float.NaN, 0, 0 }, 3, false),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync(MemoryTestData.Operation("update", "replacement", saved.Id)));

        Assert.Equal("existing", Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries).Text);
    }

    /// <summary>Meaningful updates reset exposure, retain identity, and reject delayed old-revision receipts.</summary>
    [Fact]
    public static async Task Update_resets_usage_and_fences_delayed_receipts()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var service = MemoryTestData.CreateService(fixture, new TestMemoryEmbeddingGenerator());
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "old content"));
        var entry = Assert.IsType<RepositoryMemoryEntry>(saved.Entry);
        var run = RunId.New();
        await service.RecordInclusionsAsync(MemoryTestData.Repository, run, [new RepositoryMemoryInclusion(entry.Id, entry.Revision)]);
        await service.RecordInclusionsAsync(MemoryTestData.Repository, run, [new RepositoryMemoryInclusion(entry.Id, entry.Revision)]);
        Assert.Equal(1, Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries).InclusionCount);

        var update = await service.ExecuteAsync(MemoryTestData.Operation("update", "new content", entry.Id));
        await service.RecordInclusionsAsync(MemoryTestData.Repository, RunId.New(), [new RepositoryMemoryInclusion(entry.Id, entry.Revision)]);

        var updated = Assert.Single((await service.GetSnapshotAsync(MemoryTestData.Repository)).Entries);
        Assert.Equal(entry.Id, update.Id);
        Assert.Equal(entry.Revision + 1, updated.Revision);
        Assert.Equal(0, updated.InclusionCount);
        Assert.Null(updated.LastIncludedAt);
    }

    /// <summary>Deletion is idempotent and removes the note from future search snapshots.</summary>
    [Fact]
    public static async Task Remove_is_idempotent_and_list_does_not_embed()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "temporary note"));
        generator.Generate = _ => throw new InvalidOperationException("offline");

        var removed = await service.ExecuteAsync(MemoryTestData.Operation("remove", id: saved.Id));
        var absent = await service.ExecuteAsync(MemoryTestData.Operation("remove", id: saved.Id));
        var listed = await service.ExecuteAsync(MemoryTestData.Operation("list"));

        Assert.Equal("removed", removed.Outcome);
        Assert.Equal("absent", absent.Outcome);
        Assert.Empty(listed.Entries);
        Assert.Equal(1, generator.Calls);
    }
}

internal static class MemoryTestData
{
    internal const string Repository = "test:repository";

    internal static RepositoryMemoryService CreateService(ConversationFixture fixture, ITextEmbeddingGenerator generator) =>
        new(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, new SecretOutputSanitizer());

    internal static RepositoryMemoryOperationRequest Operation(string action, string? text = null, RepositoryMemoryId? id = null) => new()
    {
        RepositoryIdentity = Repository,
        Action = action,
        Id = id,
        Text = text,
        Origin = RepositoryMemoryOrigin.Manual,
    };
}

internal sealed class TestMemoryEmbeddingGenerator : ITextEmbeddingGenerator
{
    public TextEmbeddingModelDescriptor Model { get; set; } = new("test-space", 3, 256);

    internal int Calls { get; private set; }

    internal Func<string, TextEmbeddingResult> Generate { get; set; } = _ => new TextEmbeddingResult(new float[] { 1, 0, 0 }, 3, false);

    public Task<TextEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Generate(text));
    }
}
