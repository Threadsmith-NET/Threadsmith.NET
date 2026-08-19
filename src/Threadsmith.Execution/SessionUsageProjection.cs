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
    /// <summary>Gets the overflow-safe combined token count.</summary>
    public long TotalTokens => InputTokens > long.MaxValue - OutputTokens
        ? long.MaxValue
        : InputTokens + OutputTokens;
}

/// <summary>Aggregates provider-neutral usage once per host-owned request identity.</summary>
public sealed class SessionUsageProjection
{
    private readonly Lock _gate = new();
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
            || usage.Cache?.CacheReadTokens is < 0
            || usage.Cache?.CacheWriteTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usage), "Token usage cannot be negative.");
        }

        lock (_gate)
        {
            if (!_usage.TryGetValue(sessionId, out var requests))
            {
                requests = [];
                _usage.Add(sessionId, requests);
            }

            requests[requestId] = usage;
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
            long inputTokens = restored?.InputTokens ?? 0;
            long outputTokens = restored?.OutputTokens ?? 0;
            bool isEstimate = restored?.IsEstimate ?? false;
            bool hasUnknownUsage = restored?.HasUnknownUsage ?? false;
            long cachedInputTokens = 0;
            long cacheWriteTokens = 0;
            bool hasCacheObservation = false;
            foreach (var usage in requests.Values)
            {
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
                hasCacheObservation);
        }
    }

    private static long SaturatingAdd(long current, long value)
    {
        return current > long.MaxValue - value ? long.MaxValue : current + value;
    }
}
