namespace Threadsmith.Core;

/// <summary>Host authority that supplied a frozen child model preference.</summary>
public enum AgentModelSelectionSource
{
    /// <summary>Compatible host default with no explicit preference.</summary>
    Default,

    /// <summary>Parent or session preference captured when the assignment was accepted.</summary>
    Inherited,

    /// <summary>Repository-excluding role configuration captured at process startup.</summary>
    RoleConfiguration,

    /// <summary>Explicit application-created assignment pin.</summary>
    ApplicationPin,
}

/// <summary>Secret-free frozen preference and effective route retained with a child checkpoint.</summary>
public sealed record AgentModelProvenance
{
    /// <summary>Authority that supplied the original preference, retained across fallback.</summary>
    public required AgentModelSelectionSource Source { get; init; }

    /// <summary>Originally requested provider identity, when a preference was supplied.</summary>
    public string? ConfiguredProviderId { get; init; }

    /// <summary>Originally requested profile identity, when a preference was supplied.</summary>
    public ModelProfileId? ConfiguredProfileId { get; init; }

    /// <summary>Originally requested reasoning level, normalized to a host enum name.</summary>
    public string? ConfiguredReasoningLevel { get; init; }

    /// <summary>Provider identity selected for the actual child request.</summary>
    public required string EffectiveProviderId { get; init; }

    /// <summary>Profile identity selected for the actual child request.</summary>
    public required ModelProfileId EffectiveProfileId { get; init; }

    /// <summary>Effective reasoning level, normalized to a host enum name.</summary>
    public required string EffectiveReasoningLevel { get; init; }

    /// <summary>Whether dispatch must use the repository-excluding catalog and credential resolver.</summary>
    public bool UsesTrustedCatalog { get; init; }

    /// <summary>Host-authored explanation when the requested route was unavailable or incompatible.</summary>
    public string? FallbackReason { get; init; }
}
