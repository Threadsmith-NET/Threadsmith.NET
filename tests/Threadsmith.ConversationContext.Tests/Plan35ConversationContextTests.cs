namespace Threadsmith.ConversationContext.Tests;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Cli;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Plan 35 conversation modes, assembly, pressure, inspection, and command tests.</summary>
public static class Plan35ConversationContextTests
{
    /// <summary>TUI and headless controls dispatch identical host-owned mode commands.</summary>
    [Fact]
    public static async Task Tui_and_headless_mode_controls_have_command_parity()
    {
        var dispatcher = new RecordingDispatcher();
        var projections = new EmptyProjectionStore();
        var presenter = new InteractionPresenter(dispatcher, projections);
        var headless = new HeadlessShell(dispatcher, projections, TextWriter.Null);
        var sessionId = SessionId.New();

        var tuiResult = await presenter.SetConversationModeAsync(
            sessionId,
            ConversationContextMode.Stateless);
        var headlessResult = await headless.SetConversationModeAsync(
            sessionId,
            ConversationContextMode.Stateless);

        Assert.True(tuiResult);
        Assert.True(headlessResult);
        Assert.Equal(2, dispatcher.Commands.Count);
        Assert.All(dispatcher.Commands, command =>
        {
            var mode = Assert.IsType<SetConversationContextModeCommand>(command);
            Assert.Equal(sessionId, mode.SessionId);
            Assert.Equal(ConversationContextMode.Stateless, mode.Mode);
        });
    }

    /// <summary>The terminal controller retains the latest completed run for later inspection.</summary>
    [Fact]
    public static async Task Tui_controller_retains_latest_completed_run_for_context_inspection()
    {
        var dispatcher = new RecordingDispatcher();
        var controller = new InteractionController(new InteractionPresenter(dispatcher, new EmptyProjectionStore()));
        await controller.OpenAsync("conversation-test");

        var runId = await controller.SubmitAsync("hello");
        await controller.WaitForActiveRunAsync();

        Assert.Null(controller.ActiveRunId);
        Assert.Equal(runId, controller.LatestRunId);
    }

    /// <summary>The default history budget retains 32,768 tokens without changing count or age limits.</summary>
    [Fact]
    public static void Conversation_policy_defaults_to_32768_recent_turn_tokens()
    {
        // Arrange
        var policy = new ConversationContextPolicy();

        // Act
        policy.Validate();

        // Assert
        Assert.Equal(32_768, policy.RecentTurnTokens);
        Assert.Equal(12, policy.RecentTurnCount);
        Assert.Equal(TimeSpan.FromDays(7), policy.RecentTurnMaximumAge);
    }

    /// <summary>Conversation policy rejects unsafe or nonsensical bounds.</summary>
    [Fact]
    public static void Conversation_policy_validation_is_bounded()
    {
        var policy = new ConversationContextPolicy { CompactionPressurePercent = 101 };

        Assert.Throws<ArgumentOutOfRangeException>(policy.Validate);
    }

    /// <summary>Legacy pairing, model attachments, and inspection survive the lightweight archive read.</summary>
    [Fact]
    public static async Task LongSessionSelectionPreservesTurnsMetadataAndInspection()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var session = SessionId.New();
        var old = await ArchiveAsync(fixture, session, ConversationRole.User, "expired", occurredAt: DateTimeOffset.UnixEpoch);
        var image = new ImageArtifact
        {
            Id = "archived-image", ContentHash = new string('a', 64), MediaType = "image/png",
            Width = 1, Height = 1, ByteLength = 72, Source = "clipboard",
        };
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(token);
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            WITH RECURSIVE older(n) AS (VALUES(2) UNION ALL SELECT n+1 FROM older WHERE n<96)
            INSERT INTO conversation_messages(message_id, session_id, run_id, sequence, role, content_hash,
                estimated_tokens, sensitivity, occurred_at, schema_version)
            SELECT printf('00000000-0000-0000-0000-%012d', n), session_id, run_id, sequence+n-1, role,
                content_hash, estimated_tokens, sensitivity, occurred_at, schema_version
            FROM older CROSS JOIN conversation_messages WHERE message_id=$old;
            """;
        seed.Parameters.AddWithValue("$old", old.Id.Value.ToString("D"));
        await seed.ExecuteNonQueryAsync(token);
        var user = await ArchiveAsync(fixture, session, ConversationRole.User, "recent user");
        var assistant = await ArchiveAsync(fixture, session, ConversationRole.Assistant, "recent assistant");
        var current = await ArchiveAsync(fixture, session, ConversationRole.User, "current user");
        var receipt = new ConversationReceiptProvenance(1, session, assistant.RunId, assistant.Id);
        await using var metadata = connection.CreateCommand();
        metadata.CommandText = """
            UPDATE conversation_messages SET images_json=$images WHERE message_id IN ($old,$user,$current);
            UPDATE conversation_messages SET schema_version=1 WHERE message_id IN ($user,$assistant);
            UPDATE conversation_messages SET receipt_json=$receipt WHERE message_id=$assistant;
            """;
        metadata.Parameters.AddWithValue("$old", old.Id.Value.ToString("D"));
        metadata.Parameters.AddWithValue("$user", user.Id.Value.ToString("D"));
        metadata.Parameters.AddWithValue("$assistant", assistant.Id.Value.ToString("D"));
        metadata.Parameters.AddWithValue("$current", current.Id.Value.ToString("D"));
        metadata.Parameters.AddWithValue("$images", JsonSerializer.Serialize(new[] { image }));
        metadata.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(receipt));
        await metadata.ExecuteNonQueryAsync(token);

        var selection = await fixture.Store.GetSelectionSnapshotAsync(session, DateTimeOffset.UnixEpoch.AddDays(1), null, current.Id, token);
        Assert.Equal(99, selection.Messages.Count);
        Assert.Empty(selection.Messages.Single(m => m.Id == user.Id).Images);
        Assert.Null(selection.Messages.Single(m => m.Id == assistant.Id).Receipt);
        var request = CreateRequest(fixture, session, current, "current user");
        var assembler = CreateAssembler(fixture, events);
        var result = await assembler.AssembleAsync(request, token);
        var items = result.Inspection.ConversationItems;
        Assert.Equal(new[] { user.Id, assistant.Id }, items.Where(i => i.Included).SelectMany(i => i.SourceMessageIds));
        Assert.Equal(96, items.Count(i => !i.Included));
        var expired = items.Single(i => i.Id == old.Id.Value.ToString("D"));
        Assert.Equal("metadata only; pixels omitted", expired.ImageDisposition);
        Assert.Equal(image, Assert.Single(expired.Images));
        Assert.Equal(image, Assert.Single(items.Single(i => i.Id == user.Id.Value.ToString("D")).Images));
        Assert.Contains(result.Messages!, m => m.Receipt?.OriginMessageId == assistant.Id);
        Assert.Equal(2, result.Messages!.SelectMany(m => m.Content).Count(part => part.Image == image));

        await fixture.Store.SetModeAsync(session, ConversationContextMode.Stateless, token);
        Assert.Equal(ConversationContextMode.Stateless, await fixture.Store.GetModeAsync(session, token));
        var statelessSelection = await fixture.Store.GetSelectionSnapshotAsync(session, DateTimeOffset.UnixEpoch.AddDays(1), null, current.Id, token);
        Assert.Empty(statelessSelection.Messages.Single(m => m.Id == current.Id).Images);
        Assert.Equal(image, Assert.Single(statelessSelection.Messages.Single(m => m.Id == user.Id).Images));
        var stateless = await assembler.AssembleAsync(request, token);
        Assert.Equal(image, Assert.Single(stateless.Messages!.SelectMany(m => m.Content), part => part.Image is not null).Image);
        var omitted = stateless.Inspection.ConversationItems.Single(i => i.Id == user.Id.Value.ToString("D"));
        Assert.False(omitted.Included);
        Assert.Equal(image, Assert.Single(omitted.Images));
    }

    private static async Task AddMemoryAsync(
        ConversationFixture fixture,
        SessionId sessionId,
        ConversationMessage source,
        string content)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new ConversationMemoryItem
        {
            Id = ConversationMemoryId.New(),
            SessionId = sessionId,
            Kind = ConversationMemoryKind.Decision,
            Content = content,
            SourceMessageIds = [source.Id],
            SourceRunIds = [source.RunId],
            CreatedAt = now,
            UpdatedAt = now,
        };
        var snapshot = new ConversationSummarySnapshot
        {
            SessionId = sessionId,
            Version = 1,
            ThroughMessageSequence = source.Sequence,
            MemoryIdsByKind = new Dictionary<ConversationMemoryKind, IReadOnlyList<ConversationMemoryId>>
            {
                [ConversationMemoryKind.Decision] = [item.Id],
            },
            CreatedAt = now,
        };
        await fixture.SeedHistoricalMemoryAsync(sessionId, [item], snapshot);
    }

    private static async Task<ConversationMessage> ArchiveAsync(
        ConversationFixture fixture,
        SessionId sessionId,
        ConversationRole role,
        string content,
        ConversationSensitivity sensitivity = ConversationSensitivity.None,
        RunId? runId = null,
        DateTimeOffset? occurredAt = null)
    {
        return await fixture.Store.ArchiveMessageAsync(new ConversationMessage
        {
            Id = ConversationMessageId.New(),
            SessionId = sessionId,
            RunId = runId ?? RunId.New(),
            Sequence = 0,
            Role = role,
            Content = content,
            ContentHash = "pending",
            EstimatedTokens = 1,
            Sensitivity = sensitivity,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        });
    }

    private static ContextAssembler CreateAssembler(
        ConversationFixture fixture,
        IDomainEventStream events,
        ConversationContextPolicy? policy = null,
        int maximumTokens = 32_000,
        IModelResolver? modelResolver = null,
        IConversationStore? conversationStore = null)
    {
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        return new ContextAssembler(
            evidence,
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            TestPromptLoader.Instance,
            new ContextAssemblerOptions
            {
                MaximumTokens = maximumTokens,
                Conversation = policy ?? new ConversationContextPolicy(),
            },
            modelResolver,
            conversationStore: conversationStore ?? fixture.Store);
    }

    private static ContextAssemblyRequest CreateRequest(
        ConversationFixture fixture,
        SessionId sessionId,
        ConversationMessage current,
        string intent)
    {
        return new ContextAssemblyRequest
        {
            SessionId = sessionId,
            RunId = current.RunId,
            Phase = RunPhase.EvidenceCollection,
            Task = new TaskSpecification(intent, []),
            RepositoryPath = fixture.DirectoryPath,
            CurrentMessageId = current.Id,
        };
    }

    private sealed class CountingArtifactStore : IArtifactStore
    {
        private readonly IArtifactStore _inner;

        public CountingArtifactStore(IArtifactStore inner)
        {
            _inner = inner;
        }

        public List<string> Reads { get; } = [];

        public Task<string?> ReadAsync(string contentHash, CancellationToken cancellationToken = default)
        {
            Reads.Add(contentHash);
            return _inner.ReadAsync(contentHash, cancellationToken);
        }

        public Task<ArtifactMetadata> StoreAsync(string content, string kind, SessionId? sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string contentHash, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ArtifactMetadata>> ListAsync(SessionId? sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingDispatcher : ICommandDispatcher
    {
        private readonly RunId _runId = RunId.New();
        private readonly SessionId _sessionId = SessionId.New();

        public List<object> Commands { get; } = [];

        public Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            object? response = command switch
            {
                CreateSessionCommand => _sessionId,
                SubmitRequestCommand => _runId,
                WaitForRunCommand => true,
                SetConversationContextModeCommand => true,
                _ => default(TResponse),
            };
            return Task.FromResult((TResponse?)response
                ?? throw new InvalidOperationException($"No response configured for {command.GetType().Name}."));
        }
    }

    private sealed class RecordingModelResolver : IModelResolver
    {
        private readonly int _contextWindow;
        private readonly int _maximumOutputTokens;
        private readonly int _requestOutputTokenReserve;

        public RecordingModelResolver(
            int contextWindow,
            int maximumOutputTokens = 0,
            int? requestOutputTokenReserve = null)
        {
            _contextWindow = contextWindow;
            _maximumOutputTokens = maximumOutputTokens;
            _requestOutputTokenReserve = requestOutputTokenReserve ?? maximumOutputTokens;
        }

        public ModelSelectionConstraints? Constraints { get; private set; }

        public int MaximumInputTokenBudget => _contextWindow - _requestOutputTokenReserve;

        public ModelResolution Resolve(
            WorkloadClass workloadClass,
            ModelCapabilitySet requiredCapabilities,
            ModelSelectionConstraints constraints,
            ModelProfileId? defaultModelProfileId = null)
        {
            Constraints = constraints;
            return new ModelResolution(
                new ModelProfileId(Guid.NewGuid()),
                _contextWindow,
                _maximumOutputTokens,
                [],
                [],
                ["test model"],
                _requestOutputTokenReserve);
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
}
