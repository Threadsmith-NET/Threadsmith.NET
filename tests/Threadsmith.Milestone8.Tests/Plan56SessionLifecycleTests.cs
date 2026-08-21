namespace Threadsmith.Milestone8.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Plan 56 durable catalog and independent clone verification.</summary>
public static class Plan56SessionLifecycleTests
{
    /// <summary>Migration 8 makes pre-catalog durable sessions selectable without losing conversation metadata.</summary>
    [Fact]
    public static async Task Migration_8_backfills_existing_durable_sessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"threadsmith-plan56-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var repositoryPath = Path.Combine(directory, "repository");
            Directory.CreateDirectory(repositoryPath);
            var connectionString = $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False";
            var eventStore = new SqliteEventStore(connectionString);
            await eventStore.InitializeAsync();
            _ = await new MigrationRunner(connectionString, DefaultMigrations.All.Take(8)).RunAsync();
            var sessionId = SessionId.New();
            var now = DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture);
            await eventStore.AppendAsync(new SessionCreated(sessionId, now, "Legacy session"));
            await eventStore.AppendAsync(new RepositoryOpened(sessionId, now.AddSeconds(1), repositoryPath));
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO conversation_sessions(session_id, mode, updated_at) VALUES($session, $mode, $updated);
                    INSERT INTO conversation_messages(
                        message_id, session_id, run_id, sequence, role, body, artifact_id, content_hash,
                        estimated_tokens, sensitivity, repository_revision, occurred_at, schema_version)
                    VALUES($message, $session, $run, 0, 1, 'legacy preview', NULL, 'hash', 2, 0, NULL, $updated, 1);
                    """;
                command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
                command.Parameters.AddWithValue("$mode", (int)ConversationContextMode.ConversationAware);
                command.Parameters.AddWithValue("$updated", now.AddSeconds(2).ToString("O"));
                command.Parameters.AddWithValue("$message", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$run", Guid.NewGuid().ToString("D"));
                await command.ExecuteNonQueryAsync();
            }

            _ = await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var store = new SqliteSessionLifecycleStore(connectionString);
            var restored = await store.GetAsync(sessionId);
            Assert.NotNull(restored);
            Assert.Equal("legacy preview", restored.Preview);
            Assert.Equal(1, restored.MessageCount);
            Assert.Equal(sessionId, Assert.Single(await store.ListAsync(restored.RepositoryIdentity, 10)).SessionId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Migration 8 supports deterministic repository listing and exact lookup.</summary>
    [Fact]
    public static async Task Catalog_lists_repository_sessions_newest_first_and_filters_other_repositories()
    {
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var store = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var older = CreateEntry(SessionId.New(), "repo-a", now);
        var newer = CreateEntry(SessionId.New(), "repo-a", now.AddMinutes(1));
        var other = CreateEntry(SessionId.New(), "repo-b", now.AddMinutes(2));

        await store.CreateAsync(older, EmptyUsage());
        await store.CreateAsync(newer, EmptyUsage());
        await store.CreateAsync(other, EmptyUsage());

        var listed = await store.ListAsync("repo-a", 10);
        Assert.Equal([newer.SessionId, older.SessionId], listed.Select(item => item.SessionId));
        Assert.Equal(other, await store.GetAsync(other.SessionId));
    }

    /// <summary>A clone copies sanitized conversation under new identities and then diverges independently.</summary>
    [Fact]
    public static async Task Clone_is_atomic_has_new_identities_and_does_not_modify_source()
    {
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        var sourceId = SessionId.New();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var source = CreateEntry(sourceId, "repo-a", now);
        await catalog.CreateAsync(source, new SessionDurableUsage(10, 5, false, false, true));
        var sourceMessage = await fixture.Conversations.ArchiveMessageAsync(new ConversationMessage
        {
            Id = ConversationMessageId.New(),
            SessionId = sourceId,
            RunId = RunId.New(),
            Sequence = 0,
            Role = ConversationRole.User,
            Content = "sanitized visible request",
            ContentHash = "pending",
            EstimatedTokens = 1,
            OccurredAt = now,
        });
        source = await catalog.CheckpointAsync(source, new SessionDurableUsage(10, 5, false, false, true));
        var cloneId = SessionId.New();
        var cloneRequest = source with
        {
            SessionId = cloneId,
            CreatedAt = now.AddMinutes(1),
            UpdatedAt = now.AddMinutes(1),
            CloneSourceSessionId = sourceId,
            Preview = null,
            MessageCount = 0,
        };

        var clone = await catalog.CloneAsync(
            sourceId,
            cloneRequest,
            new SessionDurableUsage(0, 0, false, false, false, 10, 5));
        var cloneState = await fixture.Conversations.GetSnapshotAsync(cloneId);
        Assert.Equal(sourceId, clone.CloneSourceSessionId);
        var clonedMessage = Assert.Single(cloneState.Messages);
        Assert.NotEqual(sourceMessage.Id, clonedMessage.Id);
        Assert.NotEqual(sourceMessage.RunId, clonedMessage.RunId);
        Assert.Equal(sourceMessage.Content, clonedMessage.Content);

        _ = await fixture.Conversations.ArchiveMessageAsync(new ConversationMessage
        {
            Id = ConversationMessageId.New(),
            SessionId = cloneId,
            RunId = RunId.New(),
            Sequence = 0,
            Role = ConversationRole.Assistant,
            Content = "clone-only response",
            ContentHash = "pending",
            EstimatedTokens = 1,
            OccurredAt = now.AddMinutes(2),
        });
        var sourceAfter = await fixture.Conversations.GetSnapshotAsync(sourceId);
        Assert.Single(sourceAfter.Messages);
        Assert.Equal("sanitized visible request", sourceAfter.Messages[0].Content);
        var cloneUsage = await catalog.GetUsageAsync(cloneId);
        Assert.Equal(0, cloneUsage.InputTokens);
        Assert.Equal(0, cloneUsage.OutputTokens);
        Assert.Equal(10, cloneUsage.InheritedInputTokens);
        Assert.Equal(5, cloneUsage.InheritedOutputTokens);
    }

    /// <summary>A clone receives detached governed evidence without changing the source snapshot.</summary>
    [Fact]
    public static async Task Evidence_copy_preserves_provenance_for_the_destination_session()
    {
        await using var events = new DomainEventStream();
        var evidenceStore = new EvidenceStore(events, new SecretOutputSanitizer());
        var sourceId = SessionId.New();
        var destinationId = SessionId.New();
        var evidence = new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = sourceId,
            Kind = EvidenceKind.SourceExcerpt,
            Content = "governed finding",
            Provenance = new EvidenceProvenance { Source = "test" },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = 4,
            InvalidationKeys = ["src/file.cs"],
        };
        await evidenceStore.AddAsync(evidence);

        evidenceStore.CopySession(sourceId, destinationId);

        var copied = Assert.Single(evidenceStore.Snapshot(destinationId));
        Assert.Equal(evidence.EvidenceId, copied.EvidenceId);
        Assert.Equal(destinationId, copied.SessionId);
        Assert.Equal(evidence.Content, copied.Content);
        Assert.Equal(sourceId, Assert.Single(evidenceStore.Snapshot(sourceId)).SessionId);
    }

    /// <summary>Lifecycle cloning starts local usage at zero and copies governed evidence.</summary>
    [Fact]
    public static async Task Lifecycle_clone_separates_inherited_usage_and_copies_evidence()
    {
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        await using var harness = LifecycleHarness.Create("repo-a", catalog, fixture.Conversations);
        var source = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand());
        harness.Usage.Observe(
            source.ActiveSession.SessionId,
            new ModelRequestUsageId(RunId.New(), "turn", 0, Guid.NewGuid()),
            new ModelUsage(13, 7));
        await harness.Evidence.AddAsync(new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = source.ActiveSession.SessionId,
            Kind = EvidenceKind.SemanticFact,
            Content = "clone-visible evidence",
            Provenance = new EvidenceProvenance { Source = "test" },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = 5,
        });

        var clone = await harness.Lifecycle.HandleAsync(new CloneSessionCommand());

        Assert.Equal(0, clone.InputTokens);
        Assert.Equal(0, clone.OutputTokens);
        Assert.Equal(13, clone.InheritedInputTokens);
        Assert.Equal(7, clone.InheritedOutputTokens);
        Assert.Equal(
            "clone-visible evidence",
            Assert.Single(harness.Evidence.Snapshot(clone.ActiveSession.SessionId)).Content);
    }

    /// <summary>Clone activation publishes a durable base and copied repository authority.</summary>
    [Fact]
    public static async Task Lifecycle_clone_publishes_durable_projection_seed_events()
    {
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        await using var harness = LifecycleHarness.Create("repo-a", catalog, fixture.Conversations);
        var source = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand());
        var observed = new List<IDomainEvent>();
        await using var subscription = harness.Events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        await harness.Events.PublishAsync(new RepositoryOpened(
            source.ActiveSession.SessionId,
            DateTimeOffset.UtcNow,
            Path.GetFullPath("repo-a"),
            WorkspaceId.New(),
            RepositoryTrustLevel.TrustedRead));

        var clone = await harness.Lifecycle.HandleAsync(new CloneSessionCommand());

        Assert.Contains(observed, item => item is SessionCreated created && created.SessionId == clone.ActiveSession.SessionId);
        Assert.Contains(observed, item => item is RepositoryOpened opened
            && opened.SessionId == clone.ActiveSession.SessionId
            && opened.TrustLevel == RepositoryTrustLevel.TrustedRead);
    }

    /// <summary>Repository rebinding activates a fresh session and scopes selectors to the opened repository.</summary>
    [Fact]
    public static async Task Repository_rebind_creates_and_lists_sessions_for_the_new_repository()
    {
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        await using var harness = LifecycleHarness.Create("repo-a", catalog, fixture.Conversations);
        var source = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand());

        await harness.Lifecycle.BindRepositoryAsync("repo-b");

        var active = await harness.Lifecycle.HandleAsync(new GetActiveSessionCommand());
        var listed = await harness.Lifecycle.HandleAsync(
            new ListResumableSessionsCommand());
        Assert.NotEqual(source.ActiveSession.RepositoryIdentity, active.RepositoryIdentity);
        Assert.Equal("repo-b", active.RepositoryDisplayName);
        Assert.Equal(active.SessionId, Assert.Single(listed).SessionId);
    }

    /// <summary>Completed-turn checkpoints persist current usage, message count, and preview immediately.</summary>
    [Fact]
    public static async Task Completed_turn_checkpoint_refreshes_catalog_metadata()
    {
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        await using var harness = LifecycleHarness.Create("repo-a", catalog, fixture.Conversations);
        var created = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand());
        var sessionId = created.ActiveSession.SessionId;
        _ = await fixture.Conversations.ArchiveMessageAsync(new ConversationMessage
        {
            Id = ConversationMessageId.New(),
            SessionId = sessionId,
            RunId = RunId.New(),
            Sequence = 0,
            Role = ConversationRole.Assistant,
            Content = "fresh selector preview",
            ContentHash = "pending",
            EstimatedTokens = 5,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        harness.Usage.Observe(
            sessionId,
            new ModelRequestUsageId(RunId.New(), "turn", 0, Guid.NewGuid()),
            new ModelUsage(21, 8));

        await harness.Lifecycle.CheckpointCompletedTurnAsync(sessionId);

        var persisted = await catalog.GetAsync(sessionId);
        Assert.NotNull(persisted);
        var usage = await catalog.GetUsageAsync(sessionId);
        Assert.Equal(1, persisted.MessageCount);
        Assert.Equal("fresh selector preview", persisted.Preview);
        Assert.Equal(21, usage.InputTokens);
        Assert.Equal(8, usage.OutputTokens);
    }

    /// <summary>A failed resume restores the source model before checkpointing the source active again.</summary>
    [Fact]
    public static async Task Resume_failure_rolls_back_model_selection_and_source_checkpoint()
    {
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        var faultingStore = new FaultingUsageLifecycleStore(catalog);
        ModelProfileId sourceProfileId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ModelProfileId targetProfileId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var configurationPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan56-{Guid.NewGuid():N}", "config.json");
        var preferences = new SessionModelPreferences(sourceProfileId, ReasoningLevel.Low);
        var activeModels = new ActiveModelSelectionService(
            CreateModelCatalog(sourceProfileId, targetProfileId),
            preferences,
            configurationPath);
        await using var harness = LifecycleHarness.Create(
            "repo-a",
            faultingStore,
            fixture.Conversations,
            activeModels);
        var source = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand());
        var targetSessionId = SessionId.New();
        await catalog.CreateAsync(
            source.ActiveSession with
            {
                SessionId = targetSessionId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = SessionLifecycleState.Idle,
                ModelSelection = new SessionModelSelectionRecord
                {
                    ProviderId = "provider-b",
                    ProfileId = targetProfileId,
                    ReasoningLevel = ReasoningLevel.Medium.ToString(),
                    Generation = 1,
                },
            },
            EmptyUsage());
        faultingStore.FailUsageFor = targetSessionId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Lifecycle.HandleAsync(
            new ResumeSessionCommand(targetSessionId)));

        Assert.Equal(sourceProfileId, activeModels.Current.Profile.Id);
        var persistedSource = await catalog.GetAsync(source.ActiveSession.SessionId);
        Assert.NotNull(persistedSource);
        Assert.Equal(sourceProfileId, persistedSource.ModelSelection?.ProfileId);
        Assert.Equal(SessionLifecycleState.Active, persistedSource.State);
    }

    /// <summary>Duplicate destination creation fails without making a partial clone selectable.</summary>
    [Fact]
    public static async Task Clone_failure_rolls_back_copied_conversation()
    {
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var source = CreateEntry(SessionId.New(), "repo-a", now);
        var destination = CreateEntry(SessionId.New(), "repo-a", now) with
        {
            CloneSourceSessionId = source.SessionId,
        };
        await catalog.CreateAsync(source, EmptyUsage());
        await catalog.CreateAsync(destination, EmptyUsage());

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.CloneAsync(
            source.SessionId,
            destination,
            EmptyUsage()));
        var destinationState = await fixture.Conversations.GetSnapshotAsync(destination.SessionId);
        Assert.Empty(destinationState.Messages);
    }

    private static SessionCatalogEntry CreateEntry(SessionId sessionId, string repository, DateTimeOffset timestamp)
    {
        return new SessionCatalogEntry
        {
            SessionId = sessionId,
            RepositoryIdentity = repository,
            RepositoryDisplayName = repository,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            State = SessionLifecycleState.Idle,
            ConversationMode = ConversationContextMode.ConversationAware,
        };
    }

    private static SessionDurableUsage EmptyUsage()
    {
        return new SessionDurableUsage(0, 0, false, false, false);
    }

    private sealed class SessionLifecycleFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private SessionLifecycleFixture(
            string directory,
            string connectionString,
            SqliteConversationStore conversations)
        {
            _directory = directory;
            ConnectionString = connectionString;
            Conversations = conversations;
        }

        internal string ConnectionString { get; }

        internal SqliteConversationStore Conversations { get; }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }

        internal static async Task<SessionLifecycleFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"threadsmith-plan56-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var connectionString = $"Data Source={Path.Combine(directory, "sessions.db")};Pooling=False";
            var eventStore = new SqliteEventStore(connectionString);
            await eventStore.InitializeAsync();
            _ = await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var sanitizer = new SecretOutputSanitizer();
            var artifacts = new ArtifactStore(
                connectionString,
                Path.Combine(directory, "artifacts"),
                sanitizer,
                TimeProvider.System);
            await artifacts.InitializeAsync();
            var conversations = new SqliteConversationStore(connectionString, artifacts, sanitizer);
            return new SessionLifecycleFixture(directory, connectionString, conversations);
        }
    }

    private sealed class LifecycleHarness : IAsyncDisposable
    {
        private readonly DomainEventStream _events;

        private LifecycleHarness(
            DomainEventStream events,
            EvidenceStore evidence,
            SessionUsageProjection usage,
            SessionLifecycleApplication lifecycle)
        {
            _events = events;
            Evidence = evidence;
            Usage = usage;
            Lifecycle = lifecycle;
        }

        internal EvidenceStore Evidence { get; }

        internal DomainEventStream Events => _events;

        internal SessionLifecycleApplication Lifecycle { get; }

        internal SessionUsageProjection Usage { get; }

        public ValueTask DisposeAsync()
        {
            return _events.DisposeAsync();
        }

        internal static LifecycleHarness Create(
            string repositoryPath,
            ISessionLifecycleStore store,
            IConversationStore conversations,
            ActiveModelSelectionService? activeModels = null)
        {
            var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var projections = new InMemoryProjectionStore();
            _ = events.Subscribe(projections.ApplyAsync);
            var usage = new SessionUsageProjection();
            var sessions = new SessionApplication(
                events,
                new UnusedModelProvider(),
                UnboundedBudget.Instance,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                sessionUsage: usage,
                conversationStore: conversations);
            var lifecycle = new SessionLifecycleApplication(
                repositoryPath,
                store,
                new UnusedSessionRestorer(),
                sessions,
                projections,
                events,
                evidence,
                new UnusedContextAssembler(),
                usage,
                activeModels);
            return new LifecycleHarness(events, evidence, usage, lifecycle);
        }
    }

    private static EffectiveModelProviderCatalog CreateModelCatalog(
        ModelProfileId sourceProfileId,
        ModelProfileId targetProfileId)
    {
        return new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration
            {
                DefaultProviderId = "provider-a",
                DefaultModelId = sourceProfileId,
                Providers =
                [
                    CreateProvider("provider-a", sourceProfileId, ReasoningLevel.Low),
                    CreateProvider("provider-b", targetProfileId, ReasoningLevel.Medium),
                ],
            },
            new ModelProviderRegistry([new TestProviderRegistration()]));
    }

    private static TestProviderConfiguration CreateProvider(
        string providerId,
        ModelProfileId profileId,
        ReasoningLevel reasoningLevel)
    {
        return new TestProviderConfiguration
        {
            Id = providerId,
            Name = providerId,
            Models =
            [
                new TestModelConfiguration
                {
                    Id = profileId,
                    Name = profileId.Value.ToString("D"),
                    ContextWindow = 32_000,
                    MaximumOutputTokens = 4_000,
                    Capabilities = new ModelCapabilitySet { Streaming = true },
                    SupportedReasoningLevels = [ReasoningLevel.None, reasoningLevel],
                    DefaultReasoningLevel = reasoningLevel,
                },
            ],
        };
    }

    private sealed class FaultingUsageLifecycleStore : ISessionLifecycleStore
    {
        private readonly ISessionLifecycleStore _inner;

        internal FaultingUsageLifecycleStore(ISessionLifecycleStore inner)
        {
            _inner = inner;
        }

        internal SessionId? FailUsageFor { get; set; }

        public Task<SessionCatalogEntry> CheckpointAsync(
            SessionCatalogEntry entry,
            SessionDurableUsage usage,
            CancellationToken cancellationToken = default)
        {
            return _inner.CheckpointAsync(entry, usage, cancellationToken);
        }

        public Task<SessionCatalogEntry> CloneAsync(
            SessionId sourceSessionId,
            SessionCatalogEntry destination,
            SessionDurableUsage usage,
            CancellationToken cancellationToken = default)
        {
            return _inner.CloneAsync(sourceSessionId, destination, usage, cancellationToken);
        }

        public Task<SessionCatalogEntry> CreateAsync(
            SessionCatalogEntry entry,
            SessionDurableUsage usage,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreateAsync(entry, usage, cancellationToken);
        }

        public Task<SessionCatalogEntry?> GetAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetAsync(sessionId, cancellationToken);
        }

        public Task<SessionDurableUsage> GetUsageAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return FailUsageFor == sessionId
                ? Task.FromException<SessionDurableUsage>(new InvalidOperationException("Injected usage read failure."))
                : _inner.GetUsageAsync(sessionId, cancellationToken);
        }

        public Task<IReadOnlyList<SessionCatalogEntry>> ListAsync(
            string repositoryIdentity,
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            return _inner.ListAsync(repositoryIdentity, maximumCount, cancellationToken);
        }
    }

    private sealed record TestModelConfiguration : ModelConfiguration;

    private sealed record TestProviderConfiguration : ModelProviderConfiguration;

    private sealed class TestProviderRegistration : IModelProviderRegistration
    {
        public Type ModelConfigurationType => typeof(TestModelConfiguration);

        public Type ProviderConfigurationType => typeof(TestProviderConfiguration);

        public string TypeDiscriminator => "test";

        public IModelProvider CreateProvider(ModelProviderActivationContext context)
        {
            return new UnusedModelProvider();
        }

        public IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider)
        {
            return provider.Models.Select(model => new ModelProfile
            {
                Id = model.Id,
                Name = model.Name,
                Provider = provider.Name,
                Endpoint = new Uri("https://models.example.invalid"),
                ModelId = model.Id.Value.ToString("D"),
                ContextWindow = model.ContextWindow,
                MaximumOutputTokens = model.MaximumOutputTokens,
                Capabilities = model.Capabilities,
                SupportedReasoningLevels = model.SupportedReasoningLevels,
                DefaultReasoningLevel = model.DefaultReasoningLevel,
            }).ToArray();
        }

        public void Validate(ModelProviderConfiguration provider)
        {
        }
    }

    private sealed class UnusedContextAssembler : IContextAssembler
    {
        public Task<ContextAssemblyResult> AssembleAsync(
            ContextAssemblyRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ContextInspectionProjection? GetInspection(RunId runId)
        {
            return null;
        }

        public void InvalidateInspections()
        {
        }
    }

    private sealed class UnusedModelProvider : IModelProvider
    {
        public IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class UnusedSessionRestorer : ISessionRestorer
    {
        public Task<SessionRestorationResult> RestoreAsync(
            SessionId sessionId,
            IProjectionStore projectionStore,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SessionRestorationResult
            {
                SessionId = sessionId,
                ReplayedEvents = 0,
                MigratedEvents = 0,
                LegacyEvents = 0,
                IsLegacy = false,
                Succeeded = true,
            });
        }
    }
}
