namespace Threadsmith.Models;

/// <summary>Describes whether and how a selected model exposes reasoning control.</summary>
public enum ReasoningControllability
{
    /// <summary>The user may select one of the advertised host reasoning levels.</summary>
    Selectable,

    /// <summary>The model reasons intrinsically and exposes no user control.</summary>
    AlwaysOn,

    /// <summary>The model does not expose reasoning.</summary>
    Unsupported,
}

/// <summary>Immutable provider-neutral projection of effective model reasoning behavior.</summary>
public sealed record EffectiveReasoningCapability
{
    /// <summary>Effective user control classification.</summary>
    public ReasoningControllability Controllability { get; init; } = ReasoningControllability.Unsupported;

    /// <summary>Selectable host levels. Empty for always-on models.</summary>
    public IReadOnlyList<ReasoningLevel> SupportedLevels { get; init; } = [ReasoningLevel.None];

    /// <summary>Validated default level for selectable models.</summary>
    public ReasoningLevel? DefaultLevel { get; init; }

    /// <summary>Sanitized compiled request compatibility mode.</summary>
    public string RequestMode { get; init; } = "legacy-standard";

    /// <summary>Compatibility schema version, or zero for the legacy branch.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Sanitized compiled response extraction mode.</summary>
    public string ResponseMode { get; init; } = "reasoning-or-reasoning-content";

    /// <summary>Stable provider/model configuration provenance.</summary>
    public string Provenance { get; init; } = "configured model profile";
}
