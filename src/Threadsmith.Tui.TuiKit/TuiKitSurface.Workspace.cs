namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Widgets;

/// <summary>Owns workspace projection and routes input through one measured layout.</summary>
internal sealed partial class TuiKitSurface
{
    private bool _startupBlocked = true;
    private string? _mouseOwner;

    /// <inheritdoc />
    public Task AttachAgentSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () =>
        {
            _agents.Attach(sessionId);
            _toolActivities = [];
            _inputEpoch++;
            RefreshDiscovery();
        },
            cancellationToken);

    /// <inheritdoc />
    public Task PresentAgentAsync(AgentPresentationSnapshot snapshot, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () =>
        {
            if (_agents.Update(snapshot))
            {
                AgentSelectionChanged();
            }
        },
            cancellationToken);

    private static Rect ComposerScreenRect(Rect region)
    {
        var content = WorkspaceLayout.ComposerContent(new Size(region.Width, region.Height));
        return new Rect(region.Left + content.Left, region.Top + content.Top, content.Width, content.Height);
    }

    private static Rect OutputScreenRect(Rect region)
    {
        var content = WorkspaceLayout.OutputContent(new Size(region.Width, region.Height));
        return new Rect(region.Left + content.Left, region.Top + content.Top, content.Width, content.Height);
    }

    private void SelectAgent(AgentView view)
    {
        if (_app.Modals.IsActive || _startupBlocked)
        {
            return;
        }

        if (_agents.Select(view))
        {
            AgentSelectionChanged();
        }
    }

    private void AgentSelectionChanged()
    {
        _inputEpoch++;
        _mouseOwner = null;
        ClosePalette();
        _composer.OnFocusChanged(false);
        _app.Focus("transcript");
        RefreshDiscovery();
    }

    private CellStyle ResolveOutputStyle(PresentationTextRole role) => ResolvePaneStyle(role, PresentationTextRole.OutputStreamPaneRole);

    private CellStyle ResolvePaneStyle(PresentationTextRole role, PresentationTextRole pane)
    {
        var style = ResolveStyle(role);
        return style.Background.Kind == ColorKind.Default ? style.WithBackground(ResolveStyle(pane).Background) : style;
    }

    private void RouteMouse(MouseEvent mouse)
    {
        if (_app.Modals.IsActive || _startupBlocked || !_app.MouseCaptureEnabled || !ModalFrame.Fits(_backend.Size))
        {
            _mouseOwner = null;
            return;
        }

        if (mouse.Kind == MouseEventKind.Press)
        {
            _mouseOwner = null;
        }

        foreach (var id in new[] { "tabs", "composer", "transcript" })
        {
            if (_mouseOwner is { } owner && owner != id && mouse.Kind is MouseEventKind.Move or MouseEventKind.Release)
            {
                continue;
            }

            var region = _app.Layout?.FindById(id)?.ContentRect(_backend.Size);
            if (region is not { } rect || mouse.X < rect.Left || mouse.X >= rect.Right || mouse.Y < rect.Top || mouse.Y >= rect.Bottom)
            {
                continue;
            }

            if (mouse.Kind == MouseEventKind.Press && id != "tabs")
            {
                _app.Focus(id == "composer" && !CanEdit ? "transcript" : id);
            }

            if (mouse.Kind == MouseEventKind.Press)
            {
                _mouseOwner = id;
            }
            else if (mouse.Kind == MouseEventKind.Release)
            {
                _mouseOwner = null;
            }

            var local = WorkspaceLayout.Translate(mouse, rect);
            switch (id)
            {
                case "tabs":
                    _tabs.HandleMouse(local);
                    break;
                case "composer":
                    _composerPane.HandleMouse(local);
                    break;
                default:
                    _outputPane.HandleMouse(local);
                    break;
            }

            return;
        }
    }

    private AgentHeaderState GetAgentHeaderState()
    {
        var child = _agents.Selected.Snapshot;
        var usage = (child is null ? _status?.AgentUsage : child.Usage) ?? new SessionUsageSnapshot(0, 0, false, HasObservation: false);
        var model = (child is null ? _status?.Model : child.Model) ?? "Model unknown";
        var provider = child is null ? _status?.ProviderName : child.ProviderName;
        var reasoning = child?.Reasoning ?? _status?.AgentRequest?.Reasoning ?? _status?.Reasoning ?? default;
        var context = child is not null ? child.ContextTokens : _status?.AgentRequest is { } request ? request.ContextTokens : _status?.ContextTokens;
        var limit = child is not null ? child.ContextLimit : _status?.AgentRequest is { } admitted ? admitted.ContextLimit : _status?.ContextLimit;
        return new AgentHeaderState(child?.Label ?? "MAIN", model, provider, reasoning, context, limit, usage, _status?.IsPostResume == true);
    }

    private sealed class OutputPane : IWidget, IFocusable, IMouseAware
    {
        private readonly TuiKitSurface _owner;
        private readonly AgentHeader _header = new();
        private readonly CachedTextRun _activity = new();

        internal OutputPane(TuiKitSurface owner)
        {
            _owner = owner;
        }

        public Size Measure(Size available) => available;

        public bool HandleKey(KeyEvent key) => _owner.SelectedTranscript.HandleKey(key);

        public bool HandleMouse(MouseEvent mouse)
        {
            var region = _owner._app.Layout?.FindById("transcript")?.ContentRect(_owner._backend.Size) ?? default;
            var content = WorkspaceLayout.OutputContent(new Size(region.Width, region.Height));
            if (mouse.Y < content.Top || mouse.Y >= content.Bottom || mouse.X < content.Left || mouse.X >= content.Right)
            {
                return true;
            }

            return _owner.SelectedTranscript.HandleMouse(WorkspaceLayout.Translate(mouse, content));
        }

        public void Render(ISurface surface)
        {
            WorkspaceLayout.DrawFrame(surface, _owner.ResolveStyle(PresentationTextRole.OutputStreamPaneRole));
            if (surface is not BufferSurface buffer || surface.Size.Height < 5 || !ModalFrame.Fits(_owner._backend.Size))
            {
                return;
            }

            var header = buffer.CreateView(WorkspaceLayout.OutputHeader(surface.Size));
            var headerStyle = _owner.ResolvePaneStyle(PresentationTextRole.AgentStatusPaneRole, PresentationTextRole.OutputStreamPaneRole);
            header.Fill(new Rect(0, 0, header.Size.Width, header.Size.Height), Cell.Blank(headerStyle));
            _header.Render(header, _owner.GetAgentHeaderState(), headerStyle);
            var content = buffer.CreateView(WorkspaceLayout.OutputContent(surface.Size));
            _owner.SelectedTranscript.ToolActivities = _owner._agents.Selected.Snapshot?.ToolActivities ?? _owner._toolActivities;
            _owner.SelectedTranscript.Render(content);
            NotificationOverlay.Render(content, _owner._app.Notifications, _owner._app.NowMilliseconds, _owner.ResolveOutputStyle);
            _activity.Draw(buffer.CreateView(WorkspaceLayout.OutputNotice(surface.Size)), 0, 0, _owner.ActivityText(), _owner.ResolveOutputStyle(PresentationTextRole.Status));
        }
    }
}
