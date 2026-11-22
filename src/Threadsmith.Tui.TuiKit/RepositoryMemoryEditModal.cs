namespace Threadsmith.Tui.TuiKit;

using System.Text;
using System.Threading.Channels;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>Text-only memory editor sharing the ordinary composer's editing behavior.</summary>
internal sealed class RepositoryMemoryEditModal : Modal
{
    private readonly TuiKitComposer _editor;
    private readonly string _original;
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly Func<bool> _canHandleMouse;
    private readonly Action _toggleMouse;
    private readonly CachedTextRun _title = new();
    private readonly CachedTextRun _save = new();
    private readonly CachedTextRun _cancel = new();
    private readonly CachedTextRun _hint = new();
    private readonly Channel<string> _saves = Channel.CreateUnbounded<string>();
    private Rect _editorBounds;
    private Rect[] _saveBounds = [];
    private Rect[] _cancelBounds = [];
    private int _focus;
    private bool _busy;
    private bool _fits;
    private string? _error;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryEditModal"/> class.</summary>
    internal RepositoryMemoryEditModal(string text, Func<PresentationTextRole, CellStyle> style, TuiResourceLimits limits, Func<bool> canHandleMouse, Action toggleMouse)
    {
        _editor = new TuiKitComposer(limits) { Text = text };
        _original = _editor.Text;
        _style = style;
        _canHandleMouse = canHandleMouse;
        _toggleMouse = toggleMouse;
        _editor.OnFocusChanged(true);
    }

    /// <summary>Gets the editor heading for creation or editing.</summary>
    internal string Title { get; init; } = "Edit memory";

    /// <summary>Gets serialized save requests.</summary>
    internal ChannelReader<string> Saves => _saves.Reader;

    /// <summary>Gets whether the draft differs from the original and can be saved.</summary>
    internal bool CanSave => !_busy && !string.IsNullOrWhiteSpace(_editor.Text) && !string.Equals(_original, _editor.Text, StringComparison.Ordinal);

    /// <summary>Gets the current text draft.</summary>
    internal string Text => _editor.Text;

    /// <summary>Gets or sets the shared clipboard writer.</summary>
    internal Func<string, bool>? CopyRequested { get => _editor.CopyRequested; set => _editor.CopyRequested = value; }

    /// <summary>Gets or sets the text-only clipboard reader callback.</summary>
    internal Action? PasteRequested { get => _editor.PasteRequested; set => _editor.PasteRequested = value; }

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (_busy)
        {
            return true;
        }

        if (key.Code == KeyCode.Escape)
        {
            RequestClose(null);
        }
        else if (key.Code == KeyCode.F12)
        {
            _toggleMouse();
        }
        else if (!_fits)
        {
            return true;
        }
        else if (key.Code == KeyCode.Tab)
        {
            _focus = (_focus + ((key.Modifiers & KeyModifiers.Shift) != 0 ? 2 : 1)) % 3;
            _editor.OnFocusChanged(_focus == 0);
        }
        else if (key.Code == KeyCode.Character && key.Rune == 's' && key.Modifiers == KeyModifiers.Ctrl)
        {
            Save();
        }
        else if (_focus != 0 && key.Code == KeyCode.Enter)
        {
            if (_focus == 1)
            {
                Save();
            }
            else
            {
                RequestClose(null);
            }
        }
        else if (_focus == 0)
        {
            try
            {
                _editor.HandleKey(key);
            }
            catch (Exception exception) when (exception is InvalidOperationException or EncoderFallbackException or DecoderFallbackException)
            {
                _error = "Text exceeds the input limit or contains invalid Unicode; draft preserved.";
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text)
    {
        if (!_busy && _fits && _focus == 0)
        {
            try
            {
                _editor.InsertText(text);
            }
            catch (Exception exception) when (exception is InvalidOperationException or EncoderFallbackException or DecoderFallbackException)
            {
                _error = "Paste exceeds the input limit or contains invalid Unicode; draft preserved.";
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandleMouse(MouseEvent mouse)
    {
        if (!_busy && _fits && _canHandleMouse())
        {
            if (Contains(_editorBounds, mouse))
            {
                _focus = 0;
                _editor.OnFocusChanged(true);
                _editor.HandleMouse(WorkspaceLayout.Translate(mouse, _editorBounds));
            }
            else if (mouse.Kind == MouseEventKind.Press && mouse.Button == MouseButton.Left)
            {
                if (_saveBounds.Any(bounds => Contains(bounds, mouse)))
                {
                    Save();
                }
                else if (_cancelBounds.Any(bounds => Contains(bounds, mouse)))
                {
                    RequestClose(null);
                }
            }
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
        var left = frame.Left + content.Left;
        var top = frame.Top + content.Top;
        var titleRows = ModalFrame.Wrap(Title + " | " + (_editor.Overwrite ? "Overwrite" : "Insert"), view.Size.Width);
        var saveLabel = (_focus == 1 ? ">" : " ") + "[Save Changes]";
        var saveRows = ModalFrame.Wrap(saveLabel + (CanSave ? string.Empty : " (disabled)"), view.Size.Width);
        var cancelRows = ModalFrame.Wrap((_focus == 2 ? ">" : " ") + "[Cancel, No Changes]", view.Size.Width);
        var hintCapacity = Math.Max(0, view.Size.Height - titleRows.Length - saveRows.Length - cancelRows.Length - 1);
        var hintRows = ModalFrame.Wrap(_busy ? "Saving..." : _error ?? "Tab: focus  Ctrl+S: save  Esc: cancel", view.Size.Width, hintCapacity);
        var editorTop = Math.Min(view.Size.Height, titleRows.Length);
        var height = Math.Max(0, view.Size.Height - editorTop - saveRows.Length - cancelRows.Length - hintRows.Length);
        _fits = height > 0;
        var saveTop = editorTop + height;
        var cancelTop = Math.Min(view.Size.Height, saveTop + saveRows.Length);
        _editorBounds = new Rect(left, top + editorTop, view.Size.Width, height);
        _saveBounds = ControlBounds(ModalFrame.Wrap(saveLabel, view.Size.Width), left, top + saveTop, view.Size.Height - saveTop);
        _cancelBounds = ControlBounds(cancelRows, left, top + cancelTop, view.Size.Height - cancelTop);
        ModalFrame.DrawLines(view, _title, 0, titleRows, _style(PresentationTextRole.SelectionPrompt));
        _editor.Style = _style(PresentationTextRole.Default);
        if (height > 0)
        {
            _editor.Render(view.CreateView(new Rect(0, editorTop, view.Size.Width, height)));
        }

        ModalFrame.DrawLines(view, _save, saveTop, saveRows, _style(CanSave ? PresentationTextRole.SelectionPrompt : PresentationTextRole.Status));
        ModalFrame.DrawLines(view, _cancel, cancelTop, cancelRows, _style(PresentationTextRole.SelectionPrompt));
        ModalFrame.DrawLines(view, _hint, cancelTop + cancelRows.Length, hintRows, _style(_error is null ? PresentationTextRole.Status : PresentationTextRole.Warning));
    }

    /// <summary>Restores editing after a rejected save, preserving the draft.</summary>
    internal void SaveFailed(string error)
    {
        _busy = false;
        _error = error;
    }

    private void Save()
    {
        if (CanSave)
        {
            _busy = true;
            _saves.Writer.TryWrite(_editor.Text);
        }
    }

    private static Rect[] ControlBounds(IReadOnlyList<string> rows, int left, int top, int height)
        => [.. rows.Take(Math.Max(0, height)).Select((row, index) => new Rect(left, top + index, UnicodeWidth.GetWidth(row), 1))];

    private static bool Contains(Rect bounds, MouseEvent mouse)
        => mouse.X >= bounds.Left && mouse.X < bounds.Left + bounds.Width && mouse.Y >= bounds.Top && mouse.Y < bounds.Top + bounds.Height;
}
