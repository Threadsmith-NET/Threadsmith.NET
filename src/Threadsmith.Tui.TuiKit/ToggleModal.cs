namespace Threadsmith.Tui.TuiKit;

using System.Threading.Channels;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Modals;
using TUIKit.Widgets;

/// <summary>Keyboard-only CheckTree over stable IDs; checkboxes reflect acknowledged host state.</summary>
internal sealed class ToggleModal : Modal
{
    private readonly InteractionToggleRequest _request;
    private readonly Dictionary<string, InteractionToggleOption> _options;
    private readonly Dictionary<string, string[]> _groups;
    private readonly ComposerBuffer _filter;
    private readonly TuiResourceLimits _limits;
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.Ordinal);
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly Action _cancel;
    private readonly Action _toggleMouse;
    private readonly CachedTextRun _text = new();
    private readonly Channel<(string Id, bool Enabled, bool Actions)> _changes = Channel.CreateBounded<(string, bool, bool)>(1);
    private CheckTree<string> _tree;
    private TranscriptView? _details;
    private string? _selected;
    private string _notice = "Arrows navigate; Space applies now; Esc closes";
    private bool _busy;
    private bool _fits = true;

    /// <summary>Initializes a new instance of the <see cref="ToggleModal"/> class.</summary>
    internal ToggleModal(InteractionToggleRequest request, Func<PresentationTextRole, CellStyle> style, Action cancel, Action toggleMouse, TuiResourceLimits? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
        _filter = new ComposerBuffer(_limits);
        _request = request;
        _style = style;
        _cancel = cancel;
        _toggleMouse = toggleMouse;
        _options = request.Options.ToDictionary(option => "item:" + option.Id, StringComparer.Ordinal);
        _groups = request.Options.GroupBy(option => option.Group).ToDictionary(group => "group:" + group.Key, group => group.Select(option => "item:" + option.Id).ToArray(), StringComparer.Ordinal);
        _tree = new CheckTree<string>(
            _groups.Keys.ToArray(),
            id => _groups.GetValueOrDefault(id) ?? [],
            id => _options.TryGetValue(id, out var option) ? (option.Locked ? "[locked] " : string.Empty) + TranscriptView.Safe(option.Label) : TranscriptView.Safe(id[6..]),
            id => _groups.ContainsKey(id),
            StringComparer.Ordinal);
        foreach (var group in _groups.Keys)
        {
            _tree.Expand(group);
        }

        ReconcileTree();
    }

    /// <summary>Gets or sets the explicit selected-detail clipboard callback.</summary>
    internal Func<string, bool>? CopyRequested { get; set; }

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl)
        {
            if (_details?.SelectedText() is { Length: > 0 } selection)
            {
                CopyRequested?.Invoke(selection);
            }
            else
            {
                _cancel();
            }
        }
        else if (key.Code == KeyCode.F12)
        {
            _toggleMouse();
        }
        else if (key.Code is KeyCode.Escape or KeyCode.Enter)
        {
            if (CancelOperation is { } cancelOperation)
            {
                cancelOperation();
                _notice = "Cancelling authentication...";
            }
            else
            {
                RequestClose(null);
            }
        }
        else if (_fits && !_busy && _details is null && SupportsActions && key.Code == KeyCode.F3)
        {
            var selected = _options.GetValueOrDefault(_tree.SelectedNode);
            if (selected?.Actions.Count > 0)
            {
                _busy = _changes.Writer.TryWrite((_tree.SelectedNode, false, true));
            }
            else
            {
                _notice = "Select an individual item with available actions.";
            }
        }
        else if (_fits && !_busy && key.Code == KeyCode.F2)
        {
            if (_details is null)
            {
                _details = new TranscriptView(_limits) { ResolveStyle = _style };
                var selected = _options.GetValueOrDefault(_tree.SelectedNode);
                var label = selected is null ? _tree.SelectedNode[6..] : selected.Label + "\n" + selected.Reason;
                _details.Present(new PresentationBatch([new PresentationTextItem([new(label, PresentationTextRole.Default)])]));
                _details.HandleKey(KeyEvent.Special(KeyCode.Home));
            }
            else
            {
                _details = null;
            }
        }
        else if (_details is not null)
        {
            if (key.Code == KeyCode.F6)
            {
                CopyRequested?.Invoke(_details.SelectedText() ?? string.Empty);
            }
            else
            {
                _details.HandleKey(key);
            }
        }
        else if (_fits && !_busy && key.Code == KeyCode.Character && key.Rune == ' ' && key.Modifiers == KeyModifiers.None)
        {
            var id = _tree.SelectedNode;
            if (Members(id).Count == 0)
            {
                _notice = _options.GetValueOrDefault(id)?.Reason ?? "This setting is locked by host policy.";
            }
            else
            {
                _busy = _changes.Writer.TryWrite((id, !Members(id).All(option => option.Enabled), false));
                _notice = "Applying host checks…";
            }
        }
        else if (key.Code == KeyCode.Character && key.Rune == ' ')
        {
            // Stock CheckTree ignores modifiers; only the host request above may change a checkbox.
        }
        else if (_fits && !_busy)
        {
            if (key.Code == KeyCode.Backspace)
            {
                _filter.Delete(true);
                Filter();
            }
            else if (key.Code == KeyCode.Character && key.Modifiers == KeyModifiers.None && _filter.Text.Length < _limits.MaximumFilterCharacters)
            {
                _filter.Insert(char.ConvertFromUtf32(key.Rune));
                Filter();
            }
            else
            {
                _tree.HandleKey(key);
                _selected = _tree.SelectedNode;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text)
    {
        if (_fits && !_busy && _details is null && text.Length <= _limits.MaximumFilterCharacters - _filter.Text.Length)
        {
            _filter.Insert(text.ReplaceLineEndings(" "));
            Filter();
        }

        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        _fits = ModalFrame.Fits(surface.Size);
        var frame = ModalFrame.Create(surface, _style(PresentationTextRole.Default));
        if (frame is null)
        {
            return;
        }

        _tree.HighlightStyle = _style(PresentationTextRole.SelectionHighlight).WithAttribute(CellAttributes.Reverse, true);
        _tree.RowStyle = _ => _style(PresentationTextRole.Default);
        _text.Draw(frame, 0, 0, _request.Title + " — keyboard only", _style(PresentationTextRole.SelectionPrompt));
        _text.Draw(frame, 0, 2, "Filter: " + _filter.Text + " | F2 details" + (SupportsActions ? "; F3 actions" : string.Empty), _style(PresentationTextRole.Muted));
        var content = frame.CreateView(new Rect(0, 3, frame.Size.Width, Math.Max(1, frame.Size.Height - 5)));
        if (_details is not null)
        {
            _details.Render(content);
        }
        else
        {
            _tree.Render(content);
        }

        _text.Draw(frame, 0, frame.Size.Height - 2, _notice, _style(PresentationTextRole.Status));
        var selected = _options.GetValueOrDefault(_tree.SelectedNode);
        _text.Draw(frame, 0, frame.Size.Height - 1, selected?.Reason ?? "Space applies immediately; closing keeps changes", _style(PresentationTextRole.Muted));
    }

    /// <summary>Gets or sets cancellation of the current individual action.</summary>
    internal Action? CancelOperation { get; set; }

    /// <summary>Gets or sets whether the caller can execute individual actions.</summary>
    internal bool SupportsActions { get; set; }

    /// <summary>Gets the selected immutable item for individual action dispatch.</summary>
    internal InteractionToggleOption? GetOption(string key) => _options.GetValueOrDefault(key);

    /// <summary>Shows progress while the host owns a cancellable action.</summary>
    internal void BeginAction(string label, Action cancel)
    {
        CancelOperation = cancel;
        _notice = label + " - waiting for authentication; Esc cancels";
    }

    /// <summary>Gets serialized pending host requests.</summary>
    internal ChannelReader<(string Id, bool Enabled, bool Actions)> Changes => _changes.Reader;

    /// <summary>Gets the exact eligible members for the current immutable group or leaf.</summary>
    internal IReadOnlyList<InteractionToggleOption> Members(string id) =>
        (_groups.TryGetValue(id, out var children) ? children : [id])
            .Where(_options.ContainsKey).Select(key => _options[key]).Where(option => !option.Locked && Matches(option)).ToArray();

    /// <summary>Reconciles one acknowledged host state without rebuilding selection or expansion.</summary>
    internal void Reconcile(string id, InteractionToggleResult result)
    {
        var key = "item:" + id;
        if (result.UpdatedOption is { } updated && updated.Id == id && updated.Group == _options[key].Group)
        {
            _options[key] = updated with { Enabled = result.Enabled };
        }
        else
        {
            _options[key] = _options[key] with { Enabled = result.Enabled };
        }

        ReconcileTree();
        _notice = result.Reason is null ? "Applied immediately; Esc closes" : TranscriptView.Safe(result.Reason);
    }

    /// <summary>Releases the serialized toggle gate after all concrete group members finish.</summary>
    internal void CompleteChange()
    {
        CancelOperation = null;
        _busy = false;
    }

    private bool Matches(InteractionToggleOption option) =>
        option.Label.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase) || option.Group.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase);

    private void Filter()
    {
        foreach (var group in _groups.Keys)
        {
            if (_filter.Text.Length == 0 || _tree.IsExpanded(group))
            {
                _expansion[group] = _tree.IsExpanded(group);
            }
        }

        var groups = _groups.Where(group => group.Value.Any(id => Matches(_options[id]))).Select(group => group.Key).ToArray();
        _tree = new CheckTree<string>(
            groups.Length == 0 ? ["empty:No matching settings"] : groups,
            id => (_groups.GetValueOrDefault(id) ?? []).Where(child => Matches(_options[child])),
            id => _options.TryGetValue(id, out var option) ? (option.Locked ? "[locked] " : string.Empty) + TranscriptView.Safe(option.Label) : TranscriptView.Safe(id[6..]),
            id => _groups.ContainsKey(id),
            StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (_expansion.GetValueOrDefault(group, true))
            {
                _tree.Expand(group);
            }
        }

        if (_selected is { } selected && _options.TryGetValue(selected, out var selectedOption) && Matches(selectedOption))
        {
            _tree.RevealTo(selected);
        }
        else if (_selected is { } selectedGroup && groups.Contains(selectedGroup, StringComparer.Ordinal))
        {
            _tree.RevealTo(selectedGroup);
        }

        ReconcileTree();
    }

    private void ReconcileTree()
    {
        foreach (var group in _groups)
        {
            _tree.SetExplicit(group.Key, group.Value.Where(id => Matches(_options[id])).All(id => _options[id].Enabled));
        }

        foreach (var option in _options)
        {
            _tree.SetExplicit(option.Key, option.Value.Enabled);
        }
    }
}
