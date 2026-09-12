namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Tui;

/// <summary>Resolves whole name lists independently across ordinary configuration providers.</summary>
internal static class AgentNameConfiguration
{
    /// <summary>Loads immutable names and bounded warnings without echoing untrusted values.</summary>
    internal static (AgentNameCatalog Catalog, IReadOnlyList<string> Warnings) Load(IConfiguration configuration)
    {
        var limits = TuiDisplayOptions.Load(configuration).Limits;
        var warnings = new List<string>();
        var roles = new Dictionary<AgentRole, IReadOnlyList<string>>();
        var shared = LoadList(configuration, "tui:agentNames:defaultNames", warnings, limits);
        foreach (var section in configuration.GetSection("tui:agentNames:byRole").GetChildren())
        {
            if (AgentRoleNames.TryParse(section.Key, out var role))
            {
                roles[role] = LoadList(configuration, section.Path, warnings, limits);
            }
            else if (!warnings.Contains("Unknown agent-name role ignored.", StringComparer.Ordinal))
            {
                warnings.Add("Unknown agent-name role ignored.");
            }
        }

        return (new AgentNameCatalog(shared, roles, limits), warnings.AsReadOnly());
    }

    private static IReadOnlyList<string> LoadList(IConfiguration configuration, string path, List<string> warnings, TuiResourceLimits limits)
    {
        string[] raw;
        var probeCount = (int)Math.Min((long)limits.MaximumAgentNames + 1, int.MaxValue);
        if (configuration is IConfigurationRoot root)
        {
            var provider = root.Providers.Reverse().FirstOrDefault(item =>
                item.TryGet(path, out _) || item.GetChildKeys([], path).Any());
            if (provider is null)
            {
                return [];
            }

            var keys = provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase).Take(probeCount).ToArray();
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
            raw = [.. configuration.GetSection(path).GetChildren().Take(probeCount)
                .Select(item => item.Value ?? string.Empty)];
        }

        var valid = AgentNameCatalog.Validate(raw, limits);
        if (valid.Count != raw.Length || valid.Count == 0)
        {
            warnings.Add("Agent-name list contained empty, invalid, duplicate, or excessive entries; usable names or fallback names will be used.");
        }

        return valid;
    }
}
