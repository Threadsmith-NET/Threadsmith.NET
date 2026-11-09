namespace Threadsmith.Core;

using System.Collections.Immutable;

/// <summary>The terminal outcome counted for a tool request.</summary>
public enum ToolTelemetryStatus
{
    /// <summary>The request completed successfully.</summary>
    Succeeded,

    /// <summary>The request failed, was rejected, timed out, or was cancelled.</summary>
    Failed,
}

/// <summary>Detached ownership of a repository database, retained across repository transitions.</summary>
/// <remarks>Host-internal storage metadata; never include in public projections or diagnostics.</remarks>
public sealed record RepositoryStorageBinding(string RepositoryIdentity, string ConnectionString);

/// <summary>Captures database ownership from the existing repository binding authority.</summary>
public interface IRepositoryStorageBindingSource
{
    /// <summary>Captures the current binding, rejecting a supplied stale repository identity.</summary>
    RepositoryStorageBinding CaptureBinding(string? repositoryIdentity = null);
}

/// <summary>One terminal tool request with captured storage ownership and optional elapsed milliseconds.</summary>
public sealed record ToolCompletionEntry(
    RepositoryStorageBinding Binding,
    string ToolId,
    ToolTelemetryStatus Status,
    DateTimeOffset CompletedAtUtc,
    long? ElapsedMilliseconds);

/// <summary>Admits terminal metadata without waiting for storage or running consumer work inline.</summary>
public interface IToolTelemetryProducer
{
    /// <summary>Attempts non-waiting admission; returns false when admissions have stopped.</summary>
    /// <remarks>Performs no database access or logging. Operational storage faults must not escape.</remarks>
    bool TryRecord(ToolCompletionEntry entry);
}

/// <summary>Provides nonblocking process-wide tool statistics persistence health.</summary>
public interface IToolStatisticsHealthSource
{
    /// <summary>Gets whether persistence is disabled for the remaining host lifetime.</summary>
    bool PersistenceDisabled { get; }
}

/// <summary>Committed aggregate statistics for one canonical tool ID.</summary>
/// <remarks>Average duration divides total duration by measured invocation count, not total count.</remarks>
public sealed record ToolStatisticsRow(
    string ToolId,
    long TotalCount,
    long SuccessCount,
    long FailureCount,
    DateTimeOffset LastInvokedAtUtc,
    long TotalDurationMilliseconds,
    long MeasuredInvocationCount,
    long? LastDurationMilliseconds);

/// <summary>An immutable, ordered snapshot of committed tool statistics.</summary>
public sealed record ToolStatisticsSnapshot(ImmutableArray<ToolStatisticsRow> Rows);

/// <summary>Reads and resets through the same ordered queue used for completions.</summary>
public interface IToolStatisticsStore
{
    /// <summary>Reads all rows after earlier admitted work has committed.</summary>
    /// <remarks>Cancellation before admission prevents admission; afterward it cancels only the caller's wait.</remarks>
    /// <exception cref="InvalidOperationException">Statistics are stopped, unavailable, or incomplete.</exception>
    Task<ToolStatisticsSnapshot> GetSnapshotAsync(RepositoryStorageBinding binding, CancellationToken cancellationToken = default);

    /// <summary>Deletes every statistics row and acknowledges only after the transaction commits.</summary>
    /// <remarks>An admitted reset can commit after the caller cancels its wait.</remarks>
    /// <exception cref="InvalidOperationException">Statistics are stopped, unavailable, or incomplete.</exception>
    Task ResetAsync(RepositoryStorageBinding binding, CancellationToken cancellationToken = default);
}

/// <summary>Lists statistics for the currently bound repository.</summary>
public sealed record ListToolStatisticsCommand(SessionId SessionId, string RepositoryIdentity)
    : ICommand<ToolStatisticsSnapshot>;

/// <summary>Resets statistics for the currently bound repository and returns committed acknowledgement.</summary>
public sealed record ResetToolStatisticsCommand(SessionId SessionId, string RepositoryIdentity)
    : ICommand<bool>;
