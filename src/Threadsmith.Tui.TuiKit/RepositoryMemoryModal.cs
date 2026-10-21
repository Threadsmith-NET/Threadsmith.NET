namespace Threadsmith.Tui.TuiKit;

using System.Threading.Channels;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Modals;
using TUIKit.Widgets;

/// <summary>Stored-memory browser using native tabs and independently scrolling columns.</summary>
internal sealed class RepositoryMemoryModal : Modal
{
    private readonly TabView _tabs = new();
    private readonly MemoryListView[] _lists;
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly Action _interrupt;
    private readonly Action _toggleMouse;
    private readonly Func<bool> _canHandleMouse;
    private readonly CachedTextRun _title = new();
    private readonly CachedTextRun _footer = new();
    private readonly Channel<(RepositoryMemoryListItem? Item, bool Delete, int Tab)> _changes = Channel.CreateUnbounded<(RepositoryMemoryListItem?, bool, int)>();
    private Rect _tabBounds;
    private bool _fits;
    private bool _busy;

    /// <summary>Gets whether mutation commands are available.</summary>
    internal bool CanManage { get; init; }

    /// <summary>Gets serialized add, edit and delete requests; a null item requests creation on the specified tab.</summary>
    internal ChannelReader<(RepositoryMemoryListItem? Item, bool Delete, int Tab)> Changes => _changes.Reader;

    private string? _error;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryModal"/> class.</summary>
    internal RepositoryMemoryModal(RepositoryMemoryListSnapshot snapshot, Func<PresentationTextRole, CellStyle> style, Action interrupt, Action toggleMouse, Func<bool> canHandleMouse)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _style = style;
        _interrupt = interrupt;
        _toggleMouse = toggleMouse;
        _canHandleMouse = canHandleMouse;
        _lists = [
            new("Preferences", snapshot.Entries.Where(item => item.Ownership == RepositoryMemoryOwnership.Ordinary && item.MemoryType == RepositoryMemoryType.StandingPreference).ToArray(), snapshot.Warnings, style),
            new("Situational", snapshot.Entries.Where(item => item.Ownership == RepositoryMemoryOwnership.Ordinary && item.MemoryType == RepositoryMemoryType.Situational).ToArray(), snapshot.Warnings, style),
            new("Intelligence", snapshot.Entries.Where(item => item.Ownership == RepositoryMemoryOwnership.Intelligence).ToArray(), snapshot.Warnings, style),
        ];
        foreach (var list in _lists)
        {
            _tabs.Add(list.Name, list);
        }
    }

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (_busy)
        {
            return true;
        }

        if (CanManage && _fits && key.Code == KeyCode.Insert)
        {
            _busy = true;
            _changes.Writer.TryWrite((null, false, _tabs.ActiveIndex));
        }
        else if (CanManage && _fits && key.Code is KeyCode.Enter or KeyCode.F4 or KeyCode.Delete)
        {
            if (_lists[_tabs.ActiveIndex].Selected is { } selected)
            {
                _busy = true;
                _changes.Writer.TryWrite((selected, key.Code == KeyCode.Delete, _tabs.ActiveIndex));
            }
        }
        else if (key.Code == KeyCode.Escape)
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
        else if (_fits && !_tabs.HandleKey(key))
        {
            _lists[_tabs.ActiveIndex].HandleKey(key);
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text) => true;

    /// <inheritdoc />
    public override bool HandleMouse(MouseEvent mouse)
    {
        if (_busy || !_fits || !_canHandleMouse() || mouse.X < _tabBounds.Left || mouse.X >= _tabBounds.Left + _tabBounds.Width
            || mouse.Y < _tabBounds.Top || mouse.Y >= _tabBounds.Top + _tabBounds.Height)
        {
            return true;
        }

        if (mouse.Kind == MouseEventKind.Wheel)
        {
            _lists[_tabs.ActiveIndex].Scroll(mouse.Button == MouseButton.WheelUp ? -3 : 3);
        }
        else
        {
            _tabs.HandleMouse(WorkspaceLayout.Translate(mouse, _tabBounds));
        }

        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default), large: true);
        _fits = view is not null;
        if (view is null)
        {
            return;
        }

        var frame = ModalFrame.Bounds(surface.Size, large: true);
        var content = WorkspaceLayout.ModalContent(new Size(frame.Width, frame.Height));
        _tabBounds = new Rect(frame.Left + content.Left, frame.Top + content.Top + 1, view.Size.Width, Math.Max(1, view.Size.Height - 2));
        _title.Draw(view, 0, 0, _error is null ? $"Repository memories / {_lists[_tabs.ActiveIndex].Name}" : "Error: " + TranscriptView.Safe(_error), _style(PresentationTextRole.SelectionPrompt));
        _tabs.ActiveStyle = _style(PresentationTextRole.AgentSelectedTabRole);
        _tabs.InactiveStyle = _style(PresentationTextRole.AgentNotSelectedTabRole);
        _tabs.Render(view.CreateView(new Rect(0, 1, _tabBounds.Width, _tabBounds.Height)));
        _footer.Draw(view, 0, view.Size.Height - 1, _busy ? "Applying memory change..." : view.Size.Width < 80 ? "Ins: add Enter: edit Del: delete Esc" : "Ins: add  Enter/F4: edit  Del: delete  Tab: category  Up/Down: select  PgUp/PgDn: scroll  F2: IDs  Esc: close", _style(PresentationTextRole.Status));
    }

    /// <summary>Reconciles host results without losing the active tab.</summary>
    internal void CompleteChange(RepositoryMemoryListSnapshot? snapshot, string? error = null)
    {
        _busy = false;
        if (snapshot is not null)
        {
            _lists[0].Update(snapshot.Entries.Where(item => item.Ownership == RepositoryMemoryOwnership.Ordinary && item.MemoryType == RepositoryMemoryType.StandingPreference).ToArray(), snapshot.Warnings);
            _lists[1].Update(snapshot.Entries.Where(item => item.Ownership == RepositoryMemoryOwnership.Ordinary && item.MemoryType == RepositoryMemoryType.Situational).ToArray(), snapshot.Warnings);
            _lists[2].Update(snapshot.Entries.Where(item => item.Ownership == RepositoryMemoryOwnership.Intelligence).ToArray(), snapshot.Warnings);
        }

        _error = error;
    }

    private sealed class MemoryListView : IWidget
    {
        private readonly Func<PresentationTextRole, CellStyle> _style;
        private readonly List<Row> _rows = [];
        private readonly Dictionary<RepositoryMemoryId, EntryLayout> _layouts = [];
        private readonly CachedTextRun[] _text = [.. Enumerable.Range(0, 128).Select(_ => new CachedTextRun())];
        private IReadOnlyList<RepositoryMemoryListItem> _entries;
        private IReadOnlyList<string> _warnings;
        private int _textIndex;
        private int _width;
        private int _top;
        private int _page = 1;
        private int _originX;
        private int _textX;
        private bool _fullIds;
        private int _selected;
        private RepositoryMemoryId? _rowEntry;
        private bool _reveal;

        internal RepositoryMemoryListItem? Selected => _entries.Count == 0 ? null : _entries[_selected];

        internal void Update(IReadOnlyList<RepositoryMemoryListItem> entries, IReadOnlyList<string> warnings)
        {
            if (_entries.SequenceEqual(entries) && _warnings.SequenceEqual(warnings, StringComparer.Ordinal))
            {
                return;
            }

            var id = Selected?.Id;
            var retained = -1;
            var currentIds = entries.Select(entry => entry.Id).ToHashSet();
            foreach (var removed in _layouts.Keys.Where(key => !currentIds.Contains(key)).ToArray())
            {
                _layouts.Remove(removed);
            }

            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].Id == id)
                {
                    retained = index;
                    break;
                }
            }

            _selected = retained >= 0 ? retained : Math.Clamp(_selected, 0, Math.Max(0, entries.Count - 1));
            _entries = entries;
            _warnings = warnings;
            _width = 0;
            _reveal = true;
        }

        internal MemoryListView(string name, IReadOnlyList<RepositoryMemoryListItem> entries, IReadOnlyList<string> warnings, Func<PresentationTextRole, CellStyle> style)
        {
            Name = name;
            _entries = entries;
            _warnings = warnings;
            _style = style;
        }

        internal string Name { get; }

        public Size Measure(Size available) => available;

        public void Render(ISurface surface)
        {
            _textIndex = 0;
            if (_width != surface.Size.Width)
            {
                Reflow(surface.Size.Width);
            }

            _page = Math.Max(1, surface.Size.Height - 2);
            Scroll(0);
            if (_reveal)
            {
                var row = _rows.FindIndex(item => item.Entry is { } id && id == Selected?.Id);
                if (row >= 0 && (row < _top || row >= _top + _page))
                {
                    _top = row;
                    Scroll(0);
                }

                _reveal = false;
            }

            Draw(surface, 0, 0, $"{_entries.Count} entries  |  {_top + (_rows.Count > 0 ? 1 : 0)}-{Math.Min(_rows.Count, _top + _page)} of {_rows.Count} lines", PresentationTextRole.Status);
            Draw(surface, 0, 1, "ID", PresentationTextRole.SelectionPrompt);
            Draw(surface, _originX, 1, "Origin", PresentationTextRole.SelectionPrompt);
            Draw(surface, _textX, 1, "Memory", PresentationTextRole.SelectionPrompt);
            for (var index = 0; index < Math.Min(_page, _rows.Count - _top); index++)
            {
                var row = _rows[_top + index];
                Draw(surface, 0, index + 2, (row.Entry is not null && row.Entry == Selected?.Id ? ">" : " ") + row.Id, row.Entry is not null && row.Entry == Selected?.Id ? PresentationTextRole.SelectionPrompt : PresentationTextRole.Status);
                Draw(surface, _originX, index + 2, row.Origin, PresentationTextRole.Status);
                Draw(surface, _textX, index + 2, row.Text, row.Role);
            }
        }

        internal void HandleKey(KeyEvent key)
        {
            if (key.Code == KeyCode.F2)
            {
                _fullIds = !_fullIds;
                _width = 0;
                return;
            }

            if (key.Code is KeyCode.Up or KeyCode.Down)
            {
                _selected = Math.Clamp(_selected + (key.Code == KeyCode.Up ? -1 : 1), 0, Math.Max(0, _entries.Count - 1));
                _reveal = true;
                return;
            }

            if (key.Code is KeyCode.Home or KeyCode.End)
            {
                _selected = key.Code == KeyCode.Home ? 0 : Math.Max(0, _entries.Count - 1);
            }

            Scroll(key.Code switch
            {
                KeyCode.Up => -1,
                KeyCode.Down => 1,
                KeyCode.PageUp => -_page,
                KeyCode.PageDown => _page,
                KeyCode.Home => -_rows.Count,
                KeyCode.End => _rows.Count,
                _ => 0,
            });
        }

        internal void Scroll(int delta) => _top = Math.Clamp(_top + delta, 0, Math.Max(0, _rows.Count - _page));

        private void Reflow(int width)
        {
            _width = width;
            _rows.Clear();
            _rowEntry = null;
            var wideIds = _fullIds && width >= 80;
            _originX = wideIds ? 38 : 10;
            _textX = _originX + 9;
            var textWidth = Math.Max(1, width - _textX);
            foreach (var warning in _warnings)
            {
                AddLines(string.Empty, string.Empty, "Warning: " + warning, textWidth, PresentationTextRole.Warning);
            }

            if (_entries.Count == 0)
            {
                AddLines(string.Empty, string.Empty, "No entries in this category.", textWidth, PresentationTextRole.Status);
            }

            foreach (var entry in _entries)
            {
                if (_layouts.TryGetValue(entry.Id, out var cached) && cached.Text == entry.Text
                    && cached.Origin == entry.Origin && cached.Width == width && cached.FullIds == _fullIds)
                {
                    _rows.AddRange(cached.Rows);
                }
                else
                {
                    var start = _rows.Count;
                    _rowEntry = entry.Id;
                    var id = entry.Id.Value.ToString("D");
                    AddLines(wideIds ? id : id[..8], entry.Origin.ToString().ToLowerInvariant(), entry.Text, textWidth, PresentationTextRole.Memory);
                    if (_fullIds && !wideIds)
                    {
                        AddLines(string.Empty, string.Empty, "ID: " + id, textWidth, PresentationTextRole.Status);
                    }

                    _layouts[entry.Id] = new(entry.Text, entry.Origin, width, _fullIds, [.. _rows.GetRange(start, _rows.Count - start)]);
                }

                _rows.Add(new(string.Empty, string.Empty, string.Empty, PresentationTextRole.Default, null));
            }

            if (_entries.Count > 0)
            {
                _rows.RemoveAt(_rows.Count - 1);
            }
        }

        private void AddLines(string id, string origin, string text, int width, PresentationTextRole role)
        {
            foreach (var paragraph in TranscriptView.Safe(text).ReplaceLineEndings("\n").Split('\n'))
            {
                var lines = paragraph.Length == 0 ? [string.Empty] : TextWrapper.Wrap(StyledText.From(paragraph), width).Select(line => line.ToPlainString()).ToArray();
                foreach (var line in lines)
                {
                    _rows.Add(new(id, origin, line, role, _rowEntry));
                    id = origin = string.Empty;
                }
            }
        }

        private void Draw(ISurface surface, int x, int y, string text, PresentationTextRole role)
        {
            if (y < surface.Size.Height)
            {
                _text[_textIndex++ % _text.Length].Draw(surface, x, y, text, _style(role));
            }
        }

        private sealed record EntryLayout(string Text, RepositoryMemoryOrigin Origin, int Width, bool FullIds, Row[] Rows);

        private sealed record Row(string Id, string Origin, string Text, PresentationTextRole Role, RepositoryMemoryId? Entry);
    }
}
