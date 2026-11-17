namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;

/// <summary>Owns retained view state independently of tab widget reconstruction.</summary>
internal sealed class AgentViews
{
    /// <summary>Combined child source and projected-text budget (styles/cells have their own per-view bounds).</summary>
    internal const int ChildTextBudget = 4 * 1024 * 1024;

    private readonly List<AgentView> _ordered = [];
    private readonly Dictionary<AgentPresentationTarget, AgentView> _children = [];
    private readonly Func<PresentationTextRole, TUIKit.CellStyle> _resolveStyle;
    private readonly TuiResourceLimits _limits;
    private readonly Action<ToolSourceDetail>? _sourceRequested;
    private readonly Func<bool>? _canCopy;
    private readonly Func<string, bool>? _copy;
    private readonly Func<bool>? _ascii;
    private SessionId _session;
    private long _childOrdinal;

    /// <summary>Initializes a new instance of the <see cref="AgentViews"/> class.</summary>
    internal AgentViews(Func<PresentationTextRole, TUIKit.CellStyle> resolveStyle, TuiResourceLimits? limits = null, Action<ToolSourceDetail>? sourceRequested = null, Func<bool>? canCopy = null, Func<string, bool>? copy = null, Func<bool>? ascii = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
        _resolveStyle = resolveStyle;
        _sourceRequested = sourceRequested;
        _canCopy = canCopy;
        _copy = copy;
        _ascii = ascii;
        Main = new AgentView(null, new TranscriptView(_limits) { ResolveStyle = resolveStyle, SourceRequested = sourceRequested, ClipboardAvailable = canCopy, CopyRequested = copy, UseAsciiBorders = ascii });
        Selected = Main;
        _ordered.Add(Main);
    }

    /// <summary>Gets permanent MAIN.</summary>
    internal AgentView Main { get; }

    /// <summary>Gets the local selected view.</summary>
    internal AgentView Selected { get; private set; }

    /// <summary>Gets MAIN followed by children in newest-first order.</summary>
    internal IReadOnlyList<AgentView> Ordered => _ordered;

    /// <summary>Gets the structural/selection revision used to rebuild lightweight headers.</summary>
    internal long Revision { get; private set; }

    /// <summary>Switches sessions without letting old output establish new membership.</summary>
    internal void Attach(SessionId session)
    {
        foreach (var view in _ordered)
        {
            if (ReferenceEquals(view, Main))
            {
                view.Transcript.RetireBlockActions();
            }
            else
            {
                view.Transcript.Present(new PresentationBatch([]) { ReplaceOutput = true });
            }
        }

        _session = session;
        _childOrdinal = 0;
        _children.Clear();
        _ordered.RemoveRange(1, _ordered.Count - 1);
        Selected = Main;
        Revision++;
    }

    /// <summary>Updates accepted membership; returns whether selection changed.</summary>
    internal bool Update(AgentPresentationSnapshot snapshot)
    {
        if (snapshot.Target.SessionId != _session)
        {
            return false;
        }

        var previous = Selected;
        if (_children.TryGetValue(snapshot.Target, out var view))
        {
            if (snapshot.Revision >= view.Snapshot?.Revision
                && (view.Snapshot?.IsTerminal != true || snapshot.IsTerminal))
            {
                if (snapshot.IsTerminal && view.Snapshot?.IsTerminal != true)
                {
                    Revision++;
                }

                view.Snapshot = snapshot;
                if (snapshot.IsTerminal)
                {
                    TrimStoppedViews();
                }
            }
        }
        else if (!snapshot.IsTerminal)
        {
            view = new AgentView(snapshot, new TranscriptView(_limits) { ResolveStyle = _resolveStyle, SourceRequested = _sourceRequested, ClipboardAvailable = _canCopy, CopyRequested = _copy, UseAsciiBorders = _ascii }, ++_childOrdinal);
            _children.Add(snapshot.Target, view);
            _ordered.Insert(1, view);
            Revision++;
            Rebalance();
        }

        return !ReferenceEquals(previous, Selected);
    }

    /// <summary>Appends only to an already accepted destination, never to the selected tab implicitly.</summary>
    internal void Present(PresentationBatch batch)
    {
        var view = batch.Target is null ? Main : _children.GetValueOrDefault(batch.Target);
        if (view?.Snapshot?.IsTerminal != true)
        {
            view?.Transcript.Present(batch);
        }
    }

    /// <summary>Closes only a retained stopped view, returning selection to MAIN when necessary.</summary>
    internal bool Close(AgentView view)
    {
        if (view.Snapshot?.IsTerminal != true || !_ordered.Contains(view))
        {
            return false;
        }

        RemoveStoppedView(view);
        Revision++;
        Rebalance();
        return true;
    }

    /// <summary>Closes stopped nonfailed children for the current session; returns whether selection changed.</summary>
    internal bool BeginUserTurn(SessionId session)
    {
        if (session != _session)
        {
            return false;
        }

        var stopped = _ordered
            .Where(view => view.Snapshot is { IsTerminal: true, State: not AgentRunStatus.Failed })
            .ToArray();
        if (stopped.Length == 0)
        {
            return false;
        }

        var previous = Selected;
        foreach (var view in stopped)
        {
            RemoveStoppedView(view);
        }

        Revision++;
        Rebalance();
        return !ReferenceEquals(previous, Selected);
    }

    /// <summary>Selects a stable existing view.</summary>
    internal bool Select(AgentView view)
    {
        if (!_ordered.Contains(view) || ReferenceEquals(Selected, view))
        {
            return false;
        }

        Selected.Transcript.InvalidateBlockActions();
        Selected = view;
        Revision++;
        return true;
    }

    /// <summary>Cycles over the complete list including overflowed tabs.</summary>
    internal bool Cycle(int direction)
    {
        var index = _ordered.IndexOf(Selected);
        return Select(_ordered[(index + direction + _ordered.Count) % _ordered.Count]);
    }

    private void RemoveStoppedView(AgentView view)
    {
        view.Transcript.Present(new PresentationBatch([]) { ReplaceOutput = true });
        if (view.Snapshot is { } snapshot)
        {
            _children.Remove(snapshot.Target);
        }

        _ordered.Remove(view);
        if (ReferenceEquals(Selected, view))
        {
            Selected = Main;
        }
    }

    private void TrimStoppedViews()
    {
        var stoppedDelegations = _ordered
            .Where(view => view.Snapshot?.IsTerminal == true)
            .OrderBy(view => view.Ordinal)
            .Select(view => (view.Snapshot?.Target.DelegationId, view.Snapshot?.Target.Generation))
            .Distinct().ToArray();
        var omitted = stoppedDelegations.Length - _limits.MaximumRetainedDelegations;
        if (omitted <= 0)
        {
            return;
        }

        var expired = stoppedDelegations.Take(omitted).ToHashSet();
        foreach (var view in _ordered.Where(view => view.Snapshot is { IsTerminal: true } snapshot
            && expired.Contains((snapshot.Target.DelegationId, snapshot.Target.Generation))).ToArray())
        {
            RemoveStoppedView(view);
        }

        Main.Transcript.Present(new PresentationBatch([new PresentationTextItem([
            new("[Older stopped child views omitted; session history is authoritative]\n", PresentationTextRole.Warning),
        ])]));
        Revision++;
        Rebalance();
    }

    private void Rebalance()
    {
        if (_children.Count == 0)
        {
            return;
        }

        foreach (var view in _children.Values)
        {
            view.Transcript.SetRetentionBudget(
                Math.Min(_limits.MaximumTranscriptBytes, _limits.MaximumChildTranscriptBytes / 2 / _children.Count),
                Math.Min(_limits.MaximumTranscriptLines, _limits.MaximumChildTranscriptLines / _children.Count));
        }
    }
}

/// <summary>Retained selection, scroll, output, and status for one agent.</summary>
internal sealed class AgentView
{
    /// <summary>Initializes a new instance of the <see cref="AgentView"/> class.</summary>
    internal AgentView(AgentPresentationSnapshot? snapshot, TranscriptView transcript, long ordinal = 0)
    {
        Snapshot = snapshot;
        Transcript = transcript;
        Ordinal = ordinal;
    }

    /// <summary>Gets or sets the latest immutable child status; null identifies MAIN.</summary>
    internal AgentPresentationSnapshot? Snapshot { get; set; }

    /// <summary>Gets retained output and its independent navigation.</summary>
    internal TranscriptView Transcript { get; }

    /// <summary>Gets the stable session-local sequence used to distinguish stopped views.</summary>
    internal long Ordinal { get; }

    /// <summary>Gets its human-readable label without renaming the underlying agent.</summary>
    internal string Label => Snapshot is { } snapshot
        ? snapshot.Label + (snapshot.IsTerminal ? $" [stopped #{Ordinal}]" : string.Empty)
        : "MAIN";
}
