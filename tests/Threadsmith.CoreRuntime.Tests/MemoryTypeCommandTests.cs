namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Actual interactive memory parsing and advisory delivery over recording host boundaries.</summary>
public static class MemoryTypeCommandTests
{
    /// <summary>Both selectors, omission, update preservation and invalid selector handling traverse the real coordinator.</summary>
    [Fact]
    public static async Task Manual_type_selectors_reach_the_host_commands()
    {
        await using var events = new DomainEventStream();
        var dispatcher = new MemoryDispatcher(0);
        var target = dispatcher.Seed(RepositoryMemoryType.Situational);
        var surface = new RecordingSurface([
            "/memory remember --type standingPreference Address the user as sir",
            "/memory remember --type situational Build with dotnet",
            "/memory remember Plain compatibility note",
            $"/memory update {target.Id.Value:D} --type standingPreference Prefer direct answers",
            $"/memory update {target.Id.Value:D} Prefer concise answers",
            "/memory remember --type invalid Must not save",
            "/memory list",
            "/quit"]);
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, new EmptyProjectionStore()), events, surface);

        await coordinator.RunAsync(
            "memory-command-fixture",
            requestedTrust: RepositoryTrustLevel.UntrustedInspection,
            repositoryConfigurationDirectoryExistedAtStartup: true).WaitAsync(TimeSpan.FromSeconds(3));

        var adds = dispatcher.Commands.OfType<RememberRepositoryMemoryCommand>().ToArray();
        Assert.True(adds.Length == 3, surface.Output);
        Assert.Equal(RepositoryMemoryType.StandingPreference, adds[0].MemoryType);
        Assert.Equal("Address the user as sir", adds[0].Text);
        Assert.Equal(RepositoryMemoryType.Situational, adds[1].MemoryType);
        Assert.Null(adds[2].MemoryType);
        var updates = dispatcher.Commands.OfType<UpdateRepositoryMemoryCommand>().ToArray();
        Assert.Equal(2, updates.Length);
        Assert.Equal(RepositoryMemoryType.StandingPreference, updates[0].MemoryType);
        Assert.Null(updates[1].MemoryType);
        Assert.Equal(RepositoryMemoryType.StandingPreference, dispatcher.Entries.Single(entry => entry.Id == target.Id).MemoryType);
        Assert.Contains("Usage: /memory remember", surface.Output, StringComparison.Ordinal);
        Assert.Contains("standing preference", surface.Output, StringComparison.Ordinal);
        Assert.Contains("situational", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Advice uses the configured strict threshold and is separated from ordinary success output.</summary>
    [Theory]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, 0, true)]
    public static async Task Added_preference_warns_only_above_the_configured_threshold(int initialCount, int threshold, bool expected)
    {
        await using var events = new DomainEventStream();
        var dispatcher = new MemoryDispatcher(initialCount);
        var surface = new RecordingSurface(["/memory remember --type standingPreference New preference", "/quit"]);
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(dispatcher, new EmptyProjectionStore()),
            events,
            surface,
            standingPreferenceWarningThresholdProvider: _ => threshold);

        await coordinator.RunAsync(
            "memory-command-fixture",
            requestedTrust: RepositoryTrustLevel.UntrustedInspection,
            repositoryConfigurationDirectoryExistedAtStartup: true).WaitAsync(TimeSpan.FromSeconds(3));

        var output = surface.Output.ReplaceLineEndings("\n");
        Assert.Equal(expected, output.Contains("You now have", StringComparison.Ordinal));
        if (expected)
        {
            Assert.Contains($"\n\nYou now have {initialCount + 1} preference memories. You may want to consider adding some of these to AGENTS.md for the repo.\n\n", output, StringComparison.Ordinal);
        }
    }

    /// <summary>Rebound options are read for each advice check and promotion also contributes a standing preference.</summary>
    [Fact]
    public static async Task Promotion_and_later_add_use_live_thresholds()
    {
        await using var events = new DomainEventStream();
        var dispatcher = new MemoryDispatcher(3);
        var target = dispatcher.Seed(RepositoryMemoryType.Situational);
        var thresholdsRead = 0;
        var surface = new RecordingSurface([
            $"/memory update {target.Id.Value:D} --type standingPreference Promote preference",
            "/memory remember --type standingPreference Later preference",
            "/quit"]);
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(dispatcher, new EmptyProjectionStore()),
            events,
            surface,
            standingPreferenceWarningThresholdProvider: _ => ++thresholdsRead == 1 ? 3 : 10);

        await coordinator.RunAsync(
            "memory-command-fixture",
            requestedTrust: RepositoryTrustLevel.UntrustedInspection,
            repositoryConfigurationDirectoryExistedAtStartup: true).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, thresholdsRead);
        Assert.Contains("You now have 4 preference memories.", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("You now have 5 preference memories.", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Startup advice is delivered through the initialized surface before the first composer read.</summary>
    [Fact]
    public static async Task Startup_preference_advice_is_visible_before_the_first_prompt()
    {
        const string warning = "You now have 4 preference memories. You may want to consider adding some of these to AGENTS.md for the repo.";
        await using var events = new DomainEventStream();
        var surface = new RecordingSurface(["/quit"]);
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(new MemoryDispatcher(4), new EmptyProjectionStore()),
            events,
            surface,
            displayWarnings: [warning]);

        await coordinator.RunAsync(
            "memory-command-fixture",
            requestedTrust: RepositoryTrustLevel.UntrustedInspection,
            repositoryConfigurationDirectoryExistedAtStartup: true).WaitAsync(TimeSpan.FromSeconds(3));

        var visibleBeforeInput = Assert.IsType<string>(surface.OutputAtFirstComposer).ReplaceLineEndings("\n");
        Assert.Contains($"\n\nWarning: {warning}\n\n", visibleBeforeInput, StringComparison.Ordinal);
    }

    private sealed class MemoryDispatcher : ICommandDispatcher
    {
        private readonly SessionId _sessionId = SessionId.New();

        public MemoryDispatcher(int initialCount)
        {
            for (var index = 0; index < initialCount; index++)
            {
                Seed(RepositoryMemoryType.StandingPreference);
            }
        }

        internal List<object> Commands { get; } = [];

        internal List<RepositoryMemoryEntry> Entries { get; } = [];

        public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            object response = command switch
            {
                CreateSessionCommand => _sessionId,
                GetRepositoryInitializationStatusCommand initialize => new RepositoryInitializationStatus(initialize.RepositoryPath, true, false),
                GetRepositoryTrustCommand trust => new RepositoryTrustState(trust.RepositoryPath, RepositoryTrustLevel.UntrustedInspection, DateTimeOffset.UnixEpoch),
                OpenRepositoryCommand opened => new RepositoryOpenResult(
                    WorkspaceId.New(),
                    opened.RepositoryPath,
                    new RepositoryTrustState(opened.RepositoryPath, opened.RequestedTrust, DateTimeOffset.UnixEpoch),
                    new RepositoryConfigurationSnapshot(null, [], []),
                    null,
                    []),
                RememberRepositoryMemoryCommand remembered => Add(remembered),
                UpdateRepositoryMemoryCommand updated => Update(updated),
                ListRepositoryMemoryCommand listed => new RepositoryMemoryReadSnapshot(listed.RepositoryIdentity, 1, Entries.ToArray(), [], []),
                _ => throw new InvalidOperationException($"Unexpected command: {command.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
        }

        internal RepositoryMemoryEntry Seed(RepositoryMemoryType type)
        {
            var entry = new RepositoryMemoryEntry
            {
                Id = RepositoryMemoryId.New(),
                RepositoryIdentity = "fixture",
                Text = $"Existing {Entries.Count}",
                ContentHash = "fixture",
                MemoryType = type,
                Origin = RepositoryMemoryOrigin.Manual,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            };
            Entries.Add(entry);
            return entry;
        }

        private RepositoryMemoryEntry Add(RememberRepositoryMemoryCommand command)
        {
            var entry = Seed(command.MemoryType ?? RepositoryMemoryType.Situational) with { Text = command.Text };
            Entries[^1] = entry;
            return entry;
        }

        private RepositoryMemoryEntry Update(UpdateRepositoryMemoryCommand command)
        {
            var index = Entries.FindIndex(entry => entry.Id == command.MemoryId);
            Entries[index] = Entries[index] with { Text = command.ReplacementText, MemoryType = command.MemoryType ?? Entries[index].MemoryType };
            return Entries[index];
        }
    }

    private sealed class EmptyProjectionStore : IProjectionStore
    {
        public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(domainEvent);
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

    private sealed class RecordingSurface : IInteractionSurface
    {
        private readonly Queue<InteractionInput> _inputs;

        public RecordingSurface(IEnumerable<string> inputs)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            _inputs = new Queue<InteractionInput>(inputs.Select(text => new InteractionInput(
                true,
                text,
                CancellationToken.None)));
        }

        public InteractionSurfaceCapabilities Capabilities { get; } = new();

        public List<PresentationBatch> Batches { get; } = [];

        public List<ComposerRequest> ComposerRequests { get; } = [];

        public string? OutputAtFirstComposer { get; private set; }

        public string Output => string.Concat(Batches
            .SelectMany(batch => batch.Items)
            .OfType<PresentationTextItem>()
            .SelectMany(item => item.Segments)
            .Select(segment => segment.Text));

        public Task<InteractionInput> ReadComposerAsync(
            ComposerRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            OutputAtFirstComposer ??= Output;
            ComposerRequests.Add(request);
            return Task.FromResult(_inputs.Dequeue());
        }

        public Task<InteractionSelectionResult> SelectAsync(
            InteractionSelectionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The scripted interaction does not request a selection.");
        }

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(batch);
            cancellationToken.ThrowIfCancellationRequested();
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task PresentSessionStatusAsync(
            SessionStatusSnapshot status,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(status);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PresentActivityUntilAsync(
            InteractionActivity activity,
            Task operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(activity);
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            return operation.WaitAsync(cancellationToken);
        }
    }
}
