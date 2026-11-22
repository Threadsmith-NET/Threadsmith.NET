namespace Threadsmith.Tui.TuiKit;

using System.Text;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Modals;
using TUIKit.Widgets;

/// <summary>A bounded query and detail view over TUIKit's authoritative fuzzy palette.</summary>
internal sealed class CommandPaletteModal : Modal
{
    private const int MaximumQueryLength = 256;
    private const int MaximumVisibleRows = 12;
    private readonly TuiKitCommandDiscovery _discovery;
    private readonly FuzzyList<Command> _palette;
    private readonly ComposerBuffer _query;
    private readonly WrappedModalList _list = new();
    private readonly CachedTextRun _title = new();
    private readonly CachedTextRun _queryRun = new();
    private readonly CachedTextRun _usage = new();
    private readonly CachedTextRun _description = new();
    private readonly CachedTextRun _noticeRun = new();
    private readonly Func<Size> _size;
    private readonly Action _controlC;
    private readonly Action _toggleMouse;
    private readonly Action _disarmEscape;
    private readonly Func<PresentationTextRole, CellStyle> _resolveStyle;
    private string _notice = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="CommandPaletteModal"/> class.</summary>
    internal CommandPaletteModal(
        TuiKitCommandDiscovery discovery,
        string query,
        Func<Size> size,
        Func<PresentationTextRole, CellStyle> resolveStyle,
        Action controlC,
        Action toggleMouse,
        Action disarmEscape)
    {
        _discovery = discovery;
        _query = new ComposerBuffer(discovery.Limits);
        _palette = discovery.BuildPalette();
        _size = size;
        _resolveStyle = resolveStyle;
        _controlC = controlC;
        _toggleMouse = toggleMouse;
        _disarmEscape = disarmEscape;
        InsertQuery(query);
    }

    /// <summary>Gets or sets the explicit clipboard request callback.</summary>
    internal Action? PasteRequested { get; set; }

    /// <summary>Gets or sets the explicit text-copy callback.</summary>
    internal Func<string, bool>? CopyRequested { get; set; }

    /// <summary>Gets the exact query for stale clipboard destination checks.</summary>
    internal string FilterText => _query.Text;

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        _disarmEscape();
        if (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl)
        {
            if (_query.Selection.Length > 0)
            {
                CopyRequested?.Invoke(_query.Selection);
            }
            else
            {
                _controlC();
            }
        }
        else if (key.Code == KeyCode.F12)
        {
            _toggleMouse();
        }
        else if (key.Code is KeyCode.Escape or KeyCode.F3)
        {
            RequestClose(null);
        }
        else if (ModalFrame.Fits(_size()))
        {
            HandleQueryKey(key);
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text)
    {
        _disarmEscape();
        if (ModalFrame.Fits(_size()))
        {
            InsertQuery(text);
        }

        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        var normal = _resolveStyle(PresentationTextRole.Default);
        var frame = ModalFrame.Create(surface, normal);
        if (frame is null)
        {
            return;
        }

        var highlight = _resolveStyle(PresentationTextRole.SelectionHighlight).WithAttribute(CellAttributes.Reverse, true);

        // Query previews and details must never displace the selected result.
        var titleRows = ModalFrame.Wrap("Commands | Enter inserts | Esc closes", frame.Size.Width, maximumRows: Math.Max(1, frame.Size.Height - 2));
        var queryRows = ModalFrame.Wrap("> " + FilterText, frame.Size.Width, maximumRows: Math.Max(1, frame.Size.Height - titleRows.Length - 1));
        ModalFrame.DrawLines(frame, _title, 0, titleRows, _resolveStyle(PresentationTextRole.SelectionPrompt));
        ModalFrame.DrawLines(frame, _queryRun, titleRows.Length, queryRows, _resolveStyle(PresentationTextRole.Status).WithAttribute(CellAttributes.Reverse, _query.Selection.Length > 0));
        var heading = titleRows.Length + queryRows.Length;
        var usage = "No matching commands";
        var description = string.Empty;
        if (_palette.SelectedItem is { } selected && _discovery.TryGet(selected.Id, out var descriptor))
        {
            usage = descriptor.Usage;
            description = descriptor.Description;
        }

        var noticeRows = ModalFrame.Wrap(_notice, frame.Size.Width);
        var usageRows = ModalFrame.Wrap(usage, frame.Size.Width);
        var descriptionRows = ModalFrame.Wrap(description, frame.Size.Width);
        var availableRows = frame.Size.Height - heading;
        var count = Math.Clamp(availableRows - noticeRows.Length - usageRows.Length - descriptionRows.Length, 1, Math.Min(availableRows, MaximumVisibleRows));
        var listTop = heading;
        RenderList(frame.CreateView(new Rect(0, listTop, frame.Size.Width, count)), normal, highlight);
        var detailsRow = listTop + count;
        ModalFrame.DrawLines(frame, _noticeRun, detailsRow, noticeRows, _resolveStyle(PresentationTextRole.Status));
        detailsRow += noticeRows.Length;
        ModalFrame.DrawLines(frame, _usage, detailsRow, usageRows, _resolveStyle(PresentationTextRole.SelectionPrompt));
        ModalFrame.DrawLines(frame, _description, detailsRow + usageRows.Length, descriptionRows, normal);
    }

    private void HandleQueryKey(KeyEvent key)
    {
        if ((key.Code == KeyCode.Insert && key.Modifiers == KeyModifiers.Shift)
            || (key.Code == KeyCode.Character && key.Rune == 'v' && key.Modifiers == KeyModifiers.Ctrl))
        {
            PasteRequested?.Invoke();
        }
        else if (key.Code == KeyCode.Character && key.Rune == 'a' && key.Modifiers == KeyModifiers.Ctrl)
        {
            _query.SelectAll();
        }
        else if (key.Code == KeyCode.F6 && _palette.SelectedItem is { } item && _discovery.TryGet(item.Id, out var descriptor))
        {
            CopyRequested?.Invoke(descriptor.Usage + "\n" + descriptor.Description);
        }
        else if (key.Modifiers == KeyModifiers.None)
        {
            switch (key.Code)
            {
                case KeyCode.Enter when _palette.SelectedItem is { } selected:
                    // Complete on the input owner before the next buffered key is dispatched.
                    selected.Handler();
                    Close(selected.Id);
                    break;
                case KeyCode.Character:
                    InsertQuery(char.ConvertFromUtf32(key.Rune));
                    break;
                case KeyCode.Backspace:
                    _query.Delete(true);
                    _palette.Query = FilterText;
                    break;
                case KeyCode.Up:
                case KeyCode.Down:
                case KeyCode.PageUp:
                case KeyCode.PageDown:
                case KeyCode.Home:
                case KeyCode.End:
                    // Empty FuzzyList.Down can leave its private selected index at -1.
                    if (_palette.MatchCount > 0)
                    {
                        _palette.HandleKey(key);
                    }

                    break;
            }
        }
    }

    private void InsertQuery(string text)
    {
        if (text.Length > _discovery.Limits.MaximumFilterCharacters - (FilterText.Length - _query.Selection.Length))
        {
            _notice = $"Query limit: {_discovery.Limits.MaximumFilterCharacters} characters";
            return;
        }

        var safe = TranscriptView.Safe(text).ReplaceLineEndings(" ");
        if (safe.Length <= _discovery.Limits.MaximumFilterCharacters - (FilterText.Length - _query.Selection.Length))
        {
            try
            {
                _query.Insert(safe);
                _palette.Query = FilterText;
                _notice = string.Empty;
            }
            catch (EncoderFallbackException)
            {
                _notice = "Invalid Unicode; query preserved";
            }
        }
        else
        {
            _notice = $"Query limit: {_discovery.Limits.MaximumFilterCharacters} characters";
        }
    }

    private void RenderList(BufferSurface view, CellStyle normal, CellStyle highlight)
    {
        _palette.HighlightStyle = highlight;
        _palette.MatchStyle = CellStyle.Default;
        _list.Render(_palette, view, _discovery.MaximumRenderedTitleLength, normal, highlight, headerRows: 1);
    }
}
