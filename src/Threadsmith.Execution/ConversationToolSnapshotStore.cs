namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Owns exact model-visible registrations outside tool-visible invocation contexts.</summary>
public interface IConversationToolSnapshotStore
{
    /// <summary>Captures one exact request-bound registration set and returns an opaque identity.</summary>
    Guid Capture(
        SessionId sessionId,
        RunId runId,
        IReadOnlyList<ToolRegistration> registrations);

    /// <summary>Resolves one exact registration set for its owning request.</summary>
    IReadOnlyList<ToolRegistration> Resolve(Guid snapshotId, SessionId sessionId, RunId runId);

    /// <summary>Releases one request-bound snapshot after model and tool processing completes.</summary>
    void Release(Guid snapshotId);
}

/// <summary>In-memory request-lifetime store for exact model-visible tool registrations.</summary>
public sealed class ConversationToolSnapshotStore : IConversationToolSnapshotStore
{
    private readonly ConcurrentDictionary<Guid, Snapshot> _snapshots = new();

    /// <inheritdoc />
    public Guid Capture(
        SessionId sessionId,
        RunId runId,
        IReadOnlyList<ToolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var snapshotId = Guid.NewGuid();
        if (!_snapshots.TryAdd(
            snapshotId,
            new Snapshot(sessionId, runId, registrations.ToArray())))
        {
            throw new InvalidOperationException("A model-visible tool snapshot identity collided.");
        }

        return snapshotId;
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolRegistration> Resolve(
        Guid snapshotId,
        SessionId sessionId,
        RunId runId)
    {
        if (snapshotId == Guid.Empty
            || !_snapshots.TryGetValue(snapshotId, out var snapshot)
            || snapshot.SessionId != sessionId
            || snapshot.RunId != runId)
        {
            throw new InvalidOperationException(
                "The exact parent model-visible tool snapshot is unavailable or does not own this request.");
        }

        return snapshot.Registrations;
    }

    /// <inheritdoc />
    public void Release(Guid snapshotId)
    {
        if (snapshotId != Guid.Empty)
        {
            _snapshots.TryRemove(snapshotId, out _);
        }
    }

    private sealed record Snapshot(
        SessionId SessionId,
        RunId RunId,
        IReadOnlyList<ToolRegistration> Registrations);
}
