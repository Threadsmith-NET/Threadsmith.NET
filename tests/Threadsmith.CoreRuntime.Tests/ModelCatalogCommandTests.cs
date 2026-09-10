namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Models;
using Threadsmith.Telemetry;
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

        await coordinator.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(3));

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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Single(surface.ComposerRequests);
        Assert.Single(maintenance.Calls);
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
