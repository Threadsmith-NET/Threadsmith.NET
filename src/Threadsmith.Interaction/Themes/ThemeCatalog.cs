namespace Threadsmith.Interaction.Themes;

using System.Collections.Frozen;
using Threadsmith.Interaction.Presentation;

/// <summary>Validated theme-owned UI presentation settings.</summary>
/// <param name="Spinner">Validated spinner id.</param>
/// <param name="SelectionMarker">Marker reserved for selector rendering.</param>
/// <param name="FooterSeparator">Separator reserved for footer rendering.</param>
internal sealed record TuiThemeUi(string Spinner, string SelectionMarker, string FooterSeparator)
{
    /// <summary>Gets the compiled UI defaults.</summary>
    internal static TuiThemeUi Default { get; } = new("dots", ">", " | ");
}

/// <summary>Combines a validated theme with its display metadata.</summary>
/// <param name="Name">Bounded display name.</param>
/// <param name="Theme">Validated semantic theme.</param>
/// <param name="Ui">Validated UI settings.</param>
/// <param name="IsBuiltIn">Whether the entry is supplied by the host.</param>
internal sealed record ConfiguredTheme(string Name, TuiTheme Theme, TuiThemeUi Ui, bool IsBuiltIn);

/// <summary>Provides the ordered built-in and configured theme universe.</summary>
internal sealed class ConfiguredThemeCatalog
{
    private readonly FrozenDictionary<string, ConfiguredTheme> _byId;

    /// <summary>Initializes a new instance of the <see cref="ConfiguredThemeCatalog"/> class.</summary>
    internal ConfiguredThemeCatalog(IEnumerable<ConfiguredTheme> themes, IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(themes);
        var ordered = themes.ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one theme is required.", nameof(themes));
        }

        Themes = ordered;
        _byId = ordered.ToFrozenDictionary(item => item.Theme.Id, StringComparer.OrdinalIgnoreCase);
        Warnings = warnings?.ToArray() ?? [];
    }

    /// <summary>Gets themes in deterministic display order.</summary>
    internal IReadOnlyList<ConfiguredTheme> Themes { get; }

    /// <summary>Gets safe catalog construction warnings.</summary>
    internal IReadOnlyList<string> Warnings { get; }

    /// <summary>Finds a theme by its case-insensitive stable id.</summary>
    internal bool TryGet(string id, out ConfiguredTheme? theme)
    {
        return _byId.TryGetValue(id, out theme);
    }
}

/// <summary>Owns the active process-local theme preference.</summary>
internal sealed class SessionThemePreferences
{
    private readonly Lock _gate = new();
    private ConfiguredTheme _activeTheme;

    /// <summary>Initializes a new instance of the <see cref="SessionThemePreferences"/> class.</summary>
    internal SessionThemePreferences(ConfiguredThemeCatalog catalog, string? defaultThemeId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        if (!string.IsNullOrWhiteSpace(defaultThemeId)
            && catalog.TryGet(defaultThemeId, out var configured)
            && configured is not null)
        {
            _activeTheme = configured;
        }
        else
        {
            _activeTheme = catalog.Themes[0];
        }
    }

    /// <summary>Gets the selectable theme catalog.</summary>
    internal ConfiguredThemeCatalog Catalog { get; }

    /// <summary>Gets the active theme snapshot.</summary>
    internal ConfiguredTheme ActiveTheme
    {
        get
        {
            lock (_gate)
            {
                return _activeTheme;
            }
        }
    }

    /// <summary>Changes the active theme when the id is known.</summary>
    internal bool TrySelect(string id, out ConfiguredTheme? selected)
    {
        if (!Catalog.TryGet(id, out var configured) || configured is null)
        {
            selected = null;
            return false;
        }

        lock (_gate)
        {
            _activeTheme = configured;
        }

        selected = configured;
        return true;
    }
}

/// <summary>Persists the selected theme outside process-local presentation state.</summary>
internal interface IThemePreferenceStore
{
    /// <summary>Sets the user-level default theme while preserving unrelated configuration.</summary>
    Task SetDefaultThemeAsync(string themeId, CancellationToken cancellationToken = default);
}

/// <summary>Defines the compiled theme collection.</summary>
internal static class BuiltInThemes
{
    /// <summary>Creates the ordered built-in theme collection.</summary>
    internal static IReadOnlyList<ConfiguredTheme> Create()
    {
        return [
        new("System", TuiTheme.System, TuiThemeUi.Default, true),
        Create("forge-dark", "Forge Dark", "#D0D0D0", "#5FAFFF", "brightmagenta", "brightcyan", "brightgreen", "brightred"),
        Create("ocean", "Ocean", "#C6E7FF", "brightcyan", "#FF9FD6", "#5FAFFF", "#67E480", "#FF6B81"),
        Create("high-contrast", "High Contrast", "brightwhite", "brightcyan", "brightyellow", "brightmagenta", "brightgreen", "brightred", accessible: true),
    ];
    }

    private static ConfiguredTheme Create(
        string id,
        string name,
        string foreground,
        string accent,
        string thinking,
        string composerPrompt,
        string success,
        string failure,
        bool accessible = false)
    {
        var emphasis = accessible ? TuiTextDecoration.Bold | TuiTextDecoration.Underline : TuiTextDecoration.Bold;
        KeyValuePair<PresentationTextRole, TuiTextStyle>[] styles =
        [
            new(PresentationTextRole.Default, new TuiTextStyle(TuiColor.Parse(foreground))),
            new(PresentationTextRole.Brand, new TuiTextStyle(TuiColor.Parse(accent), Decorations: TuiTextDecoration.Bold)),
            new(PresentationTextRole.Hyperlink, new TuiTextStyle(TuiColor.Parse(accent), Decorations: TuiTextDecoration.Underline)),
            new(PresentationTextRole.ComposerPrompt, new TuiTextStyle(TuiColor.Parse(composerPrompt), Decorations: TuiTextDecoration.Bold)),
            new(PresentationTextRole.ThinkingIndicator, new TuiTextStyle(TuiColor.Parse(thinking), Decorations: emphasis)),
            new(PresentationTextRole.SessionStatus, new TuiTextStyle(Decorations: TuiTextDecoration.Invert)),
            new(PresentationTextRole.SelectionHighlight, new TuiTextStyle(TuiColor.Parse("black"), TuiColor.Parse(accent), emphasis)),
            new(PresentationTextRole.Success, new TuiTextStyle(TuiColor.Parse(success), Decorations: emphasis)),
            new(PresentationTextRole.ToolSuccess, new TuiTextStyle(TuiColor.Parse(success), Decorations: emphasis)),
            new(PresentationTextRole.Error, new TuiTextStyle(TuiColor.Parse(failure), Decorations: emphasis)),
            new(PresentationTextRole.ToolFailure, new TuiTextStyle(TuiColor.Parse(failure), Decorations: emphasis)),
            new(PresentationTextRole.Warning, new TuiTextStyle(TuiColor.Parse("brightyellow"), Decorations: emphasis)),
            new(PresentationTextRole.DiffAdded, new TuiTextStyle(TuiColor.Parse(success))),
            new(PresentationTextRole.DiffRemoved, new TuiTextStyle(TuiColor.Parse(failure))),
            new(PresentationTextRole.DiffContext, new TuiTextStyle(TuiColor.Parse(foreground))),
        ];
        return new ConfiguredTheme(name, new TuiTheme(id, styles), TuiThemeUi.Default, true);
    }
}
