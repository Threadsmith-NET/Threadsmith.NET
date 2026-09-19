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
        addedLines = 0;
        removedLines = 0;
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var oldLines = before?.ReplaceLineEndings("\n").Split('\n') ?? [];
        var newLines = after?.ReplaceLineEndings("\n").Split('\n') ?? [];
        var builder = new StringBuilder();
        builder.Append("--- ").Append(before is null ? "/dev/null" : $"a/{relativePath}").AppendLine();
        builder.Append("+++ ").Append(after is null ? "/dev/null" : $"b/{relativePath}").AppendLine();
        builder.Append("@@ -1,").Append(oldLines.Length)
            .Append(" +1,").Append(newLines.Length).AppendLine(" @@");
        var matrixCells = ((long)oldLines.Length + 1) * ((long)newLines.Length + 1);
        if (matrixCells > Array.MaxLength
            || (long)oldLines.Length * newLines.Length > (long)maximumDiffLinesForLcs * maximumDiffLinesForLcs)
        {
            foreach (var line in oldLines)
            {
                builder.Append('-').AppendLine(line);
                removedLines++;
            }

            foreach (var line in newLines)
            {
                builder.Append('+').AppendLine(line);
                addedLines++;
            }

            return builder.ToString();
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
                builder.Append(' ').AppendLine(oldLines[oldCursor]);
                oldCursor++;
                newCursor++;
            }
            else if (newCursor < newLines.Length
                && (oldCursor == oldLines.Length
                    || lengths[oldCursor, newCursor + 1] >= lengths[oldCursor + 1, newCursor]))
            {
                builder.Append('+').AppendLine(newLines[newCursor++]);
                addedLines++;
            }
            else
            {
                builder.Append('-').AppendLine(oldLines[oldCursor++]);
                removedLines++;
            }
        }

        return builder.ToString();
    }
}

