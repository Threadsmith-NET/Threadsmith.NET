namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>Displays wrapped command usage and descriptions in native formatted columns.</summary>
internal sealed class CommandHelpModal : Modal
{
    private readonly InteractiveCommandDescriptor[] _commands;
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly Action _interrupt;
    private readonly Action _toggleMouse;
    private readonly CachedTextRun _title = new();
    private IReadOnlyList<string> _lines = [];
    private CachedTextRun[] _runs = [];
    private int _width;
    private int _height = 1;
    private int _top;

    /// <summary>Initializes a new instance of the <see cref="CommandHelpModal"/> class.</summary>
    internal CommandHelpModal(IReadOnlyList<InteractiveCommandDescriptor> commands, Func<PresentationTextRole, CellStyle> style, Action interrupt, Action toggleMouse)
    {
        _commands = [.. commands];
        _style = style;
        _interrupt = interrupt;
        _toggleMouse = toggleMouse;
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
        else
        {
            var last = Math.Max(0, _lines.Count - _height);
            _top = key.Code switch
            {
                KeyCode.Up => Math.Max(0, _top - 1),
                KeyCode.Down => Math.Min(last, _top + 1),
                KeyCode.PageUp => Math.Max(0, _top - _height),
                KeyCode.PageDown => Math.Min(last, _top + _height),
                KeyCode.Home => 0,
                KeyCode.End => last,
                _ => _top,
            };
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

        if (_width != view.Size.Width)
        {
            Reflow(view.Size.Width);
        }

        _title.Draw(view, 0, 0, "Commands — PgUp/PgDn; Esc closes", _style(PresentationTextRole.SelectionPrompt));
        _height = view.Size.Height - 2;
        _top = Math.Min(_top, Math.Max(0, _lines.Count - _height));
        for (var index = _top; index < Math.Min(_lines.Count, _top + _height); index++)
        {
            _runs[index].Draw(view, 0, index - _top + 2, _lines[index], _style(index == 0 ? PresentationTextRole.SelectionPrompt : PresentationTextRole.Default));
        }
    }

    private void Reflow(int width)
    {
        _width = width;
        var commandWidth = Math.Min(42, (width - 2) / 2);
        var descriptionWidth = width - commandWidth - 2;
        List<IReadOnlyList<string>> rows = [["Command", "Description"]];
        foreach (var command in _commands)
        {
            var usage = Wrap(command.Usage, commandWidth);
            var description = Wrap(command.Description, descriptionWidth);
            for (var row = 0; row < Math.Max(usage.Length, description.Length); row++)
            {
                rows.Add([row < usage.Length ? usage[row] : string.Empty, row < description.Length ? description[row] : string.Empty]);
            }
        }

        _lines = [.. ColumnFormatter.Format(rows), string.Empty, .. Wrap("Submit any other text to Threadsmith.", width)];
        _runs = [.. _lines.Select(_ => new CachedTextRun())];
    }

    private static string[] Wrap(string text, int width) => [..
        TextWrapper.Wrap(StyledText.From(TranscriptView.Safe(text).ReplaceLineEndings(" ")), width)
            .Select(line => line.ToPlainString())];
}
