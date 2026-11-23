namespace Threadsmith.Tui.TuiKit;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;

using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>An immutable option whose identifier is returned unchanged to the coordinator.</summary>
internal readonly record struct Choice(string Id, string Label)
{
    /// <summary>Gets the optional non-selectable heading for this option.</summary>
    internal string? Group { get; init; }

    /// <summary>Gets optional searchable text shown in the full review instead of the list.</summary>
    internal string? Details { get; init; }
}

// A fixed authoritative option array; only the view is filtered.

/// <summary>Filters a fixed option set without changing selection authority.</summary>
internal sealed class ChoiceModal : Modal
{
    private const int _maximumSearchableDetailCharacters = 4096;
    private const int _maximumCachedOptions = 128;

    private readonly ComposerBuffer _filter;
    private readonly Dictionary<int, OptionLayout> _layoutCache = [];
    private readonly TuiResourceLimits _limits;
    private readonly CachedTextRun _hintRun = new();
    private readonly Dictionary<string, CachedTextRun> _optionRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedTextRun> _groupRuns = new(StringComparer.Ordinal);
    private readonly CachedTextRun _plainPrefixRun = new();
    private readonly CachedTextRun _selectionPrefixRun = new();
    private readonly TranscriptView _prompt;
    private readonly string _title;
    private readonly string? _reviewDetails;
    private readonly Choice[] _options;
    private readonly int _contentWidth;
    private readonly bool _spaceBetweenOptions;
    private readonly SourceDocument? _scriptDocument;
    private Choice[] _matches;
    private string _filterHint = "Filter:  | F2 details; Esc cancels";
    private string _selectionMarker = ">";
    private string _selectionPrefix = "> ";
    private int _selectionPrefixWidth = 2;
    private int _selected;
    private int _height = 5;
    private int _layoutWidth = 90;
    private bool _details;
    private bool _fits = true;
    private TranscriptView? _detail;
    private Rect _detailBounds;
    private string? _copyStatus;

    /// <summary>Initializes a new instance of the <see cref="ChoiceModal"/> class.</summary>
    internal ChoiceModal(
        string title,
        IReadOnlyList<Choice> options,
        TuiResourceLimits? limits = null,
        string? reviewDetails = null,
        ToolSourceDetail? sourceDetail = null,
        bool spaceBetweenOptions = false)
    {
        _limits = limits ?? new();
        _limits.Validate();
        _filter = new ComposerBuffer(_limits);
        _title = title;
        _reviewDetails = reviewDetails;
        _prompt = new TranscriptView(_limits);
        _prompt.Present(new PresentationBatch([new PresentationTextItem([new(title, PresentationTextRole.SelectionPrompt)])]));
        _prompt.HandleKey(KeyEvent.Special(KeyCode.Home));
        _options = [.. options];
        _matches = _options;
        _spaceBetweenOptions = spaceBetweenOptions;
        if (sourceDetail is { Availability: ToolSourceAvailability.Available, Source: { } source }
            && source.Length <= ToolSourceDetail.MaximumCharacters)
        {
            _scriptDocument = new SourceDocument(source, sourceDetail.Language, new TuiKitSourceHighlighter());

            // Dedicated, bounded detail capacity: ordinary session retention remains unchanged.
            var detailLimits = _limits with
            {
                MaximumTranscriptBytes = Math.Max(_limits.MaximumTranscriptBytes, ToolSourceDetail.MaximumCharacters * 12),
                MaximumTranscriptLines = Math.Max(_limits.MaximumTranscriptLines, ToolSourceDetail.MaximumCharacters + 1),

                // Use dedicated chunks, not tiny session chunks: tab expansion must not exceed
                // the full-source line budget. Continued chunks preserve raw copy offsets.
                MaximumTranscriptLineCharacters = 1024,
            };
            _detail = new TranscriptView(detailLimits);
            _detail.PresentSource(_scriptDocument);
            _detail.HandleKey(KeyEvent.Special(KeyCode.Home));
            _details = true;
        }

        if (!_spaceBetweenOptions && _options.Length <= 2)
        {
            _contentWidth = Math.Max(
                UnicodeWidth.GetWidth(TranscriptView.Safe(title)),
                _options.Select(option => Math.Max(
                    UnicodeWidth.GetWidth(TranscriptView.Safe(option.Label)) + (option.Group is null ? 0 : 2),
                    UnicodeWidth.GetWidth(TranscriptView.Safe(option.Group ?? string.Empty)))).DefaultIfEmpty(0).Max());
        }
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
            _layoutCache.Clear();
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

        if (_scriptDocument is not null && key.Code == KeyCode.F4)
        {
            CopySource(_scriptDocument.RawSource);
            return true;
        }

        if (key.Code == KeyCode.F6 && _details)
        {
            var selection = _detail?.SelectedText() ?? string.Empty;
            if (_scriptDocument is not null)
            {
                CopySource(selection);
            }
            else
            {
                CopyRequested?.Invoke(selection);
            }

            return true;
        }

        if (key.Code == KeyCode.Escape)
        {
            RequestClose(null);
            return true;
        }

        if (key.Code == KeyCode.F2 && _scriptDocument is null)
        {
            _details = !_details;
            if (_details)
            {
                OpenReview();
            }
            else
            {
                _detail = null;
            }

            return true;
        }

        if (!_fits)
        {
            return true;
        }

        if (_details)
        {
            _detail?.HandleKey(key);
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
                MovePage(-1);
                break;
            case KeyCode.PageDown:
                MovePage(1);
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
            case KeyCode.Character when key.Modifiers == KeyModifiers.None && _filter.Text.Length < _limits.MaximumFilterCharacters:
                _filter.Insert(char.ConvertFromUtf32(key.Rune));
                Filter();
                break;
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool HandleMouse(MouseEvent mouse)
    {
        if (_fits && _details && _detail is not null
            && mouse.X >= _detailBounds.X && mouse.X < _detailBounds.X + _detailBounds.Width
            && mouse.Y >= _detailBounds.Y && mouse.Y < _detailBounds.Y + _detailBounds.Height)
        {
            return _detail.HandleMouse(new MouseEvent(mouse.Kind, mouse.Button, mouse.X - _detailBounds.X, mouse.Y - _detailBounds.Y, mouse.Modifiers, mouse.ClickCount));
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool HandlePaste(string text)
    {
        if (_fits && !_details && text.Length <= _limits.MaximumFilterCharacters - _filter.Text.Length)
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
        _fits = ModalFrame.Fits(surface.Size);
        Size? preferredSize = _scriptDocument is not null ? new Size(90, 24)
            : !_spaceBetweenOptions && !_details && _options.Length <= 2 && _title.Length < 80
                ? new Size(
                    Math.Min(90, Math.Max(_contentWidth + Math.Max(2, _selectionPrefixWidth), UnicodeWidth.GetWidth(_filterHint)) + 4),
                    Enumerable.Range(0, _matches.Length).Sum(index => GetOptionRows(index).Rows.Length) + 7)
                : new Size(120, 26);
        var frame = ModalFrame.Create(surface, ResolveStyle(PresentationTextRole.Default), expandHeight: _scriptDocument is not null, preferredSize: preferredSize);
        if (frame is null)
        {
            return;
        }

        if (_scriptDocument is not null)
        {
            var content = ModalFrame.CreateBody(
                frame,
                _hintRun,
                ResolveStyle,
                _title,
                "Read-only C# source",
                _copyStatus ?? "F4 Copy full script; F6 Copy selection",
                "Arrows/PgUp/PgDn scroll; Home/End; Esc closes",
                spaceAfterSubtitle: true);
            var bounds = ModalFrame.Bounds(surface.Size, expandHeight: true, preferredSize: preferredSize);
            var inner = WorkspaceLayout.ModalContent(new Size(bounds.Width, bounds.Height));
            var body = ModalFrame.BodyBounds(frame.Size, _title, "Read-only C# source", _copyStatus ?? "F4 Copy full script; F6 Copy selection", "Arrows/PgUp/PgDn scroll; Home/End; Esc closes", spaceAfterSubtitle: true);
            _detailBounds = new Rect(bounds.X + inner.X + body.X, bounds.Y + inner.Y + body.Y, body.Width, body.Height);
            _detail?.ResolveStyle = ResolveStyle;
            _detail?.Render(content);
            return;
        }

        var area = new Rect(0, 0, frame.Size.Width, frame.Size.Height);
        var view = frame.CreateView(area);
        var hint = _details ? "PgUp/PgDn scroll; F2 back; Esc cancels" : _filterHint;
        var maximumHintRows = Math.Max(1, Math.Min(3, area.Height - 1));
        var hintRows = ModalFrame.Wrap(hint, area.Width, maximumRows: maximumHintRows + 1);
        if (hintRows.Length > maximumHintRows)
        {
            hintRows = [.. hintRows.Take(maximumHintRows - 1), "… F2 details; Esc cancels"];
        }

        var promptHeight = _details ? 0 : Math.Min(Math.Max(0, area.Height - hintRows.Length - 2), Math.Min(6, ModalFrame.Wrap(_title, area.Width).Length));
        if (promptHeight > 0)
        {
            _prompt.ResolveStyle = role => ResolveStyle(role == PresentationTextRole.OutputStreamPaneRole
                ? PresentationTextRole.Default
                : role);
            _prompt.Render(view.CreateView(new Rect(0, 0, area.Width, promptHeight)));
        }

        var gap = area.Height > promptHeight + hintRows.Length + 3 ? 1 : 0;
        ModalFrame.DrawLines(view, _hintRun, promptHeight + gap, hintRows, ResolveStyle(PresentationTextRole.Status));
        var contentTop = Math.Min(area.Height, promptHeight + gap + hintRows.Length + gap);
        _height = Math.Max(0, area.Height - contentTop);
        _fits = _height > 0;
        if (_details)
        {
            var bounds = ModalFrame.Bounds(surface.Size, preferredSize: preferredSize);
            var inner = WorkspaceLayout.ModalContent(new Size(bounds.Width, bounds.Height));
            _detailBounds = new Rect(bounds.X + inner.X, bounds.Y + inner.Y + contentTop, area.Width, _height);
            _detail?.ResolveStyle = ResolveStyle;

            _detail?.Render(view.CreateView(new Rect(0, contentTop, area.Width, _height)));
            return;
        }

        if (_layoutWidth != area.Width)
        {
            _layoutWidth = area.Width;
            _layoutCache.Clear();
        }

        var rows = GetVisibleRows();
        for (var index = 0; index < rows.Count; index++)
        {
            var entry = rows[index];
            var row = index + contentTop;
            if (entry.Heading is { } heading)
            {
                _groupRuns[heading].Draw(view, 0, row, entry.Text, ResolveStyle(PresentationTextRole.SelectionPrompt));
                continue;
            }

            if (entry.OptionIndex < 0)
            {
                continue;
            }

            var option = _matches[entry.OptionIndex];
            var selected = entry.OptionIndex == _selected;
            var prefix = selected ? _selectionPrefix : "  ";
            var style = selected ? ResolveStyle(PresentationTextRole.SelectionHighlight) : ResolveStyle(PresentationTextRole.Default);
            var indent = option.Group is null ? 0 : 2;
            if (entry.First)
            {
                (selected ? _selectionPrefixRun : _plainPrefixRun).Draw(view, indent, row, prefix, style);
            }

            GetOptionRun(option).Draw(view, indent + Math.Max(_selectionPrefixWidth, 2), row, entry.Text, style);
        }
    }

    /// <summary>Shows the complete review using the existing scrollable details view.</summary>
    internal void OpenReview()
    {
        var selected = _matches.Length > 0 ? _matches[_selected] : default;
        var selectedDetail = selected.Label is null
            ? string.Empty
            : "\n\nSelected option: " + (selected.Group is null ? string.Empty : selected.Group + " / ") + selected.Label;
        var text = _title + (_reviewDetails is null ? string.Empty : "\n\n" + _reviewDetails) + selectedDetail
            + (selected.Details is null ? string.Empty : "\n" + selected.Details);
        var limits = _reviewDetails is { Length: <= 16384 } ? _limits with
        {
            MaximumTranscriptBytes = Math.Max(_limits.MaximumTranscriptBytes, Encoding.UTF8.GetByteCount(text) * 4),
            MaximumTranscriptLines = Math.Max(_limits.MaximumTranscriptLines, text.Length + 1),
        } : _limits;
        _details = true;
        _detail = new TranscriptView(limits) { ResolveStyle = ResolveStyle };
        _detail.Present(new PresentationBatch([new PresentationTextItem([new(text, PresentationTextRole.Default)])]));
        _detail.HandleKey(KeyEvent.Special(KeyCode.Home));
    }

    private void CopySource(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > _limits.MaximumClipboardCopyBytes)
        {
            _copyStatus = $"Copy blocked: {_limits.MaximumClipboardCopyBytes:N0}-byte clipboard limit; no text copied";
            return;
        }

        try
        {
            _copyStatus = CopyRequested?.Invoke(text) == true
                ? "Exact source sent to terminal clipboard; terminal must allow OSC 52"
                : "Copy unavailable or no selection; no successful copy reported";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or EncoderFallbackException)
        {
            _copyStatus = "Clipboard write failed; no successful copy reported";
        }
    }

    private void Move(int delta)
    {
        _selected = Math.Clamp(_selected + delta, 0, Math.Max(0, _matches.Length - 1));
    }

    private void MovePage(int direction)
    {
        if (_matches.Length == 0)
        {
            return;
        }

        var current = GetOptionRows(_selected);
        var remaining = _height;
        var target = _selected;
        for (var index = _selected + direction; index >= 0 && index < _matches.Length; index += direction)
        {
            var next = GetOptionRows(index);
            remaining -= direction > 0
                ? current.Rows.Length - current.LabelStart + next.LabelStart
                : next.Rows.Length - next.LabelStart + current.LabelStart;
            if (remaining < 0 && target != _selected)
            {
                break;
            }

            target = index;
            if (remaining <= 0)
            {
                break;
            }

            current = next;
        }

        // Even when a single option spans a page, advance at least one option.
        _selected = target;
    }

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
        var filter = _filter.Text;
        _matches = filter.Length == 0 ? _options : Array.FindAll(_options, option => Matches(option, filter));
        _layoutCache.Clear();
        _filterHint = $"Filter: {filter} | F2 details; Esc cancels";
        _selected = 0;
    }

    private static bool Matches(Choice option, string filter)
    {
        return option.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || option.Group?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
            || (option.Details is { } details
                && details.AsSpan(0, Math.Min(details.Length, _maximumSearchableDetailCharacters))
                    .Contains(filter.AsSpan(), StringComparison.OrdinalIgnoreCase));
    }

    private List<DisplayRow> GetVisibleRows()
    {
        var visible = new List<DisplayRow>(_height);
        if (_matches.Length == 0 || _height == 0)
        {
            return visible;
        }

        // Keep the selected label in view before filling the space above and below it.
        var selected = GetOptionRows(_selected);
        var start = Math.Max(0, Math.Min(selected.Rows.Length, selected.LabelStart + _height) - _height);
        visible.AddRange(new ArraySegment<DisplayRow>(selected.Rows, start, Math.Min(_height, selected.Rows.Length - start)));
        for (var index = _selected - 1; index >= 0 && visible.Count < _height; index--)
        {
            var previous = GetOptionRows(index).Rows;
            var count = Math.Min(_height - visible.Count, previous.Length);
            visible.InsertRange(0, new ArraySegment<DisplayRow>(previous, previous.Length - count, count));
        }

        for (var index = _selected + 1; index < _matches.Length && visible.Count < _height; index++)
        {
            var next = GetOptionRows(index).Rows;
            visible.AddRange(new ArraySegment<DisplayRow>(next, 0, Math.Min(_height - visible.Count, next.Length)));
        }

        return visible;
    }

    private OptionLayout GetOptionRows(int index)
    {
        if (_layoutCache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        if (_layoutCache.Count == _maximumCachedOptions)
        {
            _layoutCache.Clear();
        }

        var layout = WrapOption(index);
        _layoutCache.Add(index, layout);
        return layout;
    }

    private OptionLayout WrapOption(int index)
    {
        var rows = new List<DisplayRow>();
        var option = _matches[index];
        if (_spaceBetweenOptions && index > 0)
        {
            rows.Add(new DisplayRow(-1, null, string.Empty, false));
        }

        if (option.Group is { } group && (index == 0 || !string.Equals(group, _matches[index - 1].Group, StringComparison.Ordinal)))
        {
            foreach (var line in ModalFrame.Wrap(group, _layoutWidth))
            {
                rows.Add(new DisplayRow(-1, group, line, true));
            }

            _groupRuns.TryAdd(group, new CachedTextRun());
        }

        var labelStart = rows.Count;
        var width = Math.Max(1, _layoutWidth - (option.Group is null ? 0 : 2) - Math.Max(2, _selectionPrefixWidth));
        var lines = ModalFrame.Wrap(option.Label, width);
        if (lines.Length == 0)
        {
            lines = [string.Empty];
        }

        for (var line = 0; line < lines.Length; line++)
        {
            rows.Add(new DisplayRow(index, null, lines[line], line == 0));
        }

        return new OptionLayout([.. rows], labelStart);
    }

    private readonly record struct OptionLayout(DisplayRow[] Rows, int LabelStart);

    private readonly record struct DisplayRow(int OptionIndex, string? Heading, string Text, bool First);
}
