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
    private readonly Dictionary<string, List<string>> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _groupLabels = new(StringComparer.Ordinal);
    private readonly List<string> _roots = [];
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
    private string _cancellationNotice = "Cancelling action...";
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
        foreach (var option in request.Options)
        {
            var path = option.GroupPath.Count > 0 ? option.GroupPath : [option.Group];
            var siblings = _roots;
            var key = "group:";
            for (var index = 0; index < path.Count; index++)
            {
                key += (index == 0 ? string.Empty : "/") + Uri.EscapeDataString(path[index]);
                if (!_groups.TryGetValue(key, out var members))
                {
                    members = [];
                    _groups.Add(key, members);
                    _children.Add(key, []);
                    _groupLabels.Add(key, path[index]);
                    siblings.Add(key);
                }

                members.Add("item:" + option.Id);
                siblings = _children[key];
            }

            siblings.Add("item:" + option.Id);
        }

        _tree = CreateTree();
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
                _notice = _cancellationNotice;
            }
            else
            {
                RequestClose(null);
            }
        }
        else if (_fits && !_busy && _details is null && SupportsActions && key.Code == KeyCode.F3)
        {
            if (ActionChoices(_tree.SelectedNode).Count > 0)
            {
                _busy = _changes.Writer.TryWrite((_tree.SelectedNode, false, true));
            }
            else
            {
                _notice = _request.AllowGroupActions ? "Select a skill or group with available actions." : "Select an individual item with available actions.";
            }
        }
        else if (_fits && !_busy && key.Code == KeyCode.F2)
        {
            if (_details is null)
            {
                _details = new TranscriptView(_limits) { ResolveStyle = _style };
                var selected = _options.GetValueOrDefault(_tree.SelectedNode);
                var label = selected is null ? Label(_tree.SelectedNode) : selected.Label + "\n" + selected.Reason;
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
                _busy = _changes.Writer.TryWrite((id, !Members(id).Any(option => option.Enabled), false));
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

    /// <summary>Gets or sets cancellation of the current host operation.</summary>
    internal Action? CancelOperation { get; set; }

    /// <summary>Gets or sets whether the caller can execute individual actions.</summary>
    internal bool SupportsActions { get; set; }

    /// <summary>Gets the current eligible action targets without expanding group scope beyond the filter.</summary>
    internal IReadOnlyList<InteractionToggleOption> ActionMembers(string id) =>
        _options.TryGetValue(id, out var option) ? [option]
            : _request.AllowGroupActions && _groups.TryGetValue(id, out var members)
                ? members.Select(key => _options[key]).Where(Matches).ToArray()
                : [];

    /// <summary>Offers only actions available on every target of the current leaf or group.</summary>
    internal IReadOnlyList<InteractionSelectionOption> ActionChoices(string id)
    {
        var members = ActionMembers(id);
        return members.Count == 0 ? [] : members[0].Actions
            .Where(action => members.All(member => member.Actions.Any(candidate => candidate.Id == action.Id)))
            .ToArray();
    }

    /// <summary>Gets a safe display label without treating catalog text as a tree identity.</summary>
    internal string Label(string id) => TranscriptView.Safe(_options.TryGetValue(id, out var option)
        ? (option.Locked ? "[locked] " : string.Empty) + option.Label
        : _groupLabels.GetValueOrDefault(id) ?? "No matching settings");

    /// <summary>Shows progress while the host owns a cancellable action.</summary>
    internal void BeginAction(string label, Action cancel)
    {
        CancelOperation = cancel;
        _cancellationNotice = "Cancelling action...";
        _notice = label + " - working; Esc cancels";
    }

    /// <summary>Reports toggle progress while allowing the current host mutation to finish before stopping.</summary>
    internal void BeginToggle(string label, Action stop)
    {
        CancelOperation = stop;
        _cancellationNotice = "Stopping after current item...";
        _notice = label + " - Esc stops after current item";
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
        var matched = Matches(_options[key]);
        if (result.UpdatedOption is { } updated && updated.Id == id && updated.Group == _options[key].Group
            && updated.GroupPath.SequenceEqual(_options[key].GroupPath, StringComparer.Ordinal))
        {
            _options[key] = updated with { Enabled = result.Enabled };
        }
        else
        {
            _options[key] = _options[key] with { Enabled = result.Enabled };
        }

        if (matched != Matches(_options[key]))
        {
            Filter();
        }
        else
        {
            ReconcileTree();
        }

        _notice = result.Reason is null ? "Applied immediately; Esc closes" : TranscriptView.Safe(result.Reason);
    }

    /// <summary>Releases the serialized toggle gate after all concrete group members finish.</summary>
    internal void CompleteChange()
    {
        CancelOperation = null;
        _busy = false;
    }

    /// <summary>Reports acknowledged batch outcomes and releases the input gate in the same UI turn.</summary>
    internal void CompleteToggle(bool enabled, int total, int processed, int applied, bool cancelled)
    {
        CompleteChange();
        if (total > 1 || cancelled)
        {
            var state = enabled ? "enabled" : "disabled";
            _notice = (cancelled ? "Cancelled: " : string.Empty) + (enabled ? "Enabled " : "Disabled ") + $"{applied}/{total}";
            if (processed > applied)
            {
                _notice += $"; {processed - applied} not {state}";
            }

            if (total > processed)
            {
                _notice += $"; {total - processed} not processed";
            }
        }
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

        var groups = _groups.Keys.Where(GroupMatches).ToArray();
        _tree = CreateTree();
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

    private CheckTree<string> CreateTree()
    {
        var roots = _roots.Where(GroupMatches).ToArray();
        return new CheckTree<string>(
            roots.Length == 0 ? ["empty:No matching settings"] : roots,
            id => (_children.GetValueOrDefault(id) ?? []).Where(child =>
                _options.TryGetValue(child, out var option) ? Matches(option) : GroupMatches(child)),
            Label,
            id => _groups.ContainsKey(id),
            StringComparer.Ordinal);
    }

    private bool GroupMatches(string id) => _groups[id].Any(child => Matches(_options[child]));

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
