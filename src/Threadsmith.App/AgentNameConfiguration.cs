namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Interaction.Agents;

/// <summary>Resolves whole name lists independently across ordinary configuration providers.</summary>
internal static class AgentNameConfiguration
{
    /// <summary>Loads immutable names and bounded warnings without echoing untrusted values.</summary>
    internal static (AgentNameCatalog Catalog, IReadOnlyList<string> Warnings) Load(IConfiguration configuration)
    {
        var warnings = new List<string>();
        var roles = new Dictionary<AgentRole, IReadOnlyList<string>>();
        var shared = LoadList(configuration, "tui:agentNames:defaultNames", warnings);
        foreach (var section in configuration.GetSection("tui:agentNames:byRole").GetChildren().Take(64))
        {
            if (AgentRoleNames.TryParse(section.Key, out var role))
            {
                roles[role] = LoadList(configuration, section.Path, warnings);
            }
            else if (!warnings.Contains("Unknown agent-name role ignored.", StringComparer.Ordinal))
            {
                warnings.Add("Unknown agent-name role ignored.");
            }
        }

        return (new AgentNameCatalog(shared, roles), warnings.AsReadOnly());
    }

    private static IReadOnlyList<string> LoadList(IConfiguration configuration, string path, List<string> warnings)
    {
        string[] raw;
        if (configuration is IConfigurationRoot root)
        {
            var provider = root.Providers.Reverse().FirstOrDefault(item =>
                item.TryGet(path, out _) || item.GetChildKeys([], path).Any());
            if (provider is null)
            {
                return [];
            }

            var keys = provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase).Take(AgentNameCatalog.MaximumNames + 1).ToArray();
            if (keys.Any(key => !int.TryParse(key, out var index) || index < 0))
            {
                warnings.Add("Invalid agent-name list ignored.");
                return [];
            }

            raw = [.. keys.OrderBy(key => int.Parse(key, System.Globalization.CultureInfo.InvariantCulture))
                .Select(key => provider.TryGet(path + ":" + key, out var value) ? value ?? string.Empty : string.Empty)];
        }
        else
        {
            raw = [.. configuration.GetSection(path).GetChildren().Take(AgentNameCatalog.MaximumNames + 1)
                .Select(item => item.Value ?? string.Empty)];
        }

        var valid = AgentNameCatalog.Validate(raw);
        if (valid.Count != raw.Length || valid.Count == 0)
        {
            warnings.Add("Agent-name list contained empty, invalid, duplicate, or excessive entries; usable names or fallback names will be used.");
        }

        return valid;
    }
}
