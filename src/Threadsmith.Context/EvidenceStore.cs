namespace Threadsmith.Context;

using Threadsmith.Core;

/// <summary>Thread-safe in-memory evidence store with boundary-applied invalidation.</summary>
public sealed class EvidenceStore : IEvidenceStore
{
    private readonly IDomainEventStream _events;
    private readonly Lock _gate = new();
    private readonly Dictionary<(SessionId SessionId, EvidenceId EvidenceId), Evidence> _items = [];
    private readonly Queue<(SessionId SessionId, string Key, string Reason)> _invalidations = new();
    private readonly IOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="EvidenceStore"/> class.</summary>
    public EvidenceStore(IDomainEventStream events, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _events = events;
        _sanitizer = sanitizer;
    }

    /// <inheritdoc />
    public Task AddAsync(Evidence evidence, CancellationToken cancellationToken = default)
    {
        return AddBatchAsync([evidence], cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddBatchAsync(
        IReadOnlyList<Evidence> evidence,
        CancellationToken cancellationToken = default)
    {
        if (!await TryAddBatchAsync(evidence, static () => true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The evidence batch commit was rejected.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryAddBatchAsync(
        IReadOnlyList<Evidence> evidence,
        Func<bool> tryCommit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(tryCommit);
        Evidence[] prepared = [.. evidence.Select(Prepare)];
        if (prepared.Select(item => (item.SessionId, item.EvidenceId)).Distinct().Count()
            != prepared.Length)
        {
            throw new InvalidDataException("An evidence batch contains duplicate identities.");
        }

        IDomainEvent[] events = [.. prepared.Select(item => (IDomainEvent)new EvidenceAdded(
            item.SessionId,
            DateTimeOffset.UtcNow,
            item.EvidenceId,
            item.Kind.ToString()))];
        var commitState = 0;
        bool TryCommitBatch()
        {
            if (Interlocked.CompareExchange(ref commitState, 3, 0) != 0)
            {
                return false;
            }

            if (!tryCommit())
            {
                Volatile.Write(ref commitState, 2);
                return false;
            }

            lock (_gate)
            {
                foreach (var item in prepared)
                {
                    _items[(item.SessionId, item.EvidenceId)] = item;
                }
            }

            Volatile.Write(ref commitState, 1);
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var publication = _events.PublishCommittedBatchAsync(
            events,
            TryCommitBatch,
            cancellationToken);
        try
        {
            try
            {
                await publication.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (Interlocked.CompareExchange(ref commitState, 2, 0) == 0)
                {
                    throw;
                }

                await publication;
            }
        }
        catch (CommittedDomainEventDeliveryException) when (Volatile.Read(ref commitState) == 1)
        {
            // Producer state is authoritative after commit; observer delivery cannot roll it back.
        }

        if (Volatile.Read(ref commitState) != 1)
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<Evidence> Snapshot(SessionId sessionId)
    {
        lock (_gate)
        {
            return _items.Values
                .Where(item => item.SessionId == sessionId)
                .Select(item => item with
                {
                    InvalidationKeys = item.InvalidationKeys.ToArray(),
                })
                .OrderBy(item => item.CollectedAt)
                .ThenBy(item => item.EvidenceId.Value)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public void CopySession(SessionId sourceSessionId, SessionId destinationSessionId)
    {
        if (sourceSessionId == default)
        {
            throw new ArgumentException("The source session id cannot be default.", nameof(sourceSessionId));
        }

        if (destinationSessionId == default)
        {
            throw new ArgumentException("The destination session id cannot be default.", nameof(destinationSessionId));
        }

        lock (_gate)
        {
            foreach (var evidence in _items.Values
                .Where(item => item.SessionId == sourceSessionId)
                .ToArray())
            {
                _items[(destinationSessionId, evidence.EvidenceId)] = evidence with
                {
                    SessionId = destinationSessionId,
                    InvalidationKeys = evidence.InvalidationKeys.ToArray(),
                };
            }
        }
    }

    /// <inheritdoc />
    public void QueueInvalidation(SessionId sessionId, string key, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            _invalidations.Enqueue((sessionId, key, reason));
        }
    }

    /// <inheritdoc />
    public Task<int> ApplyInvalidationsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var staleCount = 0;
        lock (_gate)
        {
            var pendingCount = _invalidations.Count;
            for (var index = 0; index < pendingCount; index++)
            {
                var invalidation = _invalidations.Dequeue();
                if (invalidation.SessionId != sessionId)
                {
                    _invalidations.Enqueue(invalidation);
                    continue;
                }

                foreach (var pair in _items.ToArray())
                {
                    var evidence = pair.Value;
                    if (evidence.SessionId != sessionId)
                    {
                        continue;
                    }

                    var normalizedKey = invalidation.Key.Replace('\\', '/').TrimEnd('/');
                    var normalizedSource = evidence.Provenance.SourcePath?.Replace('\\', '/');
                    var matchesPath = normalizedSource is not null
                        && (string.Equals(normalizedSource, normalizedKey, PathComparison)
                            || normalizedSource.StartsWith(
                                normalizedKey + '/',
                                PathComparison));
                    var matchesKey = evidence.InvalidationKeys.Contains(
                        invalidation.Key,
                        StringComparer.OrdinalIgnoreCase);
                    if (evidence.IsStale || (!matchesPath && !matchesKey))
                    {
                        continue;
                    }

                    _items[pair.Key] = evidence with
                    {
                        IsStale = true,
                        StaleReason = invalidation.Reason,
                    };
                    staleCount++;
                }
            }
        }

        return Task.FromResult(staleCount);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private Evidence Prepare(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Content);
        ArgumentNullException.ThrowIfNull(evidence.Provenance);
        return evidence with
        {
            Content = _sanitizer.Sanitize(evidence.Content),
            InvalidationKeys = evidence.InvalidationKeys.ToArray(),
        };
    }
}
