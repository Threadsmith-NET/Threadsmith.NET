namespace Threadsmith.Core;

using System.Collections.Immutable;

/// <summary>Captured timeout preferences and mandatory restrictions, kept distinct from fallback defaults.</summary>
public sealed record ToolTimeoutPolicy
{
    /// <summary>Fallback used only when no tool-specific preference exists.</summary>
    public TimeSpan? Default { get; init; }

    /// <summary>Explicit tool preferences, including legacy tool-specific configuration.</summary>
    public ImmutableDictionary<string, TimeSpan> ByTool { get; init; } = ImmutableDictionary.Create<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mandatory machine-wide execution ceiling, independent of ordinary defaults.</summary>
    public TimeSpan? Maximum { get; init; }

    /// <summary>Mandatory per-tool execution ceilings.</summary>
    public ImmutableDictionary<string, TimeSpan> MaximumByTool { get; init; } = ImmutableDictionary.Create<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Selects a preference without treating a fallback as a ceiling.</summary>
    public TimeSpan Select(string toolId, TimeSpan declared, TimeSpan? configured = null) =>
        ByTool.TryGetValue(toolId, out var specific) ? specific : configured ?? Default ?? declared;

    /// <summary>Applies both global and tool-specific mandatory ceilings.</summary>
    public TimeSpan Clamp(string toolId, TimeSpan selected)
    {
        selected = Limit(selected, Maximum);
        return Limit(selected, MaximumByTool.TryGetValue(toolId, out var specific) ? specific : null);
    }

    private static TimeSpan Limit(TimeSpan selected, TimeSpan? maximum) =>
        maximum is { } value && value != Timeout.InfiniteTimeSpan
            && (selected == Timeout.InfiniteTimeSpan || selected > value) ? value : selected;
}
