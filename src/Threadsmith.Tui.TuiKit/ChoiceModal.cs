namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>An immutable option whose identifier is returned unchanged to the coordinator.</summary>
internal readonly record struct Choice(string Id, string Label);

// A fixed authoritative option array; only the view is filtered.

/// <summary>Filters a fixed option set without changing selection authority.</summary>
internal sealed class ChoiceModal : Modal
{
    private readonly ComposerBuffer _filter = new();
    private readonly CachedTextRun _hintRun = new();
    private readonly Dictionary<string, CachedTextRun> _optionRuns = new(StringComparer.Ordinal);
    private readonly CachedTextRun _plainPrefixRun = new();
    private readonly CachedTextRun _selectionPrefixRun = new();
    private readonly CachedTextRun _titleRun = new();
    private readonly string _title;
    private readonly Choice[] _options;
    private Choice[] _matches;
    private string _filterHint = "Filter:  | F2 details; Esc cancels";
    private string _selectionMarker = ">";
    private string _selectionPrefix = "> ";
    private int _selectionPrefixWidth = 2;
    private int _selected;
    private int _height = 5;
    private bool _details;
    private bool _fits = true;
    private TranscriptView? _detail;

    /// <summary>Initializes a new instance of the <see cref="ChoiceModal"/> class over fixed authoritative options.</summary>
    internal ChoiceModal(string title, IReadOnlyList<Choice> options)
    {
        _title = title;
        _options = [.. options];
        _matches = _options;
    }

    /// <summary>Gets or sets the explicit clipboard request callback.</summary>
    internal Action? PasteRequested { get; set; }

    /// <summary>Gets or sets semantic role resolution for the current theme.</summary>
    internal Func<PresentationTextRole, CellStyle> ResolveStyle { get; set; } = _ => CellStyle.Default;

    /// <summary>Gets or sets the visible marker used even when styles are suppressed.</summary>
    internal string SelectionMarker
    {
        get => _selectionMarker;
        set
        {
            _selectionMarker = value;
            _selectionPrefix = value + " ";
            _selectionPrefixWidth = UnicodeWidth.GetWidth(_selectionPrefix);
        }
    }

    /// <summary>Gets or sets the explicit terminal-selection toggle.</summary>
    internal Action? ToggleMouse { get; set; }

    /// <summary>Gets or sets the explicit detail-copy callback.</summary>
    internal Func<string, bool>? CopyRequested { get; set; }

    /// <summary>Gets the current filter for discarding stale clipboard results.</summary>
    internal string FilterText => _filter.Text;

    /// <inheritdoc/>
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code == KeyCode.F12)
        {
            ToggleMouse?.Invoke();
            return true;
        }

        if (key.Code == KeyCode.F6 && _details)
        {
            CopyRequested?.Invoke(_detail!.SelectedText());
            return true;
        }

        if (key.Code == KeyCode.Escape)
        {
            RequestClose(null);
            return true;
        }

        if (!_fits)
        {
            return true;
        }

        if (key.Code == KeyCode.F2)
        {
            _details = !_details;
            if (_details)
            {
                _detail = new TranscriptView { ResolveStyle = ResolveStyle };
                if (_matches.Length > 0)
                {
                    _detail.Append(_matches[_selected].Label);
                    _detail.HandleKey(KeyEvent.Special(KeyCode.Home));
                }
            }
            else
            {
                _detail = null;
            }

            return true;
        }

        if (_details)
        {
            _detail!.HandleKey(key);
            return true;
        }

        if ((key.Code == KeyCode.Insert && key.Modifiers == KeyModifiers.Shift)
            || (key.Code == KeyCode.Character && key.Rune == 'v' && key.Modifiers.HasFlag(KeyModifiers.Ctrl)))
        {
            PasteRequested?.Invoke();
            return true;
        }

        switch (key.Code)
        {
            case KeyCode.Enter when _matches.Length > 0:
                Close(_matches[_selected].Id);
                break;
            case KeyCode.Up:
                Move(-1);
                break;
            case KeyCode.Down:
                Move(1);
                break;
            case KeyCode.PageUp:
                Move(-_height);
                break;
            case KeyCode.PageDown:
                Move(_height);
                break;
            case KeyCode.Home:
                _selected = 0;
                break;
            case KeyCode.End:
                _selected = Math.Max(0, _matches.Length - 1);
                break;
            case KeyCode.Backspace:
                _filter.Delete(true);
                Filter();
                break;
            case KeyCode.Character when key.Modifiers == KeyModifiers.None && _filter.Text.Length < 256:
                _filter.Insert(char.ConvertFromUtf32(key.Rune));
                Filter();
                break;
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool HandlePaste(string text)
    {
        if (_fits && !_details && text.Length <= 256 - _filter.Text.Length)
        {
            _filter.Insert(text.ReplaceLineEndings(" "));
            Filter();
        }

        return true;
    }

    /// <inheritdoc/>
    public override void Render(ISurface surface)
    {
        // Leave the shared status row visible even when the modal needs the full width.
        _fits = surface.Size.Width >= 40 && surface.Size.Height >= 12;
        if (!_fits || surface is not BufferSurface buffer)
        {
            return;
        }

        var area = new Rect(1, 1, surface.Size.Width - 2, surface.Size.Height - 3);
        var view = buffer.CreateView(area);
        view.Fill(new Rect(0, 0, area.Width, area.Height), Cell.Blank(ResolveStyle(PresentationTextRole.Default)));
        _titleRun.Draw(view, 0, 0, _title, ResolveStyle(PresentationTextRole.SelectionPrompt));
        _hintRun.Draw(view, 0, 1, _details ? "Details: PgUp/PgDn scroll; F2 back; Esc cancels" : _filterHint, ResolveStyle(PresentationTextRole.Status));
        _height = Math.Max(1, area.Height - 3);
        if (_details)
        {
            _detail!.Render(view.CreateView(new Rect(0, 3, area.Width, _height)));
            return;
        }

        var top = Math.Max(0, _selected - _height + 1);
        for (var index = top; index < Math.Min(_matches.Length, top + _height); index++)
        {
            var selected = index == _selected;
            var prefix = selected ? _selectionPrefix : "  ";
            var style = selected ? ResolveStyle(PresentationTextRole.SelectionHighlight) : ResolveStyle(PresentationTextRole.Default);
            var row = index - top + 3;
            (selected ? _selectionPrefixRun : _plainPrefixRun).Draw(view, 0, row, prefix, style);
            GetOptionRun(_matches[index]).Draw(view, selected ? _selectionPrefixWidth : 2, row, _matches[index].Label, style);
        }
    }

    private void Move(int delta) => _selected = Math.Clamp(_selected + delta, 0, Math.Max(0, _matches.Length - 1));

    private CachedTextRun GetOptionRun(Choice option)
    {
        if (!_optionRuns.TryGetValue(option.Id, out var run))
        {
            run = new CachedTextRun();
            _optionRuns.Add(option.Id, run);
        }

        return run;
    }

    private void Filter()
    {
        var count = 0;
        foreach (var option in _options)
        {
            if (option.Label.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        if (count == _options.Length)
        {
            _matches = _options;
        }
        else
        {
            var matches = new Choice[count];
            var index = 0;
            foreach (var option in _options)
            {
                if (option.Label.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase))
                {
                    matches[index++] = option;
                }
            }

            _matches = matches;
        }

        _filterHint = $"Filter: {_filter.Text} | F2 details; Esc cancels";
        _selected = 0;
    }
}
