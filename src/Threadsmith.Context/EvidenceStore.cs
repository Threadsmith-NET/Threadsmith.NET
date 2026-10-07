namespace Threadsmith.Context;

using Threadsmith.Core;

/// <summary>Thread-safe in-memory evidence store with boundary-applied invalidation.</summary>
public sealed class EvidenceStore : IEvidenceStore
{
    private readonly IDomainEventStream _events;
    private readonly Lock _gate = new();
    private readonly Dictionary<(SessionId SessionId, EvidenceId EvidenceId), Evidence> _items = [];

    // Case-insensitive candidate lookup is a superset; supersession still uses the repository comparer.
    private readonly Dictionary<SessionId, Dictionary<string, HashSet<EvidenceId>>> _dependencies = [];
    private readonly Queue<(SessionId SessionId, string Key, string Reason, DateTimeOffset ChangedAt)> _invalidations = new();
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
        var pathComparers = prepared.Select(item => item.Provenance.RepositoryPath)
            .OfType<string>().Distinct(StringComparer.Ordinal)
            .ToDictionary(root => root, RepositoryPathPolicy.GetPathComparer, StringComparer.Ordinal);
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
                    if (item.Provenance.RepositoryPath is { } root
                        && item.FileDependencies.Any(dependency => dependency.Sha256 is not null)
                        && _dependencies.TryGetValue(item.SessionId, out var paths))
                    {
                        HashSet<EvidenceId> affected = [];
                        foreach (var dependency in item.FileDependencies.Where(dependency => dependency.Sha256 is not null))
                        {
                            if (paths.TryGetValue(dependency.Path, out var identities))
                            {
                                affected.UnionWith(identities);
                            }
                        }

                        foreach (var identity in affected)
                        {
                            var previous = _items[(item.SessionId, identity)];
                            if (previous.CollectedAt <= item.CollectedAt && previous.FileDependencies.Any(old => item.FileDependencies.Any(current =>
                                pathComparers[root].Equals(old.Path, current.Path)
                                && current.Sha256 is not null && !string.Equals(old.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase))))
                            {
                                Store(previous with { IsStale = true, StaleReason = "Superseded by a different source file version." });
                            }
                        }
                    }

                    Store(item);
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
                    FileDependencies = item.FileDependencies.ToArray(),
                })
                .OrderBy(item => item.CollectedAt)
                .ThenBy(item => item.EvidenceId.Value)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public Evidence? Find(SessionId sessionId, EvidenceId evidenceId)
    {
        lock (_gate)
        {
            return _items.TryGetValue((sessionId, evidenceId), out var evidence)
                ? evidence with { FileDependencies = evidence.FileDependencies.ToArray(), InvalidationKeys = evidence.InvalidationKeys.ToArray() }
                : null;
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
                Store(evidence with
                {
                    SessionId = destinationSessionId,
                    FileDependencies = evidence.FileDependencies.ToArray(),
                    InvalidationKeys = evidence.InvalidationKeys.ToArray(),
                });
            }
        }
    }

    /// <inheritdoc />
    public void QueueInvalidation(SessionId sessionId, string key, string reason)
    {
        QueueInvalidation(sessionId, key, reason, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public void QueueInvalidation(SessionId sessionId, string key, string reason, DateTimeOffset changedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            _invalidations.Enqueue((sessionId, key, reason, changedAt));
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

                    var root = evidence.Provenance.RepositoryPath;
                    var normalizedKey = root is null ? invalidation.Key.Replace('\\', '/').TrimEnd('/') : SourceEvidence.NormalizePath(root, invalidation.Key);
                    var comparer = root is null ? (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) : RepositoryPathPolicy.GetPathComparer(root);
                    var matchesPath = SourceEvidence.Dependencies(evidence).Any(dependency =>
                    {
                        var path = root is null ? dependency.Path.Replace('\\', '/') : SourceEvidence.NormalizePath(root, dependency.Path);
                        return comparer.Equals(path, normalizedKey)
                            || (path.Length > normalizedKey.Length && path[normalizedKey.Length] == '/' && comparer.Equals(path[..normalizedKey.Length], normalizedKey));
                    });
                    var matchesKey = evidence.InvalidationKeys.Contains(
                        invalidation.Key,
                        StringComparer.OrdinalIgnoreCase);
                    if (evidence.IsStale || evidence.CollectedAt > invalidation.ChangedAt || (!matchesPath && !matchesKey))
                    {
                        continue;
                    }

                    Store(evidence with
                    {
                        IsStale = true,
                        StaleReason = invalidation.Reason,
                    });
                    staleCount++;
                }
            }
        }

        return Task.FromResult(staleCount);
    }

    // Called only under _gate so retained history and the live dependency index change atomically.
    private void Store(Evidence evidence)
    {
        var key = (evidence.SessionId, evidence.EvidenceId);
        if (_dependencies.TryGetValue(evidence.SessionId, out var paths)
            && _items.TryGetValue(key, out var previous) && !previous.IsStale)
        {
            foreach (var dependency in previous.FileDependencies)
            {
                if (paths.TryGetValue(dependency.Path, out var identities))
                {
                    identities.Remove(previous.EvidenceId);
                    if (identities.Count == 0)
                    {
                        paths.Remove(dependency.Path);
                    }
                }
            }
        }

        _items[key] = evidence;
        if (!evidence.IsStale && evidence.FileDependencies.Count > 0)
        {
            if (paths is null)
            {
                paths = new Dictionary<string, HashSet<EvidenceId>>(StringComparer.OrdinalIgnoreCase);
                _dependencies.Add(evidence.SessionId, paths);
            }

            foreach (var dependency in evidence.FileDependencies)
            {
                if (!paths.TryGetValue(dependency.Path, out var identities))
                {
                    identities = [];
                    paths.Add(dependency.Path, identities);
                }

                identities.Add(evidence.EvidenceId);
            }
        }
    }

    private Evidence Prepare(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Content);
        ArgumentNullException.ThrowIfNull(evidence.Provenance);
        return evidence with
        {
            Content = JsonOutputSanitizer.SanitizeJsonOrText(evidence.Content, _sanitizer),
            FileDependencies = evidence.FileDependencies.Select(dependency => evidence.Provenance.RepositoryPath is { } root
                ? dependency with { Path = SourceEvidence.NormalizePath(root, dependency.Path) } : dependency).ToArray(),
            InvalidationKeys = evidence.InvalidationKeys.ToArray(),
        };
    }
}
