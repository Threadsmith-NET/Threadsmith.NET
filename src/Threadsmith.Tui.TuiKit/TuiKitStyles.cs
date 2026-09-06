namespace Threadsmith.Tui.TuiKit;

using System.Globalization;
using Threadsmith.Interaction.Presentation;
using TUIKit;

/// <summary>Adapts the shared validated theme into backend-owned cell styles.</summary>
internal sealed class TuiKitStyles
{
    private readonly CellStyle[] _styles;

    /// <summary>Initializes a new instance of the <see cref="TuiKitStyles"/> class while respecting style suppression.</summary>
    internal TuiKitStyles(ConfiguredTheme theme, bool suppress)
    {
        var resolver = new TuiThemeResolver(theme.Theme, suppress);
        var roles = Enum.GetValues<PresentationTextRole>();
        var maximum = 0;
        foreach (var role in roles)
        {
            maximum = Math.Max(maximum, (int)role);
        }

        _styles = new CellStyle[maximum + 1];
        foreach (var role in roles)
        {
            _styles[(int)role] = Convert(resolver.Resolve(role));
        }
    }

    /// <summary>Returns the terminal style for a semantic presentation role.</summary>
    internal CellStyle Resolve(PresentationTextRole role)
    {
        var index = (int)role;
        return index >= 0 && index < _styles.Length ? _styles[index] : CellStyle.Default;
    }

    private static CellStyle Convert(TuiTextStyle style)
    {
        var attributes = CellAttributes.None;
        var decoration = style.Decorations.GetValueOrDefault();
        if ((decoration & TuiTextDecoration.Bold) != 0)
        {
            attributes |= CellAttributes.Bold;
        }

        if ((decoration & TuiTextDecoration.Dim) != 0)
        {
            attributes |= CellAttributes.Dim;
        }

        if ((decoration & TuiTextDecoration.Italic) != 0)
        {
            attributes |= CellAttributes.Italic;
        }

        if ((decoration & TuiTextDecoration.Underline) != 0)
        {
            attributes |= CellAttributes.Underline;
        }

        if ((decoration & TuiTextDecoration.Strikethrough) != 0)
        {
            attributes |= CellAttributes.Strikethrough;
        }

        if ((decoration & TuiTextDecoration.Invert) != 0)
        {
            attributes |= CellAttributes.Reverse;
        }

        return new CellStyle(ColorOf(style.Foreground), ColorOf(style.Background), attributes);
    }

    private static Color ColorOf(TuiColor? color)
    {
        if (color is null)
        {
            return Color.Default;
        }

        if (color.Value.StartsWith('#'))
        {
            return Color.FromRgb(int.Parse(color.Value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        var index = color.Value switch
        {
            "black" => 0,
            "red" => 1,
            "green" => 2,
            "yellow" => 3,
            "blue" => 4,
            "magenta" => 5,
            "cyan" => 6,
            "white" => 7,
            "grey" or "brightblack" => 8,
            "brightred" => 9,
            "brightgreen" => 10,
            "brightyellow" => 11,
            "brightblue" => 12,
            "brightmagenta" => 13,
            "brightcyan" => 14,
            "brightwhite" => 15,
            _ => -1,
        };
        return index >= 0 ? Color.FromPalette((byte)index) : Color.Default;
    }
}
