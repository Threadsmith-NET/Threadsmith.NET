namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using Threadsmith.Core;

/// <summary>One sanitized user steering message admitted to an active run.</summary>
internal sealed record RunSteeringMessage(
    long Sequence,
    DateTimeOffset SubmittedAt,
    string Text);

/// <summary>Coordinates idempotent active-run pauses and safe-boundary steering delivery.</summary>
public sealed class RunSteeringCoordinator
{
    /// <summary>Maximum steering text admitted from one composer submission.</summary>
    public const int MaximumSteeringCharacters = 100_000;

    private readonly ConcurrentDictionary<RunId, RunState> _runs = new();

    /// <summary>Registers one newly active conversation run.</summary>
    public void RegisterRun(SessionId sessionId, RunId runId)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }

        if (runId == default)
        {
            throw new ArgumentException("The run id cannot be default.", nameof(runId));
        }

        if (!_runs.TryAdd(runId, new RunState(sessionId)))
        {
            throw new InvalidOperationException("The run already has an active steering registration.");
        }
    }

    /// <summary>Closes one run and releases any outstanding pause waiter.</summary>
    public void CompleteRun(SessionId sessionId, RunId runId)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.SessionId != sessionId)
        {
            return;
        }

        lock (state.Gate)
        {
            state.IsComplete = true;
            if (state.CurrentPause is { } pause)
            {
                if (!pause.Ready.Task.IsCompleted)
                {
                    pause.ReadyStatus = RunSteeringPauseWaitStatus.TooLate;
                    pause.Ready.TrySetResult(RunSteeringPauseWaitStatus.TooLate);
                }

                pause.Release.TrySetResult();
            }
        }

        _runs.TryRemove(new KeyValuePair<RunId, RunState>(runId, state));
    }

    /// <summary>Registers the exact children of the one active model-callable delegation.</summary>
    public void RegisterDelegation(
        SessionId sessionId,
        RunId runId,
        DelegationId delegationId,
        IReadOnlyList<RunId> childRunIds)
    {
        ArgumentNullException.ThrowIfNull(childRunIds);
        if (!_runs.TryGetValue(runId, out var state)
            || state.SessionId != sessionId
            || state.IsComplete)
        {
            return;
        }

        var children = childRunIds.ToHashSet();
        if (delegationId == default || children.Count == 0 || children.Contains(default))
        {
            throw new ArgumentException("The delegation must contain non-default child run ids.", nameof(childRunIds));
        }

        lock (state.Gate)
        {
            if (state.Delegation is not null)
            {
                throw new InvalidOperationException("The run already has an active steering delegation.");
            }

            state.Delegation = new DelegationState(delegationId, children);
        }
    }

    /// <summary>Marks a delegation terminal after its joined steering accounting is captured.</summary>
    public void CompleteDelegation(SessionId sessionId, RunId runId, DelegationId delegationId)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.SessionId != sessionId)
        {
            return;
        }

        lock (state.Gate)
        {
            if (state.Delegation is not { } delegation || delegation.DelegationId != delegationId)
            {
                return;
            }

            foreach (var childRunId in delegation.ChildRunIds)
            {
                delegation.TerminalChildRunIds.Add(childRunId);
            }

            TryReadyPause(state);
            state.Delegation = null;
        }
    }

    /// <summary>Records that one child reached a terminal state.</summary>
    public void CompleteChild(SessionId sessionId, RunId parentRunId, RunId childRunId)
    {
        if (!_runs.TryGetValue(parentRunId, out var state) || state.SessionId != sessionId)
        {
            return;
        }

        lock (state.Gate)
        {
            if (state.Delegation is { } delegation && delegation.ChildRunIds.Contains(childRunId))
            {
                delegation.TerminalChildRunIds.Add(childRunId);
                TryReadyPause(state);
            }
        }
    }

    /// <summary>Requests one idempotent pause for an active run.</summary>
    public RunSteeringPauseRequestResult RequestPause(SessionId sessionId, RunId runId)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.IsComplete)
        {
            return new RunSteeringPauseRequestResult(
                RunSteeringPauseRequestStatus.TooLate,
                default);
        }

        if (state.SessionId != sessionId)
        {
            return new RunSteeringPauseRequestResult(
                RunSteeringPauseRequestStatus.WrongSession,
                default);
        }

        lock (state.Gate)
        {
            if (state.IsComplete)
            {
                return new RunSteeringPauseRequestResult(
                    RunSteeringPauseRequestStatus.TooLate,
                    default);
            }

            if (state.CurrentPause is { } pending)
            {
                return new RunSteeringPauseRequestResult(
                    RunSteeringPauseRequestStatus.AlreadyPending,
                    pending.PauseId);
            }

            var pause = new PauseState(
                SteeringPauseId.New(),
                state.Delegation?.DelegationId,
                state.Delegation?.ChildRunIds.ToHashSet() ?? []);
            state.CurrentPause = pause;
            return new RunSteeringPauseRequestResult(
                RunSteeringPauseRequestStatus.Accepted,
                pause.PauseId);
        }
    }

    /// <summary>Waits for a requested pause to reach its safe boundary.</summary>
    public async Task<RunSteeringPauseWaitResult> WaitForPauseAsync(
        SessionId sessionId,
        RunId runId,
        SteeringPauseId pauseId,
        CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.SessionId != sessionId)
        {
            return new RunSteeringPauseWaitResult(RunSteeringPauseWaitStatus.Rejected, pauseId);
        }

        Task<RunSteeringPauseWaitStatus> ready;
        lock (state.Gate)
        {
            if (state.CurrentPause is not { } pause || pause.PauseId != pauseId)
            {
                return new RunSteeringPauseWaitResult(RunSteeringPauseWaitStatus.Rejected, pauseId);
            }

            ready = pause.Ready.Task;
        }

        var status = await ready.WaitAsync(cancellationToken);
        return new RunSteeringPauseWaitResult(status, pauseId);
    }

    /// <summary>Marks the ready notification published exactly once.</summary>
    public bool TryMarkPausedPublished(SessionId sessionId, RunId runId, SteeringPauseId pauseId)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.SessionId != sessionId)
        {
            return false;
        }

        lock (state.Gate)
        {
            if (state.CurrentPause is not { } pause
                || pause.PauseId != pauseId
                || pause.ReadyStatus != RunSteeringPauseWaitStatus.Ready
                || pause.PausedEventPublished)
            {
                return false;
            }

            pause.PausedEventPublished = true;
            return true;
        }
    }

    /// <summary>Submits sanitized steering text or dismisses one ready prompt.</summary>
    public RunSteeringSubmissionResult Submit(
        SessionId sessionId,
        RunId runId,
        SteeringPauseId pauseId,
        string? sanitizedText)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.IsComplete)
        {
            return new RunSteeringSubmissionResult(RunSteeringSubmissionStatus.TooLate, null);
        }

        if (state.SessionId != sessionId)
        {
            return new RunSteeringSubmissionResult(RunSteeringSubmissionStatus.Rejected, null);
        }

        lock (state.Gate)
        {
            if (state.CurrentPause is not { } pause
                || pause.PauseId != pauseId
                || pause.ReadyStatus != RunSteeringPauseWaitStatus.Ready)
            {
                return new RunSteeringSubmissionResult(RunSteeringSubmissionStatus.Rejected, null);
            }

            var text = sanitizedText;
            long? sequence = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (text.Length > MaximumSteeringCharacters)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(sanitizedText),
                            $"Steering text cannot exceed {MaximumSteeringCharacters} characters.");
                    }

                    sequence = checked(++state.LastSequence);
                    state.Messages.Add(new SteeringDelivery(
                        new RunSteeringMessage(sequence.Value, DateTimeOffset.UtcNow, text),
                        pause.DelegationId,
                        pause.ChildRunIds));
                }
            }
            finally
            {
                state.CurrentPause = null;
                pause.Release.TrySetResult();
            }

            return new RunSteeringSubmissionResult(
                sequence is null
                    ? RunSteeringSubmissionStatus.Dismissed
                    : RunSteeringSubmissionStatus.Accepted,
                sequence);
        }
    }

    /// <summary>Pauses the parent conversation before its next provider operation.</summary>
    internal async Task<IReadOnlyList<RunSteeringMessage>> PauseParentAtBoundaryAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.SessionId != sessionId)
        {
            return [];
        }

        Task? release = null;
        lock (state.Gate)
        {
            if (state.CurrentPause is { } pause
                && (pause.DelegationId is null
                    || state.Delegation is null
                    || state.Delegation.DelegationId != pause.DelegationId))
            {
                pause.ReadyStatus = RunSteeringPauseWaitStatus.Ready;
                pause.Ready.TrySetResult(RunSteeringPauseWaitStatus.Ready);
                release = pause.Release.Task;
            }
        }

        if (release is not null)
        {
            await release.WaitAsync(cancellationToken);
        }

        lock (state.Gate)
        {
            return DrainForParent(state);
        }
    }

    /// <summary>Pauses one child before its next provider operation.</summary>
    internal async Task<IReadOnlyList<RunSteeringMessage>> PauseChildAtBoundaryAsync(
        SessionId sessionId,
        RunId parentRunId,
        RunId childRunId,
        CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(parentRunId, out var state) || state.SessionId != sessionId)
        {
            return [];
        }

        Task? release = null;
        lock (state.Gate)
        {
            if (state.CurrentPause is { DelegationId: not null } pause
                && pause.ChildRunIds.Contains(childRunId))
            {
                pause.PausedChildRunIds.Add(childRunId);
                TryReadyPause(state);
                release = pause.Release.Task;
            }
        }

        if (release is not null)
        {
            await release.WaitAsync(cancellationToken);
        }

        lock (state.Gate)
        {
            return DrainForChild(state, childRunId);
        }
    }

    /// <summary>Pauses a joined delegation before its tool result can return to the parent.</summary>
    internal async Task PauseDelegationAtJoinBoundaryAsync(
        SessionId sessionId,
        RunId runId,
        DelegationId delegationId,
        CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.SessionId != sessionId)
        {
            return;
        }

        Task? release = null;
        lock (state.Gate)
        {
            if (state.Delegation is { } delegation && delegation.DelegationId == delegationId)
            {
                foreach (var childRunId in delegation.ChildRunIds)
                {
                    delegation.TerminalChildRunIds.Add(childRunId);
                }

                TryReadyPause(state);
            }

            if (state.CurrentPause is { } pause && pause.DelegationId == delegationId)
            {
                release = pause.Release.Task;
            }
        }

        if (release is not null)
        {
            await release.WaitAsync(cancellationToken);
        }

        lock (state.Gate)
        {
            if (state.Delegation is { } delegation && delegation.DelegationId == delegationId)
            {
                state.Delegation = null;
            }
        }
    }

    /// <summary>Gets joined delivery accounting for one delegation.</summary>
    internal DelegationSteeringSummary GetDelegationSummary(
        SessionId sessionId,
        RunId runId,
        DelegationId delegationId)
    {
        if (!_runs.TryGetValue(runId, out var state) || state.SessionId != sessionId)
        {
            return new DelegationSteeringSummary(0, 0, 0);
        }

        lock (state.Gate)
        {
            var deliveries = state.Messages
                .Where(message => message.DelegationId == delegationId)
                .ToArray();
            return new DelegationSteeringSummary(
                deliveries.Length,
                deliveries.Sum(message => message.DeliveredChildRunIds.Count),
                deliveries.Sum(message =>
                    message.TargetChildRunIds.Count - message.DeliveredChildRunIds.Count));
        }
    }

    private static void TryReadyPause(RunState state)
    {
        if (state.CurrentPause is not { DelegationId: not null } pause
            || state.Delegation is not { } delegation
            || delegation.DelegationId != pause.DelegationId)
        {
            return;
        }

        if (pause.ChildRunIds.All(childRunId =>
            pause.PausedChildRunIds.Contains(childRunId)
            || delegation.TerminalChildRunIds.Contains(childRunId)))
        {
            pause.ReadyStatus = RunSteeringPauseWaitStatus.Ready;
            pause.Ready.TrySetResult(RunSteeringPauseWaitStatus.Ready);
        }
    }

    private static IReadOnlyList<RunSteeringMessage> DrainForParent(RunState state)
    {
        var messages = state.Messages
            .Where(message => !message.DeliveredToParent)
            .OrderBy(message => message.Message.Sequence)
            .ToArray();
        foreach (var message in messages)
        {
            message.DeliveredToParent = true;
        }

        return messages.Select(message => message.Message).ToArray();
    }

    private static IReadOnlyList<RunSteeringMessage> DrainForChild(RunState state, RunId childRunId)
    {
        var messages = state.Messages
            .Where(message => message.DelegationId is not null
                && message.TargetChildRunIds.Contains(childRunId)
                && !message.DeliveredChildRunIds.Contains(childRunId))
            .OrderBy(message => message.Message.Sequence)
            .ToArray();
        foreach (var message in messages)
        {
            message.DeliveredChildRunIds.Add(childRunId);
        }

        return messages.Select(message => message.Message).ToArray();
    }

    private sealed class RunState(SessionId sessionId)
    {
        public Lock Gate { get; } = new();

        public SessionId SessionId { get; } = sessionId;

        public DelegationState? Delegation { get; set; }

        public bool IsComplete { get; set; }

        public long LastSequence { get; set; }

        public List<SteeringDelivery> Messages { get; } = [];

        public PauseState? CurrentPause { get; set; }
    }

    private sealed class DelegationState(DelegationId delegationId, HashSet<RunId> childRunIds)
    {
        public DelegationId DelegationId { get; } = delegationId;

        public HashSet<RunId> ChildRunIds { get; } = childRunIds;

        public HashSet<RunId> TerminalChildRunIds { get; } = [];
    }

    private sealed class PauseState(
        SteeringPauseId pauseId,
        DelegationId? delegationId,
        HashSet<RunId> childRunIds)
    {
        public HashSet<RunId> ChildRunIds { get; } = childRunIds;

        public DelegationId? DelegationId { get; } = delegationId;

        public HashSet<RunId> PausedChildRunIds { get; } = [];

        public SteeringPauseId PauseId { get; } = pauseId;

        public bool PausedEventPublished { get; set; }

        public RunSteeringPauseWaitStatus? ReadyStatus { get; set; }

        public TaskCompletionSource<RunSteeringPauseWaitStatus> Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SteeringDelivery(
        RunSteeringMessage message,
        DelegationId? delegationId,
        HashSet<RunId> targetChildRunIds)
    {
        public HashSet<RunId> DeliveredChildRunIds { get; } = [];

        public bool DeliveredToParent { get; set; }

        public DelegationId? DelegationId { get; } = delegationId;

        public RunSteeringMessage Message { get; } = message;

        public HashSet<RunId> TargetChildRunIds { get; } = targetChildRunIds;
    }
}
