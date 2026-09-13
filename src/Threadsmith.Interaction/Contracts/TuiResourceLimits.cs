namespace Threadsmith.Interaction.Contracts;

/// <summary>Resource limits for interactive presentation.</summary>
public sealed record TuiResourceLimits
{
    /// <summary>Maximum buffered keystrokes in the PrettyPrompt frontend.</summary>
    public int MaximumBufferedKeys { get; init; } = 100000;

    /// <summary>Maximum editable draft and pasted UTF-8 bytes.</summary>
    public int MaximumDraftBytes { get; init; } = 1048576;

    /// <summary>Maximum retained undo text bytes.</summary>
    public int MaximumUndoBytes { get; init; } = 1048576;

    /// <summary>Maximum retained undo operations.</summary>
    public int MaximumUndoEntries { get; init; } = 200;

    /// <summary>Maximum submitted history UTF-8 bytes per composer.</summary>
    public int MaximumHistoryBytes { get; init; } = 1048576;

    /// <summary>Maximum submitted entries per composer.</summary>
    public int MaximumHistoryEntries { get; init; } = 1000;

    /// <summary>Clipboard helper timeout across Windows, Linux, and macOS.</summary>
    public int ClipboardTimeoutMilliseconds { get; init; } = 2000;

    /// <summary>Maximum UTF-8 bytes copied to the terminal clipboard.</summary>
    public int MaximumClipboardCopyBytes { get; init; } = 65536;

    /// <summary>Maximum selectable entries in an enable/disable modal.</summary>
    public int MaximumToggleOptions { get; init; } = 2048;

    /// <summary>Maximum retained source or projected text bytes per transcript.</summary>
    public int MaximumTranscriptBytes { get; init; } = 524288;

    /// <summary>Maximum retained logical transcript chunks.</summary>
    public int MaximumTranscriptLines { get; init; } = 1024;

    /// <summary>Maximum characters per projected line chunk.</summary>
    public int MaximumTranscriptLineCharacters { get; init; } = 16384;

    /// <summary>Maximum cached glyph rows per transcript.</summary>
    public int MaximumGlyphCacheRows { get; init; } = 512;

    /// <summary>Maximum links offered by the output link selector.</summary>
    public int MaximumLinks { get; init; } = 512;

    /// <summary>Aggregate child source and projected text bytes shared among agent tabs.</summary>
    public int MaximumChildTranscriptBytes { get; init; } = 4194304;

    /// <summary>Aggregate projected child transcript chunks.</summary>
    public int MaximumChildTranscriptLines { get; init; } = 8192;

    /// <summary>Maximum recently closed delegation records.</summary>
    public int MaximumRetainedDelegations { get; init; } = 64;

    /// <summary>Maximum child output updates pending agent registration.</summary>
    public int MaximumPendingAgentUpdates { get; init; } = 256;

    /// <summary>Maximum child progress summary characters.</summary>
    public int MaximumAgentProgressCharacters { get; init; } = 240;

    /// <summary>Markdown parser and semantic validation limits.</summary>
    public Threadsmith.Interaction.Markdown.MarkdownRenderingLimits Markdown { get; init; } = new();

    /// <summary>Maximum modal search/filter characters.</summary>
    public int MaximumFilterCharacters { get; init; } = 256;

    /// <summary>Maximum command palette title characters.</summary>
    public int MaximumCommandTitleCharacters { get; init; } = 512;

    /// <summary>Maximum configured names per role or shared list.</summary>
    public int MaximumAgentNames { get; init; } = 128;

    /// <summary>Maximum normalized agent-name UTF-16 units.</summary>
    public int MaximumAgentNameCharacters { get; init; } = 32;

    /// <summary>Maximum configured themes.</summary>
    public int MaximumThemes { get; init; } = 32;

    /// <summary>Maximum theme display-name characters.</summary>
    public int MaximumThemeNameCharacters { get; init; } = 80;

    /// <summary>Maximum theme spinner, marker, or separator characters.</summary>
    public int MaximumThemeUiCharacters { get; init; } = 40;

    /// <summary>Maximum retained semantic-refresh start identities.</summary>
    public int MaximumTrackedRefreshStarts { get; init; } = 128;

    /// <summary>Maximum delegation summaries displayed.</summary>
    public int MaximumDisplayedDelegations { get; init; } = 12;

    /// <summary>Maximum retained delegation status records.</summary>
    public int MaximumTrackedDelegations { get; init; } = 64;

    /// <summary>Maximum expanded tool or memory output characters.</summary>
    public int MaximumToolInspectionCharacters { get; init; } = 98304;

    /// <summary>Rejects nonpositive limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBufferedKeys);
        ArgumentNullException.ThrowIfNull(Markdown);
        Markdown.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFilterCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCommandTitleCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAgentNames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAgentNameCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumThemes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumThemeNameCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumThemeUiCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTrackedRefreshStarts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDisplayedDelegations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTrackedDelegations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumToolInspectionCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDraftBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumUndoBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumUndoEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHistoryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHistoryEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ClipboardTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumClipboardCopyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumToggleOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTranscriptBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTranscriptLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTranscriptLineCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumGlyphCacheRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLinks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumChildTranscriptBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumChildTranscriptLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRetainedDelegations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPendingAgentUpdates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAgentProgressCharacters);
    }
}
