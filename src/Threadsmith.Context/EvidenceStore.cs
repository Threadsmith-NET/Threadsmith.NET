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
    private readonly Queue<(SessionId SessionId, Invalidation Change)> _invalidations = new();
    private readonly Dictionary<SessionId, InvalidationWatermarks> _invalidationWatermarks = [];
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
        var observations = prepared.Select(item => PrepareObservations(item, pathComparers)).ToArray();
        var invalidationPaths = prepared.Select(item => PrepareInvalidationPaths(item, pathComparers)).ToArray();
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
                for (var index = 0; index < prepared.Length; index++)
                {
                    var item = prepared[index];
                    var (byPath, conflicting) = observations[index];
                    if (conflicting)
                    {
                        item = item with { IsStale = true, StaleReason = "Contains different versions of the same source file." };
                    }

                    if (!item.IsStale && invalidationPaths[index] is { } paths
                        && _invalidationWatermarks.TryGetValue(item.SessionId, out var watermarks)
                        && watermarks.FindMatch(item, paths) is { } change)
                    {
                        item = item with { IsStale = true, StaleReason = change.Reason };
                    }

                    Store(ReconcileObservations(item, byPath));
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
        var pathKey = NormalizeInvalidationPath(key);
        lock (_gate)
        {
            var change = new Invalidation(key, reason, changedAt);
            _invalidations.Enqueue((sessionId, change));
            if (!_invalidationWatermarks.TryGetValue(sessionId, out var watermarks))
            {
                watermarks = new();
                _invalidationWatermarks.Add(sessionId, watermarks);
            }

            watermarks.Record(change, pathKey);
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

                    if (evidence.IsStale || !Invalidates(evidence, invalidation.Change))
                    {
                        continue;
                    }

                    Store(evidence with
                    {
                        IsStale = true,
                        StaleReason = invalidation.Change.Reason,
                    });
                    staleCount++;
                }
            }
        }

        return Task.FromResult(staleCount);
    }

    private static bool Invalidates(Evidence evidence, Invalidation change)
    {
        var dependencies = SourceEvidence.Dependencies(evidence);
        if (evidence.InvalidationKeys.Contains(change.Key, StringComparer.OrdinalIgnoreCase)
            && ObserveKeyAt(evidence, change.Key) <= change.ChangedAt)
        {
            return true;
        }

        var root = evidence.Provenance.RepositoryPath;
        var normalized = root is null ? change.Key.Replace('\\', '/').TrimEnd('/') : SourceEvidence.NormalizePath(root, change.Key);
        var comparer = root is null ? DefaultPathComparer() : RepositoryPathPolicy.GetPathComparer(root);
        foreach (var dependency in dependencies)
        {
            if ((dependency.ObservedAt ?? evidence.CollectedAt) > change.ChangedAt)
            {
                continue;
            }

            var path = root is null ? dependency.Path.Replace('\\', '/') : SourceEvidence.NormalizePath(root, dependency.Path);
            if (comparer.Equals(path, normalized)
                || (path.Length > normalized.Length && path[normalized.Length] == '/' && comparer.Equals(path[..normalized.Length], normalized)))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset ObserveKeyAt(Evidence evidence, string key)
    {
        var observed = evidence.SemanticObservedAt ?? evidence.CollectedAt;
        if (key.Equals("repository", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in evidence.FileDependencies)
            {
                if (file.ObservedAt is { } at && at < observed)
                {
                    observed = at;
                }
            }
        }

        return observed;
    }

    private static Dictionary<string, DateTimeOffset>? PrepareInvalidationPaths(
        Evidence evidence, Dictionary<string, StringComparer> pathComparers)
    {
        if (evidence.IsStale || (evidence.SemanticObservedAt is null && !evidence.FileDependencies.Any(file => file.ObservedAt is not null)))
        {
            return null;
        }

        var root = evidence.Provenance.RepositoryPath;
        var paths = new Dictionary<string, DateTimeOffset>(root is null ? DefaultPathComparer() : pathComparers[root]);
        foreach (var dependency in SourceEvidence.Dependencies(evidence))
        {
            var observedAt = dependency.ObservedAt ?? evidence.CollectedAt;
            var path = root is null ? NormalizeInvalidationPath(dependency.Path) : SourceEvidence.NormalizePath(root, dependency.Path);
            while (true)
            {
                RecordEarliest(path);
                if (root is not null)
                {
                    RecordEarliest(NormalizeInvalidationPath(Path.GetFullPath(path, root)));
                }

                var separator = path.LastIndexOf('/');
                if (separator <= 0)
                {
                    break;
                }

                path = path[..separator];
            }

            void RecordEarliest(string candidate)
            {
                if (!paths.TryGetValue(candidate, out var earlier) || observedAt < earlier)
                {
                    paths[candidate] = observedAt;
                }
            }
        }

        return paths;
    }

    private static string NormalizeInvalidationPath(string key)
    {
        var path = key.Replace('\\', '/');
        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        }

        // Relative keys must not be resolved against the process working directory.
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".." && segments.Count != 0 && segments[^1] != "..")
            {
                segments.RemoveAt(segments.Count - 1);
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return segments.Count == 0 ? "." : string.Join('/', segments);
    }

    private static (Dictionary<string, FileObservation>? ByPath, bool Conflicting) PrepareObservations(
        Evidence item, Dictionary<string, StringComparer> pathComparers)
    {
        var root = item.Provenance.RepositoryPath;
        Dictionary<string, FileObservation>? byPath = null;
        var conflicting = false;
        foreach (var dependency in item.FileDependencies)
        {
            if (dependency.Sha256 is not { } digest)
            {
                continue;
            }

            var path = root is null ? dependency.Path.Replace('\\', '/') : dependency.Path;
            var at = dependency.ObservedAt ?? item.CollectedAt;
            byPath ??= new(root is null ? DefaultPathComparer() : pathComparers[root]);
            if (!byPath.TryGetValue(path, out var previous))
            {
                byPath.Add(path, new(digest, at, at));
            }
            else if (string.Equals(previous.Digest, digest, StringComparison.OrdinalIgnoreCase))
            {
                byPath[path] = new(digest, at < previous.Earliest ? at : previous.Earliest, at > previous.Latest ? at : previous.Latest);
            }
            else
            {
                conflicting = true;
                if (at >= previous.Latest)
                {
                    byPath[path] = new(digest, at, at);
                }
            }
        }

        return (root is null ? null : byPath, conflicting);
    }

    private static StringComparer DefaultPathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private Evidence ReconcileObservations(Evidence incoming, Dictionary<string, FileObservation>? byPath)
    {
        if (byPath is null || !_dependencies.TryGetValue(incoming.SessionId, out var paths))
        {
            return incoming;
        }

        const string superseded = "Superseded by a different source file version.";
        HashSet<EvidenceId> affected = [];
        foreach (var path in byPath.Keys)
        {
            if (paths.TryGetValue(path, out var identities))
            {
                affected.UnionWith(identities);
            }
        }

        foreach (var identity in affected)
        {
            var previous = _items[(incoming.SessionId, identity)];
            var supersedesPrevious = false;
            foreach (var old in previous.FileDependencies)
            {
                if (!byPath.TryGetValue(old.Path, out var current)
                    || string.Equals(old.Sha256, current.Digest, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var at = old.ObservedAt ?? previous.CollectedAt;
                supersedesPrevious |= current.Latest >= at;
                if (!incoming.IsStale && old.Sha256 is not null && current.Earliest < at)
                {
                    incoming = incoming with { IsStale = true, StaleReason = superseded };
                }

                if (supersedesPrevious && incoming.IsStale)
                {
                    break;
                }
            }

            if (supersedesPrevious)
            {
                Store(previous with { IsStale = true, StaleReason = superseded });
            }
        }

        return incoming;
    }

    private sealed class InvalidationWatermarks
    {
        private readonly Dictionary<string, Invalidation> _keys = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Invalidation> _paths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Invalidation> _caseInsensitivePaths = new(StringComparer.OrdinalIgnoreCase);

        public void Record(Invalidation change, string path)
        {
            RecordLatest(_keys, change.Key, change);
            RecordLatest(_paths, path, change);
            RecordLatest(_caseInsensitivePaths, path, change);
        }

        public Invalidation? FindMatch(Evidence evidence, Dictionary<string, DateTimeOffset> paths)
        {
            foreach (var key in evidence.InvalidationKeys)
            {
                if (_keys.TryGetValue(key, out var change) && ObserveKeyAt(evidence, key) <= change.ChangedAt)
                {
                    return change;
                }
            }

            var pathIndex = paths.Comparer == StringComparer.OrdinalIgnoreCase ? _caseInsensitivePaths : _paths;
            foreach (var (path, observedAt) in paths)
            {
                if (pathIndex.TryGetValue(path, out var change) && observedAt <= change.ChangedAt)
                {
                    return change;
                }
            }

            return null;
        }

        private static void RecordLatest(Dictionary<string, Invalidation> index, string key, Invalidation change)
        {
            if (!index.TryGetValue(key, out var previous) || previous.ChangedAt <= change.ChangedAt)
            {
                index[key] = change;
            }
        }
    }

    private readonly record struct Invalidation(string Key, string Reason, DateTimeOffset ChangedAt);

    private readonly record struct FileObservation(string Digest, DateTimeOffset Earliest, DateTimeOffset Latest);

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
