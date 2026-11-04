namespace Threadsmith.Extensions.Runtime;

using System.Collections.Frozen;
using System.Text.Json;

/// <summary>Repository-owned selection and settings from <c>.threadsmith/extensions.json</c>.
/// Selection is data, not authority to execute extension code.</summary>
public sealed class ExtensionSelectionConfig
{
    /// <summary>Maximum UTF-8 selection-file size, including any BOM.</summary>
    internal const int MaximumFileBytes = 256 * 1024;

    /// <summary>Maximum extension settings sections.</summary>
    internal const int MaximumSections = 128;

    /// <summary>Maximum settings per extension.</summary>
    internal const int MaximumKeys = 128;

    /// <summary>Maximum descriptor ID or setting-key length.</summary>
    internal const int MaximumNameLength = 256;

    /// <summary>Maximum setting-value length.</summary>
    internal const int MaximumValueLength = 8192;

    /// <summary>Safe diagnostic shared by file and explicit-settings validation.</summary>
    internal const string InvalidSettingsDiagnostic = "ExtensionSettingsInvalid: extension configuration is invalid or unavailable.";

    /// <summary>Gets the discovery directory, relative to the repository root unless absolute.</summary>
    public string DiscoveryDirectory { get; init; } = ".threadsmith/extensions";

    /// <summary>Gets requested automatic loads. Repository selection does not authorize them.</summary>
    public IReadOnlyList<string> AutoLoad { get; init; } = [];

    /// <summary>Gets settings keyed by stable descriptor ID. IDs and setting keys are case-sensitive.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Settings { get; init; }
        = FrozenDictionary<string, IReadOnlyDictionary<string, string?>>.Empty;

    /// <summary>Loads selection data, falling back to no automatic loads on invalid or unavailable data.</summary>
    /// <param name="configPath">The absolute selection-file path.</param>
    /// <returns>The parsed configuration or safe defaults.</returns>
    public static ExtensionSelectionConfig LoadOrDefault(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        try
        {
            return Load(configPath);
        }
        catch (ExtensionLoadException)
        {
            // Startup must not turn invalid repository data into code-execution authority.
            return new ExtensionSelectionConfig();
        }
    }

    /// <summary>Loads bounded data for explicit loading. Only an absent file permits empty defaults.</summary>
    internal static ExtensionSelectionConfig Load(string configPath, bool deferSettings = false)
    {
        try
        {
            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[(int)Math.Min(stream.Length, MaximumFileBytes) + 1];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > MaximumFileBytes || stream.ReadByte() != -1)
            {
                throw new ExtensionLoadException(InvalidSettingsDiagnostic);
            }

            // Accept the UTF-8 BOM supported by the previous text reader, within the same byte limit.
            var offset = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset, length - offset), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ExtensionLoadException(InvalidSettingsDiagnostic);
            }

            JsonElement? deferredSettings = null;
            var discoveryDirectory = ".threadsmith/extensions";
            IReadOnlyList<string> autoLoad = [];
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> settings
                = FrozenDictionary<string, IReadOnlyDictionary<string, string?>>.Empty;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ExtensionLoadException(InvalidSettingsDiagnostic);
                }

                if (property.Name.Equals("discoveryDirectory", StringComparison.OrdinalIgnoreCase))
                {
                    discoveryDirectory = property.Value.GetString() ?? throw new ExtensionLoadException(InvalidSettingsDiagnostic);
                    if (string.IsNullOrWhiteSpace(discoveryDirectory))
                    {
                        throw new ExtensionLoadException(InvalidSettingsDiagnostic);
                    }
                }
                else if (property.Name.Equals("autoLoad", StringComparison.OrdinalIgnoreCase))
                {
                    autoLoad = property.Value.EnumerateArray()
                        .Select(value => value.GetString() ?? throw new ExtensionLoadException(InvalidSettingsDiagnostic))
                        .ToArray();
                }
                else if (property.Name.Equals("settings", StringComparison.OrdinalIgnoreCase))
                {
                    settings = ReadSettings(property.Value, materialize: !deferSettings);
                    if (deferSettings)
                    {
                        deferredSettings = property.Value.Clone();
                    }
                }
            }

            return new ExtensionSelectionConfig
            {
                DiscoveryDirectory = discoveryDirectory,
                AutoLoad = autoLoad,
                Settings = settings,
                DeferredSettings = deferredSettings,
            };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ExtensionSelectionConfig();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException)
        {
            // Do not retain parser exceptions, paths, keys, or values in a public diagnostic.
            throw new ExtensionLoadException(InvalidSettingsDiagnostic);
        }
    }

    /// <summary>Copies and bounds explicitly supplied settings before asynchronous loading begins.</summary>
    internal static IReadOnlyDictionary<string, string?> Snapshot(IReadOnlyDictionary<string, string?> values)
    {
        if (values.Count > MaximumKeys)
        {
            throw new ExtensionLoadException(InvalidSettingsDiagnostic);
        }

        var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
        var characters = 0;
        foreach (var entry in values)
        {
            characters += entry.Key.Length + (entry.Value?.Length ?? 0);
            if (copy.Count >= MaximumKeys || string.IsNullOrWhiteSpace(entry.Key)
                || entry.Key.Length > MaximumNameLength || entry.Value?.Length > MaximumValueLength
                || characters > MaximumFileBytes / 4 || !copy.TryAdd(entry.Key, entry.Value))
            {
                throw new ExtensionLoadException(InvalidSettingsDiagnostic);
            }
        }

        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Returns only the selected, already validated immutable settings section.</summary>
    internal IReadOnlyDictionary<string, string?> GetSettings(string descriptorId)
    {
        if (DeferredSettings is { } element && element.TryGetProperty(descriptorId, out var section))
        {
            return section.EnumerateObject().ToFrozenDictionary(
                property => property.Name, property => property.Value.GetString(), StringComparer.Ordinal);
        }

        return Settings.GetValueOrDefault(descriptorId) ?? FrozenDictionary<string, string?>.Empty;
    }

    private JsonElement? DeferredSettings { get; init; }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> ReadSettings(
        JsonElement element, bool materialize)
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var sectionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in element.EnumerateObject())
        {
            if (sectionNames.Count >= MaximumSections || string.IsNullOrWhiteSpace(section.Name)
                || section.Name.Length > MaximumNameLength || !sectionNames.Add(section.Name))
            {
                throw new ExtensionLoadException(InvalidSettingsDiagnostic);
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var characters = 0;
            foreach (var property in section.Value.EnumerateObject())
            {
                var value = property.Value.GetString();
                characters += property.Name.Length + (value?.Length ?? 0);
                if (keys.Count >= MaximumKeys || string.IsNullOrWhiteSpace(property.Name)
                    || property.Name.Length > MaximumNameLength || value?.Length > MaximumValueLength
                    || characters > MaximumFileBytes / 4 || !keys.Add(property.Name))
                {
                    throw new ExtensionLoadException(InvalidSettingsDiagnostic);
                }
            }

            if (materialize)
            {
                sections.Add(section.Name, section.Value.EnumerateObject().ToFrozenDictionary(
                    property => property.Name, property => property.Value.GetString(), StringComparer.Ordinal));
            }
        }

        return sections.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
