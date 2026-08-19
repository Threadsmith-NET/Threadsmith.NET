namespace Threadsmith.Tui;

using PrettyPrompt.Rendering;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;

/// <summary>Immutable terminal-neutral state displayed beside the active composer.</summary>
/// <param name="Folder">Host-reported active working folder.</param>
/// <param name="Repository">Active repository display name.</param>
/// <param name="Model">Effective model display name and id.</param>
/// <param name="Reasoning">Effective reasoning level.</param>
/// <param name="ContextTokens">Latest governed request token estimate, or <see langword="null"/>.</param>
/// <param name="ContextLimit">Effective request context limit, or <see langword="null"/>.</param>
/// <param name="Usage">Cumulative provider usage for the active session.</param>
internal sealed record TuiSessionStatus(
    string Folder,
    string Repository,
    string Model,
    ReasoningLevel Reasoning,
    long? ContextTokens,
    long? ContextLimit,
    SessionUsageSnapshot Usage)
{
    /// <summary>Gets the current local Git branch, or null when unavailable or detached.</summary>
    internal string? Branch { get; init; }
}

/// <summary>Builds immutable status state from host-owned repository, model, and context projections.</summary>
internal static class TuiSessionStatusFactory
{
    /// <summary>Gets a repository display name, preserving filesystem roots that have no file name.</summary>
    /// <param name="repositoryPath">Absolute repository path.</param>
    /// <returns>The final path component, or the trimmed path when it is a filesystem root.</returns>
    internal static string GetRepositoryDisplayName(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var trimmedPath = Path.TrimEndingDirectorySeparator(repositoryPath);
        var repositoryName = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(repositoryName) ? trimmedPath : repositoryName;
    }

    /// <summary>Creates an effective composer status snapshot.</summary>
    /// <param name="folder">Current host working folder.</param>
    /// <param name="repository">Current repository display name.</param>
    /// <param name="fallbackModel">Fallback model status when no configured profile is active.</param>
    /// <param name="profile">Effective configured model profile, or <see langword="null"/>.</param>
    /// <param name="reasoning">Effective reasoning level.</param>
    /// <param name="inspection">Latest governed context inspection, or <see langword="null"/>.</param>
    /// <param name="usage">Current session usage snapshot.</param>
    /// <param name="branch">Current local Git branch, or null when unavailable or detached.</param>
    /// <returns>An immutable terminal-neutral status snapshot.</returns>
    internal static TuiSessionStatus Create(
        string folder,
        string repository,
        string fallbackModel,
        ModelProfile? profile,
        ReasoningLevel reasoning,
        ContextInspectionProjection? inspection,
        SessionUsageSnapshot usage,
        string? branch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackModel);
        ArgumentNullException.ThrowIfNull(usage);

        long? contextLimit = profile?.ContextWindow ?? inspection?.TokenBudget;

        return new TuiSessionStatus(
            folder,
            repository,
            profile?.Name ?? fallbackModel,
            reasoning,
            inspection?.EstimatedTokens,
            contextLimit,
            usage)
        {
            Branch = branch,
        };
    }
}

/// <summary>Produces bounded single-line status text without taking ownership of terminal scrollback.</summary>
internal static class TuiSessionStatusFormatter
{
    private const int MaximumSourceLength = 512;
    private const string Ellipsis = "…";

    /// <summary>Formats a responsive status row for the available terminal width.</summary>
    /// <param name="status">Immutable host-derived status.</param>
    /// <param name="availableWidth">Available terminal columns.</param>
    /// <param name="separator">Validated theme-provided separator.</param>
    /// <returns>A single-line row, or an empty string when no safe layout fits.</returns>
    internal static string Format(TuiSessionStatus status, int availableWidth, string separator)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(separator);
        if (availableWidth <= 0)
        {
            return string.Empty;
        }

        var model = $"model {Bound(status.Model)} ({status.Reasoning.ToString().ToLowerInvariant()})";
        var context = status.ContextTokens is { } used && status.ContextLimit is { } limit && limit > 0
            ? $"ctx ~{FormatCount(used)}/{FormatCount(limit)} {Math.Min(999, (used * 100m) / limit):0}%"
            : "ctx --";
        var estimate = status.Usage.IsEstimate ? "~" : string.Empty;
        var tokenValue = !status.Usage.HasObservation
            ? "--"
            : status.Usage.HasUnknownUsage
            ? status.Usage.TotalTokens == 0
                ? "--"
                : $"{estimate}{FormatCount(status.Usage.TotalTokens)}+?"
            : $"{estimate}{FormatCount(status.Usage.TotalTokens)}";
        var tokens = $"tokens {tokenValue}";
        var repository = $"repo {Bound(status.Repository)}";
        var branch = $"branch {Bound(status.Branch ?? "--")}";
        var folder = $"folder {AbbreviatePath(status.Folder)}";

        string[] layouts =
        [
            string.Join(separator, folder, repository, branch, model, context, tokens),
            string.Join(separator, repository, branch, model, context, tokens),
            string.Join(separator, model, context, tokens),
        ];
        foreach (var layout in layouts)
        {
            if (GetWidth(layout) <= availableWidth)
            {
                return PadToWidth(layout, availableWidth);
            }
        }

        var fixedWidth = GetWidth(string.Join(separator, context, tokens)) + GetWidth(separator);
        var modelWidth = availableWidth - fixedWidth;
        var reasoning = $" ({status.Reasoning.ToString().ToLowerInvariant()})";
        const string modelPrefix = "model: ";
        var modelValueWidth = modelWidth - GetWidth(modelPrefix) - GetWidth(reasoning);
        if (modelValueWidth >= GetWidth(Ellipsis))
        {
            var compactModel = $"{modelPrefix}{Truncate(Bound(status.Model), modelValueWidth)}{reasoning}";
            var compact = string.Join(separator, compactModel, context, tokens);
            if (GetWidth(compact) <= availableWidth)
            {
                return PadToWidth(compact, availableWidth);
            }
        }

        return string.Empty;
    }

    private static string Bound(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var safe = string.Concat(value
            .Take(MaximumSourceLength)
            .Select(character => char.IsControl(character) ? ' ' : character));
        return string.IsNullOrWhiteSpace(safe) ? "--" : safe.Trim();
    }

    private static string AbbreviatePath(string value)
    {
        var safe = Bound(value);
        if (GetWidth(safe) <= 36)
        {
            return safe;
        }

        var normalized = safe.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            return Truncate(safe, 36);
        }

        var prefix = normalized.StartsWith('/')
            ? "/"
            : normalized.Length >= 2 && normalized[1] == ':'
                ? normalized[..2] + "/"
                : string.Empty;
        return Truncate($"{prefix}{Ellipsis}/{segments[^2]}/{segments[^1]}", 36);
    }

    private static string FormatCount(long value)
    {
        var safe = Math.Max(0, value);
        return safe switch
        {
            >= 1_000_000_000 => $"{safe / 1_000_000_000m:0.#}b",
            >= 1_000_000 => $"{safe / 1_000_000m:0.#}m",
            >= 1_000 => $"{safe / 1_000m:0.#}k",
            _ => safe.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static int GetWidth(string value)
    {
        return UnicodeWidth.GetWidth(value.AsSpan());
    }

    private static string PadToWidth(string value, int width)
    {
        var padding = width - GetWidth(value);
        return padding <= 0 ? value : value + new string(' ', padding);
    }

    private static string Truncate(string value, int width)
    {
        if (GetWidth(value) <= width)
        {
            return value;
        }

        var ellipsisWidth = GetWidth(Ellipsis);
        if (width <= ellipsisWidth)
        {
            return width == ellipsisWidth ? Ellipsis : string.Empty;
        }

        var length = UnicodeWidth.GetLengthThatFits(value.AsSpan(), width - ellipsisWidth);
        return string.Concat(value.AsSpan(0, length), Ellipsis);
    }
}
