namespace Threadsmith.Core;

using System.Text;

/// <summary>Shared bounded, linear-space text comparison for mutation previews and cumulative execution results.</summary>
public static class UnifiedTextDiff
{
    /// <summary>Renders a unified diff using the configured comparison-work bound.</summary>
    public static string Create(
        string relativePath,
        string? before,
        string? after,
        int maximumDiffLinesForLcs,
        out int addedLines,
        out int removedLines,
        CancellationToken cancellationToken = default)
    {
        _ = TryCreate(
            relativePath,
            before,
            after,
            maximumDiffLinesForLcs,
            int.MaxValue,
            out var diff,
            out addedLines,
            out removedLines,
            cancellationToken);
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
        out int removedLines,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDiffLinesForLcs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputCharacters);
        cancellationToken.ThrowIfCancellationRequested();
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

        var workBudget = checked((long)maximumDiffLinesForLcs * maximumDiffLinesForLcs);
        foreach (var (prefix, line) in CompareLines(oldLines, newLines, new(workBudget, cancellationToken)))
        {
            if (!TryAppendLine(builder, prefix, line, maximumOutputCharacters))
            {
                diff = string.Empty;
                return false;
            }

            addedLines += prefix == '+' ? 1 : 0;
            removedLines += prefix == '-' ? 1 : 0;
        }

        diff = builder.ToString();
        return true;
    }

    // Myers' bidirectional shortest-edit search splits the problem without retaining a
    // quadratic matrix or frontier history. Equal edges are removed before each search.
    private static IEnumerable<(char Prefix, string Line)> CompareLines(
        string[] before,
        string[] after,
        ComparisonWorkBudget workBudget)
    {
        var pending = new Stack<(int OldStart, int OldEnd, int NewStart, int NewEnd)>();
        pending.Push((0, before.Length, 0, after.Length));
        while (pending.TryPop(out var range))
        {
            var (oldStart, oldEnd, newStart, newEnd) = range;
            while (oldStart < oldEnd && newStart < newEnd && before[oldStart] == after[newStart])
            {
                if (!workBudget.TrySpend())
                {
                    foreach (var item in CompareAsReplacement(before, oldStart, oldEnd, after, newStart, newEnd, workBudget))
                    {
                        yield return item;
                    }

                    oldStart = oldEnd;
                    newStart = newEnd;
                    break;
                }

                yield return (' ', before[oldStart++]);
                newStart++;
            }

            if (oldStart == oldEnd || newStart == newEnd)
            {
                foreach (var item in CompareAsReplacement(before, oldStart, oldEnd, after, newStart, newEnd, workBudget))
                {
                    yield return item;
                }

                continue;
            }

            var suffix = 0;
            while (oldStart < oldEnd - suffix && newStart < newEnd - suffix
                && before[oldEnd - suffix - 1] == after[newEnd - suffix - 1])
            {
                if (!workBudget.TrySpend())
                {
                    suffix = 0;
                    break;
                }

                suffix++;
            }

            if (workBudget.Exhausted)
            {
                foreach (var item in CompareAsReplacement(before, oldStart, oldEnd, after, newStart, newEnd, workBudget))
                {
                    yield return item;
                }

                continue;
            }

            if (suffix > 0)
            {
                pending.Push((oldEnd - suffix, oldEnd, newEnd - suffix, newEnd));
                oldEnd -= suffix;
                newEnd -= suffix;
            }

            // No shared line means deletion/insertion is the exact shortest edit, not
            // a size-dependent fallback. This also keeps complete replacements linear.
            var oldValues = new HashSet<string>(StringComparer.Ordinal);
            for (var index = oldStart; index < oldEnd; index++)
            {
                if (!workBudget.TrySpend())
                {
                    break;
                }

                oldValues.Add(before[index]);
            }

            var sharesLine = false;
            for (var index = newStart; index < newEnd && !sharesLine; index++)
            {
                if (!workBudget.TrySpend())
                {
                    break;
                }

                sharesLine = oldValues.Contains(after[index]);
            }

            if (!sharesLine || workBudget.Exhausted)
            {
                foreach (var item in CompareAsReplacement(before, oldStart, oldEnd, after, newStart, newEnd, workBudget))
                {
                    yield return item;
                }

                continue;
            }

            if (!TryFindSplit(
                before.AsSpan(oldStart, oldEnd - oldStart),
                after.AsSpan(newStart, newEnd - newStart),
                workBudget,
                out var split))
            {
                foreach (var item in CompareAsReplacement(before, oldStart, oldEnd, after, newStart, newEnd, workBudget))
                {
                    yield return item;
                }

                continue;
            }

            var (oldSplit, newSplit) = split;
            pending.Push((oldStart + oldSplit, oldEnd, newStart + newSplit, newEnd));
            pending.Push((oldStart, oldStart + oldSplit, newStart, newStart + newSplit));
        }
    }

    private static bool TryFindSplit(
        ReadOnlySpan<string> before,
        ReadOnlySpan<string> after,
        ComparisonWorkBudget workBudget,
        out (int Old, int New) split)
    {
        split = default;
        var maximumDistance = checked((int)(((long)before.Length + after.Length + 1) / 2));
        var offset = maximumDistance + 1;
        var forward = new int[checked((2 * maximumDistance) + 3)];
        var reverse = new int[forward.Length];
        Array.Fill(forward, -1);
        Array.Fill(reverse, -1);
        forward[offset + 1] = 0;
        reverse[offset + 1] = 0;
        var delta = before.Length - after.Length;
        var oddDelta = (delta & 1) != 0;
        var forwardStart = 0;
        var forwardEnd = 0;
        var reverseStart = 0;
        var reverseEnd = 0;
        for (var distance = 0; distance <= maximumDistance; distance++)
        {
            for (var diagonal = -distance + forwardStart; diagonal <= distance - forwardEnd; diagonal += 2)
            {
                if (!workBudget.TrySpend())
                {
                    return false;
                }

                var position = offset + diagonal;
                var x = diagonal == -distance || (diagonal != distance && forward[position - 1] < forward[position + 1])
                    ? forward[position + 1] : forward[position - 1] + 1;
                var y = x - diagonal;
                while (x < before.Length && y < after.Length && before[x] == after[y])
                {
                    if (!workBudget.TrySpend())
                    {
                        return false;
                    }

                    x++;
                    y++;
                }

                forward[position] = x;
                if (x > before.Length)
                {
                    forwardEnd += 2;
                }
                else if (y > after.Length)
                {
                    forwardStart += 2;
                }
                else if (oddDelta && offset + delta - diagonal is var opposite && opposite >= 0 && opposite < reverse.Length
                    && reverse[opposite] != -1 && x >= before.Length - reverse[opposite])
                {
                    split = (x, y);
                    return true;
                }
            }

            for (var diagonal = -distance + reverseStart; diagonal <= distance - reverseEnd; diagonal += 2)
            {
                if (!workBudget.TrySpend())
                {
                    return false;
                }

                var position = offset + diagonal;
                var x = diagonal == -distance || (diagonal != distance && reverse[position - 1] < reverse[position + 1])
                    ? reverse[position + 1] : reverse[position - 1] + 1;
                var y = x - diagonal;
                while (x < before.Length && y < after.Length && before[before.Length - x - 1] == after[after.Length - y - 1])
                {
                    if (!workBudget.TrySpend())
                    {
                        return false;
                    }

                    x++;
                    y++;
                }

                reverse[position] = x;
                if (x > before.Length)
                {
                    reverseEnd += 2;
                }
                else if (y > after.Length)
                {
                    reverseStart += 2;
                }
                else if (!oddDelta && offset + delta - diagonal is var opposite && opposite >= 0 && opposite < forward.Length
                    && forward[opposite] != -1 && forward[opposite] >= before.Length - x)
                {
                    var oldSplit = forward[opposite];
                    split = (oldSplit, oldSplit - delta + diagonal);
                    return true;
                }
            }
        }

        throw new InvalidOperationException("No shortest-edit split was found.");
    }

    private static IEnumerable<(char Prefix, string Line)> CompareAsReplacement(
        string[] before,
        int oldStart,
        int oldEnd,
        string[] after,
        int newStart,
        int newEnd,
        ComparisonWorkBudget workBudget)
    {
        for (var index = oldStart; index < oldEnd; index++)
        {
            workBudget.CheckCancellation();
            yield return ('-', before[index]);
        }

        for (var index = newStart; index < newEnd; index++)
        {
            workBudget.CheckCancellation();
            yield return ('+', after[index]);
        }
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

    private sealed class ComparisonWorkBudget
    {
        private readonly CancellationToken _cancellationToken;
        private long _remaining;

        public ComparisonWorkBudget(long remaining, CancellationToken cancellationToken)
        {
            _remaining = remaining;
            _cancellationToken = cancellationToken;
        }

        public bool Exhausted => _remaining == 0;

        public void CheckCancellation()
        {
            _cancellationToken.ThrowIfCancellationRequested();
        }

        public bool TrySpend()
        {
            CheckCancellation();
            if (_remaining == 0)
            {
                return false;
            }

            _remaining--;
            return true;
        }
    }
}
