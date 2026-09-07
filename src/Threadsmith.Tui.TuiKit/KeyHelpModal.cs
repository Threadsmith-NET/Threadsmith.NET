namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>Displays noninteractive keyboard help over the application frame.</summary>
internal sealed class KeyHelpModal : Modal
{
    private readonly string[] _entries;
    private readonly CachedTextRun[] _entryRuns;
    private readonly CachedTextRun _titleRun = new();
    private readonly string _title;
    private int _top;
    private int _height = 1;

    /// <summary>Initializes a new instance of the <see cref="KeyHelpModal"/> class.</summary>
    internal KeyHelpModal(string title, IReadOnlyList<string> entries)
    {
        _title = title;
        _entries = [.. entries];
        _entryRuns = new CachedTextRun[_entries.Length];
        for (var index = 0; index < _entryRuns.Length; index++)
        {
            _entryRuns[index] = new CachedTextRun();
        }
    }

    /// <summary>Gets or sets semantic role resolution for the current theme.</summary>
    internal Func<PresentationTextRole, CellStyle> ResolveStyle { get; set; } = _ => CellStyle.Default;

    /// <summary>Gets or sets the explicit terminal-selection toggle.</summary>
    internal Action? ToggleMouse { get; set; }

    /// <inheritdoc/>
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code is KeyCode.Escape or KeyCode.F1)
        {
            RequestClose(null);
        }
        else if (key.Code == KeyCode.F12)
        {
            ToggleMouse?.Invoke();
        }
        else
        {
            _top = key.Code switch
            {
                KeyCode.Up => Math.Max(0, _top - 1),
                KeyCode.Down => Math.Min(Math.Max(0, _entries.Length - _height), _top + 1),
                KeyCode.PageUp => Math.Max(0, _top - _height),
                KeyCode.PageDown => Math.Min(Math.Max(0, _entries.Length - _height), _top + _height),
                KeyCode.Home => 0,
                KeyCode.End => Math.Max(0, _entries.Length - _height),
                _ => _top,
            };
        }

        return true;
    }

    /// <inheritdoc/>
    public override void Render(ISurface surface)
    {
        var view = ModalFrame.Create(surface, ResolveStyle(PresentationTextRole.Default));
        if (view is null)
        {
            return;
        }

        _titleRun.Draw(view, 1, 0, _title, ResolveStyle(PresentationTextRole.SelectionPrompt));
        _height = view.Size.Height - 1;
        _top = Math.Min(_top, Math.Max(0, _entries.Length - _height));
        for (var index = _top; index < _entries.Length && index - _top < _height; index++)
        {
            _entryRuns[index].Draw(view, 2, index - _top + 1, _entries[index], ResolveStyle(PresentationTextRole.Default));
        }
    }
}
