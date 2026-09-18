namespace Threadsmith.Skills;

using System.Collections.Concurrent;
using Threadsmith.Core;

/// <summary>Request-scoped skill invocation and side-effect state shared by invoke_skill and nested procedure tools.</summary>
internal sealed class SkillInvocationOperationState : IAsyncDisposable
{
    /// <summary>Operation-scope key for the shared skill invocation state.</summary>
    internal static readonly object OperationScopeKey = typeof(SkillInvocationOperationState);

    private readonly ConcurrentDictionary<SkillInvocationOperationKey, SkillInvocationOperationEntry> _invocations = [];
    private readonly ConcurrentDictionary<SkillInvocationId, List<SkillSideEffectRecord>> _sideEffects = [];

    /// <summary>Gets an existing invocation entry without starting a replacement.</summary>
    public bool TryGet(SkillInvocationOperationKey key, out SkillInvocationOperationEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_invocations.TryGetValue(key, out entry))
        {
            return false;
        }

        if (entry.Result is null)
        {
            entry = entry with { SideEffects = SnapshotSideEffects(entry.InvocationId) };
        }

        return true;
    }

    /// <summary>Starts a new invocation or returns the existing matching invocation.</summary>
    public bool TryStart(
        SkillInvocationOperationKey key,
        SkillInvocationId invocationId,
        out SkillInvocationOperationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(key);
        entry = _invocations.GetOrAdd(
            key,
            _ => new SkillInvocationOperationEntry(invocationId, DateTimeOffset.UtcNow));
        if (entry.InvocationId != invocationId && entry.Result is null)
        {
            entry = entry with { SideEffects = SnapshotSideEffects(entry.InvocationId) };
        }

        return entry.InvocationId == invocationId;
    }

    /// <summary>Records the final invocation result for duplicate callers.</summary>
    public void Complete(SkillInvocationOperationKey key, SkillInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(result);
        var sideEffects = SnapshotSideEffects(result.InvocationId);
        if (sideEffects.Count == 0)
        {
            sideEffects = result.Checkpoint.Steps.SelectMany(item => item.SideEffects).ToArray();
        }

        _invocations.AddOrUpdate(
            key,
            _ => new SkillInvocationOperationEntry(
                result.InvocationId,
                DateTimeOffset.UtcNow,
                result,
                sideEffects),
            (_, existing) => existing.InvocationId == result.InvocationId
                ? existing with
                {
                    Result = result,
                    SideEffects = sideEffects,
                }
                : existing);
    }

    /// <summary>Removes a failed start entry when it produced no observed side effects.</summary>
    public void RemoveIfNoSideEffects(SkillInvocationOperationKey key, SkillInvocationId invocationId)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_invocations.TryGetValue(key, out var entry)
            || entry.InvocationId != invocationId
            || entry.Result is not null)
        {
            return;
        }

        var sideEffects = SnapshotSideEffects(invocationId);
        if (sideEffects.Count == 0)
        {
            ((ICollection<KeyValuePair<SkillInvocationOperationKey, SkillInvocationOperationEntry>>)_invocations)
                .Remove(new KeyValuePair<SkillInvocationOperationKey, SkillInvocationOperationEntry>(key, entry));
            return;
        }

        _invocations.TryUpdate(key, entry with { SideEffects = sideEffects }, entry);
    }

    /// <summary>Records one externally visible side effect for the owning skill invocation.</summary>
    public void AddSideEffect(SkillInvocationId invocationId, SkillSideEffectRecord sideEffect)
    {
        ArgumentNullException.ThrowIfNull(sideEffect);
        var list = _sideEffects.GetOrAdd(invocationId, _ => []);
        lock (list)
        {
            list.Add(sideEffect);
        }
    }

    /// <summary>Returns side effects observed so far for one invocation.</summary>
    public IReadOnlyList<SkillSideEffectRecord> SnapshotSideEffects(SkillInvocationId invocationId)
    {
        if (!_sideEffects.TryGetValue(invocationId, out var list))
        {
            return [];
        }

        lock (list)
        {
            return list.ToArray();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _invocations.Clear();
        _sideEffects.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Canonical operation-scoped identity for one invoke_skill request.</summary>
internal sealed record SkillInvocationOperationKey(
    SessionId SessionId,
    RunId RunId,
    string Selector,
    string CanonicalInputJson);

/// <summary>Operation-scoped skill invocation state retained for duplicate callers.</summary>
internal sealed record SkillInvocationOperationEntry(
    SkillInvocationId InvocationId,
    DateTimeOffset StartedAt,
    SkillInvocationResult? Result = null,
    IReadOnlyList<SkillSideEffectRecord>? SideEffects = null);
