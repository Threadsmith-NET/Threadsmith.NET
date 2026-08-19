namespace Threadsmith.Tui;

using Microsoft.Extensions.Configuration;

/// <summary>Immutable effective interactive display settings.</summary>
public sealed record TuiDisplayOptions
{
    /// <summary>Gets whether request, ordinary-tool, and MCP durations are displayed.</summary>
    public bool ShowOperationDurations { get; init; } = true;

    /// <summary>Gets whether ordinary model answers use bounded semantic markdown rendering.</summary>
    public bool RenderMarkdown { get; init; } = true;

    /// <summary>Gets bounded diagnostics produced while reading display configuration.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>Loads one immutable effective snapshot from normal layered configuration.</summary>
    /// <param name="configuration">Effective configuration, or null for compiled defaults.</param>
    /// <returns>Validated display options.</returns>
    public static TuiDisplayOptions Load(IConfiguration? configuration)
    {
        const string durationKey = "tui:showOperationDurations";
        const string markdownKey = "tui:renderMarkdown";
        var diagnostics = new List<string>(2);
        var showDurations = ParseBoolean(configuration?[durationKey], durationKey, diagnostics);
        var renderMarkdown = ParseBoolean(configuration?[markdownKey], markdownKey, diagnostics);
        return new TuiDisplayOptions
        {
            ShowOperationDurations = showDurations,
            RenderMarkdown = renderMarkdown,
            Diagnostics = diagnostics.ToArray(),
        };
    }

    private static bool ParseBoolean(string? configured, string key, List<string> diagnostics)
    {
        if (configured is null)
        {
            return true;
        }

        if (bool.TryParse(configured, out var enabled))
        {
            return enabled;
        }

        diagnostics.Add($"Configuration '{key}' must be true or false; using the compiled default true.");
        return true;
    }
}
