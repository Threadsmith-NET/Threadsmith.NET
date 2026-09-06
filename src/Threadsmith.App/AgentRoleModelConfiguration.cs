namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Binds the closed role-routing schema at the repository-excluding startup boundary.</summary>
internal static class AgentRoleModelConfiguration
{
    private const string SectionPath = "agents:roleModels";

    /// <summary>Validates role routes against trusted enabled provider bindings and freezes the result.</summary>
    internal static AgentRoleModelPolicy Load(
        IConfiguration trustedConfiguration,
        EffectiveModelProviderCatalog? trustedCatalog,
        EffectiveModelProviderCatalog? providerCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        if (trustedConfiguration.GetSection("agents").GetChildren().Any(section =>
            string.Equals(section.Key, "roleProfiles", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Trusted agents:roleProfiles is no longer supported; use agents:roleModels.");
        }

        var section = trustedConfiguration.GetSection(SectionPath);
        if (section.Value is not null)
        {
            throw new InvalidOperationException("Trusted agents:roleModels must be an object of role entries.");
        }

        var routes = new List<AgentRoleModelPreference>();
        foreach (var entry in section.GetChildren())
        {
            if (!AgentRoleNames.TryParse(entry.Key, out var role))
            {
                throw new InvalidOperationException("Trusted agents:roleModels contains an unknown role key.");
            }

            if (entry.Value is not null || entry.GetChildren().Any(field =>
                field.Key is not ("providerId" or "profileId" or "reasoningLevel")
                || field.GetChildren().Any()))
            {
                throw new InvalidOperationException(
                    "Trusted agents:roleModels entries allow only scalar providerId, profileId, and reasoningLevel fields.");
            }

            var providerId = entry["providerId"];
            if (string.IsNullOrWhiteSpace(providerId))
            {
                throw new InvalidOperationException("Trusted agents:roleModels requires providerId for every entry.");
            }

            if (!Guid.TryParseExact(entry["profileId"], "D", out var id) || id == Guid.Empty)
            {
                throw new InvalidOperationException("Trusted agents:roleModels requires a valid profileId GUID for every entry.");
            }

            var profileId = new ModelProfileId(id);
            var profile = trustedCatalog?.Definitions.FirstOrDefault(item => item.Profile.Id == profileId)?.Profile
                ?? throw new InvalidOperationException(
                    "Trusted agents:roleModels refers to a missing or disabled model profile.");
            var reasoning = profile.DefaultReasoningLevel;
            if (entry.GetChildren().Any(field => field.Key == "reasoningLevel"))
            {
                var configured = entry["reasoningLevel"];
                if (!Enum.GetNames<ReasoningLevel>().Any(name => string.Equals(name, configured, StringComparison.OrdinalIgnoreCase))
                    || !Enum.TryParse(configured, ignoreCase: true, out reasoning))
                {
                    throw new InvalidOperationException("Trusted agents:roleModels reasoningLevel must be a supported level name.");
                }
            }

            routes.Add(new AgentRoleModelPreference(role, providerId, profileId, reasoning));
        }

        return new AgentRoleModelPolicy(trustedCatalog, routes, providerCatalog);
    }
}
