namespace Threadsmith.Tui;

using System.Collections.Frozen;
using System.Globalization;

/// <summary>Identifies the semantic purpose of text rendered by the interactive terminal.</summary>
internal enum TuiTextRole
{
    Default,
    Brand,
    Muted,
    Status,
    SessionStatus,
    Hyperlink,
    ToolSuccess,
    ToolFailure,
    SelectionPrompt,
    SelectionItem,
    SelectionHighlight,
    Success,
    Warning,
    Error,
    UserPrompt,
    ComposerPrompt,
    ThinkingIndicator,
    Reasoning,
    DiffAdded,
    DiffRemoved,
    DiffContext,
    MarkdownHeading,
    MarkdownStrong,
    MarkdownEmphasis,
    MarkdownStrikethrough,
    MarkdownCode,
    MarkdownQuote,
    MarkdownListMarker,
    MarkdownTableBorder,
}

/// <summary>Represents one terminal-neutral text fragment and its semantic rendering role.</summary>
/// <param name="Text">Visible text.</param>
/// <param name="Role">Semantic role.</param>
/// <param name="LinkTarget">Optional validated target for host-owned hyperlink text.</param>
internal sealed record TuiTextSegment(string Text, TuiTextRole Role, Uri? LinkTarget = null)
{
    private const int MaximumLinkLength = 2048;

    /// <summary>Validates text and any optional hyperlink metadata.</summary>
    /// <exception cref="ArgumentException">The hyperlink target is unsafe or unbounded.</exception>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Text);
        if (LinkTarget is null)
        {
            return;
        }

        var target = LinkTarget.OriginalString;
        if (!LinkTarget.IsAbsoluteUri
            || target.Length > MaximumLinkLength
            || target.Any(char.IsControl)
            || Uri.UnescapeDataString(target).Any(char.IsControl)
            || LinkTarget.Scheme is not "file" and not "http" and not "https")
        {
            throw new ArgumentException("Hyperlink targets must be bounded absolute file, HTTP, or HTTPS URIs.", nameof(LinkTarget));
        }
    }
}

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
    internal TuiTheme(string id, IEnumerable<KeyValuePair<TuiTextRole, TuiTextStyle>> styles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(styles);
        if (id.Length > MaximumIdLength || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Theme ids may contain only letters, digits, '-' and '_'.", nameof(id));
        }

        var copiedStyles = new Dictionary<TuiTextRole, TuiTextStyle>();
        foreach ((TuiTextRole role, TuiTextStyle style) in styles)
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
    internal IReadOnlyDictionary<TuiTextRole, TuiTextStyle> Styles { get; }

    /// <summary>Gets the terminal-native compiled theme.</summary>
    internal static TuiTheme System { get; } = new(
        "system",
        Enum.GetValues<TuiTextRole>()
            .Select(role => KeyValuePair.Create(role, GetSystemStyle(role))));

    private static TuiTextStyle GetSystemStyle(TuiTextRole role)
    {
        return role switch
        {
            TuiTextRole.SessionStatus => new TuiTextStyle(Decorations: TuiTextDecoration.Invert),
            TuiTextRole.MarkdownHeading or TuiTextRole.MarkdownStrong or TuiTextRole.MarkdownListMarker
                => new TuiTextStyle(Decorations: TuiTextDecoration.Bold),
            TuiTextRole.MarkdownEmphasis => new TuiTextStyle(Decorations: TuiTextDecoration.Italic),
            TuiTextRole.MarkdownStrikethrough => new TuiTextStyle(Decorations: TuiTextDecoration.Strikethrough),
            TuiTextRole.MarkdownQuote or TuiTextRole.MarkdownTableBorder
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
    internal TuiTextStyle Resolve(TuiTextRole role)
    {
        if (!Enum.IsDefined(role))
        {
            role = TuiTextRole.Default;
        }

        if (_suppressStyles)
        {
            return new TuiTextStyle();
        }

        _theme.Styles.TryGetValue(TuiTextRole.Default, out TuiTextStyle? themeDefault);
        _theme.Styles.TryGetValue(role, out TuiTextStyle? requested);
        TuiTextStyle systemDefault = TuiTheme.System.Styles[TuiTextRole.Default];
        TuiTextStyle systemRole = TuiTheme.System.Styles.TryGetValue(role, out TuiTextStyle? value)
            ? value
            : systemDefault;
        TuiTextDecoration decorations = requested?.Decorations
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
