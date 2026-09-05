namespace Threadsmith.Tui.TuiKit;

using TUIKit.Unicode;

/// <summary>Measures whole text elements through the selected backend's public width API.</summary>
internal static class UnicodeWidth
{
    /// <summary>Measures display cells using the pinned terminal backend's grapheme rules.</summary>
    internal static int GetWidth(string text)
    {
        var width = 0;
        foreach (var element in Graphemes.Split(text))
        {
            width += Math.Max(1, element.Width);
        }

        return width;
    }

    /// <summary>Returns the UTF-16 length that fits without splitting a grapheme.</summary>
    internal static int GetLengthThatFits(string text, int width)
    {
        var length = 0;
        foreach (var element in Graphemes.Split(text))
        {
            var cells = Math.Max(1, element.Width);
            if (cells > width)
            {
                break;
            }

            length += element.Text.Length;
            width -= cells;
        }

        return length;
    }
}
