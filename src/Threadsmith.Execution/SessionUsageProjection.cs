namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Identifies one provider request for idempotent session usage accounting.</summary>
/// <param name="RunId">Run that owns the request.</param>
/// <param name="Stage">Stable host-owned request stage.</param>
/// <param name="Round">Zero-based request round within the stage.</param>
/// <param name="InvocationId">Host-generated identity for this provider invocation.</param>
public sealed record ModelRequestUsageId(RunId RunId, string Stage, int Round, Guid InvocationId);

/// <summary>Immutable cumulative provider-token usage for one session.</summary>
/// <param name="InputTokens">Cumulative provider-reported input tokens.</param>
/// <param name="OutputTokens">Cumulative provider-reported output tokens.</param>
/// <param name="IsEstimate">Whether any contributing request usage was estimated.</param>
/// <param name="HasUnknownUsage">Whether at least one provider request completed without usage metadata.</param>
/// <param name="HasObservation">Whether at least one provider request completion was observed.</param>
/// <param name="CachedInputTokens">Cumulative provider-reported cache-read input tokens.</param>
/// <param name="CacheWriteTokens">Cumulative provider-reported cache-write input tokens.</param>
/// <param name="HasCacheObservation">Whether at least one request supplied cache counters.</param>
public sealed record SessionUsageSnapshot(
    long InputTokens,
    long OutputTokens,
    bool IsEstimate,
    bool HasUnknownUsage = false,
    bool HasObservation = true,
    long CachedInputTokens = 0,
    long CacheWriteTokens = 0,
    bool HasCacheObservation = false)
{
    /// <summary>Gets cumulative reasoning tokens only when every contributing request reports its breakdown.</summary>
    public long? ReasoningTokens { get; init; }

    /// <summary>Gets the latest observed request for this agent, independent of cumulative counters.</summary>
    public ModelRequestUsageSnapshot? LatestRequest { get; init; }

    /// <summary>Gets the overflow-safe combined token count.</summary>
    public long TotalTokens => InputTokens > long.MaxValue - OutputTokens
        ? long.MaxValue
        : InputTokens + OutputTokens;
}

/// <summary>Aggregates provider-neutral usage once per host-owned request identity.</summary>
public sealed class SessionUsageProjection
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, HashSet<RunId>> _children = [];
    private readonly Dictionary<(SessionId Session, RunId Run), AgentRequestStatus> _requests = [];
    private readonly Dictionary<SessionId, AgentRequestStatus> _rootRequests = [];
    private readonly Dictionary<(SessionId Session, RunId? Child), ModelRequestUsageSnapshot> _latestUsage = [];

    /// <summary>Registers explicit child ownership before requests, including child compaction.</summary>
    public void RegisterChild(SessionId sessionId, RunId runId)
    {
        lock (_gate)
        {
            if (!_children.TryGetValue(sessionId, out var children))
            {
                children = [];
                _children.Add(sessionId, children);
            }

            children.Add(runId);
        }
    }

    /// <summary>Records the latest actually prepared request without charging usage.</summary>
    public void ObserveRequest(SessionId sessionId, RunId runId, AgentRequestStatus status)
    {
        lock (_gate)
        {
            if (IsChild(sessionId, runId))
            {
                _requests[(sessionId, runId)] = status;
            }
            else
            {
                _rootRequests[sessionId] = status;
            }
        }
    }

    /// <summary>Gets the latest request for a child or across explicitly root-owned requests.</summary>
    public AgentRequestStatus? GetRequestStatus(SessionId sessionId, RunId? childRunId = null)
    {
        lock (_gate)
        {
            return childRunId is { } child ? _requests.GetValueOrDefault((sessionId, child)) : _rootRequests.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>Releases transient child request context while preserving authoritative accounting.</summary>
    public void RetireRequestStatus(SessionId sessionId, RunId runId)
    {
        lock (_gate)
        {
            _requests.Remove((sessionId, runId));
        }
    }

    /// <summary>Gets root-only or exact child usage from the same deduplicated request observations.</summary>
    public SessionUsageSnapshot GetOwnerSnapshot(SessionId sessionId, RunId? childRunId = null)
    {
        lock (_gate)
        {
            var result = new SessionUsageSnapshot(0, 0, false, HasObservation: false);
            if (!_usage.TryGetValue(sessionId, out var requests))
            {
                return result;
            }

            long? reasoningTokens = 0;
            foreach (var pair in requests.Where(pair => childRunId is { } child
                ? pair.Key.RunId == child : !IsChild(sessionId, pair.Key.RunId)))
            {
                var usage = pair.Value;
                reasoningTokens = reasoningTokens is { } accumulated && usage?.ReasoningTokens is { } reasoning
                    ? SaturatingAdd(accumulated, reasoning) : null;
                result = result with
                {
                    HasObservation = true,
                    HasUnknownUsage = result.HasUnknownUsage || usage is null,
                    IsEstimate = result.IsEstimate || usage?.IsEstimate == true,
                    InputTokens = SaturatingAdd(result.InputTokens, usage?.InputTokens ?? 0),
                    OutputTokens = SaturatingAdd(result.OutputTokens, usage?.OutputTokens ?? 0),
                    CachedInputTokens = SaturatingAdd(result.CachedInputTokens, usage?.Cache?.Availability == CacheUsageAvailability.Reported ? usage.Cache.CacheReadTokens ?? 0 : 0),
                    CacheWriteTokens = SaturatingAdd(result.CacheWriteTokens, usage?.Cache?.Availability == CacheUsageAvailability.Reported ? usage.Cache.CacheWriteTokens ?? 0 : 0),
                    HasCacheObservation = result.HasCacheObservation || usage?.Cache?.Availability == CacheUsageAvailability.Reported,
                };
            }

            return result with
            {
                ReasoningTokens = result.HasObservation ? reasoningTokens : null,
                LatestRequest = _latestUsage.GetValueOrDefault((sessionId, childRunId)),
            };
        }
    }

    /// <summary>Gets whether historical totals have no reconstructable agent ownership.</summary>
    public bool HasRestoredUsage(SessionId sessionId)
    {
        lock (_gate)
        {
            return _restored.ContainsKey(sessionId);
        }
    }

    /// <summary>Checks explicit child ownership, including retired requests.</summary>
    public bool IsChildRun(SessionId sessionId, RunId runId)
    {
        lock (_gate)
        {
            return IsChild(sessionId, runId);
        }
    }

    private bool IsChild(SessionId sessionId, RunId runId) => _children.TryGetValue(sessionId, out var children) && children.Contains(runId);

    private readonly Dictionary<SessionId, SessionDurableUsage> _restored = [];
    private readonly Dictionary<SessionId, Dictionary<ModelRequestUsageId, ModelUsage?>> _usage = [];

    /// <summary>Records or replaces the normalized usage for one provider request.</summary>
    /// <param name="sessionId">Session that owns the request.</param>
    /// <param name="requestId">Stable request identity used for deduplication.</param>
    /// <param name="usage">Latest normalized provider usage for the request.</param>
    public void Observe(SessionId sessionId, ModelRequestUsageId requestId, ModelUsage usage)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId.Stage);
        ArgumentNullException.ThrowIfNull(usage);
        if (requestId.RunId == default || requestId.Round < 0 || requestId.InvocationId == Guid.Empty)
        {
            throw new ArgumentException("The usage request identity is invalid.", nameof(requestId));
        }

        if (usage.InputTokens < 0
            || usage.OutputTokens < 0
            || usage.ReasoningTokens is < 0
            || usage.Cache?.CacheReadTokens is < 0
            || usage.Cache?.CacheWriteTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usage), "Token usage cannot be negative.");
        }

        if (usage.ReasoningTokens > usage.OutputTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(usage), "Reasoning tokens must be included in output tokens.");
        }

        lock (_gate)
        {
            if (!_usage.TryGetValue(sessionId, out var requests))
            {
                requests = [];
                _usage.Add(sessionId, requests);
            }

            requests[requestId] = usage;
            _latestUsage[(sessionId, IsChild(sessionId, requestId.RunId) ? requestId.RunId : null)] = new(requestId, usage);
        }
    }

    /// <summary>Records a completed provider request that returned no normalized usage metadata.</summary>
    /// <param name="sessionId">Session that owns the request.</param>
    /// <param name="requestId">Stable request identity used for deduplication.</param>
    public void ObserveMissing(SessionId sessionId, ModelRequestUsageId requestId)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId.Stage);
        if (requestId.RunId == default || requestId.Round < 0 || requestId.InvocationId == Guid.Empty)
        {
            throw new ArgumentException("The usage request identity is invalid.", nameof(requestId));
        }

        lock (_gate)
        {
            if (!_usage.TryGetValue(sessionId, out var requests))
            {
                requests = [];
                _usage.Add(sessionId, requests);
            }

            requests[requestId] = null;
            _latestUsage[(sessionId, IsChild(sessionId, requestId.RunId) ? requestId.RunId : null)] = new(requestId, null);
        }
    }

    /// <summary>Restores one durable subtotal without fabricating provider request identities.</summary>
    public void Restore(SessionId sessionId, SessionDurableUsage usage)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(usage);
        lock (_gate)
        {
            _restored[sessionId] = usage;
            _usage.Remove(sessionId);
            _children.Remove(sessionId);
            _rootRequests.Remove(sessionId);
            foreach (var key in _latestUsage.Keys.Where(key => key.Session == sessionId).ToArray())
            {
                _latestUsage.Remove(key);
            }

            foreach (var key in _requests.Keys.Where(key => key.Session == sessionId).ToArray())
            {
                _requests.Remove(key);
            }
        }
    }

    /// <summary>Gets a detached durable usage record including inherited clone history.</summary>
    public SessionDurableUsage GetDurableSnapshot(SessionId sessionId)
    {
        var snapshot = GetSnapshot(sessionId);
        lock (_gate)
        {
            _restored.TryGetValue(sessionId, out var restored);
            return new SessionDurableUsage(
                snapshot.InputTokens,
                snapshot.OutputTokens,
                snapshot.IsEstimate,
                snapshot.HasUnknownUsage,
                snapshot.HasObservation,
                restored?.InheritedInputTokens ?? 0,
                restored?.InheritedOutputTokens ?? 0);
        }
    }

    /// <summary>Gets a detached cumulative snapshot for one session.</summary>
    /// <param name="sessionId">Session whose usage is requested.</param>
    /// <returns>Cumulative usage, including whether any provider completion has been observed.</returns>
    public SessionUsageSnapshot GetSnapshot(SessionId sessionId)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }

        lock (_gate)
        {
            _restored.TryGetValue(sessionId, out var restored);
            if (!_usage.TryGetValue(sessionId, out var requests)
                && restored is null)
            {
                return new SessionUsageSnapshot(0, 0, false, HasObservation: false);
            }

            requests ??= [];
            var inputTokens = restored?.InputTokens ?? 0;
            var outputTokens = restored?.OutputTokens ?? 0;
            var isEstimate = restored?.IsEstimate ?? false;
            var hasUnknownUsage = restored?.HasUnknownUsage ?? false;
            long cachedInputTokens = 0;
            long cacheWriteTokens = 0;
            var hasCacheObservation = false;
            long? reasoningTokens = restored is null ? 0 : null;
            foreach (var usage in requests.Values)
            {
                reasoningTokens = reasoningTokens is { } accumulated && usage?.ReasoningTokens is { } reasoning
                    ? SaturatingAdd(accumulated, reasoning) : null;
                if (usage is null)
                {
                    hasUnknownUsage = true;
                    continue;
                }

                inputTokens = inputTokens > long.MaxValue - usage.InputTokens
                    ? long.MaxValue
                    : inputTokens + usage.InputTokens;
                outputTokens = outputTokens > long.MaxValue - usage.OutputTokens
                    ? long.MaxValue
                    : outputTokens + usage.OutputTokens;
                isEstimate |= usage.IsEstimate;
                if (usage.Cache?.Availability == CacheUsageAvailability.Reported)
                {
                    hasCacheObservation = true;
                    cachedInputTokens = SaturatingAdd(
                        cachedInputTokens,
                        usage.Cache.CacheReadTokens ?? 0);
                    cacheWriteTokens = SaturatingAdd(
                        cacheWriteTokens,
                        usage.Cache.CacheWriteTokens ?? 0);
                }
            }

            return new SessionUsageSnapshot(
                inputTokens,
                outputTokens,
                isEstimate,
                hasUnknownUsage,
                HasObservation: requests.Count > 0 || restored?.HasObservation == true,
                cachedInputTokens,
                cacheWriteTokens,
                hasCacheObservation)
            {
                ReasoningTokens = requests.Count > 0 ? reasoningTokens : null,
            };
        }
    }

    private static long SaturatingAdd(long current, long value)
    {
        return current > long.MaxValue - value ? long.MaxValue : current + value;
    }
}
