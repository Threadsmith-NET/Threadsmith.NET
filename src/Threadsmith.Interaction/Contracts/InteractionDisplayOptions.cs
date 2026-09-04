namespace Threadsmith.Interaction.Contracts;

/// <summary>Frontend-bound behavioral options consumed by shared interaction coordination.</summary>
/// <param name="RenderMarkdown">Whether answer Markdown is parsed into semantic documents.</param>
/// <param name="ShowOperationDurations">Whether operation durations are included in activity text.</param>
public sealed record InteractionDisplayOptions(
    bool RenderMarkdown = true,
    bool ShowOperationDurations = true);
