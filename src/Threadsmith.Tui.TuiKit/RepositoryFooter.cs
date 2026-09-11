namespace Threadsmith.Tui.TuiKit;

using System.Globalization;
using Threadsmith.Core;
using TUIKit;

/// <summary>Fits repository context and every Git counter into one measured row.</summary>
internal static class RepositoryFooter
{
    /// <summary>Formats a bounded footer, including compact unknown and partial markers.</summary>
    internal static string Format(string folder, string? fallbackBranch, RepositoryGitStatus? git, int width, string separator)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var counts = git is null ? "Git ?" : $"S{Count(git.Staged)} M{Count(git.Modified)} U{Count(git.Untracked)} !{Count(git.Conflicts)}{(git.IsTruncated ? "+" : string.Empty)}";
        var branch = TranscriptView.Safe(git is { Detached: true } ? "detached" : git?.Branch ?? fallbackBranch ?? "?");
        var safeFolder = TranscriptView.Safe(folder);
        var gap = UnicodeWidth.GetWidth(separator) > 3 ? " | " : separator;
        var available = Math.Max(0, width - UnicodeWidth.GetWidth(counts + gap + gap));
        var branchBudget = Math.Min(available / 2, Math.Max(6, width / 3));
        branch = Clip(branch, branchBudget);
        safeFolder = Clip(safeFolder, Math.Max(0, available - UnicodeWidth.GetWidth(branch)));
        return Clip(safeFolder + gap + branch + gap + counts, width);
    }

    private static string Count(int value) => value > 999 ? "999+" : value.ToString(CultureInfo.InvariantCulture);

    private static string Clip(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        return UnicodeWidth.GetWidth(value) <= width ? value : value[..UnicodeWidth.GetLengthThatFits(value, width - 1)] + "…";
    }
}
