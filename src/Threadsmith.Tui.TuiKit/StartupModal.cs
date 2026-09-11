namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>A modal startup projection that discards all ordinary keys and paste.</summary>
internal sealed class StartupModal : Modal
{
    private readonly string _logo;
    private readonly string _label;
    private readonly long _started;
    private readonly Action _cancel;
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly IReadOnlyList<string> _completed;
    private readonly IReadOnlyList<string> _details;
    private readonly CachedTextRun _text = new();

    /// <summary>Initializes a new instance of the <see cref="StartupModal"/> class.</summary>
    internal StartupModal(string logo, string label, IReadOnlyList<string> completed, Action cancel, Func<PresentationTextRole, CellStyle> style, IReadOnlyList<string>? details = null)
    {
        _logo = logo;
        _label = label;
        _completed = completed;
        _details = details ?? [];
        _started = TimeProvider.System.GetTimestamp();
        _cancel = cancel;
        _style = style;
    }

    /// <summary>Gets the real monotonic duration of this operation.</summary>
    internal TimeSpan Elapsed => TimeProvider.System.GetElapsedTime(_started);

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code == KeyCode.Escape || (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl))
        {
            _cancel();
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text) => true;

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default));
        if (view is null)
        {
            return;
        }

        var lines = _logo.ReplaceLineEndings("\n").Split('\n');
        var logoHeight = Math.Min(lines.Length, Math.Max(1, view.Size.Height - _completed.Count - _details.Count - 4));
        for (var index = 0; index < logoHeight; index++)
        {
            _text.Draw(view, 0, index, lines[index], _style(PresentationTextRole.Brand));
        }

        var y = logoHeight + 1;
        foreach (var detail in _details.Take(Math.Max(0, view.Size.Height - y - 2)))
        {
            _text.Draw(view, 0, y++, detail, _style(PresentationTextRole.Status));
        }

        foreach (var phase in _completed.Take(Math.Max(0, view.Size.Height - y - 2)))
        {
            _text.Draw(view, 0, y++, phase, _style(PresentationTextRole.Success));
        }

        _text.Draw(view, 0, y++, $"{_label} · {Elapsed.TotalSeconds:0.0}s", _style(PresentationTextRole.Status));
        _text.Draw(view, 0, y, "Loading — input discarded; Ctrl+C cancels", _style(PresentationTextRole.Muted));
    }
}
