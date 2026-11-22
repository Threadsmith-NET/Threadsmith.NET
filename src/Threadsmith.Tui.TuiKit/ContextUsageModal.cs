namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Modals;
using TUIKit.Widgets;

/// <summary>Bounded viewport over a frozen request, with graphs rendered by native TUIKit widgets.</summary>
internal sealed class ContextUsageModal : Modal
{
    private readonly ContextUsageSnapshot? _snapshot;
    private readonly IReadOnlyList<ContextUsageComponent> _categories;
    private readonly IReadOnlyList<ContextUsageComponent> _components;
    private readonly int _stablePrefixRows;
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly Action _interrupt;
    private readonly Action _toggleMouse;
    private readonly Func<bool> _canHandleMouse;
    private readonly BarChart _chart = new();
    private readonly ProgressBar _progress = new();
    private readonly CellBuffer _chartBuffer = new(1, 2);
    private readonly CachedTextRun[] _text = [.. Enumerable.Range(0, 100).Select(_ => new CachedTextRun())];
    private IReadOnlyList<ContextUsageComponent> _rows = [];
    private int _textIndex;
    private int _selected;
    private int _top;
    private int _page = 1;
    private bool _categoryView;
    private bool _memoryView;
    private int _memoryTop;
    private int _memoryWidth;
    private IReadOnlyList<string> _memoryLines = [];
    private double _maximum;
    private int _basisWidth;
    private IReadOnlyList<string> _basisLines = [];

    /// <summary>Initializes a new instance of the <see cref="ContextUsageModal"/> class with frozen metadata.</summary>
    internal ContextUsageModal(ContextUsageSnapshot? snapshot, Func<PresentationTextRole, CellStyle> style, Action interrupt, Action toggleMouse, Func<bool>? canHandleMouse = null)
    {
        _snapshot = snapshot;
        _categories = snapshot is null ? [] : ContextUsageFormatter.Categories(snapshot);
        _components = snapshot is null ? [] : ContextUsageFormatter.OrderedContributions(snapshot).ToArray();
        _stablePrefixRows = snapshot is null ? 0 : ContextUsageFormatter.StablePrefixContributionCount(snapshot);
        _style = style;
        _interrupt = interrupt;
        _toggleMouse = toggleMouse;
        _canHandleMouse = canHandleMouse ?? (() => true);
        Reflow();
    }

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code == KeyCode.Escape)
        {
            RequestClose(null);
        }
        else if (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl)
        {
            _interrupt();
        }
        else if (key.Code == KeyCode.F12)
        {
            _toggleMouse();
        }
        else if (key.Code == KeyCode.Character && key.Rune is 'm' or 'M')
        {
            _memoryView = !_memoryView;
        }
        else if (_memoryView)
        {
            _memoryTop = Math.Clamp(
                key.Code switch
                {
                    KeyCode.Up => _memoryTop - 1,
                    KeyCode.Down => _memoryTop + 1,
                    KeyCode.PageUp => _memoryTop - _page,
                    KeyCode.PageDown => _memoryTop + _page,
                    KeyCode.Home => 0,
                    KeyCode.End => _memoryLines.Count - _page,
                    _ => _memoryTop,
                },
                0,
                Math.Max(0, _memoryLines.Count - _page));
        }
        else if (key.Code == KeyCode.Tab)
        {
            _categoryView = !_categoryView;
            _selected = _top = 0;
            Reflow();
        }
        else
        {
            _selected = Math.Clamp(
                key.Code switch
                {
                    KeyCode.Up => _selected - 1,
                    KeyCode.Down => _selected + 1,
                    KeyCode.PageUp => _selected - _page,
                    KeyCode.PageDown => _selected + _page,
                    KeyCode.Home => 0,
                    KeyCode.End => _rows.Count - 1,
                    _ => _selected,
                },
                0,
                Math.Max(0, _rows.Count - 1));
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text)
    {
        return true;
    }

    /// <inheritdoc />
    public override bool HandleMouse(MouseEvent mouse)
    {
        if (!_canHandleMouse())
        {
            return true;
        }

        if (mouse.Kind == MouseEventKind.Wheel)
        {
            var delta = mouse.Button == MouseButton.WheelUp ? -3 : 3;
            if (_memoryView)
            {
                _memoryTop = Math.Clamp(_memoryTop + delta, 0, Math.Max(0, _memoryLines.Count - _page));
            }
            else
            {
                _selected = Math.Clamp(_selected + delta, 0, Math.Max(0, _rows.Count - 1));
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        _textIndex = 0;
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default), large: true);
        if (view is null)
        {
            return;
        }

        var headerHeight = Draw(view, 0, "Context usage — Estimated", PresentationTextRole.SelectionPrompt);
        if (_snapshot is not { } snapshot)
        {
            headerHeight += Draw(view, headerHeight, "No captured request usage available.");
            Draw(view, headerHeight, "Send a request first; Esc closes.");
            return;
        }

        var requestHeader = $"{snapshot.ModelName ?? snapshot.ModelProfileId?.Value.ToString() ?? "Model unknown"} · {snapshot.Stage} #{snapshot.Round + 1} · {snapshot.CapturedAt:HH:mm:ss} UTC · {(snapshot.DispatchStarted ? "submitted" : "prepared; submission unobserved")}";
        if (_memoryView)
        {
            headerHeight += Draw(view, headerHeight, requestHeader);
            RenderMemories(view, snapshot, headerHeight);
            return;
        }

        var requestTop = headerHeight;
        var stableHeader = $"Host-stable prefix {snapshot.StablePrefixTokens:N0} tokens · cache eligible · actual cache use requires provider telemetry";
        headerHeight += ModalFrame.Wrap(requestHeader, view.Size.Width).Length + ModalFrame.Wrap(stableHeader, view.Size.Width).Length;
        if (_basisWidth != view.Size.Width)
        {
            _basisWidth = view.Size.Width;
            _basisLines = TextWrapper.Wrap(StyledText.From(ContextUsageFormatter.Label(snapshot.EstimationBasis)), _basisWidth).Select(line => line.ToPlainString()).ToArray();
        }

        var stableSuffix = !_categoryView && _selected < _stablePrefixRows ? " · stable prefix" : string.Empty;
        var selectedRows = ModalFrame.Wrap(_rows.Count > 0 ? $"{ContextUsageFormatter.Label(_rows[_selected].Label)} · {_rows[_selected].Tokens:N0} tokens · {ContextUsageFormatter.Percent(_rows[_selected].Tokens, snapshot.InputTokens)} of input{stableSuffix}" : "No included content.", view.Size.Width);
        var navigationRows = ModalFrame.Wrap("↑↓ PgUp/Dn Home/End · Tab categories/order · M memories · Esc close", view.Size.Width);
        var footerHeight = _basisLines.Count + selectedRows.Length + navigationRows.Length;
        var footerTop = Math.Max(headerHeight, view.Size.Height - footerHeight);
        var bodyHeight = Math.Max(0, footerTop - headerHeight);
        if (bodyHeight < 6)
        {
            // Essential usage and resize guidance take priority over request metadata and explanations.
            var compact = view.CreateView(new Rect(0, requestTop, view.Size.Width, view.Size.Height - requestTop));
            var y = Draw(compact, 0, $"Input {snapshot.InputTokens:N0} · Used {ContextUsageFormatter.Percent(snapshot.InputTokens, snapshot.ContextWindow)}");
            Draw(compact, y, "Enlarge terminal for graphs; Esc closes.");
            _page = 1;
            return;
        }

        var stableTop = requestTop + Draw(view, requestTop, requestHeader);
        Draw(view, stableTop, stableHeader);
        ModalFrame.DrawLines(view, _text[_textIndex++ % _text.Length], footerTop, _basisLines, _style(PresentationTextRole.Default));
        ModalFrame.DrawLines(view, _text[_textIndex++ % _text.Length], footerTop + _basisLines.Count, selectedRows, _style(PresentationTextRole.Default));
        ModalFrame.DrawLines(view, _text[_textIndex++ % _text.Length], footerTop + _basisLines.Count + selectedRows.Length, navigationRows, _style(PresentationTextRole.Default));

        var sideBySide = view.Size.Width >= 100;
        var detailWidth = sideBySide ? view.Size.Width - 39 : view.Size.Width;
        var inputLabel = $"Input {snapshot.InputTokens:N0} tokens · {ContextUsageFormatter.Percent(snapshot.InputTokens, snapshot.ContextWindow)} used";
        var windowLabel = $"Window {snapshot.ContextWindow?.ToString("N0") ?? "?"} · Reserve {snapshot.OutputReserve:N0}";
        var summaryWidth = sideBySide ? 38 : view.Size.Width;
        var summaryHeight = ModalFrame.Wrap(inputLabel, summaryWidth).Length + ModalFrame.Wrap(windowLabel, summaryWidth).Length + 1;
        var summary = sideBySide
            ? view.CreateView(new Rect(detailWidth + 1, headerHeight, 38, bodyHeight))
            : view.CreateView(new Rect(0, headerHeight, view.Size.Width, Math.Min(bodyHeight, summaryHeight)));
        var summaryY = Draw(summary, 0, inputLabel);
        summaryY += Draw(summary, summaryY, windowLabel);
        if (snapshot.ContextWindow is > 0)
        {
            _progress.Value = (double)snapshot.InputTokens / snapshot.ContextWindow.Value;
            _progress.FillColor = _style(PresentationTextRole.Status).Foreground;
            if (summaryY < summary.Size.Height)
            {
                _progress.Render(summary.CreateView(new Rect(0, summaryY, summary.Size.Width, 1)));
            }
        }
        else
        {
            Draw(summary, summaryY, "Window usage unavailable");
        }

        if (sideBySide)
        {
            var y = summaryY + 1;
            y += Draw(summary, y, "Categories — % of input (rounded)", PresentationTextRole.SelectionPrompt);
            var visible = 0;
            foreach (var item in _categories)
            {
                var label = $"{item.Label} {ContextUsageFormatter.Percent(item.Tokens, snapshot.InputTokens)}";
                var height = ModalFrame.Wrap(label, summary.Size.Width).Length;
                var more = ModalFrame.Wrap($"+{_categories.Count - visible} categories · Tab for all", summary.Size.Width).Length;
                if (y + height + 1 + more > summary.Size.Height)
                {
                    break;
                }

                y += Draw(summary, y, label);
                Graph(summary, y++, item.Tokens, _categories.Count > 0 ? _categories[0].Tokens : 0);
                visible++;
            }

            if (visible < _categories.Count)
            {
                Draw(summary, y, $"+{_categories.Count - visible} categories · Tab for all");
            }
        }

        var start = headerHeight + (sideBySide ? 0 : summary.Size.Height + 1);
        var detail = view.CreateView(new Rect(0, start, detailWidth, Math.Max(0, footerTop - start)));
        _top = Math.Clamp(_top, Math.Max(0, _selected - Math.Max(1, detail.Size.Height / 2) + 1), _selected);
        var firstMeasured = _top;
        var labels = new List<string[]>();
        var physicalRows = 0;
        for (var index = _top; index <= _selected && index < _rows.Count; index++)
        {
            var lines = ContributionRows(index, detailWidth);
            labels.Add(lines);
            physicalRows += lines.Length + 1;
        }

        while (physicalRows > detail.Size.Height && _top < _selected)
        {
            physicalRows -= labels[_top - firstMeasured].Length + 1;
            _top++;
        }

        var yDetail = 0;
        var visibleRows = 0;
        for (var index = _top; index < _rows.Count && yDetail < detail.Size.Height; index++)
        {
            var lines = ContributionRows(index, detailWidth);
            if (index > _selected && yDetail + lines.Length + 1 > detail.Size.Height)
            {
                break;
            }

            ModalFrame.DrawLines(detail, _text[_textIndex++ % _text.Length], yDetail, lines, _style(index == _selected ? PresentationTextRole.SelectionPrompt : PresentationTextRole.Default));
            yDetail += lines.Length;
            Graph(detail, yDetail, _rows[index].Tokens, _maximum, leftPadding: 2);
            var prefixContinuation = !_categoryView && index < _stablePrefixRows - 1 ? '│' : ' ';
            Draw(detail, yDetail++, $" {prefixContinuation}");
            visibleRows++;
        }

        _page = Math.Max(1, visibleRows);
    }

    private string[] ContributionRows(int index, int width)
    {
        var item = _rows[index];
        var marker = !_categoryView && index < _stablePrefixRows ? index == _stablePrefixRows - 1 ? '└' : '│' : ' ';
        return ModalFrame.Wrap($"{(index == _selected ? ">" : " ")}{marker}[{item.Container}] {item.Label}", width);
    }

    private void RenderMemories(BufferSurface view, ContextUsageSnapshot snapshot, int headerHeight)
    {
        if (_memoryWidth != view.Size.Width)
        {
            _memoryWidth = view.Size.Width;
            _memoryLines = ModalFrame.Wrap(ContextUsageFormatter.FormatMemories(snapshot), _memoryWidth);
        }

        var footer = ModalFrame.Wrap("↑↓ PgUp/Dn Home/End scroll · M usage · Esc close", view.Size.Width);
        var bodyTop = Math.Min(view.Size.Height, headerHeight + Draw(view, headerHeight, $"Included memories: {snapshot.Memories.Count}", PresentationTextRole.Memory));
        _page = Math.Max(0, view.Size.Height - bodyTop - footer.Length);
        _memoryTop = Math.Clamp(_memoryTop, 0, Math.Max(0, _memoryLines.Count - _page));
        for (var index = 0; index < Math.Min(_page, _memoryLines.Count - _memoryTop); index++)
        {
            _text[_textIndex++ % _text.Length].Draw(view, 0, index + bodyTop, _memoryLines[_memoryTop + index], _style(PresentationTextRole.Memory));
        }

        ModalFrame.DrawLines(view, _text[_textIndex++ % _text.Length], bodyTop + _page, footer, _style(PresentationTextRole.Default));
    }

    private int Draw(ISurface surface, int y, string text, PresentationTextRole role = PresentationTextRole.Default)
    {
        return ModalFrame.DrawWrapped(surface, _text[_textIndex++ % _text.Length], y, ContextUsageFormatter.Label(text), _style(role));
    }

    private void Graph(BufferSurface surface, int y, long tokens, double maximum, int leftPadding = 0)
    {
        if (y < 0 || y >= surface.Size.Height)
        {
            return;
        }

        // A clipped second row fixes the native chart's scale across scrolling.
        // The scratch surface is always two rows, independent of the source inventory size.
        var width = Math.Max(1, surface.Size.Width - leftPadding);
        if (_chartBuffer.Width != width)
        {
            _chartBuffer.Resize(width, 2);
        }

        _chart.Clear();
        _chart.Add(string.Empty, tokens);
        _chart.Add(string.Empty, maximum);
        _chartBuffer.Clear(_style(PresentationTextRole.Default));
        _chart.Render(new BufferSurface(_chartBuffer));
        for (var x = 0; x < width && x + leftPadding < surface.Size.Width; x++)
        {
            var cell = _chartBuffer.Get(x, 0);
            surface.Set(x + leftPadding, y, Cell.Glyph(cell.Grapheme, _style(PresentationTextRole.Status), 1));
        }
    }

    private void Reflow()
    {
        _rows = _categoryView ? _categories : _components;
        _maximum = _rows.Select(item => (double)item.Tokens).DefaultIfEmpty().Max();
        _selected = Math.Min(_selected, Math.Max(0, _rows.Count - 1));
    }
}
