namespace Threadsmith.Context;

using Threadsmith.Core;

/// <summary>Queues evidence invalidation when repository knowledge changes.</summary>
public sealed class ContextLifecycleObserver
{
    private readonly IConversationMemoryInvalidator? _conversationMemoryInvalidator;
    private readonly IEvidenceStore _evidenceStore;
    private readonly IPromptAppendLoader _promptAppendLoader;
    private readonly RepositoryMemoryInvalidator? _repositoryMemoryInvalidator;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SessionId, string> _repositoryIdentities = new();

    /// <summary>Initializes a new instance of the <see cref="ContextLifecycleObserver"/> class.</summary>
    public ContextLifecycleObserver(
        IEvidenceStore evidenceStore,
        IPromptAppendLoader promptAppendLoader,
        IConversationMemoryInvalidator? conversationMemoryInvalidator = null,
        RepositoryMemoryInvalidator? repositoryMemoryInvalidator = null)
    {
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ArgumentNullException.ThrowIfNull(promptAppendLoader);
        _evidenceStore = evidenceStore;
        _promptAppendLoader = promptAppendLoader;
        _conversationMemoryInvalidator = conversationMemoryInvalidator;
        _repositoryMemoryInvalidator = repositoryMemoryInvalidator;
    }

    /// <summary>Observes durable lifecycle events at event-stream order.</summary>
    public async Task ObserveAsync(
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
            _repositoryIdentities[repository.SessionId] = RepositoryIdentity.Create(repository.Path);
            _evidenceStore.QueueInvalidation(
                repository.SessionId,
                "repository",
                "The active repository snapshot changed.");
            _promptAppendLoader.QueueRepositoryInvalidation(repository.Path);
        }

        if (_conversationMemoryInvalidator is not null
            && (domainEvent is RepositoryOpened || domainEvent is MutationApplied))
        {
            var invalidationKeys = CreateInvalidationKeys(domainEvent);
            await _conversationMemoryInvalidator.InvalidateAtTurnBoundaryAsync(
                domainEvent.SessionId,
                invalidationKeys,
                currentRepositoryRevision: null,
                cancellationToken);
        }

        if (domainEvent is MutationApplied
            && _repositoryMemoryInvalidator is not null
            && _repositoryIdentities.TryGetValue(domainEvent.SessionId, out var repositoryIdentity))
        {
            await _repositoryMemoryInvalidator.InvalidateAtTurnBoundaryAsync(
                domainEvent.SessionId,
                repositoryIdentity,
                CreateInvalidationKeys(domainEvent),
                currentRepositoryRevision: null,
                cancellationToken);
        }
    }

    private static IReadOnlyList<string> CreateInvalidationKeys(IDomainEvent domainEvent)
    {
        if (domainEvent is not MutationApplied mutation)
        {
            return ["repository"];
        }

        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(mutation.RelativePath))
        {
            keys.Add($"file:{mutation.RelativePath}");
        }

        if (!string.IsNullOrWhiteSpace(mutation.DestinationRelativePath))
        {
            keys.Add($"file:{mutation.DestinationRelativePath}");
        }

        return keys.Count == 0 ? ["repository"] : keys;
    }
}
