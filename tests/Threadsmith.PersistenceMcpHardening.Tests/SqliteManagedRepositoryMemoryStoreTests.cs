namespace Threadsmith.PersistenceMcpHardening.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Real SQLite contracts for explicit memories, snapshots, revisions, eviction and migration.</summary>
public sealed class SqliteManagedRepositoryMemoryStoreTests
{
    private static readonly TextEmbeddingModelDescriptor Model = new("test-space", 3, 256);
    private static readonly TextEmbeddingResult Embedding = new(new float[] { 1, 0, 0 }, 5, false);

    /// <summary>FTS follows committed text changes and requires two distinct terms for multi-term queries.</summary>
    [Fact]
    public async Task Crud_synchronizes_fts_and_preserves_repository_isolation()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var added = await fixture.Store.AddAsync("repo", Write("WidgetFactory uses careful cancellation"), Model, Embedding, new());
        var entry = Assert.IsType<RepositoryMemoryEntry>(added.Entry);
        Assert.Single((await fixture.Store.GetSnapshotAsync("repo", ["WidgetFactory", "cancellation"])).LexicalMatches);
        Assert.Empty((await fixture.Store.GetSnapshotAsync("repo", ["WidgetFactory", "missing"])).LexicalMatches);
        Assert.Single((await fixture.Store.GetSnapshotAsync("repo", ["WidgetFactory"])).LexicalMatches);
        Assert.Empty((await fixture.Store.GetSnapshotAsync("other", ["WidgetFactory"])).Entries);
        var updated = await fixture.Store.UpdateAsync("repo", entry.Id, 1, Write("Database snapshots remain detached"), Model, Embedding, new());
        Assert.Equal(RepositoryMemoryWriteStatus.Updated, updated.Status);
        Assert.Empty((await fixture.Store.GetSnapshotAsync("repo", ["WidgetFactory"])).LexicalMatches);
        Assert.Single((await fixture.Store.GetSnapshotAsync("repo", ["snapshots", "detached"])).LexicalMatches);
        Assert.True(await fixture.Store.RemoveAsync("repo", entry.Id));
        Assert.Empty((await fixture.Store.GetSnapshotAsync("repo", ["snapshots"])).LexicalMatches);
        Assert.False(await fixture.Store.RemoveAsync("repo", entry.Id));
    }

    /// <summary>Exact retries do not renew content age or revision and cross-entry duplicates never merge.</summary>
    [Fact]
    public async Task Duplicates_and_conflicts_leave_content_and_recency_unchanged()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var first = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Keep exact CASE"), Model, Embedding, new())).Entry);
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        var duplicate = await fixture.Store.AddAsync("repo", Write(first.Text), Model, Embedding, new());
        Assert.Equal(RepositoryMemoryWriteStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.UpdatedAt, duplicate.Entry?.UpdatedAt);
        var second = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Another preference"), Model, Embedding, new())).Entry);
        Assert.Equal(RepositoryMemoryWriteStatus.Duplicate, (await fixture.Store.UpdateAsync("repo", second.Id, 1, Write(first.Text), Model, Embedding, new())).Status);
        Assert.Equal(RepositoryMemoryWriteStatus.Unchanged, (await fixture.Store.UpdateAsync("repo", first.Id, 1, Write(first.Text), Model, Embedding, new())).Status);
        await fixture.Store.UpdateAsync("repo", first.Id, 1, Write("Corrected preference"), Model, Embedding, new());
        Assert.Equal(RepositoryMemoryWriteStatus.Conflict, (await fixture.Store.UpdateAsync("repo", first.Id, 1, Write("Stale replacement"), Model, Embedding, new())).Status);
        Assert.Equal(2, (await fixture.Store.GetSnapshotAsync("repo", [])).Entries.Count);
    }

    /// <summary>Complete-input bounds reject truncation, oversized text and invalid vectors before any eviction.</summary>
    [Fact]
    public async Task Invalid_embeddings_and_text_never_evict_at_capacity_one()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var options = new RepositoryMemoryOptions { MaxNumberOfRepoMemories = 1 };
        var entry = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Existing memory"), Model, Embedding, options)).Entry);
        foreach (var invalid in new[]
        {
            Embedding with { WasTruncated = true },
            Embedding with { InputTokenCount = 257 },
            Embedding with { Vector = new float[] { float.NaN, 0, 0 } },
            Embedding with { Vector = new float[] { 0, 0, 0 } },
            Embedding with { Vector = new float[] { 1, 0 } },
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.AddAsync("repo", Write("Invalid replacement"), Model, invalid, options));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.AddAsync("repo", Write(new string('x', 2_001)), Model, Embedding, options));
        Assert.Equal(entry.Id, Assert.Single((await fixture.Store.GetSnapshotAsync("repo", [])).Entries).Id);
        var boundary = await fixture.Store.AddAsync("repo", Write(new string('x', 2_000)), Model, Embedding with { InputTokenCount = 256 }, options);
        Assert.Equal(RepositoryMemoryWriteStatus.Added, boundary.Status);
    }

    /// <summary>Capacity one evicts the old entry rather than the incoming note, even during protection.</summary>
    [Fact]
    public async Task New_entries_are_not_their_own_eviction_victim()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var options = new RepositoryMemoryOptions { MaxNumberOfRepoMemories = 1 };
        var first = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("First"), Model, Embedding, options)).Entry);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await fixture.Store.AddAsync("repo", Write("Second"), Model, Embedding, options);
        Assert.Equal(first.Id, Assert.Single(second.EvictedIds));
        Assert.Equal("Second", Assert.Single((await fixture.Store.GetSnapshotAsync("repo", [])).Entries).Text);
    }

    /// <summary>Expired frequency decays while meaningful corrections receive a new protection window.</summary>
    [Fact]
    public async Task Eviction_ages_frequency_and_protects_meaningful_corrections()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var options = new RepositoryMemoryOptions { MaxNumberOfRepoMemories = 3 };
        var ancient = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Ancient frequent"), Model, Embedding, options)).Entry);
        for (var i = 0; i < 8; i++)
        {
            await fixture.Store.RecordInclusionsAsync("repo", RunId.New(), [new(ancient.Id, 1)]);
        }

        fixture.Clock.Advance(TimeSpan.FromDays(180));
        var corrected = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Correction candidate"), Model, Embedding, options)).Entry);
        var recent = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Recently used"), Model, Embedding, options)).Entry);
        fixture.Clock.Advance(TimeSpan.FromDays(8));
        await fixture.Store.UpdateAsync("repo", corrected.Id, 1, Write("Meaningful correction"), Model, Embedding, options);
        await fixture.Store.RecordInclusionsAsync("repo", RunId.New(), [new(recent.Id, 1)]);
        var removed = await fixture.Store.EnforceCapacityAsync("repo", options with { MaxNumberOfRepoMemories = 2 });
        Assert.Equal(ancient.Id, Assert.Single(removed));
        Assert.Contains((await fixture.Store.GetSnapshotAsync("repo", [])).Entries, entry => entry.Id == corrected.Id);
    }

    /// <summary>Concurrent store instances serialize their capacity checks in SQLite.</summary>
    [Fact]
    public async Task Concurrent_additions_cannot_exceed_capacity()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var options = new RepositoryMemoryOptions { MaxNumberOfRepoMemories = 2 };
        await Task.WhenAll(Enumerable.Range(0, 5).Select(index => Task.Run(async () =>
        {
            var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString, fixture.Clock);
            await store.AddAsync("repo", Write("Memory " + index), Model, Embedding, options);
        })));
        Assert.Equal(2, (await fixture.Store.GetSnapshotAsync("repo", [])).Entries.Count);
    }

    /// <summary>A failed SQL insert rolls back the preceding eviction and its FTS removal.</summary>
    [Fact]
    public async Task Insert_failure_rolls_back_eviction_and_fts()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var options = new RepositoryMemoryOptions { MaxNumberOfRepoMemories = 1 };
        var entry = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Keep this"), Model, Embedding, options)).Entry);
        await fixture.ExecuteAsync("CREATE TRIGGER fail_insert BEFORE INSERT ON managed_memories BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Store.AddAsync("repo", Write("Rejected insert"), Model, Embedding, options));
        var snapshot = await fixture.Store.GetSnapshotAsync("repo", ["Keep"]);
        Assert.Equal(entry.Id, Assert.Single(snapshot.Entries).Id);
        Assert.Single(snapshot.LexicalMatches);
    }

    /// <summary>Receipts are idempotent across retries and are fenced against corrected or deleted content.</summary>
    [Fact]
    public async Task Inclusion_receipts_preserve_revision_and_fts_then_reset_on_correction()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var entry = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Stable prompt text"), Model, Embedding, new())).Entry);
        var before = await fixture.Store.GetSnapshotAsync("repo", ["Stable"]);
        var run = RunId.New();
        await fixture.Store.RecordInclusionsAsync("repo", run, [new(entry.Id, 1), new(entry.Id, 1)]);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Store.RecordInclusionsAsync("repo", run, [new(entry.Id, 1)]);
        var after = await fixture.Store.GetSnapshotAsync("repo", ["Stable"]);
        var included = Assert.Single(after.Entries);
        Assert.Equal(1, included.InclusionCount);
        Assert.Equal(fixture.Clock.GetUtcNow(), included.LastIncludedAt);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.LexicalMatches, after.LexicalMatches);
        await fixture.Store.UpdateAsync("repo", entry.Id, 1, Write("New prompt text"), Model, Embedding, new());
        await fixture.Store.RecordInclusionsAsync("repo", run, [new(entry.Id, 1)]);
        Assert.Equal(0, Assert.Single((await fixture.Store.GetSnapshotAsync("repo", [])).Entries).InclusionCount);
        await fixture.Store.RecordInclusionsAsync("repo", run, [new(entry.Id, 2)]);
        Assert.Equal(1, Assert.Single((await fixture.Store.GetSnapshotAsync("repo", [])).Entries).InclusionCount);
        await fixture.Store.RemoveAsync("repo", entry.Id);
        await fixture.Store.RecordInclusionsAsync("repo", run, [new(entry.Id, 2)]);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM managed_memory_inclusions;"));
    }

    /// <summary>Invalid BLOBs are excluded without disabling lexical matching; rebuilds require unchanged content.</summary>
    [Fact]
    public async Task Corrupted_vectors_fall_back_and_rebuild_is_revision_fenced()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var entry = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Unicode café preference"), Model, Embedding, new())).Entry);
        await fixture.ExecuteAsync("UPDATE managed_memories SET embedding = X'0102';");
        var snapshot = await fixture.Store.GetSnapshotAsync("repo", ["cafe"]);
        Assert.True(Assert.Single(snapshot.Entries).Embedding.IsEmpty);
        Assert.NotEmpty(snapshot.Warnings);
        Assert.Single(snapshot.LexicalMatches);
        Assert.False(await fixture.Store.AttachEmbeddingAsync("repo", entry.Id, 2, entry.ContentHash, Model, Embedding));
        Assert.False(await fixture.Store.AttachEmbeddingAsync("repo", entry.Id, 1, "wrong hash", Model, Embedding));
        Assert.True(await fixture.Store.AttachEmbeddingAsync("repo", entry.Id, 1, entry.ContentHash, Model, Embedding));
        var rebuilt = await fixture.Store.GetSnapshotAsync("repo", []);
        Assert.Equal(3, Assert.Single(rebuilt.Entries).Embedding.Length);
        Assert.True(rebuilt.Revision > snapshot.Revision);
        Assert.Empty((await fixture.Store.GetSnapshotAsync("repo", ["\" OR *", "(", ")"])).LexicalMatches);
    }

    /// <summary>Migration preserves only explicit active manual provenance, including text beyond new write limits.</summary>
    [Fact]
    public async Task Migration_imports_manual_only_and_backup_restores_prior_wal_state()
    {
        await using var fixture = await MemoryDatabase.CreateAsync(migrate: false);
        await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All.Take(10)).RunAsync();
        await using var walConnection = new SqliteConnection(fixture.ConnectionString);
        await walConnection.OpenAsync();
        await using var walMode = walConnection.CreateCommand();
        walMode.CommandText = "PRAGMA journal_mode=WAL;";
        await walMode.ExecuteNonQueryAsync();
        var manual = RepositoryMemoryId.New();
        await InsertLegacyAsync(fixture, manual, RepositoryMemoryAuthority.UserAuthored, RepositoryMemoryValidity.Active, userCommand: true, new string('m', 2_001));
        await InsertLegacyAsync(fixture, RepositoryMemoryId.New(), RepositoryMemoryAuthority.HostObserved, RepositoryMemoryValidity.Active, userCommand: false, "Automatic host receipt");
        await InsertLegacyAsync(fixture, RepositoryMemoryId.New(), RepositoryMemoryAuthority.UserAuthored, RepositoryMemoryValidity.Forgotten, userCommand: true, "Forgotten manual");
        await InsertLegacyAsync(fixture, RepositoryMemoryId.New(), RepositoryMemoryAuthority.UserAuthored, RepositoryMemoryValidity.Active, userCommand: false, "No command provenance");
        var runner = new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All);
        Assert.Equal(10, await runner.RunAsync());
        var imported = Assert.Single((await fixture.Store.GetSnapshotAsync("repo", [])).Entries);
        Assert.Equal(manual, imported.Id);
        Assert.Equal(2_001, imported.Text.Length);
        Assert.Equal(RepositoryMemoryOrigin.Manual, imported.Origin);
        Assert.True(imported.Embedding.IsEmpty);
        var backup = Assert.IsType<string>(runner.LastBackupPath);
        Assert.True(File.Exists(backup));
        await using var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
        await restored.OpenAsync();
        await using var query = restored.CreateCommand();
        query.CommandText = "SELECT count(*) FROM repository_memory;";
        Assert.Equal(4L, await query.ExecuteScalarAsync());
        Assert.Equal(10, await runner.RunAsync());
        Assert.Equal(1, await fixture.ScalarAsync("SELECT imported_count FROM managed_memory_migration;"));
        Assert.Equal(3, await fixture.ScalarAsync("SELECT dropped_count FROM managed_memory_migration;"));
    }

    /// <summary>Repository switches select distinct local databases and reject stale identity calls.</summary>
    [Fact]
    public async Task Repository_router_isolates_stores_and_rebinds_existing_content()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var firstRoot = Path.Combine(fixture.DirectoryPath, "first");
        var secondRoot = Path.Combine(fixture.DirectoryPath, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        using var router = new RepositoryBoundMemoryStore(fixture.ConnectionString, fixture.DirectoryPath, fixture.Clock);
        await router.BindRepositoryAsync(firstRoot);
        var first = RepositoryIdentity.Create(firstRoot);
        await router.AddAsync(first, Write("First repository only"), Model, Embedding, new());
        await router.BindRepositoryAsync(secondRoot);
        var second = RepositoryIdentity.Create(secondRoot);
        Assert.Empty((await router.GetSnapshotAsync(second, [])).Entries);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await router.GetSnapshotAsync(first, []));
        await router.AddAsync(second, Write("Second repository only"), Model, Embedding, new());
        await router.BindRepositoryAsync(firstRoot);
        Assert.Equal("First repository only", Assert.Single((await router.GetSnapshotAsync(first, [])).Entries).Text);
        Assert.True(File.Exists(Path.Combine(firstRoot, ".threadsmith", "threadsmith.db")));
        Assert.True(File.Exists(Path.Combine(secondRoot, ".threadsmith", "threadsmith.db")));
    }

    /// <summary>The existing retention service removes receipts only for runs in expired sessions.</summary>
    [Fact]
    public async Task Retention_prunes_expired_run_receipts_without_forgetting_memory()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var entry = Assert.IsType<RepositoryMemoryEntry>((await fixture.Store.AddAsync("repo", Write("Durable content"), Model, Embedding, new())).Entry);
        var expiredRun = RunId.New();
        var currentRun = RunId.New();
        var events = new SqliteEventStore(fixture.ConnectionString);
        await events.AppendAsync(new PlanProposed(SessionId.New(), fixture.Clock.GetUtcNow().AddDays(-90), "old", expiredRun));
        await events.AppendAsync(new PlanProposed(SessionId.New(), fixture.Clock.GetUtcNow(), "current", currentRun));
        await fixture.Store.RecordInclusionsAsync("repo", expiredRun, [new(entry.Id, 1)]);
        await fixture.Store.RecordInclusionsAsync("repo", currentRun, [new(entry.Id, 1)]);
        var artifacts = new ArtifactStore(fixture.ConnectionString, Path.Combine(fixture.DirectoryPath, "artifacts"), new SecretOutputSanitizer(), fixture.Clock);
        await artifacts.InitializeAsync();
        var retention = new RetentionService(events, artifacts, new(), NullLogger<RetentionService>.Instance, fixture.Clock);
        await retention.RunAsync();
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM managed_memory_inclusions;"));
        Assert.Equal(2, Assert.Single((await fixture.Store.GetSnapshotAsync("repo", [])).Entries).InclusionCount);
    }

    /// <summary>Cancellation before a write cannot remove a victim, including at capacity one.</summary>
    [Fact]
    public async Task Cancelled_write_never_evicts_content()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var options = new RepositoryMemoryOptions { MaxNumberOfRepoMemories = 1 };
        await fixture.Store.AddAsync("repo", Write("Preserved"), Model, Embedding, options);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.AddAsync("repo", Write("Cancelled"), Model, Embedding, options, cancellation.Token));
        Assert.Equal("Preserved", Assert.Single((await fixture.Store.GetSnapshotAsync("repo", [])).Entries).Text);
    }

    /// <summary>A forward migration applies the same cap to manual imports while retaining a consistent backup.</summary>
    [Fact]
    public async Task Migration_enforces_capacity_with_oldest_protected_import_evicted()
    {
        await using var fixture = await MemoryDatabase.CreateAsync(migrate: false);
        await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All.Take(10)).RunAsync();
        for (var index = 0; index < 22; index++)
        {
            await InsertLegacyAsync(fixture, RepositoryMemoryId.New(), RepositoryMemoryAuthority.UserAuthored, RepositoryMemoryValidity.Active, true, "Manual memory " + index);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All).RunAsync();
        Assert.Equal(20, (await fixture.Store.GetSnapshotAsync("repo", [])).Entries.Count);
        Assert.Equal(2, await fixture.ScalarAsync("SELECT evicted_count FROM managed_memory_migration;"));
        Assert.DoesNotContain((await fixture.Store.GetSnapshotAsync("repo", [])).Entries, entry => entry.Text == "Manual memory 0");
    }

    /// <summary>The configured capacity is applied before importing more than the default twenty manual entries.</summary>
    [Fact]
    public async Task Migration_honors_capacity_fifty_without_discarding_thirty_manual_entries()
    {
        await using var fixture = await MemoryDatabase.CreateAsync(migrate: false);
        await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All.Take(10)).RunAsync();
        for (var index = 0; index < 30; index++)
        {
            await InsertLegacyAsync(fixture, RepositoryMemoryId.New(), RepositoryMemoryAuthority.UserAuthored, RepositoryMemoryValidity.Active, true, "Manual preference " + index);
        }

        await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.ForRepositoryMemoryCapacity(50)).RunAsync();
        Assert.Equal(30, (await fixture.Store.GetSnapshotAsync("repo", [])).Entries.Count);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT evicted_count FROM managed_memory_migration;"));
    }

    /// <summary>Session retention in repository A follows durable receipt ownership into repository B after rebinding.</summary>
    [Fact]
    public async Task Retention_follows_repository_switch_and_preserves_current_run_receipts()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var owner = new SqliteEventStore(fixture.ConnectionString);
        var repositoryB = Path.Combine(fixture.DirectoryPath, "repository-b");
        Directory.CreateDirectory(repositoryB);
        using var router = new RepositoryBoundMemoryStore(fixture.ConnectionString, fixture.DirectoryPath, fixture.Clock, owner);
        await router.BindRepositoryAsync(repositoryB, 50);
        var identityB = RepositoryIdentity.Create(repositoryB);
        var entry = Assert.IsType<RepositoryMemoryEntry>((await router.AddAsync(identityB, Write("B's durable preference"), Model, Embedding, new())).Entry);
        var expiredRun = RunId.New();
        var currentRun = RunId.New();
        await owner.AppendAsync(new PlanProposed(SessionId.New(), fixture.Clock.GetUtcNow().AddDays(-90), "expired", expiredRun));
        await owner.AppendAsync(new PlanProposed(SessionId.New(), fixture.Clock.GetUtcNow(), "current", currentRun));
        await router.RecordInclusionsAsync(identityB, expiredRun, [new(entry.Id, 1)]);
        await router.RecordInclusionsAsync(identityB, currentRun, [new(entry.Id, 1)]);
        Assert.Equal(2, await fixture.ScalarAsync("SELECT count(*) FROM memory_inclusion_retention_targets;"));
        var artifacts = new ArtifactStore(fixture.ConnectionString, Path.Combine(fixture.DirectoryPath, "artifacts"), new SecretOutputSanitizer(), fixture.Clock);
        await artifacts.InitializeAsync();
        var retention = new RetentionService(owner, artifacts, new(), NullLogger<RetentionService>.Instance, fixture.Clock);
        await retention.RunAsync();
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM memory_inclusion_retention_targets;"));
        await using var b = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(repositoryB, ".threadsmith", "threadsmith.db"), Pooling = false }.ToString());
        await b.OpenAsync();
        await using var remaining = b.CreateCommand();
        remaining.CommandText = "SELECT run_id FROM managed_memory_inclusions;";
        Assert.Equal(currentRun.Value.ToString("D"), await remaining.ExecuteScalarAsync());
        Assert.Equal(2, Assert.Single((await router.GetSnapshotAsync(identityB, [])).Entries).InclusionCount);
    }

    /// <summary>A failed durable owner registration cannot leave an untracked receipt in the rebound memory database.</summary>
    [Fact]
    public async Task Receipt_registration_failure_prevents_cross_repository_receipt_write()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var owner = new SqliteEventStore(fixture.ConnectionString);
        var repositoryB = Path.Combine(fixture.DirectoryPath, "repository-b");
        Directory.CreateDirectory(repositoryB);
        using var router = new RepositoryBoundMemoryStore(fixture.ConnectionString, fixture.DirectoryPath, fixture.Clock, owner);
        await router.BindRepositoryAsync(repositoryB);
        var identityB = RepositoryIdentity.Create(repositoryB);
        var entry = Assert.IsType<RepositoryMemoryEntry>((await router.AddAsync(identityB, Write("B preference"), Model, Embedding, new())).Entry);
        await fixture.ExecuteAsync("CREATE TRIGGER fail_retention_registration BEFORE INSERT ON memory_inclusion_retention_targets BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => router.RecordInclusionsAsync(identityB, RunId.New(), [new(entry.Id, 1)]));
        Assert.Equal(0, Assert.Single((await router.GetSnapshotAsync(identityB, [])).Entries).InclusionCount);
    }

    /// <summary>Retention rejects a tampered destination path before writing outside the registered repository database.</summary>
    [Fact]
    public async Task Cross_repository_retention_rejects_redirected_database_path()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var owner = new SqliteEventStore(fixture.ConnectionString);
        var repositoryB = Path.Combine(fixture.DirectoryPath, "repository-b");
        Directory.CreateDirectory(repositoryB);
        using var router = new RepositoryBoundMemoryStore(fixture.ConnectionString, fixture.DirectoryPath, fixture.Clock, owner);
        await router.BindRepositoryAsync(repositoryB);
        var identityB = RepositoryIdentity.Create(repositoryB);
        var entry = Assert.IsType<RepositoryMemoryEntry>((await router.AddAsync(identityB, Write("Keep receipt"), Model, Embedding, new())).Entry);
        var expiredRun = RunId.New();
        await owner.AppendAsync(new PlanProposed(SessionId.New(), fixture.Clock.GetUtcNow().AddDays(-90), "expired", expiredRun));
        await router.RecordInclusionsAsync(identityB, expiredRun, [new(entry.Id, 1)]);
        await fixture.ExecuteAsync("UPDATE memory_inclusion_retention_targets SET database_path = repository_root || '/unrelated.db';");
        var warnings = await owner.PruneExpiredMemoryInclusionsAsync(fixture.Clock.GetUtcNow().AddDays(-30));
        Assert.Single(warnings);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM memory_inclusion_retention_targets;"));
        Assert.Equal(1, Assert.Single((await router.GetSnapshotAsync(identityB, [])).Entries).InclusionCount);
    }

    /// <summary>Custom initial persistence remains the receipt owner and survives A-to-B-to-A rebinding with reduced capacity.</summary>
    [Fact]
    public async Task Custom_initial_database_records_usage_and_survives_repository_round_trip()
    {
        await using var fixture = await MemoryDatabase.CreateAsync(relativeDatabasePath: Path.Combine(".threadsmith", "custom.db"));
        var owner = new SqliteEventStore(fixture.ConnectionString);
        using var router = new RepositoryBoundMemoryStore(fixture.ConnectionString, fixture.DirectoryPath, fixture.Clock, owner);
        var identityA = RepositoryIdentity.Create(fixture.DirectoryPath);
        var first = Assert.IsType<RepositoryMemoryEntry>((await router.AddAsync(identityA, Write("A first"), Model, Embedding, new())).Entry);
        await router.RecordInclusionsAsync(identityA, RunId.New(), [new(first.Id, 1)]);
        Assert.Equal(1, Assert.Single((await router.GetSnapshotAsync(identityA, [])).Entries).InclusionCount);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM memory_inclusion_retention_targets;"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = Assert.IsType<RepositoryMemoryEntry>((await router.AddAsync(identityA, Write("A second"), Model, Embedding, new())).Entry);
        var repositoryB = Path.Combine(fixture.DirectoryPath, "repository-b");
        Directory.CreateDirectory(repositoryB);
        await router.BindRepositoryAsync(repositoryB);
        await router.AddAsync(RepositoryIdentity.Create(repositoryB), Write("B only"), Model, Embedding, new());
        await router.BindRepositoryAsync(fixture.DirectoryPath, 1);
        Assert.Equal(second.Id, Assert.Single((await router.GetSnapshotAsync(identityA, [])).Entries).Id);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, ".threadsmith", "threadsmith.db")));
        var expiredRun = RunId.New();
        await owner.AppendAsync(new PlanProposed(SessionId.New(), fixture.Clock.GetUtcNow().AddDays(-90), "expired", expiredRun));
        await router.RecordInclusionsAsync(identityA, expiredRun, [new(second.Id, 1)]);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT count(*) FROM managed_memory_inclusions;"));
        await owner.PruneExpiredMemoryInclusionsAsync(fixture.Clock.GetUtcNow().AddDays(-30));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM managed_memory_inclusions;"));
        Assert.Equal(1, Assert.Single((await router.GetSnapshotAsync(identityA, [])).Entries).InclusionCount);
    }

    /// <summary>An unavailable foreign receipt table defers cleanup without blocking healthy-owner retention, then retries after owner events expire.</summary>
    [Fact]
    public async Task Foreign_retention_failure_preserves_retry_target_and_does_not_block_owner()
    {
        await using var fixture = await MemoryDatabase.CreateAsync();
        var owner = new SqliteEventStore(fixture.ConnectionString);
        var repositoryB = Path.Combine(fixture.DirectoryPath, "repository-b");
        var repositoryC = Path.Combine(fixture.DirectoryPath, "repository-c");
        Directory.CreateDirectory(repositoryB);
        Directory.CreateDirectory(repositoryC);
        using var router = new RepositoryBoundMemoryStore(fixture.ConnectionString, fixture.DirectoryPath, fixture.Clock, owner);
        await router.BindRepositoryAsync(repositoryB);
        var identityB = RepositoryIdentity.Create(repositoryB);
        var entryB = Assert.IsType<RepositoryMemoryEntry>((await router.AddAsync(identityB, Write("B preference"), Model, Embedding, new())).Entry);
        var runB = new RunId(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var runC = new RunId(Guid.Parse("20000000-0000-0000-0000-000000000002"));
        await owner.AppendAsync(new PlanProposed(SessionId.New(), fixture.Clock.GetUtcNow().AddDays(-90), "B expired", runB));
        await owner.AppendAsync(new PlanProposed(SessionId.New(), fixture.Clock.GetUtcNow().AddDays(-90), "C expired", runC));
        await router.RecordInclusionsAsync(identityB, runB, [new(entryB.Id, 1)]);
        await router.BindRepositoryAsync(repositoryC);
        var identityC = RepositoryIdentity.Create(repositoryC);
        var entryC = Assert.IsType<RepositoryMemoryEntry>((await router.AddAsync(identityC, Write("C preference"), Model, Embedding, new())).Entry);
        await router.RecordInclusionsAsync(identityC, runC, [new(entryC.Id, 1)]);
        await fixture.ExecuteAsync("UPDATE memory_inclusion_retention_targets SET registered_at = '2020-01-01T00:00:00.0000000+00:00';");
        // Cross the production 128-target page boundary: repeated failures for B must not
        // spend another timeout per run, and healthy C sorts after all 129 B targets.
        await fixture.ExecuteAsync("""
            WITH RECURSIVE runs(n) AS (SELECT 2 UNION ALL SELECT n + 1 FROM runs WHERE n < 129)
            INSERT INTO memory_inclusion_retention_targets(run_id, repository_identity, repository_root, database_path, registered_at)
            SELECT printf('10000000-0000-0000-0000-%012d', n), target.repository_identity,
                target.repository_root, target.database_path, target.registered_at
            FROM runs CROSS JOIN memory_inclusion_retention_targets target
            WHERE target.run_id = '10000000-0000-0000-0000-000000000001';
            """);
        await using var b = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(repositoryB, ".threadsmith", "threadsmith.db"), Pooling = false }.ToString());
        await b.OpenAsync();
        await using var receiptSchema = b.CreateCommand();
        receiptSchema.CommandText = "ALTER TABLE managed_memory_inclusions RENAME TO temporarily_unavailable_inclusions;";
        await receiptSchema.ExecuteNonQueryAsync();
        var warnings = await owner.PruneExpiredMemoryInclusionsAsync(fixture.Clock.GetUtcNow().AddDays(-30));
        Assert.Single(warnings);
        Assert.Equal(129, await fixture.ScalarAsync("SELECT count(*) FROM memory_inclusion_retention_targets;"));
        var artifacts = new ArtifactStore(fixture.ConnectionString, Path.Combine(fixture.DirectoryPath, "artifacts"), new SecretOutputSanitizer(), fixture.Clock);
        await artifacts.InitializeAsync();
        await new RetentionService(owner, artifacts, new(), NullLogger<RetentionService>.Instance, fixture.Clock).RunAsync();
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM domain_events;"));
        Assert.Equal(129, await fixture.ScalarAsync("SELECT count(*) FROM memory_inclusion_retention_targets;"));
        receiptSchema.CommandText = "ALTER TABLE temporarily_unavailable_inclusions RENAME TO managed_memory_inclusions;";
        await receiptSchema.ExecuteNonQueryAsync();
        Assert.Empty(await owner.PruneExpiredMemoryInclusionsAsync(fixture.Clock.GetUtcNow().AddDays(-30)));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT count(*) FROM memory_inclusion_retention_targets;"));
        receiptSchema.CommandText = "SELECT count(*) FROM managed_memory_inclusions;";
        Assert.Equal(0L, await receiptSchema.ExecuteScalarAsync());
    }

    private static RepositoryMemoryWrite Write(string text) => new() { Text = text, Origin = RepositoryMemoryOrigin.Manual };

    private static async Task InsertLegacyAsync(
        MemoryDatabase fixture,
        RepositoryMemoryId id,
        RepositoryMemoryAuthority authority,
        RepositoryMemoryValidity validity,
        bool userCommand,
        string text)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repository_memory(memory_id, repository_identity, kind, authority, validity, sensitivity,
                content, content_hash, created_at, updated_at, schema_version)
            VALUES($id, 'repo', 0, $authority, $validity, 0, $text, '', $now, $now, 1);
            INSERT INTO repository_memory_sources(memory_id, source_kind, source_id, ordinal)
            VALUES($id, $source, 'explicit-fixture', 0);
            """;
        command.Parameters.AddWithValue("$id", id.Value.ToString());
        command.Parameters.AddWithValue("$authority", (int)authority);
        command.Parameters.AddWithValue("$validity", (int)validity);
        command.Parameters.AddWithValue("$source", userCommand ? (int)RepositoryMemorySourceKind.UserCommand : (int)RepositoryMemorySourceKind.Run);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", fixture.Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MemoryClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class MemoryDatabase : IAsyncDisposable
    {
        private readonly string _directory;

        private MemoryDatabase(string directory, string relativeDatabasePath)
        {
            _directory = directory;
            var databasePath = Path.Combine(directory, relativeDatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? directory);
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            Store = new SqliteManagedRepositoryMemoryStore(ConnectionString, Clock);
        }

        internal string DirectoryPath => _directory;

        internal string ConnectionString { get; }

        internal MemoryClock Clock { get; } = new();

        internal SqliteManagedRepositoryMemoryStore Store { get; }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var actualParent = Directory.GetParent(_directory)?.FullName;
            if (!string.Equals(actualParent, expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(_directory).StartsWith("threadsmith-plan103-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing cleanup outside the exact owned fixture directory.");
            }

            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }

        internal static async Task<MemoryDatabase> CreateAsync(bool migrate = true, string relativeDatabasePath = "memory.db")
        {
            var directory = Path.Combine(Path.GetTempPath(), "threadsmith-plan103-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var fixture = new MemoryDatabase(directory, relativeDatabasePath);
            await new SqliteEventStore(fixture.ConnectionString).InitializeAsync();
            if (migrate)
            {
                await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All).RunAsync();
            }

            return fixture;
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<int> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
    }
}
