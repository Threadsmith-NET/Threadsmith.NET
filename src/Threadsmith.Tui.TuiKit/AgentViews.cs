namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Presentation;

/// <summary>Owns retained view state independently of tab widget reconstruction.</summary>
internal sealed class AgentViews
{
    /// <summary>Combined child source and projected-text budget (styles/cells have their own per-view bounds).</summary>
    internal const int ChildTextBudget = 4 * 1024 * 1024;

    private readonly List<AgentView> _ordered = [];
    private readonly Dictionary<AgentPresentationTarget, AgentView> _children = [];
    private readonly Func<PresentationTextRole, TUIKit.CellStyle> _resolveStyle;
    private SessionId _session;

    /// <summary>Initializes a new instance of the <see cref="AgentViews"/> class.</summary>
    internal AgentViews(Func<PresentationTextRole, TUIKit.CellStyle> resolveStyle)
    {
        _resolveStyle = resolveStyle;
        Main = new AgentView(null, new TranscriptView { ResolveStyle = resolveStyle });
        Selected = Main;
        _ordered.Add(Main);
    }

    /// <summary>Gets permanent MAIN.</summary>
    internal AgentView Main { get; }

    /// <summary>Gets the local selected view.</summary>
    internal AgentView Selected { get; private set; }

    /// <summary>Gets stable creation order.</summary>
    internal IReadOnlyList<AgentView> Ordered => _ordered;

    /// <summary>Gets the structural/selection revision used to rebuild lightweight headers.</summary>
    internal long Revision { get; private set; }

    /// <summary>Switches sessions without letting old output establish new membership.</summary>
    internal void Attach(SessionId session)
    {
        _session = session;
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
        if (snapshot.IsTerminal)
        {
            if (_children.Remove(snapshot.Target, out var removed))
            {
                _ordered.Remove(removed);
                if (ReferenceEquals(Selected, removed))
                {
                    Selected = Main;
                }

                Revision++;
                Rebalance();
            }
        }
        else if (_children.TryGetValue(snapshot.Target, out var view))
        {
            if (snapshot.Revision >= view.Snapshot?.Revision)
            {
                view.Snapshot = snapshot;
            }
        }
        else
        {
            view = new AgentView(snapshot, new TranscriptView { ResolveStyle = _resolveStyle });
            _children.Add(snapshot.Target, view);
            _ordered.Add(view);
            Revision++;
            Rebalance();
        }

        return !ReferenceEquals(previous, Selected);
    }

    /// <summary>Appends only to an already accepted destination, never to the selected tab implicitly.</summary>
    internal void Present(PresentationBatch batch)
    {
        var view = batch.Target is null ? Main : _children.GetValueOrDefault(batch.Target);
        view?.Transcript.Present(batch);
    }

    /// <summary>Selects a stable existing view.</summary>
    internal bool Select(AgentView view)
    {
        if (!_ordered.Contains(view) || ReferenceEquals(Selected, view))
        {
            return false;
        }

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

    private void Rebalance()
    {
        if (_children.Count == 0)
        {
            return;
        }

        foreach (var view in _children.Values)
        {
            view.Transcript.SetRetentionBudget(
                Math.Min(TranscriptView.ByteLimit, ChildTextBudget / 2 / _children.Count),
                Math.Min(TranscriptView.LineLimit, 8192 / _children.Count));
        }
    }
}

/// <summary>Retained selection, scroll, output, and status for one agent.</summary>
internal sealed class AgentView
{
    /// <summary>Initializes a new instance of the <see cref="AgentView"/> class.</summary>
    internal AgentView(AgentPresentationSnapshot? snapshot, TranscriptView transcript)
    {
        Snapshot = snapshot;
        Transcript = transcript;
    }

    /// <summary>Gets or sets the latest immutable child status; null identifies MAIN.</summary>
    internal AgentPresentationSnapshot? Snapshot { get; set; }

    /// <summary>Gets retained output and its independent navigation.</summary>
    internal TranscriptView Transcript { get; }

    /// <summary>Gets its human-readable label.</summary>
    internal string Label => Snapshot?.Label ?? "MAIN";
}
