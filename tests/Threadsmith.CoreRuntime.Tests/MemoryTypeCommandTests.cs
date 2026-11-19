namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Sessions;
using Xunit;

/// <summary>Actual interactive memory parsing and advisory delivery over recording host boundaries.</summary>
public static class MemoryTypeCommandTests
{
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
            repositoryConfigurationDirectoryExistedAtStartup: true).WaitAsync(TestContext.Current.CancellationToken);

        var visibleBeforeInput = Assert.IsType<string>(surface.OutputAtFirstComposer).ReplaceLineEndings("\n");
        Assert.Contains($"\n\nWarning: {warning}\n\n", visibleBeforeInput, StringComparison.Ordinal);
    }

    /// <summary>Manual collisions show no-write guidance and retain structured retry/update metadata.</summary>
    [Fact]
    public static async Task Reconciliation_output_and_manual_resolution_flags_are_actionable()
    {
        await using var events = new DomainEventStream();
        var dispatcher = new MemoryDispatcher(0) { Reconcile = true };
        var existing = dispatcher.Seed(RepositoryMemoryType.Situational);
        var surface = new RecordingSurface([
            "/memory remember --kind constraint --concepts cancellation New convention",
            $"/memory remember --confirm-distinct-from {existing.Id.Value:D}@1 Distinct convention",
            $"/memory update {existing.Id.Value:D} --expected-revision 1 --concepts [] Replacement",
            "/quit"]);
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, new EmptyProjectionStore()), events, surface);
        await coordinator.RunAsync(
            "memory-command-fixture",
            requestedTrust: RepositoryTrustLevel.UntrustedInspection,
            repositoryConfigurationDirectoryExistedAtStartup: true,
            cancellationToken: TestContext.Current.CancellationToken);
        var adds = dispatcher.Commands.OfType<RememberRepositoryMemoryCommand>().ToArray();
        Assert.Equal(2, adds.Length);
        Assert.Equal(ManagedRepositoryMemoryKind.Constraint, adds[0].Kind);
        Assert.Equal(["cancellation"], adds[0].Concepts);
        Assert.Equal(new RepositoryMemoryInclusion(existing.Id, 1), Assert.Single(adds[1].ConfirmDistinctFrom));
        var update = Assert.Single(dispatcher.Commands.OfType<UpdateRepositoryMemoryCommand>());
        Assert.Equal(1, update.ExpectedRevision);
        Assert.Empty(update.Concepts!);
        Assert.Contains("Memory was not added", surface.Output, StringComparison.Ordinal);
        Assert.Contains(existing.Id.Value.ToString("D") + "@1", surface.Output, StringComparison.Ordinal);
        Assert.Contains("--confirm-distinct-from", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Remembered repository memory", surface.Output, StringComparison.Ordinal);
        Assert.Single(dispatcher.Entries);
    }

    /// <summary>Browser edits preserve metadata and use revision-checked host commands.</summary>
    [Theory]
    [InlineData(RepositoryMemoryType.StandingPreference, RepositoryMemoryOwnership.Ordinary)]
    [InlineData(RepositoryMemoryType.Situational, RepositoryMemoryOwnership.Ordinary)]
    [InlineData(RepositoryMemoryType.Situational, RepositoryMemoryOwnership.Intelligence)]
    public static async Task Browser_mutations_use_host_commands_with_revision_and_refresh(RepositoryMemoryType type, RepositoryMemoryOwnership ownership)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var dispatcher = new MemoryDispatcher(0);
        dispatcher.Seed(type);
        dispatcher.Entries[0] = dispatcher.Entries[0] with { Ownership = ownership, Revision = 7 };
        var surface = new MemoryBrowserSurface(["/memory", "/quit"]) { Mutate = true };
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, new EmptyProjectionStore()), events, surface);
        await coordinator.RunAsync(
            "memory-command-fixture",
            requestedTrust: RepositoryTrustLevel.UntrustedInspection,
            repositoryConfigurationDirectoryExistedAtStartup: true,
            cancellationToken: ct);
        var update = Assert.Single(dispatcher.Commands.OfType<UpdateRepositoryMemoryCommand>());
        Assert.Equal(7, update.ExpectedRevision);
        Assert.Null(update.MemoryType);
        Assert.Equal("Edited memory", update.ReplacementText);
        var afterEdit = surface.Snapshots[1].Entries.Single();
        Assert.Equal(type, afterEdit.MemoryType);
        Assert.Equal(ownership, afterEdit.Ownership);
        Assert.Single(dispatcher.Commands.OfType<ForgetRepositoryMemoryCommand>());
        Assert.Empty(surface.Snapshots[2].Entries);
    }

    /// <summary>Retained creation routes every tab through the manual host command and refreshes the projection.</summary>
    [Fact]
    public static async Task Browser_adds_each_partition()
    {
        await using var events = new DomainEventStream();
        var dispatcher = new MemoryDispatcher(0);
        var surface = new MemoryBrowserSurface(["/memory", "/quit"]) { AddMemories = true };
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, new EmptyProjectionStore()), events, surface);
        await coordinator.RunAsync(
            "memory-command-fixture",
            requestedTrust: RepositoryTrustLevel.UntrustedInspection,
            repositoryConfigurationDirectoryExistedAtStartup: true,
            cancellationToken: TestContext.Current.CancellationToken);
        var adds = dispatcher.Commands.OfType<RememberRepositoryMemoryCommand>().ToArray();
        Assert.Equal(3, adds.Length);
        Assert.Equal(RepositoryMemoryType.StandingPreference, adds[0].MemoryType);
        Assert.Equal(RepositoryMemoryOwnership.Ordinary, adds[0].Ownership);
        Assert.Equal(RepositoryMemoryType.Situational, adds[1].MemoryType);
        Assert.Equal(RepositoryMemoryOwnership.Ordinary, adds[1].Ownership);
        Assert.Equal(RepositoryMemoryType.Situational, adds[2].MemoryType);
        Assert.Equal(RepositoryMemoryOwnership.Intelligence, adds[2].Ownership);
        Assert.Equal(3, surface.Snapshots[^1].Entries.Count);
    }

    private sealed class StatisticsBrowserSurface : RecordingSurface, IToolStatisticsSurface
    {
        internal StatisticsBrowserSurface(IEnumerable<string> inputs)
            : base(inputs)
        {
        }

        public async Task ShowToolStatisticsAsync(Func<CancellationToken, Task<ToolStatisticsSnapshot>> load, Func<CancellationToken, Task<ToolStatisticsSnapshot>> reset, CancellationToken cancellationToken = default)
        {
            Assert.Single((await load(cancellationToken)).Rows);
            Assert.Empty((await reset(cancellationToken)).Rows);
        }
    }

    private sealed class StatisticsStore : IToolStatisticsStore, IRepositoryStorageBindingSource
    {
        private readonly RepositoryStorageBinding _binding;
        private int _reads;
        private bool _committed;

        internal StatisticsStore(string path)
        {
            _binding = new(RepositoryIdentity.Create(path), "Data Source=unused.db");
        }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Commit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Reads => Volatile.Read(ref _reads);

        public RepositoryStorageBinding CaptureBinding(string? repositoryIdentity = null)
        {
            Assert.Equal(_binding.RepositoryIdentity, repositoryIdentity);
            return _binding;
        }

        public Task<ToolStatisticsSnapshot> GetSnapshotAsync(RepositoryStorageBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_binding, binding);
            Interlocked.Increment(ref _reads);
            return Task.FromResult(new ToolStatisticsSnapshot(_committed ? [] : [new("removed-tool", 3, 2, 1, DateTimeOffset.UnixEpoch, 400, 2, null)]));
        }

        public async Task ResetAsync(RepositoryStorageBinding binding, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_binding, binding);
            Entered.TrySetResult();
            await Commit.Task.WaitAsync(cancellationToken);
            _committed = true;
        }
    }

    private sealed class MemoryBrowserSurface : RecordingSurface, IRepositoryMemoryListSurface
    {
        public MemoryBrowserSurface(IEnumerable<string> inputs)
            : base(inputs)
        {
        }

        public List<RepositoryMemoryListSnapshot> Snapshots { get; } = [];

        public bool Mutate { get; init; }

        public bool AddMemories { get; init; }

        public async Task ManageRepositoryMemoriesAsync(
            RepositoryMemoryListSnapshot snapshot,
            Func<RepositoryMemoryListItem, string?, CancellationToken, Task<RepositoryMemoryListSnapshot>> change,
            Func<RepositoryMemoryType, RepositoryMemoryOwnership, string, CancellationToken, Task<RepositoryMemoryListSnapshot>> add,
            CancellationToken cancellationToken = default)
        {
            await ShowRepositoryMemoriesAsync(snapshot, cancellationToken);
            if (AddMemories)
            {
                Snapshots.Add(await add(RepositoryMemoryType.StandingPreference, RepositoryMemoryOwnership.Ordinary, "New preference", cancellationToken));
                Snapshots.Add(await add(RepositoryMemoryType.Situational, RepositoryMemoryOwnership.Ordinary, "New situation", cancellationToken));
                Snapshots.Add(await add(RepositoryMemoryType.Situational, RepositoryMemoryOwnership.Intelligence, "New intelligence", cancellationToken));
            }

            if (Mutate)
            {
                var edited = await change(snapshot.Entries.Single(), "Edited memory", cancellationToken);
                Snapshots.Add(edited);
                Snapshots.Add(await change(edited.Entries.Single(), null, cancellationToken));
            }
        }

        public Task ShowRepositoryMemoriesAsync(RepositoryMemoryListSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }
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

        public bool Reconcile { get; init; }

        internal ICommandDispatcher? StatisticsDispatcher { get; init; }

        internal List<object> Commands { get; } = [];

        internal List<RepositoryMemoryEntry> Entries { get; } = [];

        public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (command is ListToolStatisticsCommand or ResetToolStatisticsCommand)
            {
                return (StatisticsDispatcher ?? throw new InvalidOperationException("Unexpected statistics command.")).DispatchAsync(command, cancellationToken);
            }

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
                ForgetRepositoryMemoryCommand removed => Entries.RemoveAll(entry => entry.Id == removed.MemoryId) > 0,
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

        private RepositoryMemoryOperationResult Add(RememberRepositoryMemoryCommand command)
        {
            if (Reconcile)
            {
                return new RepositoryMemoryOperationResult("reconciliationRequired", null, null, Entries.ToArray(), []);
            }

            var entry = Seed(command.MemoryType ?? RepositoryMemoryType.Situational) with { Text = command.Text, Ownership = command.Ownership };
            Entries[^1] = entry;
            return new RepositoryMemoryOperationResult("added", entry.Id, entry, [], []);
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

    private class RecordingSurface : IInteractionSurface
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
