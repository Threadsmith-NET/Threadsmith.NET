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
    private Rect _saveBounds;
    private Rect _cancelBounds;
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

    /// <summary>Gets serialized save requests.</summary>
    internal ChannelReader<string> Saves => _saves.Reader;

    /// <summary>Gets whether the draft differs from the original and can be saved.</summary>
    internal bool CanSave => !_busy && !string.Equals(_original, _editor.Text, StringComparison.Ordinal);

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
                if (Contains(_saveBounds, mouse))
                {
                    Save();
                }
                else if (Contains(_cancelBounds, mouse))
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
        var height = Math.Max(1, view.Size.Height - 4);
        _editorBounds = new Rect(left, top + 1, view.Size.Width, height);
        _saveBounds = new Rect(left, top + height + 1, 16, 1);
        _cancelBounds = new Rect(left, top + height + 2, 24, 1);
        _title.Draw(view, 0, 0, "Edit memory | " + (_editor.Overwrite ? "Overwrite" : "Insert"), _style(PresentationTextRole.SelectionPrompt));
        _editor.Style = _style(PresentationTextRole.Default);
        _editor.Render(view.CreateView(new Rect(0, 1, view.Size.Width, height)));
        _save.Draw(view, 0, height + 1, (_focus == 1 ? ">" : " ") + "[Save Changes]" + (CanSave ? string.Empty : " (disabled)"), _style(CanSave ? PresentationTextRole.SelectionPrompt : PresentationTextRole.Status));
        _cancel.Draw(view, 0, height + 2, (_focus == 2 ? ">" : " ") + "[Cancel, No Changes]", _style(PresentationTextRole.SelectionPrompt));
        _hint.Draw(view, 0, height + 3, _busy ? "Saving..." : _error is not null ? TranscriptView.Safe(_error) : "Tab: focus  Ctrl+S: save  Esc: cancel", _style(_error is null ? PresentationTextRole.Status : PresentationTextRole.Warning));
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

    private static bool Contains(Rect bounds, MouseEvent mouse)
        => mouse.X >= bounds.Left && mouse.X < bounds.Left + bounds.Width && mouse.Y >= bounds.Top && mouse.Y < bounds.Top + bounds.Height;
}
