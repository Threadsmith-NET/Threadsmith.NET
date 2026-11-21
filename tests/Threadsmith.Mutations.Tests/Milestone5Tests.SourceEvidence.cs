namespace Threadsmith.Mutations.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

public static partial class Milestone5Tests
{
    /// <summary>Refresh completion fences older semantic observations even without a confidence event.</summary>
    [Fact]
    public static async Task SemanticRefresh_InvalidatesOlderScriptEvidenceWithoutConfidenceChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var sanitizer = new PassthroughSanitizer();
        var store = new EvidenceStore(events, sanitizer);
        var observer = new ContextLifecycleObserver(store, new PromptAppendLoader(sanitizer));
        var refreshedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var old = new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = SessionId.New(),
            Kind = EvidenceKind.ToolResult,
            Content = "Script symbol lookup",
            CollectedAt = refreshedAt.AddSeconds(2),
            Provenance = new EvidenceProvenance { Source = "tool:csharp_script" },
            InvalidationKeys = ["repository", "semantic"],
            SemanticObservedAt = refreshedAt.AddSeconds(-1),
        };
        var late = old with { EvidenceId = EvidenceId.New() };
        var current = old with { EvidenceId = EvidenceId.New(), SemanticObservedAt = refreshedAt.AddSeconds(1) };
        await store.AddAsync(old, cancellationToken);

        var refreshed = new SemanticRefreshCompleted(
            old.SessionId,
            refreshedAt,
            SemanticRefreshId.New(),
            WorkspaceId.New(),
            SemanticRefreshReason.ExternalChange,
            SemanticRefreshMode.Incremental,
            ChangedFileCount: 1,
            DirtyVersion: 1,
            AppliedVersion: 1,
            SemanticConfidenceLevel.FullSemantic,
            ElapsedMilliseconds: 0);
        await observer.ObserveAsync(refreshed, cancellationToken);
        Assert.Equal(1, await store.ApplyInvalidationsAsync(old.SessionId, cancellationToken));
        await store.AddBatchAsync([late, current], cancellationToken);

        Assert.True(store.Find(old.SessionId, old.EvidenceId)!.IsStale);
        Assert.True(store.Find(old.SessionId, late.EvidenceId)!.IsStale);
        Assert.False(store.Find(old.SessionId, current.EvidenceId)!.IsStale);
    }

    /// <summary>Late reads use only matching file and ancestor watermarks and retain their observation times.</summary>
    [Fact]
    public static async Task EvidenceAdmission_MatchesPathWatermarksAtDependencyBoundaries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var store = new EvidenceStore(events, new PassthroughSanitizer());
        var root = AppContext.BaseDirectory;
        var changedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var old = new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = SessionId.New(),
            Kind = EvidenceKind.ToolResult,
            Content = "File read",
            CollectedAt = changedAt.AddSeconds(2),
            Provenance = new EvidenceProvenance { Source = "tool:read_file", RepositoryPath = root },
            FileDependencies = [new("src/Example.cs", null, ObservedAt: changedAt.AddSeconds(-1))],
        };
        var other = old with { EvidenceId = EvidenceId.New(), FileDependencies = [old.FileDependencies[0] with { Path = "src-other/Example.cs" }] };
        var exact = old with { EvidenceId = EvidenceId.New(), FileDependencies = [old.FileDependencies[0] with { Path = "tests/Test.cs" }] };
        var newer = old with { EvidenceId = EvidenceId.New(), FileDependencies = [old.FileDependencies[0] with { ObservedAt = changedAt.AddSeconds(1) }] };
        store.QueueInvalidation(old.SessionId, Path.Combine(root, "src"), "Ancestor changed", changedAt);
        store.QueueInvalidation(old.SessionId, "tests\\nested\\..\\Test.cs", "File changed", changedAt);
        store.QueueInvalidation(old.SessionId, Path.Combine(root, "src"), "Older notification", changedAt.AddSeconds(-2));
        await store.ApplyInvalidationsAsync(old.SessionId, cancellationToken);

        await store.AddBatchAsync([old, other, exact, newer], cancellationToken);

        Assert.Equal("Ancestor changed", store.Find(old.SessionId, old.EvidenceId)!.StaleReason);
        Assert.Equal("File changed", store.Find(old.SessionId, exact.EvidenceId)!.StaleReason);
        Assert.False(store.Find(old.SessionId, other.EvidenceId)!.IsStale);
        Assert.False(store.Find(old.SessionId, newer.EvidenceId)!.IsStale);
    }

    /// <summary>A result containing different versions of one file is historical even on first admission.</summary>
    [Fact]
    public static async Task EvidenceVersions_FirstMixedVersionResultIsStoredAsStale()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var store = new EvidenceStore(events, new PassthroughSanitizer());
        var root = AppContext.BaseDirectory;
        var first = new EvidenceFileDependency("src/Foo.cs", "AA", new SourceRange(1, 1, 1, 2), DateTimeOffset.UnixEpoch);
        var second = new EvidenceFileDependency(Path.Combine(root, "src", "Foo.cs"), "BB", new SourceRange(2, 1, 2, 2), DateTimeOffset.UnixEpoch.AddSeconds(1));
        var mixed = new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = SessionId.New(),
            Kind = EvidenceKind.ToolResult,
            Content = "Two source pages",
            CollectedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
            Provenance = new EvidenceProvenance { Source = "tool:csharp_script", RepositoryPath = root },
            FileDependencies = [first, second],
        };
        var coherent = mixed with
        {
            EvidenceId = EvidenceId.New(),
            FileDependencies = [first with { Sha256 = "bb" }, second],
        };

        // Act
        await store.AddAsync(mixed, cancellationToken);
        var stored = Assert.Single(store.Snapshot(mixed.SessionId));
        await store.AddAsync(coherent, cancellationToken);

        // Assert
        Assert.True(stored.IsStale);
        Assert.NotNull(stored.StaleReason);
        Assert.Equal(new[] { first, second with { Path = first.Path } }, stored.FileDependencies);
        Assert.False(store.Find(mixed.SessionId, coherent.EvidenceId)!.IsStale);
    }

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
