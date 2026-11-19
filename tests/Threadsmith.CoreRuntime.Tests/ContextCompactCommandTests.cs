namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Sessions;
using Xunit;

/// <summary>Regression coverage for the retired interactive context-compaction command.</summary>
public static class ContextCompactCommandTests
{
    private sealed class RecordingDispatcher : ICommandDispatcher
    {
        private readonly SessionId _sessionId = SessionId.New();

        public List<object> Commands { get; } = [];

        public Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            var response = command switch
            {
                CreateSessionCommand => _sessionId,
                _ => throw new InvalidOperationException($"Unexpected command: {command.GetType().Name}."),
            };
            return Task.FromResult((TResponse)(object)response);
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
