namespace Threadsmith.Core;

using System.Text;

/// <summary>Shared bounded text comparison for mutation previews and cumulative execution results.</summary>
public static class UnifiedTextDiff
{
    /// <summary>Renders a unified diff using the configured LCS size bound.</summary>
    public static string Create(
        string relativePath,
        string? before,
        string? after,
        int maximumDiffLinesForLcs,
        out int addedLines,
        out int removedLines)
    {
        _ = TryCreate(
            relativePath,
            before,
            after,
            maximumDiffLinesForLcs,
            int.MaxValue,
            out var diff,
            out addedLines,
            out removedLines);
        return diff;
    }

    /// <summary>Attempts to render a unified diff without exceeding the configured output bound.</summary>
    public static bool TryCreate(
        string relativePath,
        string? before,
        string? after,
        int maximumDiffLinesForLcs,
        int maximumOutputCharacters,
        out string diff,
        out int addedLines,
        out int removedLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDiffLinesForLcs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputCharacters);
        addedLines = 0;
        removedLines = 0;
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            diff = string.Empty;
            return true;
        }

        var oldLines = before?.ReplaceLineEndings("\n").Split('\n') ?? [];
        var newLines = after?.ReplaceLineEndings("\n").Split('\n') ?? [];
        var builder = new StringBuilder();
        if (!TryAppendLine(builder, $"--- {(before is null ? "/dev/null" : $"a/{relativePath}")}", maximumOutputCharacters)
            || !TryAppendLine(builder, $"+++ {(after is null ? "/dev/null" : $"b/{relativePath}")}", maximumOutputCharacters)
            || !TryAppendLine(builder, $"@@ -1,{oldLines.Length} +1,{newLines.Length} @@", maximumOutputCharacters))
        {
            diff = string.Empty;
            return false;
        }

        var matrixCells = ((long)oldLines.Length + 1) * ((long)newLines.Length + 1);
        if (matrixCells > Array.MaxLength
            || (long)oldLines.Length * newLines.Length > (long)maximumDiffLinesForLcs * maximumDiffLinesForLcs)
        {
            foreach (var line in oldLines)
            {
                if (!TryAppendLine(builder, '-', line, maximumOutputCharacters))
                {
                    diff = string.Empty;
                    return false;
                }

                removedLines++;
            }

            foreach (var line in newLines)
            {
                if (!TryAppendLine(builder, '+', line, maximumOutputCharacters))
                {
                    diff = string.Empty;
                    return false;
                }

                addedLines++;
            }

            diff = builder.ToString();
            return true;
        }

        var lengths = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var oldIndex = oldLines.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Length - 1; newIndex >= 0; newIndex--)
            {
                lengths[oldIndex, newIndex] = string.Equals(
                    oldLines[oldIndex],
                    newLines[newIndex],
                    StringComparison.Ordinal)
                    ? lengths[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(lengths[oldIndex + 1, newIndex], lengths[oldIndex, newIndex + 1]);
            }
        }

        var oldCursor = 0;
        var newCursor = 0;
        while (oldCursor < oldLines.Length || newCursor < newLines.Length)
        {
            if (oldCursor < oldLines.Length
                && newCursor < newLines.Length
                && string.Equals(oldLines[oldCursor], newLines[newCursor], StringComparison.Ordinal))
            {
                if (!TryAppendLine(builder, ' ', oldLines[oldCursor], maximumOutputCharacters))
                {
                    diff = string.Empty;
                    return false;
                }

                oldCursor++;
                newCursor++;
            }
            else if (newCursor < newLines.Length
                && (oldCursor == oldLines.Length
                    || lengths[oldCursor, newCursor + 1] >= lengths[oldCursor + 1, newCursor]))
            {
                if (!TryAppendLine(builder, '+', newLines[newCursor], maximumOutputCharacters))
                {
                    diff = string.Empty;
                    return false;
                }

                newCursor++;
                addedLines++;
            }
            else
            {
                if (!TryAppendLine(builder, '-', oldLines[oldCursor], maximumOutputCharacters))
                {
                    diff = string.Empty;
                    return false;
                }

                oldCursor++;
                removedLines++;
            }
        }

        diff = builder.ToString();
        return true;
    }

    private static bool TryAppendLine(
        StringBuilder builder,
        string value,
        int maximumOutputCharacters)
    {
        return TryAppend(builder, value, maximumOutputCharacters)
            && TryAppend(builder, Environment.NewLine, maximumOutputCharacters);
    }

    private static bool TryAppendLine(
        StringBuilder builder,
        char prefix,
        string value,
        int maximumOutputCharacters)
    {
        if (1L + value.Length + Environment.NewLine.Length > maximumOutputCharacters - (long)builder.Length)
        {
            return false;
        }

        builder.Append(prefix).AppendLine(value);
        return true;
    }

    private static bool TryAppend(
        StringBuilder builder,
        string value,
        int maximumOutputCharacters)
    {
        if (value.Length > maximumOutputCharacters - (long)builder.Length)
        {
            return false;
        }

        builder.Append(value);
        return true;
    }
}
