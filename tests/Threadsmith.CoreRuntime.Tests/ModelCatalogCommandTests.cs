namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Models;
using Xunit;

/// <summary>Provider-maintenance errors stay local to interactive commands.</summary>
public static class ModelCatalogCommandTests
{
    /// <summary>Invalid provider status and refresh commands report an error, then accept help and quit.</summary>
    [Theory]
    [InlineData("status", "typo")]
    [InlineData("refresh", "typo")]
    [InlineData("status", "openai-compatible")]
    [InlineData("refresh", "openai-compatible")]
    public static async Task Provider_error_reports_status_and_preserves_the_command_loop(string action, string providerId)
    {
        await using var events = new DomainEventStream();
        var maintenance = new RejectingMaintenance();
        var dispatcher = new CommandDispatcher([new CreateSessionHandler(), new ModelCatalogMaintenanceApplication(maintenance)]);
        var surface = new RecordingSurface([$"/models {action} {providerId}", "/help", "/quit"]);
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, new EmptyProjectionStore()), events, surface);

        await coordinator.RunAsync(modelStatus: "Test model").WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, surface.ComposerRequests.Count);
        Assert.Equal((action, providerId), Assert.Single(maintenance.Calls));
        Assert.Contains(providerId, surface.Output, StringComparison.Ordinal);
        Assert.Contains("/help", surface.Output, StringComparison.Ordinal);
        var error = Assert.Single(surface.Batches.SelectMany(batch => batch.Items).OfType<PresentationTextItem>(), item => item.Segments.Any(segment => segment.Role == PresentationTextRole.Error));
        var text = string.Concat(error.Segments.Select(segment => segment.Text)).TrimEnd();
        Assert.Contains(providerId == "typo" ? "not configured" : "does not support", text, StringComparison.Ordinal);
    }

    /// <summary>Command cancellation continues to propagate instead of becoming a recoverable status error.</summary>
    [Theory]
    [InlineData("status")]
    [InlineData("refresh")]
    public static async Task Cancellation_is_not_swallowed_by_the_command_error_boundary(string action)
    {
        await using var events = new DomainEventStream();
        var maintenance = new RejectingMaintenance { Cancel = true };
        var dispatcher = new CommandDispatcher([new CreateSessionHandler(), new ModelCatalogMaintenanceApplication(maintenance)]);
        var surface = new RecordingSurface([$"/models {action} typo", "/help", "/quit"]);
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, new EmptyProjectionStore()), events, surface);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(modelStatus: "Test model").WaitAsync(TestContext.Current.CancellationToken));

        Assert.Single(surface.ComposerRequests);
        Assert.Single(maintenance.Calls);
    }

    /// <summary>The model picker groups and orders entries without confusing duplicate names or cancellation.</summary>
    [Theory]
    [InlineData("alpha", false)]
    [InlineData("zulu", false)]
    [InlineData("alpha", true)]
    public static async Task ModelsGroupAndSortOptionsAndSelectByStableIdentity(string selectedProviderId, bool cancel)
    {
        SelectableModelEntry[] catalog =
        [
            CreateEntry("zulu", "Zulu provider", "Shared"),
            CreateEntry("alpha", "alpha provider", "zebra"),
            CreateEntry("zulu", "Zulu provider", "apple"),
            CreateEntry("alpha", "alpha provider", "Shared"),
            CreateEntry("alpha", "alpha provider", "apple"),
            CreateEntry("zulu", "Zulu provider", "Zulu"),
        ];
        var models = new RecordingModels(catalog, catalog[4]);
        var target = catalog.Single(entry => entry.ProviderId == selectedProviderId && entry.Profile.Name == "Shared");
        var surface = new RecordingSurface(["/models", "/help", "/quit"])
        {
            SelectionResult = cancel
                ? new InteractionSelectionResult(null, IsCancelled: true)
                : new InteractionSelectionResult(target.Profile.Id.Value.ToString("D")),
        };
        await using var events = new DomainEventStream();
        var dispatcher = new CommandDispatcher([new CreateSessionHandler(), models]);
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(dispatcher, new EmptyProjectionStore()),
            events,
            surface,
            activeModelSelectionAvailable: true);

        await coordinator.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(surface.SelectionRequests);
        Assert.Equal(
            ["alpha provider", "alpha provider", "alpha provider", "Zulu provider", "Zulu provider", "Zulu provider"],
            request.Options.Select(option => option.Group));
        Assert.Equal(
            ["apple [current]", "Shared", "zebra", "apple", "Shared", "Zulu"],
            request.Options.Select(option => option.Label));
        int[] expectedOrder = [4, 3, 1, 2, 0, 5];
        Assert.Equal(
            expectedOrder.Select(index => catalog[index].Profile.Id.Value.ToString("D")),
            request.Options.Select(option => option.Id));
        Assert.Equal(3, surface.ComposerRequests.Count);
        Assert.Contains("/help", surface.Output, StringComparison.Ordinal);
        if (cancel)
        {
            Assert.Empty(models.Selections);
            Assert.Equal(catalog[4].Profile.Id, models.Current.Profile.Id);
            Assert.DoesNotContain("Model set to ", surface.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(target.Profile.Id, Assert.Single(models.Selections));
            Assert.Equal(target.ProviderId, models.Current.ProviderId);
            Assert.Equal(target.Profile.Id, models.Current.Profile.Id);
            Assert.Contains("Model set to Shared", surface.Output, StringComparison.Ordinal);
        }
    }

    private static SelectableModelEntry CreateEntry(string providerId, string providerName, string name)
    {
        return new SelectableModelEntry
        {
            ProviderId = providerId,
            ProviderName = providerName,
            Profile = new ModelProfile
            {
                Id = ModelProfileId.New(),
                Name = name,
                Provider = providerId,
                ProviderName = providerName,
                Endpoint = new Uri("https://models.example/v1/chat/completions"),
                ModelId = name,
                ContextWindow = 128000,
                MaximumOutputTokens = 4096,
                Capabilities = new ModelCapabilitySet { Streaming = true },
                Cost = new ModelCostMetadata(),
                SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            },
        };
    }

    private sealed class RecordingModels :
        ICommandHandler<ListActiveModelsCommand, IReadOnlyList<SelectableModelEntry>>,
        ICommandHandler<GetActiveModelSelectionCommand, ActiveModelSelectionSnapshot>,
        ICommandHandler<SelectActiveModelCommand, ActiveModelSelectionResult>
    {
        private readonly IReadOnlyList<SelectableModelEntry> _catalog;

        public RecordingModels(IReadOnlyList<SelectableModelEntry> catalog, SelectableModelEntry current)
        {
            _catalog = catalog;
            Current = new ActiveModelSelectionSnapshot { ProviderId = current.ProviderId, Profile = current.Profile };
        }

        public ActiveModelSelectionSnapshot Current { get; private set; }

        public List<ModelProfileId> Selections { get; } = [];

        public Task<IReadOnlyList<SelectableModelEntry>> HandleAsync(ListActiveModelsCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_catalog);
        }

        public Task<ActiveModelSelectionSnapshot> HandleAsync(GetActiveModelSelectionCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task<ActiveModelSelectionResult> HandleAsync(SelectActiveModelCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Selections.Add(command.ProfileId);
            var entry = _catalog.Single(candidate => candidate.Profile.Id == command.ProfileId);
            Current = new ActiveModelSelectionSnapshot { ProviderId = entry.ProviderId, Profile = entry.Profile };
            return Task.FromResult(new ActiveModelSelectionResult
            {
                Selection = Current,
                Changed = true,
                Persisted = true,
                ReasoningPreserved = true,
            });
        }
    }

    private sealed class CreateSessionHandler : ICommandHandler<CreateSessionCommand, SessionId>
    {
        public Task<SessionId> HandleAsync(CreateSessionCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SessionId.New());
        }
    }

    private sealed class RejectingMaintenance : IModelCatalogMaintenance
    {
        internal List<(string Action, string ProviderId)> Calls { get; } = [];

        internal bool Cancel { get; init; }

        public Task<ModelCatalogProviderStatus> GetStatusAsync(string providerId, CancellationToken cancellationToken = default)
        {
            Calls.Add(("status", providerId));
            return Task.FromException<ModelCatalogProviderStatus>(CreateFailure(providerId));
        }

        public Task<ModelCatalogRefreshResult> RefreshAsync(string providerId, CancellationToken cancellationToken = default)
        {
            Calls.Add(("refresh", providerId));
            return Task.FromException<ModelCatalogRefreshResult>(CreateFailure(providerId));
        }

        private Exception CreateFailure(string providerId)
        {
            if (Cancel)
            {
                return new OperationCanceledException("Fixture cancellation.");
            }

            return providerId == "typo"
                ? new KeyNotFoundException($"Anthropic provider '{providerId}' is not configured.")
                : new NotSupportedException($"Provider '{providerId}' does not support catalog maintenance.");
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

        public List<InteractionSelectionRequest> SelectionRequests { get; } = [];

        public InteractionSelectionResult? SelectionResult { get; init; }

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
            ComposerRequests.Add(request);
            return Task.FromResult(_inputs.Dequeue());
        }

        public Task<InteractionSelectionResult> SelectAsync(
            InteractionSelectionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            SelectionRequests.Add(request);
            return Task.FromResult(SelectionResult
                ?? throw new InvalidOperationException("The scripted interaction does not request a selection."));
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
