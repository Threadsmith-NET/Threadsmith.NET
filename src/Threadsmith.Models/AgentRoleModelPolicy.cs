namespace Threadsmith.Models;

using System.Collections.Frozen;
using Threadsmith.Core;

/// <summary>One validated role preference over the trusted compiled-provider catalog.</summary>
public sealed record AgentRoleModelPreference(
    AgentRole Role,
    string ProviderId,
    ModelProfileId ProfileId,
    ReasoningLevel ReasoningLevel);

/// <summary>Immutable startup role routes and provider identities for child selection.</summary>
public sealed class AgentRoleModelPolicy
{
    private readonly FrozenDictionary<AgentRole, AgentRoleModelPreference> _roleModels;
    private readonly EffectiveModelProviderCatalog? _trustedProviders;
    private readonly EffectiveModelProviderCatalog? _providers;

    /// <summary>Initializes a new instance of the <see cref="AgentRoleModelPolicy"/> class.</summary>
    public AgentRoleModelPolicy(
        EffectiveModelProviderCatalog? trustedCatalog = null,
        IEnumerable<AgentRoleModelPreference>? roleModels = null,
        EffectiveModelProviderCatalog? providerCatalog = null)
    {
        _trustedProviders = trustedCatalog;
        _providers = providerCatalog;
        TrustedCatalog = trustedCatalog?.ModelCatalog ?? new ConfiguredModelCatalog([]);
        var routes = new Dictionary<AgentRole, AgentRoleModelPreference>();
        foreach (var route in roleModels ?? [])
        {
            ArgumentNullException.ThrowIfNull(route);
            if (!Enum.IsDefined(route.Role) || !routes.TryAdd(route.Role, route))
            {
                throw new InvalidOperationException("Trusted agents:roleModels contains an unknown or duplicate role.");
            }

            var definition = trustedCatalog?.Definitions.FirstOrDefault(item => item.Profile.Id == route.ProfileId)
                ?? throw new InvalidOperationException(
                    "Trusted agents:roleModels refers to a missing or disabled model profile.");
            if (!string.Equals(definition.ProviderId, route.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Trusted agents:roleModels providerId and profileId do not match.");
            }

            if (!Enum.IsDefined(route.ReasoningLevel) || !definition.Profile.SupportsReasoningLevel(route.ReasoningLevel))
            {
                throw new InvalidOperationException("Trusted agents:roleModels reasoningLevel is unsupported by the profile.");
            }

            routes[route.Role] = route with { ProviderId = definition.ProviderId };
        }

        _roleModels = routes.ToFrozenDictionary();
    }

    /// <summary>Gets all immutable role preferences captured at startup.</summary>
    public IReadOnlyDictionary<AgentRole, AgentRoleModelPreference> RoleModels => _roleModels;

    /// <summary>Gets the catalog eligible for configured role requests and their compatible fallbacks.</summary>
    public ConfiguredModelCatalog TrustedCatalog { get; }

    /// <summary>Gets the configured preference for a defined role, or null to preserve inheritance.</summary>
    public AgentRoleModelPreference? Get(AgentRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        return _roleModels.GetValueOrDefault(role);
    }

    /// <summary>Resolves the stable provider identity within the selected catalog authority.</summary>
    internal string GetProviderId(ModelProfile profile, bool usesTrustedCatalog)
    {
        var providers = usesTrustedCatalog ? _trustedProviders : _providers;
        return providers?.Get(profile.Id).ProviderId ?? profile.Provider;
    }
}
