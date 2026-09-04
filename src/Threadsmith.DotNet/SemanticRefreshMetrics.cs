namespace Threadsmith.DotNet;

using System.Diagnostics.Metrics;

/// <summary>Low-cardinality host telemetry for semantic refresh coordination.</summary>
internal static class SemanticRefreshMetrics
{
    private static readonly Meter _meter = new("Threadsmith.SemanticRefresh", "1.0");

    /// <summary>Gets refresh cycles by mode and outcome.</summary>
    public static Counter<long> Cycles { get; } = _meter.CreateCounter<long>(
        "threadsmith.semantic.refresh.cycles");

    /// <summary>Gets refresh duration by mode and outcome.</summary>
    public static Histogram<double> Duration { get; } = _meter.CreateHistogram<double>(
        "threadsmith.semantic.refresh.duration",
        "ms");

    /// <summary>Gets refreshes requiring a subsequent dirty generation.</summary>
    public static Counter<long> DirtyFollowUps { get; } = _meter.CreateCounter<long>(
        "threadsmith.semantic.refresh.dirty_followups");

    /// <summary>Gets explicit force requests.</summary>
    public static Counter<long> Forced { get; } = _meter.CreateCounter<long>(
        "threadsmith.semantic.refresh.forced");

    /// <summary>Gets callers joining an existing refresh worker.</summary>
    public static Counter<long> JoinedWaiters { get; } = _meter.CreateCounter<long>(
        "threadsmith.semantic.refresh.joined_waiters");

    /// <summary>Gets pending work discarded with an obsolete binding.</summary>
    public static Counter<long> ObsoleteDiscarded { get; } = _meter.CreateCounter<long>(
        "threadsmith.semantic.refresh.obsolete_discarded");

    /// <summary>Gets watcher and unstable-bind recovery requests.</summary>
    public static Counter<long> RecoveryRequested { get; } = _meter.CreateCounter<long>(
        "threadsmith.semantic.refresh.recovery_requested");
}
