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

        return true;
    }

    /// <inheritdoc/>
    public override void Render(ISurface surface)
    {
        if (surface is not BufferSurface buffer || surface.Size.Width < 40 || surface.Size.Height < 12)
        {
            return;
        }

        // Cover the complete application frame above the persistent status row. Leaving a
        // margin here exposes characters from the transcript and composer beneath the modal.
        var area = new Rect(0, 0, surface.Size.Width, surface.Size.Height - 1);
        var view = buffer.CreateView(area);
        view.Fill(new Rect(0, 0, area.Width, area.Height), Cell.Blank(ResolveStyle(PresentationTextRole.Default)));
        _titleRun.Draw(view, 1, 0, _title, ResolveStyle(PresentationTextRole.SelectionPrompt));

        for (var index = 0; index < _entries.Length && index + 1 < area.Height; index++)
        {
            _entryRuns[index].Draw(view, 2, index + 1, _entries[index], ResolveStyle(PresentationTextRole.Default));
        }
    }
}
