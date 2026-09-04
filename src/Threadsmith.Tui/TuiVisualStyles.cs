namespace Threadsmith.Tui;

using System.Collections.Frozen;
using System.Globalization;
using Threadsmith.Interaction.Presentation;

/// <summary>Identifies terminal decorations that a theme may request.</summary>
[Flags]
internal enum TuiTextDecoration
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Strikethrough = 16,
    Invert = 32,
}

/// <summary>Represents a validated named or RGB terminal color.</summary>
internal sealed record TuiColor
{
    private const int MaximumColorLength = 32;

    private static readonly FrozenSet<string> NamedColors = new[]
    {
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "grey", "brightblack", "brightred", "brightgreen", "brightyellow", "brightblue",
        "brightmagenta", "brightcyan", "brightwhite",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private TuiColor(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical color name or uppercase <c>#RRGGBB</c> value.</summary>
    internal string Value { get; }

    /// <summary>Parses a bounded console color name or <c>#RRGGBB</c> value.</summary>
    /// <param name="value">Color text to parse.</param>
    /// <returns>A validated color.</returns>
    /// <exception cref="ArgumentException">The value is not a supported color.</exception>
    internal static TuiColor Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumColorLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("Theme colors must be bounded and cannot contain control characters.", nameof(value));
        }

        if (value.Length == 7 && value[0] == '#'
            && int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return new TuiColor(value.ToUpperInvariant());
        }

        if (NamedColors.Contains(value))
        {
            return new TuiColor(value.ToLowerInvariant());
        }

        throw new ArgumentException($"Unsupported theme color '{value}'.", nameof(value));
    }
}

/// <summary>Defines optional terminal colors and decorations for one semantic role.</summary>
/// <param name="Foreground">Foreground color, or <see langword="null"/> to inherit.</param>
/// <param name="Background">Background color, or <see langword="null"/> to inherit.</param>
/// <param name="Decorations">Requested decorations.</param>
internal sealed record TuiTextStyle(
    TuiColor? Foreground = null,
    TuiColor? Background = null,
    TuiTextDecoration? Decorations = null)
{
    private const TuiTextDecoration AllDecorations = TuiTextDecoration.Bold
        | TuiTextDecoration.Dim
        | TuiTextDecoration.Italic
        | TuiTextDecoration.Underline
        | TuiTextDecoration.Strikethrough
        | TuiTextDecoration.Invert;

    /// <summary>Ensures that no unknown decoration bits enter the terminal adapter.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An unsupported decoration was supplied.</exception>
    internal void Validate()
    {
        if ((Decorations.GetValueOrDefault() & ~AllDecorations) != TuiTextDecoration.None)
        {
            throw new ArgumentOutOfRangeException(nameof(Decorations), "The theme contains an unsupported decoration.");
        }
    }
}

/// <summary>Maps semantic terminal roles to validated styles.</summary>
internal sealed record TuiTheme
{
    private const int MaximumIdLength = 40;

    /// <summary>Initializes a new instance of the <see cref="TuiTheme"/> class.</summary>
    /// <param name="id">Stable theme identifier.</param>
    /// <param name="styles">Role styles. Duplicate keys must have been rejected by the configuration parser.</param>
    internal TuiTheme(string id, IEnumerable<KeyValuePair<PresentationTextRole, TuiTextStyle>> styles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(styles);
        if (id.Length > MaximumIdLength || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Theme ids may contain only letters, digits, '-' and '_'.", nameof(id));
        }

        var copiedStyles = new Dictionary<PresentationTextRole, TuiTextStyle>();
        foreach ((var role, var style) in styles)
        {
            ArgumentNullException.ThrowIfNull(style);
            style.Validate();
            if (!copiedStyles.TryAdd(role, style))
            {
                throw new ArgumentException($"Theme role '{role}' is defined more than once.", nameof(styles));
            }
        }

        Id = id;
        Styles = copiedStyles.ToFrozenDictionary();
    }

    /// <summary>Gets the stable theme identifier.</summary>
    internal string Id { get; }

    /// <summary>Gets the immutable role-style map.</summary>
    internal IReadOnlyDictionary<PresentationTextRole, TuiTextStyle> Styles { get; }

    /// <summary>Gets the terminal-native compiled theme.</summary>
    internal static TuiTheme System { get; } = new(
        "system",
        Enum.GetValues<PresentationTextRole>()
            .Select(role => KeyValuePair.Create(role, GetSystemStyle(role))));

    private static TuiTextStyle GetSystemStyle(PresentationTextRole role)
    {
        return role switch
        {
            PresentationTextRole.SessionStatus => new TuiTextStyle(Decorations: TuiTextDecoration.Invert),
            PresentationTextRole.MarkdownHeading or PresentationTextRole.MarkdownStrong or PresentationTextRole.MarkdownListMarker
                => new TuiTextStyle(Decorations: TuiTextDecoration.Bold),
            PresentationTextRole.MarkdownEmphasis => new TuiTextStyle(Decorations: TuiTextDecoration.Italic),
            PresentationTextRole.MarkdownStrikethrough => new TuiTextStyle(Decorations: TuiTextDecoration.Strikethrough),
            PresentationTextRole.MarkdownQuote or PresentationTextRole.MarkdownTableBorder
                => new TuiTextStyle(Decorations: TuiTextDecoration.Dim),
            _ => new TuiTextStyle(),
        };
    }
}

/// <summary>Resolves partial theme styles through the system theme and terminal defaults.</summary>
internal sealed class TuiThemeResolver
{
    private readonly TuiTheme _theme;
    private readonly bool _suppressStyles;

    /// <summary>Initializes a new instance of the <see cref="TuiThemeResolver"/> class.</summary>
    /// <param name="theme">Active theme.</param>
    /// <param name="suppressStyles">Whether colors and decorations must be suppressed.</param>
    internal TuiThemeResolver(TuiTheme theme, bool suppressStyles = false)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        _suppressStyles = suppressStyles;
    }

    /// <summary>Determines whether terminal styling must be suppressed for accessibility or capability fallback.</summary>
    /// <param name="isOutputRedirected">Whether standard output is redirected.</param>
    /// <param name="noColor">Value of the standard <c>NO_COLOR</c> variable.</param>
    /// <param name="terminal">Value of the terminal capability variable.</param>
    /// <returns><see langword="true"/> when output must remain plain text.</returns>
    internal static bool ShouldSuppressStyles(bool isOutputRedirected, string? noColor, string? terminal)
    {
        return GetSuppressionReason(isOutputRedirected, noColor, terminal) is not null;
    }

    /// <summary>Gets a bounded diagnostic reason when terminal styling must be suppressed.</summary>
    /// <param name="isOutputRedirected">Whether standard output is redirected.</param>
    /// <param name="noColor">Value of the standard <c>NO_COLOR</c> variable.</param>
    /// <param name="terminal">Value of the terminal capability variable.</param>
    /// <returns>A host-owned reason code, or <see langword="null"/> when styles are supported.</returns>
    internal static string? GetSuppressionReason(bool isOutputRedirected, string? noColor, string? terminal)
    {
        return isOutputRedirected
                ? "redirected-output"
                : noColor is not null
                    ? "no-color"
                    : string.Equals(terminal, "dumb", StringComparison.OrdinalIgnoreCase)
                        ? "limited-terminal"
                        : null;
    }

    /// <summary>Resolves a semantic role without introducing terminal-library types.</summary>
    /// <param name="role">Role to resolve.</param>
    /// <returns>The resolved style.</returns>
    internal TuiTextStyle Resolve(PresentationTextRole role)
    {
        if (!Enum.IsDefined(role))
        {
            role = PresentationTextRole.Default;
        }

        if (_suppressStyles)
        {
            return new TuiTextStyle();
        }

        _theme.Styles.TryGetValue(PresentationTextRole.Default, out var themeDefault);
        _theme.Styles.TryGetValue(role, out var requested);
        var systemDefault = TuiTheme.System.Styles[PresentationTextRole.Default];
        var systemRole = TuiTheme.System.Styles.TryGetValue(role, out var value)
            ? value
            : systemDefault;
        var decorations = requested?.Decorations
            ?? systemRole.Decorations
            ?? themeDefault?.Decorations
            ?? systemDefault.Decorations
            ?? TuiTextDecoration.None;
        return new TuiTextStyle(
            requested?.Foreground ?? themeDefault?.Foreground ?? systemRole.Foreground ?? systemDefault.Foreground,
            requested?.Background ?? themeDefault?.Background ?? systemRole.Background ?? systemDefault.Background,
            decorations);
    }
}
