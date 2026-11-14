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
}

// A fixed authoritative option array; only the view is filtered.

/// <summary>Filters a fixed option set without changing selection authority.</summary>
internal sealed class ChoiceModal : Modal
{
    private readonly ComposerBuffer _filter;
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
    private readonly SourceDocument? _scriptDocument;
    private Choice[] _matches;
    private DisplayRow[] _rows = [];
    private int[] _optionRows = [];
    private string _filterHint = "Filter:  | F2 details; Esc cancels";
    private string _selectionMarker = ">";
    private string _selectionPrefix = "> ";
    private int _selectionPrefixWidth = 2;
    private int _selected;
    private int _height = 5;
    private bool _details;
    private bool _fits = true;
    private TranscriptView? _detail;
    private Rect _detailBounds;
    private string? _copyStatus;

    /// <summary>Initializes a new instance of the <see cref="ChoiceModal"/> class.</summary>
    internal ChoiceModal(string title, IReadOnlyList<Choice> options, TuiResourceLimits? limits = null, string? reviewDetails = null, ToolSourceDetail? sourceDetail = null)
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
        BuildRows();
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

        if (_options.Length <= 2)
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

        if (!_fits)
        {
            return true;
        }

        if (key.Code == KeyCode.F2 && _scriptDocument is null)
        {
            _details = !_details;
            if (_details)
            {
                _detail = new TranscriptView(_limits) { ResolveStyle = ResolveStyle };
                var selectedDetail = _matches.Length > 0
                    ? "\n\nSelected option: " + (_matches[_selected].Group is { } group ? group + " / " : string.Empty) + _matches[_selected].Label
                    : string.Empty;
                var reviewDetails = _reviewDetails is null ? string.Empty : "\n\n" + _reviewDetails;
                _detail.Present(new PresentationBatch([new PresentationTextItem([new(_title + reviewDetails + selectedDetail, PresentationTextRole.Default)])]));
                _detail.HandleKey(KeyEvent.Special(KeyCode.Home));
            }
            else
            {
                _detail = null;
            }

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
            : !_details && _options.Length <= 2 && _title.Length < 80
                ? new Size(Math.Min(90, Math.Max(_contentWidth + Math.Max(2, _selectionPrefixWidth), UnicodeWidth.GetWidth(_filterHint)) + 4), _rows.Length + 7)
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
            var body = WorkspaceLayout.ModalBody(frame.Size, spaceAfterSubtitle: true);
            _detailBounds = new Rect(bounds.X + inner.X + body.X, bounds.Y + inner.Y + body.Y, body.Width, body.Height);
            _detail?.ResolveStyle = ResolveStyle;
            _detail?.Render(content);
            return;
        }

        var area = new Rect(0, 0, frame.Size.Width, frame.Size.Height);
        var view = frame.CreateView(area);
        var promptHeight = _details ? 0 : Math.Min(Math.Max(1, area.Height - 7), Math.Min(6, Math.Max(1, (UnicodeWidth.GetWidth(TranscriptView.Safe(_title)) + area.Width - 1) / area.Width)));
        if (promptHeight > 0)
        {
            _prompt.ResolveStyle = role => ResolveStyle(role == PresentationTextRole.OutputStreamPaneRole
                ? PresentationTextRole.Default
                : role);
            _prompt.Render(view.CreateView(new Rect(0, 0, area.Width, promptHeight)));
        }

        var hint = _details ? "PgUp/PgDn scroll; F2 back; Esc cancels" : _filterHint;
        _hintRun.Draw(view, 0, promptHeight + 1, hint, ResolveStyle(PresentationTextRole.Status));
        var contentTop = promptHeight + 3;
        _height = Math.Max(1, area.Height - contentTop);
        if (_details)
        {
            var bounds = ModalFrame.Bounds(surface.Size, preferredSize: preferredSize);
            var inner = WorkspaceLayout.ModalContent(new Size(bounds.Width, bounds.Height));
            _detailBounds = new Rect(bounds.X + inner.X, bounds.Y + inner.Y + contentTop, area.Width, _height);
            _detail?.ResolveStyle = ResolveStyle;

            _detail?.Render(view.CreateView(new Rect(0, contentTop, area.Width, _height)));
            return;
        }

        var selectedRow = _matches.Length > 0 ? _optionRows[_selected] : 0;
        var top = Math.Max(0, selectedRow - _height + 1);
        for (var index = top; index < Math.Min(_rows.Length, top + _height); index++)
        {
            var entry = _rows[index];
            var row = index - top + contentTop;
            if (entry.Heading is { } heading)
            {
                _groupRuns[heading].Draw(view, 0, row, heading, ResolveStyle(PresentationTextRole.SelectionPrompt));
                continue;
            }

            var option = _matches[entry.OptionIndex];
            var selected = entry.OptionIndex == _selected;
            var prefix = selected ? _selectionPrefix : "  ";
            var style = selected ? ResolveStyle(PresentationTextRole.SelectionHighlight) : ResolveStyle(PresentationTextRole.Default);
            var indent = option.Group is null ? 0 : 2;
            (selected ? _selectionPrefixRun : _plainPrefixRun).Draw(view, indent, row, prefix, style);
            GetOptionRun(option).Draw(view, indent + (selected ? _selectionPrefixWidth : 2), row, option.Label, style);
        }
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

        var targetRow = _optionRows[_selected] + (direction * _height);
        var target = Array.BinarySearch(_optionRows, targetRow);
        if (target < 0)
        {
            var insertion = ~target;
            target = direction > 0 ? insertion - 1 : insertion;
        }

        // A heading can occupy the entire page in a short viewport; still advance one option.
        _selected = Math.Clamp(direction > 0 ? Math.Max(_selected + 1, target) : Math.Min(_selected - 1, target), 0, _matches.Length - 1);
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
        var count = 0;
        foreach (var option in _options)
        {
            if (Matches(option))
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
                if (Matches(option))
                {
                    matches[index++] = option;
                }
            }

            _matches = matches;
        }

        BuildRows();
        _filterHint = $"Filter: {_filter.Text} | F2 details; Esc cancels";
        _selected = 0;
    }

    private bool Matches(Choice option)
    {
        return option.Label.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase)
            || option.Group?.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void BuildRows()
    {
        var rows = new List<DisplayRow>();
        _optionRows = new int[_matches.Length];
        string? previousGroup = null;
        for (var index = 0; index < _matches.Length; index++)
        {
            var option = _matches[index];
            if (option.Group is { } group && !string.Equals(group, previousGroup, StringComparison.Ordinal))
            {
                rows.Add(new DisplayRow(-1, group));
                _groupRuns.TryAdd(group, new CachedTextRun());
            }

            previousGroup = option.Group;
            _optionRows[index] = rows.Count;
            rows.Add(new DisplayRow(index, null));
        }

        _rows = [.. rows];
    }

    private readonly record struct DisplayRow(int OptionIndex, string? Heading);
}
