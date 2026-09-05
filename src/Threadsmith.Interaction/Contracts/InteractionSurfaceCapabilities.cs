namespace Threadsmith.Interaction.Contracts;

/// <summary>Describes genuinely optional immutable frontend behavior.</summary>
/// <param name="SupportsActiveRunInput">Whether an active-run input lease can be created.</param>
/// <param name="SupportsExactRedirectedSource">Whether redirected output can admit exact raw source.</param>
/// <param name="SupportsRetainedStatus">Whether status may refresh during input, runs, and modals.</param>
public sealed record InteractionSurfaceCapabilities(
    bool SupportsActiveRunInput = false,
    bool SupportsExactRedirectedSource = false,
    bool SupportsRetainedStatus = false);
