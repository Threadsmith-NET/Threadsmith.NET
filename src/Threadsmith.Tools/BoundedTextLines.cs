namespace Threadsmith.Tools;

using System.Text;

/// <summary>A line page selected from either a live file or immutable source text.</summary>
public sealed record TextLinePage(IReadOnlyList<string> Lines, bool ContentLimitReached);

/// <summary>Shared UTF-8 byte and line limits for ordinary and frozen-source read tools.</summary>
public static class BoundedTextLines
{
    /// <summary>Selects complete lines without exceeding the configured UTF-8 content budget.</summary>
    /// <param name="lines">Already decoded source lines.</param>
    /// <param name="startLine">One-based first requested line.</param>
    /// <param name="maximumLines">Maximum complete lines to select.</param>
    /// <param name="maximumBytes">UTF-8 content bound.</param>
    /// <param name="countTrailingNewline">Whether each selected line reserves a trailing newline.</param>
    /// <param name="cancellationToken">Stops selection.</param>
    public static TextLinePage Select(
        IReadOnlyList<string> lines,
        int startLine,
        int maximumLines,
        int maximumBytes,
        bool countTrailingNewline = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        var selected = new List<string>();
        var bytes = 0;
        var contentLimit = false;
        for (var index = Math.Min(startLine - 1, lines.Count); index < lines.Count && selected.Count < maximumLines; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Encoding.UTF8.GetByteCount(lines[index]);
            var separator = countTrailingNewline || selected.Count > 0 ? 1 : 0;
            if ((long)bytes + separator + length > maximumBytes)
            {
                contentLimit = true;
                break;
            }

            selected.Add(lines[index]);
            bytes += separator + length;
        }

        return new TextLinePage(selected, contentLimit);
    }
}
