namespace Threadsmith.Context;

using Threadsmith.Core;

/// <summary>Queues evidence invalidation when repository knowledge changes.</summary>
public sealed class ContextLifecycleObserver
{
    private readonly IEvidenceStore _evidenceStore;
    private readonly IPromptAppendLoader _promptAppendLoader;

    /// <summary>Initializes a new instance of the <see cref="ContextLifecycleObserver"/> class.</summary>
    public ContextLifecycleObserver(
        IEvidenceStore evidenceStore,
        IPromptAppendLoader promptAppendLoader)
    {
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ArgumentNullException.ThrowIfNull(promptAppendLoader);
        _evidenceStore = evidenceStore;
        _promptAppendLoader = promptAppendLoader;
    }

    /// <summary>Observes durable lifecycle events at event-stream order.</summary>
    public Task ObserveAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();
        if (domainEvent is SemanticConfidenceChanged confidence)
        {
            _evidenceStore.QueueInvalidation(
                confidence.SessionId,
                "semantic",
                $"Semantic confidence changed to {confidence.Confidence}.");
        }

        if (domainEvent is RepositoryOpened repository)
        {
            _evidenceStore.QueueInvalidation(
                repository.SessionId,
                "repository",
                "The active repository snapshot changed.");
            _promptAppendLoader.QueueRepositoryInvalidation(repository.Path);
        }

        return Task.CompletedTask;
    }
}
