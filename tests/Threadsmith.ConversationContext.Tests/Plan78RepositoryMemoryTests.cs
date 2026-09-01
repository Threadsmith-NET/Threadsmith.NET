namespace Threadsmith.ConversationContext.Tests;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Cli;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tui;
using Xunit;

/// <summary>Plan 78 repository-scoped cross-session memory persistence and tolerance tests.</summary>
public static class Plan78RepositoryMemoryTests
{
    /// <summary>Stable repository-memory identifiers preserve value equality through JSON.</summary>
    [Fact]
    public static void Repository_memory_identifier_round_trips()
    {
        var memoryId = RepositoryMemoryId.New();

        var json = JsonSerializer.Serialize(new IdentifierPair(memoryId));
        var restored = JsonSerializer.Deserialize<IdentifierPair>(json);

        Assert.NotNull(restored);
        Assert.Equal(memoryId, restored.MemoryId);
    }

    /// <summary>Explicit repository-memory commands share deterministic headless handlers and emit audit events.</summary>
    [Fact]
    public static async Task Repository_memory_commands_round_trip_and_publish_audit_events()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var application = new RepositoryMemoryApplication(
            new RepositoryMemoryGovernor(
                new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer()),
                new SecretOutputSanitizer()),
            events);
        var sessionId = SessionId.New();
        var repositoryIdentity = "repo-memory-commands";

        var remembered = await application.HandleAsync(new RememberRepositoryMemoryCommand(
            sessionId,
            repositoryIdentity,
            "Use src/Threadsmith.sln for full solution builds.",
            RepositoryMemoryKind.WorkflowFact));
        var duplicate = await application.HandleAsync(new RememberRepositoryMemoryCommand(
            sessionId,
            repositoryIdentity,
            "  use   src/Threadsmith.sln for full solution builds.  ",
            RepositoryMemoryKind.WorkflowFact));
        var listed = await application.HandleAsync(new ListRepositoryMemoryCommand(sessionId, repositoryIdentity));
        var inspected = await application.HandleAsync(new InspectRepositoryMemoryCommand(
            sessionId,
            repositoryIdentity,
            remembered.Id));
        var replacement = await application.HandleAsync(new SupersedeRepositoryMemoryCommand(
            sessionId,
            repositoryIdentity,
            remembered.Id,
            "Use src/Threadsmith.sln for full solution builds and tests."));
        var forgotten = await application.HandleAsync(new ForgetRepositoryMemoryCommand(
            sessionId,
            repositoryIdentity,
            replacement.Id));
        var final = await application.HandleAsync(new ListRepositoryMemoryCommand(sessionId, repositoryIdentity));

        Assert.Single(listed.Items);
        Assert.Equal(remembered.Id, duplicate.Id);
        Assert.Equal(remembered.Id, inspected?.Id);
        Assert.True(forgotten);
        Assert.Equal(RepositoryMemoryValidity.Superseded, final.Items.Single(item => item.Id == remembered.Id).Validity);
        Assert.Equal(RepositoryMemoryValidity.Forgotten, final.Items.Single(item => item.Id == replacement.Id).Validity);
        Assert.Contains(observed, item => item is RepositoryMemoryRemembered rememberedEvent
            && rememberedEvent.MemoryId == remembered.Id);
        Assert.Single(observed.OfType<RepositoryMemoryRemembered>());
        Assert.Contains(observed, item => item is RepositoryMemorySuperseded supersededEvent
            && supersededEvent.SupersededId == remembered.Id
            && supersededEvent.ReplacementId == replacement.Id);
        Assert.Contains(observed, item => item is RepositoryMemoryValidityChanged changed
            && changed.MemoryId == replacement.Id
            && changed.Validity == RepositoryMemoryValidity.Forgotten);
    }

    /// <summary>Capacity demotions are returned by the governor and published as validity audit events.</summary>
    [Fact]
    public static async Task Remembering_beyond_active_bound_publishes_overflow_demotion()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var application = new RepositoryMemoryApplication(
            new RepositoryMemoryGovernor(
                new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer()),
                new SecretOutputSanitizer(),
                new RepositoryMemoryPolicy { MaximumActiveItems = 1 }),
            events);
        var sessionId = SessionId.New();
        var repositoryIdentity = "repo-memory-overflow";

        var first = await application.HandleAsync(new RememberRepositoryMemoryCommand(
            sessionId,
            repositoryIdentity,
            "First workflow fact.",
            RepositoryMemoryKind.WorkflowFact));
        var second = await application.HandleAsync(new RememberRepositoryMemoryCommand(
            sessionId,
            repositoryIdentity,
            "Second workflow fact.",
            RepositoryMemoryKind.WorkflowFact));
        var snapshot = await application.HandleAsync(new ListRepositoryMemoryCommand(sessionId, repositoryIdentity));

        Assert.Equal(RepositoryMemoryValidity.Stale, snapshot.Items.Single(item => item.Id == first.Id).Validity);
        Assert.Equal(RepositoryMemoryValidity.Active, snapshot.Items.Single(item => item.Id == second.Id).Validity);
        var changed = Assert.Single(observed.OfType<RepositoryMemoryValidityChanged>());
        Assert.Equal(first.Id, changed.MemoryId);
        Assert.Equal(RepositoryMemoryValidity.Stale, changed.Validity);
    }

    /// <summary>Concurrent remembers serialize capacity selection with insertion.</summary>
    [Fact]
    public static async Task Concurrent_remembers_cannot_exceed_active_item_bound()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        const string repositoryIdentity = "repo-memory-concurrent-bound";
        var governor = new RepositoryMemoryGovernor(
            new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer()),
            new SecretOutputSanitizer(),
            new RepositoryMemoryPolicy { MaximumActiveItems = 2 });
        var sessionId = SessionId.New();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => governor.RememberAsync(
            new RememberRepositoryMemoryCommand(
                sessionId,
                repositoryIdentity,
                $"Concurrent workflow fact {index}.",
                RepositoryMemoryKind.WorkflowFact))));
        var snapshot = await governor.ListAsync(new ListRepositoryMemoryCommand(sessionId, repositoryIdentity));

        Assert.Equal(8, snapshot.Items.Count);
        Assert.Equal(2, snapshot.Items.Count(item => item.Validity == RepositoryMemoryValidity.Active));
        Assert.Equal(6, snapshot.Items.Count(item => item.Validity == RepositoryMemoryValidity.Stale));
    }

    /// <summary>Host-observed promotion shares explicit-memory character and active-item bounds.</summary>
    [Fact]
    public static async Task Host_observed_promotion_is_bounded_and_retains_overflow_as_inactive_audit_state()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        const string repositoryIdentity = "repo-memory-host-bounds";
        var governor = new RepositoryMemoryGovernor(
            new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer()),
            new SecretOutputSanitizer(),
            new RepositoryMemoryPolicy { MaximumItemCharacters = 12, MaximumActiveItems = 1 });
        var sessionId = SessionId.New();

        await governor.PromoteHostObservedAsync(new HostObservedRepositoryMemoryPromotion(
            sessionId,
            RunId.New(),
            repositoryIdentity,
            RepositoryMemoryKind.WorkflowFact,
            "First host-observed workflow fact is deliberately long."));
        await governor.PromoteHostObservedAsync(new HostObservedRepositoryMemoryPromotion(
            sessionId,
            RunId.New(),
            repositoryIdentity,
            RepositoryMemoryKind.WorkflowFact,
            "Second host-observed workflow fact is also deliberately long."));
        var snapshot = await governor.ListAsync(new ListRepositoryMemoryCommand(sessionId, repositoryIdentity));

        Assert.Equal(2, snapshot.Items.Count);
        Assert.All(snapshot.Items, item => Assert.True(item.Content.Length <= 12));
        Assert.Single(snapshot.Items, item => item.Validity == RepositoryMemoryValidity.Active);
        Assert.Single(snapshot.Items, item => item.Validity == RepositoryMemoryValidity.Stale);
    }

    /// <summary>Validation reactivates independent user memory and retains unverifiable repository-dependent items.</summary>
    [Fact]
    public static async Task Validate_rechecks_stale_items_and_publishes_each_durable_outcome()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var repositoryIdentity = "repo-memory-validation";
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());
        var reactivated = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Explicit preference without repository support.",
        });
        var retained = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Fact backed by a repository path.",
            Scope = new RepositoryMemoryScope { Paths = ["src/Threadsmith.sln"] },
        });
        foreach (var item in new[] { reactivated, retained })
        {
            Assert.True(await store.UpdateValidityAsync(
                repositoryIdentity,
                item.Id,
                RepositoryMemoryValidity.Stale,
                "Repository support changed."));
        }

        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var application = new RepositoryMemoryApplication(
            new RepositoryMemoryGovernor(store, new SecretOutputSanitizer()),
            events);
        var sessionId = SessionId.New();

        var snapshot = await application.HandleAsync(
            new ValidateRepositoryMemoryCommand(sessionId, repositoryIdentity));
        var repeated = await application.HandleAsync(
            new ValidateRepositoryMemoryCommand(sessionId, repositoryIdentity));

        var activeItem = snapshot.Items.Single(item => item.Id == reactivated.Id);
        Assert.Equal(RepositoryMemoryValidity.Active, activeItem.Validity);
        Assert.Contains("reactivated", activeItem.StateReason, StringComparison.Ordinal);
        var staleItem = snapshot.Items.Single(item => item.Id == retained.Id);
        Assert.Equal(RepositoryMemoryValidity.Stale, staleItem.Validity);
        Assert.Contains("remains stale", staleItem.StateReason, StringComparison.Ordinal);
        var validityEvents = observed.OfType<RepositoryMemoryValidityChanged>().ToArray();
        Assert.Single(validityEvents);
        Assert.DoesNotContain(validityEvents, item => item.MemoryId == retained.Id);
        Assert.Equal(
            snapshot.Items.Select(item => (item.Id, item.Validity, item.StateReason)),
            repeated.Items.Select(item => (item.Id, item.Validity, item.StateReason)));
    }

    /// <summary>Repository lifecycle observation invalidates only repository-dependent memory after mutations.</summary>
    [Fact]
    public static async Task Repository_memory_invalidates_repository_dependent_items_after_mutation()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var sanitizer = new SecretOutputSanitizer();
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, sanitizer);
        var repositoryIdentity = RepositoryIdentity.Create(fixture.DirectoryPath);
        var dependent = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Path-dependent workflow fact.",
            Scope = new RepositoryMemoryScope { Paths = ["src/Threadsmith.sln"] },
        });
        var independent = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Independent user preference.",
        });
        var observer = new ContextLifecycleObserver(
            new EvidenceStore(events, sanitizer),
            new PromptAppendLoader(sanitizer),
            repositoryMemoryInvalidator: new RepositoryMemoryInvalidator(store, events: events));
        var sessionId = SessionId.New();

        await observer.ObserveAsync(new RepositoryOpened(sessionId, DateTimeOffset.UtcNow, fixture.DirectoryPath));
        await observer.ObserveAsync(new MutationApplied(
            sessionId,
            DateTimeOffset.UtcNow,
            MutationId.New(),
            RelativePath: "src/Threadsmith.sln"));
        var snapshot = await store.GetSnapshotAsync(repositoryIdentity);

        Assert.Equal(RepositoryMemoryValidity.Stale, snapshot.Items.Single(item => item.Id == dependent.Id).Validity);
        Assert.Equal(RepositoryMemoryValidity.Active, snapshot.Items.Single(item => item.Id == independent.Id).Validity);
        Assert.Contains(observed, item => item is RepositoryMemoryValidityChanged changed
            && changed.MemoryId == dependent.Id
            && changed.Validity == RepositoryMemoryValidity.Stale);
    }

    /// <summary>TUI presenter and headless shell repository-memory operations dispatch the same host commands.</summary>
    [Fact]
    public static async Task Tui_and_headless_repository_memory_controls_have_command_parity()
    {
        var dispatcher = new RecordingRepositoryMemoryDispatcher();
        var projections = new EmptyProjectionStore();
        var presenter = new TuiPresenter(dispatcher, projections);
        var headless = new HeadlessShell(dispatcher, projections, TextWriter.Null);
        var sessionId = SessionId.New();
        var repositoryIdentity = "repo-memory-surface-parity";

        _ = await presenter.RememberRepositoryMemoryAsync(
            sessionId,
            repositoryIdentity,
            "Use explicit repository memory commands.",
            RepositoryMemoryKind.WorkflowFact);
        _ = await headless.RememberRepositoryMemoryAsync(
            sessionId,
            repositoryIdentity,
            "Use explicit repository memory commands.");
        _ = await presenter.ListRepositoryMemoryAsync(sessionId, repositoryIdentity);
        _ = await headless.ListRepositoryMemoryAsync(sessionId, repositoryIdentity);

        Assert.Equal(4, dispatcher.Commands.Count);
        Assert.Equal(2, dispatcher.Commands.OfType<RememberRepositoryMemoryCommand>().Count());
        Assert.Equal(2, dispatcher.Commands.OfType<ListRepositoryMemoryCommand>().Count());
        Assert.All(dispatcher.Commands.OfType<RememberRepositoryMemoryCommand>(), command =>
        {
            Assert.Equal(sessionId, command.SessionId);
            Assert.Equal(repositoryIdentity, command.RepositoryIdentity);
            Assert.Equal(RepositoryMemoryKind.WorkflowFact, command.Kind);
        });
    }

    /// <summary>Context assembly retrieves only active bounded repository memory and reports omissions.</summary>
    [Fact]
    public static async Task Context_assembly_includes_active_repository_memory_with_inspection_rationale()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, sanitizer);
        var repositoryIdentity = "repo-memory-context";
        var active = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Use src/Threadsmith.sln for full solution builds.",
            Scope = new RepositoryMemoryScope { Paths = ["src/Threadsmith.sln"] },
        });
        var stale = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Use the retired Threadsmith.Legacy.sln for builds.",
        });
        await store.UpdateValidityAsync(
            repositoryIdentity,
            stale.Id,
            RepositoryMemoryValidity.Stale,
            "Supporting solution file changed.");
        var assembler = CreateAssembler(fixture, events, store);

        var result = await assembler.AssembleAsync(CreateRequest(fixture, repositoryIdentity));

        Assert.Contains("<repository_memory>", result.ModelInput, StringComparison.Ordinal);
        Assert.Contains("Use src/Threadsmith.sln", result.ModelInput, StringComparison.Ordinal);
        Assert.DoesNotContain("Threadsmith.Legacy.sln", result.ModelInput, StringComparison.Ordinal);
        Assert.True(result.ModelConstraints.ContainsSensitiveData);
        Assert.True(result.Inspection.TokensByCategory["repositoryMemory"] > 0);
        var included = Assert.Single(result.Inspection.RepositoryMemoryItems, item => item.Included);
        Assert.Equal(active.Id, included.Id);
        Assert.Equal(RepositoryMemoryValidity.Active, included.Validity);
        var omitted = Assert.Single(result.Inspection.RepositoryMemoryItems, item => !item.Included);
        Assert.Equal(stale.Id, omitted.Id);
        Assert.Contains("stale", omitted.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Automatic repository memory must be relevant and recent while explicit user memory remains eligible.</summary>
    [Fact]
    public static async Task Context_assembly_filters_automatic_memory_by_relevance_and_age()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());
        const string repositoryIdentity = "repo-memory-admission";
        var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var relevant = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Authority = RepositoryMemoryAuthority.HostObserved,
            Content = "Completed request: Build and test Threadsmith solution.",
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddHours(-1),
        });
        var unrelated = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Authority = RepositoryMemoryAuthority.HostObserved,
            Content = "Completed request: Explain terminal color preferences.",
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddHours(-1),
        });
        var oldAutomatic = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Authority = RepositoryMemoryAuthority.HostObserved,
            Kind = RepositoryMemoryKind.ArchitectureDecision,
            Content = "Completed request: Build and test Threadsmith solution.",
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now.AddDays(-3),
        });
        var explicitMemory = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Keep release notes concise.",
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-30),
        });
        var assembler = CreateAssembler(
            fixture,
            events,
            store,
            timeProvider: new FixedTimeProvider(now));

        var result = await assembler.AssembleAsync(CreateRequest(fixture, repositoryIdentity));

        Assert.Contains(result.Inspection.RepositoryMemoryItems, item => item.Id == relevant.Id && item.Included);
        Assert.Contains(result.Inspection.RepositoryMemoryItems, item => item.Id == explicitMemory.Id && item.Included);
        Assert.Contains(result.Inspection.RepositoryMemoryItems, item =>
            item.Id == unrelated.Id
            && !item.Included
            && item.Rationale.Contains("relevance score", StringComparison.Ordinal));
        Assert.Contains(result.Inspection.RepositoryMemoryItems, item =>
            item.Id == oldAutomatic.Id
            && !item.Included
            && item.Rationale.Contains("maximum prompt age", StringComparison.Ordinal));
    }

    /// <summary>A single meaningful query term can fully match fresh automatic memory.</summary>
    [Fact]
    public static async Task Context_assembly_includes_exact_single_term_automatic_memory()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());
        const string repositoryIdentity = "repo-memory-single-term";
        var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var memory = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Authority = RepositoryMemoryAuthority.HostObserved,
            Content = "OAuth authentication workflow.",
            CreatedAt = now,
            UpdatedAt = now,
        });
        var assembler = CreateAssembler(
            fixture,
            events,
            store,
            timeProvider: new FixedTimeProvider(now));
        var request = CreateRequest(fixture, repositoryIdentity) with
        {
            Task = new TaskSpecification("OAuth", []),
        };

        var result = await assembler.AssembleAsync(request);

        var included = Assert.Single(result.Inspection.RepositoryMemoryItems, item => item.Included);
        Assert.Equal(memory.Id, included.Id);
        Assert.Equal(1.0d, included.Score);
    }

    /// <summary>A duplicate automatic observation does not extend recency without new durable provenance.</summary>
    [Fact]
    public static async Task Repository_memory_duplicate_observation_does_not_refresh_recency()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var firstObservedAt = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(firstObservedAt);
        var store = new SqliteRepositoryMemoryStore(
            fixture.ConnectionString,
            new SecretOutputSanitizer(),
            timeProvider);
        const string repositoryIdentity = "repo-memory-refresh";
        var candidate = CreateMemory(repositoryIdentity) with
        {
            Authority = RepositoryMemoryAuthority.HostObserved,
            CreatedAt = default,
            UpdatedAt = default,
        };
        var first = await store.InsertBoundedAsync(candidate, 12);
        var refreshedAt = firstObservedAt.AddDays(3);
        timeProvider.UtcNow = refreshedAt;

        var duplicate = await store.InsertBoundedAsync(candidate with { Id = RepositoryMemoryId.New() }, 12);

        Assert.True(first.WasInserted);
        Assert.False(duplicate.WasInserted);
        Assert.Equal(first.Item.Id, duplicate.Item.Id);
        Assert.Equal(firstObservedAt, duplicate.Item.CreatedAt);
        Assert.Equal(firstObservedAt, duplicate.Item.UpdatedAt);
    }

    /// <summary>Repository-memory relevance and age settings reject unsafe values.</summary>
    [Fact]
    public static void Repository_memory_context_policy_validates_admission_settings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RepositoryMemoryContextPolicy { MinimumRelevanceScore = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RepositoryMemoryContextPolicy { AutomaticMemoryMaximumAge = TimeSpan.Zero }.Validate());
    }

    /// <summary>Repository-memory retrieval remains bounded and reports pressure omissions.</summary>
    [Fact]
    public static async Task Context_assembly_omits_repository_memory_beyond_configured_budget()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, sanitizer);
        var repositoryIdentity = "repo-memory-context-budget";
        var first = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Remember first workflow fact for builds.",
        });
        var second = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Remember second workflow fact for tests.",
        });
        var assembler = CreateAssembler(
            fixture,
            events,
            store,
            new RepositoryMemoryContextPolicy { MaximumItems = 1, MaximumTokens = 1_000 });

        var result = await assembler.AssembleAsync(CreateRequest(fixture, repositoryIdentity));

        var included = Assert.Single(result.Inspection.RepositoryMemoryItems, item => item.Included);
        Assert.True(included.Id == first.Id || included.Id == second.Id);
        var omitted = Assert.Single(result.Inspection.RepositoryMemoryItems, item => !item.Included);
        Assert.Contains("item budget", omitted.Rationale, StringComparison.Ordinal);
    }

    /// <summary>Repository-memory framing and metadata count against the configured token budget.</summary>
    [Fact]
    public static async Task Context_assembly_charges_rendered_repository_memory_overhead()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, sanitizer);
        const string repositoryIdentity = "repo-memory-rendered-budget";
        var memory = await store.UpsertAsync(CreateMemory(repositoryIdentity) with { Content = "x" });
        var assembler = CreateAssembler(
            fixture,
            events,
            store,
            new RepositoryMemoryContextPolicy { MaximumItems = 1, MaximumTokens = 1 });

        var result = await assembler.AssembleAsync(CreateRequest(fixture, repositoryIdentity));

        Assert.DoesNotContain("<repository_memory>", result.ModelInput, StringComparison.Ordinal);
        var omitted = Assert.Single(result.Inspection.RepositoryMemoryItems);
        Assert.Equal(memory.Id, omitted.Id);
        Assert.False(omitted.Included);
        Assert.True(omitted.EstimatedTokens > 1);
        Assert.Contains("token budget", omitted.Rationale, StringComparison.Ordinal);
    }

    /// <summary>Context retrieval uses the same canonical identity as repository lifecycle persistence.</summary>
    [Fact]
    public static async Task Context_assembly_uses_shared_repository_identity_by_default()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, sanitizer);
        var repositoryIdentity = RepositoryIdentity.Create(fixture.DirectoryPath);
        var memory = await store.UpsertAsync(CreateMemory(repositoryIdentity));
        var assembler = CreateAssembler(fixture, events, store);
        var request = CreateRequest(fixture, repositoryIdentity) with { RepositoryIdentity = null };

        var result = await assembler.AssembleAsync(request);

        var included = Assert.Single(result.Inspection.RepositoryMemoryItems, item => item.Included);
        Assert.Equal(memory.Id, included.Id);
    }

    /// <summary>Repository memory survives independent store instances against the same repository database.</summary>
    [Fact]
    public static async Task Repository_memory_persists_across_store_instances()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var repositoryIdentity = "repo-memory-a";
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());
        var item = CreateMemory(repositoryIdentity) with
        {
            Content = "Use src/Threadsmith.sln for full solution builds token=top-secret.",
            Scope = new RepositoryMemoryScope
            {
                Paths = ["src/Threadsmith.sln"],
                Projects = ["src/Threadsmith.Core/Threadsmith.Core.csproj"],
                Symbols = ["Threadsmith.Core.RepositoryMemoryItem"],
            },
        };

        var persisted = await store.UpsertAsync(item);
        var reopened = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());
        var snapshot = await reopened.GetSnapshotAsync(repositoryIdentity);

        Assert.NotEqual(string.Empty, persisted.ContentHash);
        var restored = Assert.Single(snapshot.Items);
        Assert.Equal(item.Id, restored.Id);
        Assert.Equal(RepositoryMemoryValidity.Active, restored.Validity);
        Assert.Equal("src/Threadsmith.sln", Assert.Single(restored.Scope.Paths));
        Assert.Equal("src/Threadsmith.Core/Threadsmith.Core.csproj", Assert.Single(restored.Scope.Projects));
        Assert.Equal("Threadsmith.Core.RepositoryMemoryItem", Assert.Single(restored.Scope.Symbols));
        Assert.DoesNotContain("top-secret", restored.Content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", restored.Content, StringComparison.Ordinal);
        Assert.Equal(restored.ContentHash, persisted.ContentHash);
    }

    /// <summary>Superseded and forgotten repository memory remains inspectable audit metadata.</summary>
    [Fact]
    public static async Task Supersede_and_forget_preserve_audit_items()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var repositoryIdentity = "repo-memory-b";
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());
        var oldItem = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Use tabs for this repository.",
        });
        var replacement = await store.UpsertAsync(CreateMemory(repositoryIdentity) with
        {
            Content = "Use spaces for this repository.",
            SupersedesId = oldItem.Id,
        });

        var superseded = await store.UpdateValidityAsync(
            repositoryIdentity,
            oldItem.Id,
            RepositoryMemoryValidity.Superseded,
            "User correction superseded this item.");
        var forgotten = await store.UpdateValidityAsync(
            repositoryIdentity,
            replacement.Id,
            RepositoryMemoryValidity.Forgotten,
            "User forgot this item.");
        var snapshot = await store.GetSnapshotAsync(repositoryIdentity);

        Assert.True(superseded);
        Assert.True(forgotten);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal(RepositoryMemoryValidity.Superseded, snapshot.Items.Single(item => item.Id == oldItem.Id).Validity);
        var replacementAudit = snapshot.Items.Single(item => item.Id == replacement.Id);
        Assert.Equal(oldItem.Id, replacementAudit.SupersedesId);
        Assert.Equal(RepositoryMemoryValidity.Forgotten, replacementAudit.Validity);
        Assert.Equal("User forgot this item.", replacementAudit.StateReason);
    }

    /// <summary>Correcting an inactive item preserves the active-item bound and publishes the required demotion.</summary>
    [Fact]
    public static async Task Superseding_inactive_memory_enforces_active_item_bound()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        const string repositoryIdentity = "repo-memory-supersede-bound";
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());
        var inactive = await store.UpsertAsync(CreateMemory(repositoryIdentity) with { Content = "Inactive fact." });
        Assert.True(await store.UpdateValidityAsync(
            repositoryIdentity,
            inactive.Id,
            RepositoryMemoryValidity.Stale,
            "Repository support changed."));
        var active = await store.UpsertAsync(CreateMemory(repositoryIdentity) with { Content = "Active fact." });
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var application = new RepositoryMemoryApplication(
            new RepositoryMemoryGovernor(
                store,
                new SecretOutputSanitizer(),
                new RepositoryMemoryPolicy { MaximumActiveItems = 1 }),
            events);

        var replacement = await application.HandleAsync(new SupersedeRepositoryMemoryCommand(
            SessionId.New(),
            repositoryIdentity,
            inactive.Id,
            "Corrected inactive fact."));
        var snapshot = await store.GetSnapshotAsync(repositoryIdentity);

        Assert.Equal(RepositoryMemoryValidity.Active, snapshot.Items.Single(item => item.Id == replacement.Id).Validity);
        Assert.Equal(RepositoryMemoryValidity.Stale, snapshot.Items.Single(item => item.Id == active.Id).Validity);
        Assert.Single(snapshot.Items, item => item.Validity == RepositoryMemoryValidity.Active);
        Assert.Contains(observed, item => item is RepositoryMemoryValidityChanged changed
            && changed.MemoryId == active.Id
            && changed.Validity == RepositoryMemoryValidity.Stale);
    }

    /// <summary>Capacity updates and insertion share one transaction so a stale snapshot cannot partially demote memory.</summary>
    [Fact]
    public static async Task Capacity_update_conflict_rolls_back_new_memory()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        const string repositoryIdentity = "repo-memory-capacity-conflict";
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());
        var existing = await store.UpsertAsync(CreateMemory(repositoryIdentity) with { Content = "Existing fact." });
        var replacement = CreateMemory(repositoryIdentity) with { Content = "New fact." };
        var staleUpdate = new RepositoryMemoryStateUpdate(
            existing.Id,
            RepositoryMemoryValidity.Stale,
            RepositoryMemoryValidity.Forgotten,
            "Conflicting stale snapshot update.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertWithStateUpdatesAsync(
            replacement,
            [staleUpdate]));
        var snapshot = await store.GetSnapshotAsync(repositoryIdentity);

        Assert.Single(snapshot.Items);
        Assert.Equal(existing.Id, snapshot.Items[0].Id);
        Assert.Equal(RepositoryMemoryValidity.Active, snapshot.Items[0].Validity);
    }

    /// <summary>Future repository-memory schemas produce bounded warnings and no fabricated items.</summary>
    [Fact]
    public static async Task Unknown_repository_memory_schema_produces_warning()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var repositoryIdentity = "repo-memory-c";
        await InsertFutureRepositoryMemoryAsync(fixture.ConnectionString, repositoryIdentity, 999);
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());

        var snapshot = await store.GetSnapshotAsync(repositoryIdentity);

        Assert.Empty(snapshot.Items);
        Assert.Contains("UnsupportedRepositoryMemorySchema:999", snapshot.Warnings);
    }

    /// <summary>Malformed durable rows fail closed instead of crashing restoration or weakening sensitivity policy.</summary>
    [Fact]
    public static async Task Malformed_repository_memory_is_omitted_with_bounded_warning()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        const string repositoryIdentity = "repo-memory-malformed";
        await InsertMalformedRepositoryMemoryAsync(fixture.ConnectionString, repositoryIdentity);
        var store = new SqliteRepositoryMemoryStore(fixture.ConnectionString, new SecretOutputSanitizer());

        var snapshot = await store.GetSnapshotAsync(repositoryIdentity);

        Assert.Empty(snapshot.Items);
        Assert.Contains("MalformedRepositoryMemoryItem", snapshot.Warnings);
    }

    private static RepositoryMemoryItem CreateMemory(string repositoryIdentity)
    {
        var now = DateTimeOffset.UtcNow;
        return new RepositoryMemoryItem
        {
            Id = RepositoryMemoryId.New(),
            RepositoryIdentity = repositoryIdentity,
            Kind = RepositoryMemoryKind.WorkflowFact,
            Authority = RepositoryMemoryAuthority.UserAuthored,
            Content = "Use src/Threadsmith.sln for full solution builds.",
            Sources =
            [
                new RepositoryMemorySource
                {
                    Kind = RepositoryMemorySourceKind.UserCommand,
                    SourceId = "test-command",
                    Description = "Explicit user memory command.",
                },
            ],
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static ContextAssembler CreateAssembler(
        ConversationFixture fixture,
        IDomainEventStream events,
        IRepositoryMemoryStore repositoryMemoryStore,
        RepositoryMemoryContextPolicy? repositoryMemoryPolicy = null,
        TimeProvider? timeProvider = null)
    {
        var sanitizer = new SecretOutputSanitizer();
        return new ContextAssembler(
            new EvidenceStore(events, sanitizer),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            new ContextAssemblerOptions
            {
                RepositoryMemory = repositoryMemoryPolicy ?? new RepositoryMemoryContextPolicy(),
            },
            conversationStore: fixture.Store,
            repositoryMemoryStore: repositoryMemoryStore,
            timeProvider: timeProvider);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return UtcNow;
        }
    }

    private static ContextAssemblyRequest CreateRequest(
        ConversationFixture fixture,
        string repositoryIdentity)
    {
        return new ContextAssemblyRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Phase = RunPhase.EvidenceCollection,
            Task = new TaskSpecification("Build and test Threadsmith solution", []),
            RepositoryPath = fixture.DirectoryPath,
            RepositoryIdentity = repositoryIdentity,
        };
    }

    private static async Task InsertFutureRepositoryMemoryAsync(
        string connectionString,
        string repositoryIdentity,
        int schemaVersion)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repository_memory(
                memory_id, repository_identity, kind, authority, validity, sensitivity,
                content, content_hash, repository_revision, supersedes_id, state_reason,
                created_at, updated_at, schema_version)
            VALUES($memory, $repository, 0, 0, 0, 0, 'future', 'hash', NULL, NULL, NULL, $now, $now, $schema);
            """;
        command.Parameters.AddWithValue("$memory", RepositoryMemoryId.New().Value.ToString("D"));
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$schema", schemaVersion);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertMalformedRepositoryMemoryAsync(
        string connectionString,
        string repositoryIdentity)
    {
        var memoryId = RepositoryMemoryId.New().Value.ToString("D");
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repository_memory(
                memory_id, repository_identity, kind, authority, validity, sensitivity,
                content, content_hash, repository_revision, supersedes_id, state_reason,
                created_at, updated_at, schema_version)
            VALUES($memory, $repository, 0, 0, 0, 999, 'malformed', 'hash', NULL, NULL, NULL, $now, $now, 1);
            INSERT INTO repository_memory_sources(memory_id, source_kind, source_id, description, ordinal)
            VALUES($memory, 0, 'test-command', NULL, 0);
            """;
        command.Parameters.AddWithValue("$memory", memoryId);
        command.Parameters.AddWithValue("$repository", repositoryIdentity);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingRepositoryMemoryDispatcher : ICommandDispatcher
    {
        private readonly RepositoryMemoryItem _item = CreateMemory("repo-memory-surface-parity");

        public List<object> Commands { get; } = [];

        public Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            object? response = command switch
            {
                RememberRepositoryMemoryCommand remember => _item with
                {
                    RepositoryIdentity = remember.RepositoryIdentity,
                    Kind = remember.Kind,
                    Content = remember.Text,
                },
                ListRepositoryMemoryCommand list => new RepositoryMemorySnapshot
                {
                    RepositoryIdentity = list.RepositoryIdentity,
                    Items = [_item with { RepositoryIdentity = list.RepositoryIdentity }],
                },
                InspectRepositoryMemoryCommand inspect => _item with
                {
                    RepositoryIdentity = inspect.RepositoryIdentity,
                    Id = inspect.MemoryId,
                },
                SupersedeRepositoryMemoryCommand supersede => _item with
                {
                    RepositoryIdentity = supersede.RepositoryIdentity,
                    Id = RepositoryMemoryId.New(),
                    SupersedesId = supersede.MemoryId,
                    Content = supersede.ReplacementText,
                },
                ForgetRepositoryMemoryCommand => true,
                ValidateRepositoryMemoryCommand validate => new RepositoryMemorySnapshot
                {
                    RepositoryIdentity = validate.RepositoryIdentity,
                    Items = [_item with { RepositoryIdentity = validate.RepositoryIdentity }],
                },
                _ => default(TResponse),
            };
            return Task.FromResult((TResponse?)response
                ?? throw new InvalidOperationException($"No response configured for {command.GetType().Name}."));
        }
    }

    private sealed class EmptyProjectionStore : IProjectionStore
    {
        public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<TProjection?> GetAsync<TProjection>(
            ProjectionKey key,
            CancellationToken cancellationToken = default)
            where TProjection : class, IProjection
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<TProjection?>(null);
        }
    }

    private sealed record IdentifierPair(RepositoryMemoryId MemoryId);
}
