namespace Threadsmith.Interaction.Agents;

using System.Diagnostics.CodeAnalysis;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Models;

/// <summary>Serializes child lifecycle, transient text, and tool correlation independently of MAIN.</summary>
internal sealed class AgentWorkspaceProjection : IAsyncDisposable
{
    private readonly IInteractionSurface _surface;
    private readonly IAgentWorkspaceSurface _workspace;
    private readonly AgentDisplayStream? _display;
    private readonly SessionUsageProjection? _usage;
    private readonly ConfiguredModelCatalog? _models;
    private readonly AgentNameAllocator _names;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop;
    private readonly Dictionary<RunId, ChildState> _children = [];
    private readonly Dictionary<AgentPresentationTarget, long> _revisions = [];
    private readonly Dictionary<DelegationId, int> _accepted = [];
    private readonly Dictionary<AgentPresentationTarget, AgentPresentationSnapshot> _terminal = [];
    private readonly Queue<(DelegationId Id, int Generation)> _closed = [];
    private readonly HashSet<DelegationId> _closedIds = [];
    private readonly Dictionary<ToolInvocationId, ToolInvocationStarted> _tools = [];
    private readonly Dictionary<DelegationId, ToolInvocationId> _delegationTools = [];
    private readonly Dictionary<AgentPresentationTarget, PresentationTextSegment> _progress = [];
    private readonly bool _markdown;
    private readonly bool _showOperationDurations;
    private readonly List<AgentDisplayText> _pendingDisplay = [];
    private SessionId _session;
    private Task _pump = Task.CompletedTask;

    /// <summary>Initializes a new instance of the <see cref="AgentWorkspaceProjection"/> class.</summary>
    internal AgentWorkspaceProjection(IInteractionSurface surface, AgentDisplayStream? display, SessionUsageProjection? usage, ConfiguredModelCatalog? models, AgentNameCatalog names, bool markdown, CancellationToken cancellationToken)
        : this(surface, display, usage, models, names, markdown, true, cancellationToken)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AgentWorkspaceProjection"/> class with the host duration preference.</summary>
    internal AgentWorkspaceProjection(IInteractionSurface surface, AgentDisplayStream? display, SessionUsageProjection? usage, ConfiguredModelCatalog? models, AgentNameCatalog names, bool markdown, bool showOperationDurations, CancellationToken cancellationToken)
    {
        _surface = surface;
        _workspace = (IAgentWorkspaceSurface)surface;
        _display = display;
        _usage = usage;
        _models = models;
        _names = new AgentNameAllocator(names);
        _markdown = markdown;
        _showOperationDurations = showOperationDurations;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _display?.Attach();
    }

    /// <summary>Gets the number of retained active/recent delegation ownership records.</summary>
    internal int RetainedDelegationCount => _accepted.Count;

    /// <inheritdoc />
    [SuppressMessage("Usage", "VSTHRD003", Justification = "The owned pump is cancelled before its completion is joined.")]
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _pump;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }

        _display?.Detach();
        _gate.Dispose();
        _stop.Dispose();
    }

    /// <summary>Replaces session ownership before accepting any subsequent output.</summary>
    internal async Task AttachAsync(SessionId session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var child in _children.Values)
            {
                _names.Release(child.Snapshot.Target);
            }

            _children.Clear();
            _tools.Clear();
            _delegationTools.Clear();
            _progress.Clear();
            _revisions.Clear();
            _accepted.Clear();
            _terminal.Clear();
            _closed.Clear();
            _closedIds.Clear();
            _pendingDisplay.Clear();
            _session = session;
            await _workspace.AttachAgentSessionAsync(session, cancellationToken);
            if (_pump.IsCompleted)
            {
                _pump = PumpAsync(_stop.Token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Routes exact child events before MAIN's semantic collector can see them.</summary>
    internal async Task<bool> ObserveAsync(IDomainEvent item, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (item.SessionId != _session)
            {
                return false;
            }

            if (item is DelegationCheckpointWritten checkpoint)
            {
                if (checkpoint.Phase == DelegationCheckpointPhase.Accepted)
                {
                    var previous = _accepted.GetValueOrDefault(checkpoint.DelegationId);
                    if (checkpoint.Generation > previous)
                    {
                        foreach (var child in _children.Values.Where(child => child.Snapshot.Target.DelegationId == checkpoint.DelegationId).ToArray())
                        {
                            var target = child.Snapshot.Target;
                            await ObserveLifecycleAsync(
                                new AgentRunLifecycleObserved(_session, checkpoint.OccurredAt, target.DelegationId, target.AssignmentId, target.RunId, child.Snapshot.Role, AgentRunStatus.Discarded, target.Generation, "Replaced by a newer generation", child.Snapshot.Revision + 1),
                                cancellationToken);
                        }

                        ForgetTargets(checkpoint.DelegationId);
                        _accepted[checkpoint.DelegationId] = checkpoint.Generation;
                        _delegationTools.Remove(checkpoint.DelegationId);
                        if (checkpoint.ToolInvocationId is { } invocation)
                        {
                            _delegationTools[checkpoint.DelegationId] = invocation;
                        }

                        _closedIds.Remove(checkpoint.DelegationId);
                    }
                }

                if (checkpoint.Phase is DelegationCheckpointPhase.Completed or DelegationCheckpointPhase.Failed or DelegationCheckpointPhase.Cancelled
                    or DelegationCheckpointPhase.ResearchJoined or DelegationCheckpointPhase.ReviewsJoined or DelegationCheckpointPhase.WorkersFrozen
                    && _accepted.GetValueOrDefault(checkpoint.DelegationId) == checkpoint.Generation
                    && _closedIds.Add(checkpoint.DelegationId))
                {
                    _closed.Enqueue((checkpoint.DelegationId, checkpoint.Generation));
                }

                TrimClosed();
                return false;
            }

            await DrainAsync(cancellationToken);
            if (item is AgentRunLifecycleObserved lifecycle)
            {
                await ObserveLifecycleAsync(lifecycle, cancellationToken);
                return true;
            }

            if (item is ToolInvocationStarted started && _children.ContainsKey(started.RunId))
            {
                _tools[started.ToolInvocationId] = started;
                await FlushAsync(_children[started.RunId], cancellationToken);
                var child = _children[started.RunId];
                child.ToolActivities[started.ToolInvocationId] = InteractionPresentationFormatter.CreateToolActivity(started, TimeProvider.System, _showOperationDurations);
                child.Snapshot = child.Snapshot with { ToolActivities = child.ToolActivities.Values.ToArray() };
                await _workspace.PresentAgentAsync(child.Snapshot, cancellationToken);
                return true;
            }

            if (item is ToolInvocationCompleted completed)
            {
                var matched = _tools.Remove(completed.ToolInvocationId, out var start);
                if (start is not null && _children.TryGetValue(start.RunId, out var child))
                {
                    var transcript = new ConversationTranscript(string.Empty, _showOperationDurations);
                    transcript.Apply(start);
                    transcript.Apply(completed);
                    var segments = new List<PresentationTextSegment>();
                    InteractionEventSegments.Append(segments, completed, transcript.Text);
                    await _surface.PresentAsync(new PresentationBatch([new PresentationTextItem(segments)]) { Target = child.Snapshot.Target }, cancellationToken);
                    child.ToolActivities.Remove(completed.ToolInvocationId);
                    child.Snapshot = child.Snapshot with { ToolActivities = child.ToolActivities.Values.ToArray() };
                    await _workspace.PresentAgentAsync(child.Snapshot, cancellationToken);
                    return true;
                }

                return matched || (completed.RunId != default && _usage?.IsChildRun(_session, completed.RunId) == true);
            }

            return item is ToolInvocationStarted late && _usage?.IsChildRun(_session, late.RunId) == true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Refreshes effective request/usage metadata without modifying session accounting.</summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DrainAsync(cancellationToken);
            foreach (var child in _children.Values)
            {
                var request = _usage?.GetRequestStatus(_session, child.Snapshot.Target.RunId);
                var profile = request is null ? null : _models?.Profiles.FirstOrDefault(profile => profile.Id == request.ProfileId);
                var snapshot = child.Snapshot with
                {
                    Model = request is null ? null : profile?.Name ?? "Model unavailable",
                    ProviderName = profile?.ProviderName ?? profile?.Provider,
                    Reasoning = request?.Reasoning ?? default,
                    ContextTokens = request?.ContextTokens,
                    ContextLimit = request?.ContextLimit,
                    Usage = _usage?.GetOwnerSnapshot(_session, child.Snapshot.Target.RunId),
                };
                if (snapshot != child.Snapshot)
                {
                    child.Snapshot = snapshot;
                    await _workspace.PresentAgentAsync(snapshot, cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Captures current progress for exactly one tool; completion releases its live correlation.</summary>
    internal async Task<IReadOnlyList<PresentationTextSegment>> GetToolProgressAsync(ToolInvocationId invocation, bool complete, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var delegations = _delegationTools.Where(pair => pair.Value == invocation).Select(pair => pair.Key).ToHashSet();
            var entries = _progress.Where(pair => delegations.Contains(pair.Key.DelegationId)).Select(pair => pair.Value).ToArray();
            if (complete)
            {
                foreach (var delegation in delegations)
                {
                    _delegationTools.Remove(delegation);
                }
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RefreshAsync(cancellationToken);
        }
    }

    private async Task ObserveLifecycleAsync(AgentRunLifecycleObserved lifecycle, CancellationToken cancellationToken)
    {
        var target = new AgentPresentationTarget(_session, lifecycle.DelegationId, lifecycle.AssignmentId, lifecycle.ChildRunId, lifecycle.Generation);
        if (_revisions.TryGetValue(target, out var revision) && lifecycle.Revision <= revision)
        {
            return;
        }

        if (!_accepted.TryGetValue(lifecycle.DelegationId, out var generation) || lifecycle.Generation != generation)
        {
            return;
        }

        _revisions[target] = lifecycle.Revision;
        var terminal = lifecycle.Status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled or AgentRunStatus.Discarded;
        if (!_children.TryGetValue(lifecycle.ChildRunId, out var state))
        {
            if (terminal)
            {
                var terminalSnapshot = _terminal.GetValueOrDefault(target)
                    ?? new AgentPresentationSnapshot(target, _names.Allocate(target, lifecycle.Role), lifecycle.Role, lifecycle.Status, lifecycle.Revision);
                _names.Release(target);
                _terminal[target] = terminalSnapshot with { State = lifecycle.Status, Revision = lifecycle.Revision };
                await PresentOutcomeAsync(_terminal[target], lifecycle.Reason, revision > 0, cancellationToken);
                _usage?.RetireRequestStatus(_session, lifecycle.ChildRunId);
                return;
            }

            if (revision > 0 || _closedIds.Contains(lifecycle.DelegationId))
            {
                return;
            }

            var snapshot = new AgentPresentationSnapshot(target, _names.Allocate(target, lifecycle.Role), lifecycle.Role, lifecycle.Status, lifecycle.Revision);
            state = new ChildState(snapshot, _markdown);
            _children.Add(lifecycle.ChildRunId, state);
            _usage?.RegisterChild(_session, lifecycle.ChildRunId);
        }
        else if (state.Snapshot.Target != target)
        {
            return;
        }

        state.Snapshot = state.Snapshot with { State = lifecycle.Status, Revision = lifecycle.Revision };
        if (terminal)
        {
            await FlushAsync(state, cancellationToken);
            state.ToolActivities.Clear();
            state.Snapshot = state.Snapshot with { ToolActivities = [] };
            _terminal[target] = state.Snapshot;
            await PresentOutcomeAsync(state.Snapshot, lifecycle.Reason, false, cancellationToken);
            _children.Remove(lifecycle.ChildRunId);
            _names.Release(target);
            _usage?.RetireRequestStatus(_session, lifecycle.ChildRunId);
            foreach (var key in _tools.Where(pair => pair.Value.RunId == lifecycle.ChildRunId).Select(pair => pair.Key).ToArray())
            {
                _tools.Remove(key);
            }
        }

        if (!terminal)
        {
            await PresentOutcomeAsync(state.Snapshot, lifecycle.Reason, false, cancellationToken);
        }

        await _workspace.PresentAgentAsync(state.Snapshot, cancellationToken);
    }

    private Task PresentOutcomeAsync(AgentPresentationSnapshot snapshot, string reason, bool correction, CancellationToken cancellationToken)
    {
        var safe = TerminalControlEncoder.Encode(reason);
        safe = safe.Replace('\r', ' ').Replace('\n', ' ');
        safe = safe.Length > 240 ? safe[..240] + "…" : safe;
        var entry = new PresentationTextSegment(
            $"{snapshot.Label}: {snapshot.State}{(safe.Length > 0 ? " — " + safe : string.Empty)}",
            snapshot.State is AgentRunStatus.Failed or AgentRunStatus.Discarded ? PresentationTextRole.Error : PresentationTextRole.Status);
        var unchanged = _progress.GetValueOrDefault(snapshot.Target) == entry;
        _progress[snapshot.Target] = entry;
        if (_delegationTools.ContainsKey(snapshot.Target.DelegationId) || !snapshot.IsTerminal || unchanged)
        {
            return Task.CompletedTask;
        }

        var text = $"{(correction ? "Updated outcome: " : string.Empty)}{snapshot.Label}: {snapshot.State}{(safe.Length > 0 ? " — " + safe : string.Empty)}\n";
        return _surface.PresentAsync(new PresentationBatch([new PresentationTextItem([new(text, snapshot.State is AgentRunStatus.Failed or AgentRunStatus.Discarded ? PresentationTextRole.Error : PresentationTextRole.Status)])]), cancellationToken);
    }

    private void ForgetTargets(DelegationId delegation)
    {
        foreach (var target in _revisions.Keys.Where(target => target.DelegationId == delegation).ToArray())
        {
            _revisions.Remove(target);
            _terminal.Remove(target);
            _progress.Remove(target);
        }
    }

    private void TrimClosed()
    {
        var attempts = _closed.Count;
        while (_closed.Count > 64 && attempts-- > 0)
        {
            var entry = _closed.Dequeue();
            var delegation = entry.Id;
            if (!_closedIds.Contains(delegation) || _accepted.GetValueOrDefault(delegation) != entry.Generation)
            {
                continue;
            }

            if (_children.Values.Any(child => child.Snapshot.Target.DelegationId == delegation))
            {
                _closed.Enqueue(entry);
                continue;
            }

            _closedIds.Remove(delegation);
            _accepted.Remove(delegation);
            _delegationTools.Remove(delegation);
            ForgetTargets(delegation);
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        if (_display is null)
        {
            return;
        }

        var incoming = _pendingDisplay.Concat(_display.Drain(out var omitted)).ToArray();
        _pendingDisplay.Clear();
        foreach (var item in incoming)
        {
            if (item.SessionId != _session)
            {
                continue;
            }

            if (!_children.TryGetValue(item.RunId, out var child))
            {
                if (!_revisions.Keys.Any(target => target.RunId == item.RunId))
                {
                    if (_pendingDisplay.Count < 256)
                    {
                        _pendingDisplay.Add(item);
                    }
                    else
                    {
                        omitted++;
                    }
                }

                continue;
            }

            if (item.IsReasoning)
            {
                if (_display.IncludeReasoningText)
                {
                    await _surface.PresentAsync(
                        new PresentationBatch([new PresentationTextItem([
                        new(TerminalControlEncoder.Encode(item.Text), PresentationTextRole.Reasoning),
                    ])]) { Target = child.Snapshot.Target },
                        cancellationToken);
                }

                continue;
            }

            if (item.ResponseId != default && child.ResponseId != item.ResponseId)
            {
                await FlushAsync(child, cancellationToken);
                child.InFence = false;
                child.ResponseId = item.ResponseId;
            }

            var immediate = child.Answer.Append(item.Text);
            child.Characters += item.Text.Length;
            if (immediate is not null)
            {
                await _surface.PresentAsync(new PresentationBatch([immediate]) { Target = child.Snapshot.Target }, cancellationToken);
            }

            // Complete paragraphs appear while the provider is running. Fenced blocks stay together.
            foreach (var line in item.Text.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
                {
                    child.InFence = !child.InFence;
                }
            }

            if (item.CompletesResponse || child.Characters >= 16384 || (!child.InFence && item.Text == "\n"))
            {
                await FlushAsync(child, cancellationToken);
            }
        }

        if (omitted > 0)
        {
            await _surface.PresentAsync(
                new PresentationBatch([new PresentationTextItem([
                new($"[Child display queue omitted {omitted} fragments; execution and accounting are unaffected]\n", PresentationTextRole.Warning),
            ])]),
                cancellationToken);
        }
    }

    private async Task FlushAsync(ChildState state, CancellationToken cancellationToken)
    {
        var output = state.Answer.Flush(cancellationToken);
        state.Characters = 0;
        if (output is not null)
        {
            await _surface.PresentAsync(new PresentationBatch([output]) { Target = state.Snapshot.Target }, cancellationToken);
        }
    }

    private sealed class ChildState
    {
        internal ChildState(AgentPresentationSnapshot snapshot, bool markdown)
        {
            Snapshot = snapshot;
            Answer = new ModelAnswerCollector(markdown);
        }

        internal AgentPresentationSnapshot Snapshot { get; set; }

        internal ModelAnswerCollector Answer { get; }

        internal int Characters { get; set; }

        internal bool InFence { get; set; }

        internal Guid ResponseId { get; set; }

        internal Dictionary<ToolInvocationId, InteractionActivity> ToolActivities { get; } = [];
    }
}
