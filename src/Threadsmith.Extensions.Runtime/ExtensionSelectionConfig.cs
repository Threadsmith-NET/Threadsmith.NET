namespace Threadsmith.Extensions.Runtime;

using System.Text.Json;

/// <summary>Repo-level extension selection configuration loaded from <c>.threadsmith/extensions.json</c>
/// (strategy §21 layered precedence: repo-level only — never the user <c>~/</c> config, per the user's
/// standing instruction). Selects which discovered extensions are loaded at application startup.</summary>
public sealed class ExtensionSelectionConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Initializes a new instance of the <see cref="ExtensionSelectionConfig"/> class.</summary>
    public ExtensionSelectionConfig()
    {
    }

    /// <summary>Gets the discovery directory (relative to the repository root) scanned for extension packages.
    /// Defaults to <c>.threadsmith/extensions</c>.</summary>
    public string DiscoveryDirectory { get; init; } = ".threadsmith/extensions";

    /// <summary>Gets the extension ids to load automatically at startup. An empty list loads nothing.</summary>
    public IReadOnlyList<string> AutoLoad { get; init; } = [];

    /// <summary>Loads the repo-level extension selection config, returning defaults when the file is absent.</summary>
    /// <param name="configPath">The absolute path to <c>.threadsmith/extensions.json</c>.</param>
    /// <returns>The parsed config, or a default instance when the file is missing or invalid.</returns>
    public static ExtensionSelectionConfig LoadOrDefault(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        if (!File.Exists(configPath))
        {
            return new ExtensionSelectionConfig();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<ExtensionSelectionConfig>(json, JsonOptions) ?? new ExtensionSelectionConfig();
        }
        catch
        {
            // Repo config is untrusted input (§22.2): a malformed file falls back to safe defaults
            // (load nothing) rather than failing startup.
            return new ExtensionSelectionConfig();
        }
    }
}