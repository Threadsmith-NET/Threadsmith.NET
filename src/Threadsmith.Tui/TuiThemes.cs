namespace Threadsmith.Tui;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Threadsmith.Interaction.Presentation;

/// <summary>Atomically persists the default theme in the ordinary user configuration file.</summary>
internal sealed class UserConfigurationThemePreferenceStore : IThemePreferenceStore
{
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);
    private readonly string _configurationPath;

    /// <summary>Initializes a new instance of the <see cref="UserConfigurationThemePreferenceStore"/> class.</summary>
    internal UserConfigurationThemePreferenceStore(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        _configurationPath = Path.GetFullPath(configurationPath);
    }

    /// <inheritdoc />
    public async Task SetDefaultThemeAsync(string themeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themeId);
        var gate = Gates.GetOrAdd(_configurationPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_configurationPath)
                ?? throw new InvalidOperationException("The user configuration path has no parent directory.");
            Directory.CreateDirectory(directory);
            RejectReparsePoint(directory);
            RejectReparsePoint(_configurationPath);

            (var original, var root) = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
            var tui = GetOrCreateObject(root, "tui");
            SetProperty(tui, "defaultTheme", themeId);
            var updated = UpdateThemeDefault(original, themeId);
            if (updated.Length > MaximumConfigurationBytes)
            {
                throw new InvalidOperationException("The updated user configuration exceeds the supported size.");
            }

            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(
                    temporaryPath,
                    updated,
                    cancellationToken).ConfigureAwait(false);
                RejectReparsePoint(temporaryPath);
                File.Move(temporaryPath, _configurationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        KeyValuePair<string, JsonNode?>[] matches =
        [
            .. parent.Where(item => string.Equals(item.Key, propertyName, StringComparison.OrdinalIgnoreCase)),
        ];
        if (matches.Length > 1 || (matches.Length == 1 && matches[0].Value is not JsonObject))
        {
            throw new InvalidOperationException($"User configuration property '{propertyName}' must be one object.");
        }

        if (matches.Length == 1)
        {
            return matches[0].Value?.AsObject()
                ?? throw new InvalidOperationException($"User configuration property '{propertyName}' is invalid.");
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("User configuration paths cannot be links or reparse points.");
        }
    }

    private static void SetProperty(JsonObject parent, string propertyName, string value)
    {
        string[] matches =
        [
            .. parent.Select(item => item.Key)
                .Where(name => string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase)),
        ];
        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"User configuration property '{propertyName}' is duplicated.");
        }

        parent[matches.Length == 1 ? matches[0] : propertyName] = value;
    }

    private static byte[] UpdateThemeDefault(byte[] original, string themeId)
    {
        var location = LocateTheme(original);
        var serializedTheme = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(themeId));
        if (location.DefaultTheme is ValueLocation existing)
        {
            return ReplaceRange(original, existing.Start, existing.End, serializedTheme);
        }

        var property = Combine(Encoding.UTF8.GetBytes("\"defaultTheme\":"), serializedTheme);
        if (location.Tui is ObjectLocation tui)
        {
            return InsertProperty(original, tui, property);
        }

        var tuiProperty = Combine(
            Encoding.UTF8.GetBytes("\"tui\":{"),
            property,
            Encoding.UTF8.GetBytes("}"));
        return InsertProperty(original, location.Root, tuiProperty);
    }

    private static ThemeJsonLocation LocateTheme(byte[] original)
    {
        var prefixLength = GetUtf8BomPrefixLength(original);
        var reader = new Utf8JsonReader(original.AsSpan(prefixLength), new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Allow,
            MaxDepth = 64,
        });
        if (!ReadSignificantToken(ref reader) || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidOperationException("The user configuration root must be an object.");
        }

        var scan = ScanObject(ref reader, ObjectPurpose.Root, prefixLength);
        return new ThemeJsonLocation(scan.Location, scan.Tui, scan.DefaultTheme);
    }

    private static ObjectScanResult ScanObject(
        ref Utf8JsonReader reader,
        ObjectPurpose purpose,
        int prefixLength)
    {
        var hasProperties = false;
        var lastValueEnd = checked((int)reader.BytesConsumed) + prefixLength;
        ObjectLocation? tui = null;
        ValueLocation? defaultTheme = null;
        while (ReadSignificantToken(ref reader))
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                var closingBrace = checked((int)reader.TokenStartIndex) + prefixLength;
                return new ObjectScanResult(
                    new ObjectLocation(closingBrace, hasProperties, lastValueEnd),
                    tui,
                    defaultTheme);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidOperationException("The user configuration object is malformed.");
            }

            hasProperties = true;
            var propertyName = reader.GetString()
                ?? throw new InvalidOperationException("The user configuration property name is invalid.");
            if (!ReadSignificantToken(ref reader))
            {
                throw new InvalidOperationException("The user configuration property has no value.");
            }

            var valueStart = checked((int)reader.TokenStartIndex) + prefixLength;
            var isTui = purpose == ObjectPurpose.Root
                && string.Equals(propertyName, "tui", StringComparison.OrdinalIgnoreCase);
            var isDefaultTheme = purpose == ObjectPurpose.Tui
                && string.Equals(propertyName, "defaultTheme", StringComparison.OrdinalIgnoreCase);
            if (isTui && reader.TokenType == JsonTokenType.StartObject)
            {
                var nested = ScanObject(ref reader, ObjectPurpose.Tui, prefixLength);
                tui = nested.Location;
                defaultTheme = nested.DefaultTheme;
            }
            else
            {
                SkipValue(ref reader);
                if (isDefaultTheme)
                {
                    defaultTheme = new ValueLocation(
                        valueStart,
                        checked((int)reader.BytesConsumed) + prefixLength);
                }
            }

            lastValueEnd = checked((int)reader.BytesConsumed) + prefixLength;
        }

        throw new InvalidOperationException("The user configuration object is incomplete.");
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return;
        }

        var depth = 1;
        while (depth > 0 && reader.Read())
        {
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                depth++;
            }
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                depth--;
            }
        }

        if (depth != 0)
        {
            throw new InvalidOperationException("The user configuration value is incomplete.");
        }
    }

    private static bool ReadSignificantToken(ref Utf8JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.Comment)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] InsertProperty(byte[] original, ObjectLocation location, byte[] property)
    {
        var hasTrailingComma = location.HasProperties
            && HasTrailingComma(original.AsSpan(location.LastValueEnd, location.ClosingBrace - location.LastValueEnd));
        byte[] separator = location.HasProperties && !hasTrailingComma ? [(byte)','] : [];
        return ReplaceRange(
            original,
            location.ClosingBrace,
            location.ClosingBrace,
            Combine(separator, property));
    }

    private static bool HasTrailingComma(ReadOnlySpan<byte> trailingTrivia)
    {
        var index = 0;
        while (index < trailingTrivia.Length)
        {
            var current = trailingTrivia[index];
            if (current is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                index++;
                continue;
            }

            if (current == (byte)',')
            {
                return true;
            }

            if (current == (byte)'/' && index + 1 < trailingTrivia.Length
                && trailingTrivia[index + 1] == (byte)'/')
            {
                index += 2;
                while (index < trailingTrivia.Length && trailingTrivia[index] is not ((byte)'\r' or (byte)'\n'))
                {
                    index++;
                }

                continue;
            }

            if (current == (byte)'/' && index + 1 < trailingTrivia.Length
                && trailingTrivia[index + 1] == (byte)'*')
            {
                index += 2;
                while (index + 1 < trailingTrivia.Length
                    && (trailingTrivia[index] != (byte)'*' || trailingTrivia[index + 1] != (byte)'/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, trailingTrivia.Length);
                continue;
            }

            throw new InvalidOperationException("The user configuration contains invalid object trivia.");
        }

        return false;
    }

    private static byte[] ReplaceRange(byte[] original, int start, int end, byte[] replacement)
    {
        var updated = new byte[checked(original.Length - (end - start) + replacement.Length)];
        original.AsSpan(0, start).CopyTo(updated);
        replacement.CopyTo(updated.AsSpan(start));
        original.AsSpan(end).CopyTo(updated.AsSpan(start + replacement.Length));
        return updated;
    }

    private static byte[] Combine(params byte[][] segments)
    {
        var length = segments.Sum(segment => segment.Length);
        var combined = new byte[length];
        var offset = 0;
        foreach (var segment in segments)
        {
            segment.CopyTo(combined, offset);
            offset += segment.Length;
        }

        return combined;
    }

    private async Task<(byte[] Bytes, JsonObject Root)> ReadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configurationPath))
        {
            var emptyRoot = Encoding.UTF8.GetBytes("{}");
            return (emptyRoot, new JsonObject());
        }

        var info = new FileInfo(_configurationPath);
        if (info.Length > MaximumConfigurationBytes)
        {
            throw new InvalidOperationException("The user configuration exceeds the supported size.");
        }

        var bytes = await File.ReadAllBytesAsync(_configurationPath, cancellationToken).ConfigureAwait(false);
        var prefixLength = GetUtf8BomPrefixLength(bytes);
        var node = JsonNode.Parse(bytes.AsSpan(prefixLength), documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64,
        });
        return node is JsonObject root
            ? (bytes, root)
            : throw new InvalidOperationException("The user configuration root must be an object.");
    }

    private static int GetUtf8BomPrefixLength(byte[] bytes)
    {
        return bytes.AsSpan().StartsWith([(byte)0xEF, (byte)0xBB, (byte)0xBF]) ? 3 : 0;
    }

    private enum ObjectPurpose
    {
        Root,
        Tui,
    }

    private readonly record struct ObjectLocation(int ClosingBrace, bool HasProperties, int LastValueEnd);

    private readonly record struct ValueLocation(int Start, int End);

    private readonly record struct ObjectScanResult(
        ObjectLocation Location,
        ObjectLocation? Tui,
        ValueLocation? DefaultTheme);

    private readonly record struct ThemeJsonLocation(
        ObjectLocation Root,
        ObjectLocation? Tui,
        ValueLocation? DefaultTheme);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

/// <summary>Loads bounded theme configuration and merges it with compiled themes.</summary>
internal static class TuiThemeConfigurationLoader
{
    private const int MaximumThemes = 32;
    private const int MaximumNameLength = 80;
    private const int MaximumUiValueLength = 40;

    /// <summary>Loads the effective configured and built-in catalog.</summary>
    internal static (ConfiguredThemeCatalog Catalog, string DefaultThemeId) Load(IConfiguration? configuration)
    {
        try
        {
            return LoadValidated(configuration);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            var themes = BuiltInThemes.Create();
            var detail = SafeMessage(exception.Message);
            var warning = $"Configured themes are invalid; using system. {detail}";
            return (new ConfiguredThemeCatalog(themes, [warning]), "system");
        }
    }

    private static (ConfiguredThemeCatalog Catalog, string DefaultThemeId) LoadValidated(IConfiguration? configuration)
    {
        var themes = BuiltInThemes.Create().ToList();
        var positions = themes
            .Select((theme, index) => new KeyValuePair<string, int>(theme.Theme.Id, index))
            .ToDictionary(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var configuredThemes = new List<ConfiguredTheme>();
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                var layerValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var pendingPaths = new Stack<string>();
                pendingPaths.Push("tui:themes");
                while (pendingPaths.TryPop(out var parentPath))
                {
                    foreach (var childKey in provider
                        .GetChildKeys([], parentPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var childPath = ConfigurationPath.Combine(parentPath, childKey);
                        if (provider.TryGet(childPath, out var value))
                        {
                            layerValues[childPath] = value;
                        }

                        pendingPaths.Push(childPath);
                    }
                }

                var layer = new ConfigurationBuilder()
                    .AddInMemoryCollection(layerValues)
                    .Build();
                IConfigurationSection[] layerSections = [.. layer.GetSection("tui:themes").GetChildren()];
                if (layerSections.Length > MaximumThemes)
                {
                    throw new InvalidOperationException($"At most {MaximumThemes} configured themes are supported.");
                }

                AddValidThemes(layerSections, configuredThemes, warnings);
            }
        }
        else
        {
            IConfigurationSection[] effectiveSections = configuration is null
                ? []
                : [.. configuration.GetSection("tui:themes").GetChildren()];
            if (effectiveSections.Length > MaximumThemes)
            {
                throw new InvalidOperationException($"At most {MaximumThemes} configured themes are supported.");
            }

            AddValidThemes(effectiveSections, configuredThemes, warnings);
        }

        if (configuredThemes.Select(theme => theme.Theme.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > MaximumThemes)
        {
            throw new InvalidOperationException($"At most {MaximumThemes} configured themes are supported.");
        }

        foreach (var theme in configuredThemes)
        {
            if (positions.TryGetValue(theme.Theme.Id, out var existingIndex))
            {
                themes[existingIndex] = theme;
                warnings.Add($"Theme '{theme.Theme.Id}' replaced an earlier definition.");
            }
            else
            {
                positions.Add(theme.Theme.Id, themes.Count);
                themes.Add(theme);
            }
        }

        var catalog = new ConfiguredThemeCatalog(themes, warnings);
        var requestedDefault = configuration?["tui:defaultTheme"] ?? "system";
        if (!catalog.TryGet(requestedDefault, out _))
        {
            warnings.Add($"Unknown default theme '{SafeId(requestedDefault)}'; using system.");
            catalog = new ConfiguredThemeCatalog(themes, warnings);
            requestedDefault = "system";
        }

        return (catalog, requestedDefault);
    }

    private static void AddValidThemes(
        IEnumerable<IConfigurationSection> sections,
        ICollection<ConfiguredTheme> themes,
        ICollection<string> warnings)
    {
        foreach (var section in sections)
        {
            try
            {
                themes.Add(ParseTheme(section));
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                var configuredId = section["id"] ?? section.Key;
                warnings.Add(
                    $"Configured theme '{SafeId(configuredId)}' is invalid and was ignored. {SafeMessage(exception.Message)}");
            }
        }
    }

    private static ConfiguredTheme ParseTheme(IConfigurationSection section)
    {
        var id = section["id"] ?? throw new InvalidOperationException("Configured themes require an id.");
        var name = section["name"] ?? id;
        ValidateText(name, MaximumNameLength, "Theme names");
        var styles = new List<KeyValuePair<PresentationTextRole, TuiTextStyle>>();
        foreach (var styleSection in section.GetSection("styles").GetChildren())
        {
            string[] supportedStyleSettings =
            [
                "foreground", "background", "bold", "dim", "italic", "underline", "strikethrough", "invert",
            ];
            var unknownStyleSetting = styleSection.GetChildren().Select(child => child.Key)
                .FirstOrDefault(key => !supportedStyleSettings.Contains(key, StringComparer.OrdinalIgnoreCase));
            if (unknownStyleSetting is not null)
            {
                throw new InvalidOperationException($"Unsupported theme style setting '{SafeId(unknownStyleSetting)}'.");
            }

            if (!Enum.TryParse(styleSection.Key, ignoreCase: true, out PresentationTextRole role)
                || !Enum.IsDefined(role))
            {
                throw new InvalidOperationException($"Unknown semantic theme role '{SafeId(styleSection.Key)}'.");
            }

            var hasDecorationSetting = Decorations.Any(item => styleSection[item.Key] is not null);
            TuiTextDecoration? decorations = hasDecorationSetting ? TuiTextDecoration.None : null;
            foreach ((var key, var decoration) in Decorations)
            {
                if (styleSection.GetValue<bool?>(key) == true)
                {
                    decorations = decorations.GetValueOrDefault() | decoration;
                }
            }

            styles.Add(new KeyValuePair<PresentationTextRole, TuiTextStyle>(
                role,
                new TuiTextStyle(
                    ParseOptionalColor(styleSection["foreground"]),
                    ParseOptionalColor(styleSection["background"]),
                    decorations)));
        }

        var uiSection = section.GetSection("ui");
        string[] supportedUi = ["spinner", "selectionMarker", "footerSeparator"];
        var unknownUi = uiSection.GetChildren().Select(child => child.Key)
            .FirstOrDefault(key => !supportedUi.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unknownUi is not null)
        {
            throw new InvalidOperationException($"Unsupported theme UI setting '{SafeId(unknownUi)}'.");
        }

        var spinner = uiSection["spinner"] ?? TuiThemeUi.Default.Spinner;
        var marker = uiSection["selectionMarker"] ?? TuiThemeUi.Default.SelectionMarker;
        var separator = uiSection["footerSeparator"] ?? TuiThemeUi.Default.FooterSeparator;
        ValidateText(spinner, MaximumUiValueLength, "Theme spinner names");
        ValidateText(marker, MaximumUiValueLength, "Theme selection markers");
        ValidateText(separator, MaximumUiValueLength, "Theme footer separators");
        if (!string.Equals(spinner, "dots", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The supported theme spinner is 'dots'.");
        }

        return new ConfiguredTheme(name, new TuiTheme(id, styles), new TuiThemeUi(spinner, marker, separator), false);
    }

    private static readonly KeyValuePair<string, TuiTextDecoration>[] Decorations =
    [
        new("bold", TuiTextDecoration.Bold),
        new("dim", TuiTextDecoration.Dim),
        new("italic", TuiTextDecoration.Italic),
        new("underline", TuiTextDecoration.Underline),
        new("strikethrough", TuiTextDecoration.Strikethrough),
        new("invert", TuiTextDecoration.Invert),
    ];

    private static TuiColor? ParseOptionalColor(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : TuiColor.Parse(value);
    }

    private static void ValidateText(string value, int maximumLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{label} must be non-empty, bounded, and free of control characters.");
        }
    }

    private static string SafeId(string value)
    {
        return new([.. value.Where(character => !char.IsControl(character)).Take(40)]);
    }

    private static string SafeMessage(string value)
    {
        return new([.. value.Where(character => !char.IsControl(character)).Take(160)]);
    }
}
