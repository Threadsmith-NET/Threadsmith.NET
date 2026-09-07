namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Contracts;
using TUIKit;
using TUIKit.Input;
using TUIKit.Widgets;

/// <summary>Keeps transient suggestion selection and dismissal separate from the editable draft.</summary>
internal sealed class ComposerAutocomplete : IDisposable
{
    private readonly ComposerCommandCompletion _completion;
    private readonly AutocompleteOverlay _overlay;
    private readonly Action<string> _accept;
    private Observation? _observed;

    /// <summary>Initializes a new instance of the <see cref="ComposerAutocomplete"/> class.</summary>
    internal ComposerAutocomplete(TuiKitCommandDiscovery discovery, ComposerCommandCompletion completion, Action<string> accept)
    {
        _completion = completion;
        _accept = accept;
        _overlay = new AutocompleteOverlay(discovery) { MaxRows = 6 };
        _overlay.Accepted += _accept;
    }

    /// <summary>Gets the unchanged draft targeted by the current suggestions.</summary>
    internal ComposerCompletionTarget? Target { get; private set; }

    /// <summary>Gets whether suggestions currently own navigation and acceptance keys.</summary>
    internal bool IsVisible => _overlay.IsVisible;

    /// <inheritdoc />
    public void Dispose()
    {
        _overlay.Accepted -= _accept;
        _overlay.Hide();
        Target = null;
        _observed = null;
    }

    /// <summary>Recomputes only after a destination change, preserving navigation and dismissal across frames.</summary>
    internal void Refresh(ComposerBuffer buffer, ComposerPurpose purpose, long epoch, bool enabled)
    {
        var observation = new Observation(buffer, buffer.Revision, buffer.Caret, buffer.Anchor, purpose, epoch, enabled);
        if (observation == _observed)
        {
            return;
        }

        _observed = observation;
        Target = enabled ? _completion.Capture(buffer, purpose, epoch) : null;
        if (Target is { } target)
        {
            _overlay.SetInput(target.Prefix);
        }
        else
        {
            _overlay.Hide();
        }
    }

    /// <summary>Gives suggestions first refusal only for unmodified keys.</summary>
    internal bool HandleKey(KeyEvent key)
    {
        // TUIKit's overlay ignores modifiers; modified editor keys must retain their meaning.
        return key.Modifiers == KeyModifiers.None && _overlay.HandleKey(key);
    }

    /// <summary>Anchors suggestions within the composer or transcript without covering persistent rows.</summary>
    internal void Render(BufferSurface root, Rect composer, Rect activity, (int X, int Y) caret, CellStyle normal, CellStyle highlight)
    {
        if (!_overlay.IsVisible)
        {
            return;
        }

        _overlay.Style = normal;
        _overlay.HighlightStyle = highlight.WithAttribute(CellAttributes.Reverse, true);
        var rows = Math.Min(_overlay.MaxRows, _overlay.Suggestions.Count);
        var width = _overlay.Suggestions.Max(UnicodeWidth.GetWidth) + 1;
        var x = Math.Clamp(caret.X, 0, Math.Max(0, composer.Width - width));
        if (caret.Y + 1 + rows <= composer.Height)
        {
            _overlay.RenderAt(root.CreateView(composer), x, caret.Y);
        }
        else
        {
            // The activity row separates composer and transcript. Flip into the transcript
            // instead of covering activity or truncating a selectable suggestion at that row.
            var view = root.CreateView(new Rect(0, 0, root.Size.Width, activity.Top));
            _overlay.RenderAt(view, composer.Left + x, activity.Top);
        }
    }

    private sealed record Observation(ComposerBuffer Buffer, int Revision, int Caret, int Anchor, ComposerPurpose Purpose, long Epoch, bool Enabled);
}
