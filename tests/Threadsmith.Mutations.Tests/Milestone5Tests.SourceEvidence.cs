namespace Threadsmith.Mutations.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

public static partial class Milestone5Tests
{
    /// <summary>The live dependency index follows all evidence identity and staleness changes.</summary>
    [Fact]
    public static async Task EvidenceVersions_IndexTracksReplacementCopyAndInvalidation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["a.txt"] = "a", ["b.txt"] = "b" });
        await using var events = new DomainEventStream();
        var store = new EvidenceStore(events, new PassthroughSanitizer());
        var original = new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = repository.SessionId,
            Content = "original",
            Provenance = new EvidenceProvenance { Source = "index fixture", RepositoryPath = repository.Root },
            FileDependencies = [new("a.txt", "v1"), new("b.txt", null)],
            CollectedAt = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(original, token);
        var copiedSession = SessionId.New();
        store.CopySession(repository.SessionId, copiedSession);
        var replaced = original with { FileDependencies = [new("b.txt", "v1")] };
        await store.AddAsync(replaced, token);
        await store.AddAsync(original with { EvidenceId = EvidenceId.New(), FileDependencies = [] }, token);
        await store.AddAsync(original with { EvidenceId = EvidenceId.New(), FileDependencies = [new("b.txt", null)] }, token);
        Assert.False(store.Find(repository.SessionId, original.EvidenceId)!.IsStale);
        var next = original with { EvidenceId = EvidenceId.New(), FileDependencies = [new("a.txt", "v2")], CollectedAt = original.CollectedAt.AddSeconds(1) };
        await store.AddAsync(next, token);
        Assert.False(store.Find(repository.SessionId, original.EvidenceId)!.IsStale);
        Assert.False(store.Find(copiedSession, original.EvidenceId)!.IsStale);
        await store.AddAsync(next with { SessionId = copiedSession }, token);
        Assert.True(store.Find(copiedSession, original.EvidenceId)!.IsStale);

        var complement = next with { EvidenceId = EvidenceId.New(), FileDependencies = [new("a.txt", "v2", new SourceRange(2, 1, 2, 2))] };
        await store.AddAsync(complement, token);
        await store.AddAsync(original with { EvidenceId = EvidenceId.New(), FileDependencies = [new("a.txt", "v0")] }, token);
        Assert.False(store.Find(repository.SessionId, next.EvidenceId)!.IsStale);
        Assert.False(store.Find(repository.SessionId, complement.EvidenceId)!.IsStale);
        store.QueueInvalidation(repository.SessionId, "a.txt", "changed", next.CollectedAt);
        await store.ApplyInvalidationsAsync(repository.SessionId, token);
        await store.AddAsync(next with { CollectedAt = next.CollectedAt.AddSeconds(1) }, token);
        await store.AddAsync(next with { EvidenceId = EvidenceId.New(), FileDependencies = [new("a.txt", "v3")], CollectedAt = next.CollectedAt.AddSeconds(2) }, token);
        Assert.True(store.Find(repository.SessionId, next.EvidenceId)!.IsStale);
        Assert.False(store.Find(repository.SessionId, original.EvidenceId)!.IsStale);
        await store.AddAsync(next with { EvidenceId = EvidenceId.New(), FileDependencies = [new("b.txt", "v2")] }, token);
        Assert.True(store.Find(repository.SessionId, original.EvidenceId)!.IsStale);
    }

    /// <summary>All file dependencies invalidate while late events preserve current complementary ranges.</summary>
    [Fact]
    public static async Task EvidenceVersions_InvalidateAllDependenciesAndPreserveCurrentRanges()
    {
        var token = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["a.txt"] = "a", ["b.txt"] = "b" });
        await using var events = new DomainEventStream();
        var store = new EvidenceStore(events, new PassthroughSanitizer());
        var before = DateTimeOffset.UtcNow;
        var old = new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = repository.SessionId,
            Kind = EvidenceKind.SourceExcerpt,
            Content = "old multi-file",
            CollectedAt = before,
            Provenance = new EvidenceProvenance { Source = "test", RepositoryPath = repository.Root, SourcePath = "a.txt" },
            FileDependencies = [new("a.txt", "old"), new(Path.Combine(repository.Root, "b.txt"), "old")],
        };
        await store.AddAsync(old, token);
        var observer = new ContextLifecycleObserver(store, new PromptAppendLoader(new PassthroughSanitizer()));
        await observer.ObserveAsync(new MutationApplied(repository.SessionId, before, MutationId.New(), RelativePath: "b.txt") { Type = MutationType.MoveFile, DestinationRelativePath = "c.txt" }, token);
        await store.ApplyInvalidationsAsync(repository.SessionId, token);
        Assert.True(store.Find(repository.SessionId, old.EvidenceId)!.IsStale);

        var hash = repository.Baseline.Files.Single(file => file.RelativePath == "a.txt").Sha256;
        var current = old with
        {
            EvidenceId = EvidenceId.New(),
            Content = "current first range",
            CollectedAt = before.AddSeconds(1),
            FileDependencies = [new("a.txt", hash, new SourceRange(1, 1, 1, 2))],
        };
        await store.AddAsync(current, token);
        var complement = current with { EvidenceId = EvidenceId.New(), Content = "current second range", FileDependencies = [new("a.txt", hash, new SourceRange(2, 1, 2, 2))] };
        await store.AddAsync(complement, token);
        await observer.ObserveAsync(new MutationApplied(repository.SessionId, before, MutationId.New(), RelativePath: "a.txt"), token);
        await store.ApplyInvalidationsAsync(repository.SessionId, token);
        Assert.False(store.Find(repository.SessionId, current.EvidenceId)!.IsStale);
        Assert.False(store.Find(repository.SessionId, complement.EvidenceId)!.IsStale);
        await observer.ObserveAsync(new MutationSetRolledBack(repository.SessionId, before.AddSeconds(2), MutationSetId.New(), ["a.txt"]), token);
        await store.ApplyInvalidationsAsync(repository.SessionId, token);
        Assert.True(store.Find(repository.SessionId, current.EvidenceId)!.IsStale);
    }
}
