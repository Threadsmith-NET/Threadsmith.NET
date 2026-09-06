namespace Threadsmith.Interaction.Commands;

using System.Collections.Frozen;
using System.Text;

/// <summary>Describes one fixed interactive command for parsing and help projection.</summary>
/// <param name="Name">Canonical slash-command name.</param>
/// <param name="Usage">Exact usage text shown in help.</param>
/// <param name="Description">Exact help description.</param>
/// <param name="IsFrontendLocal">Whether the command is handled by the composed frontend.</param>
public sealed record InteractiveCommandDescriptor(
    string Name,
    string Usage,
    string Description,
    bool IsFrontendLocal = false);

/// <summary>Owns the fixed ordered host command catalog.</summary>
public static class InteractiveCommandCatalog
{
    private const int DescriptionColumn = 42;

    private static readonly InteractiveCommandDescriptor[] Entries =
    [
        new("/agents", "/agents [<id> [cancel|cancel-child <id>]]", "List, inspect, or cancel delegation trees"),
        new("/auth", "/auth openai-codex [login|status|logout]", "Manage Codex authentication"),
        new("/clone", "/clone", "Clone governed context into an independent session"),
        new("/code_explore_inspect", "/code_explore_inspect {on|off}", "Show future code_explore outputs in the tool block for this session"),
        new("/code_explore_output", "/code_explore_output {structured|markdown}", "Set code_explore output format for this session"),
        new("/context", "/context [mode|inspect|compact]", "Inspect or control bounded conversation context"),
        new("/extensions", "/extensions", "Browse, load, and unload extensions (Up/Down, Enter)"),
        new("/fetch-authorize", "/fetch-authorize <url> [redirect ...]", "Authorize one exact URL chain for web_fetch"),
        new("/help", "/help", "Show commands"),
        new("/hooks", "/hooks [list|inspect|enable|disable|test|approve|revoke|audit]", "Govern lifecycle hooks"),
        new("/mcp", "/mcp [list|inspect|connect|disconnect|reconnect|capabilities|capability|enable|disable|resource read|prompt get|auth|logout|revoke|switch-account|diagnose]", "Manage MCP profiles and capabilities"),
        new("/memory", "/memory [remember|list|inspect|supersede|forget|validate]", "Manage local repository memory"),
        new("/models", "/models", "Select and persist the repository model"),
        new("/new", "/new", "Start a fresh independent session"),
        new("/open", "/open [path]", "Open a repository and choose trust"),
        new("/plan-policy", "/plan-policy [name|current|reset|revoke]", "Select or report plan approval policy"),
        new("/policy", "/policy [name|current]", "Select or report mutation approval policy"),
        new("/quit", "/quit", "End the interactive session"),
        new("/reasoning", "/reasoning [level]", "Set reasoning effort for the active model (none|minimal|low|medium|high)"),
        new("/resume", "/resume [id]", "Resume a durable repository session"),
        new("/semantic_refresh", "/semantic_refresh", "Force and await a complete semantic refresh"),
        new("/skills", "/skills [list|refresh|inspect|provenance|install|uninstall|verify|enable|disable|pin|use|continue|resume|status|cancel]", "Govern skills"),
        new("/theme", "/theme [id|current]", "Select, change, or report the active theme", IsFrontendLocal: true),
        new("/thinking", "/thinking [on|off]", "Stream future reasoning (Ctrl+T toggles on an empty composer)"),
        new("/tools", "/tools", "Browse and toggle repository tool availability (Up/Down, Enter)"),
        new("/trust", "/trust [inspect|read|build|mutation]", "Set or upgrade repository trust"),
        new("/validation", "/validation retry", "Resume interrupted post-apply validation"),
    ];

    private static readonly FrozenDictionary<string, InteractiveCommandDescriptor> ByName = Entries
        .ToFrozenDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<InteractiveCommandDescriptor> ReadOnlyEntries = Array.AsReadOnly(Entries);

    /// <summary>Gets the ordered immutable command descriptors.</summary>
    public static IReadOnlyList<InteractiveCommandDescriptor> All => ReadOnlyEntries;

    /// <summary>Finds a command by canonical name.</summary>
    /// <param name="name">Slash-command name.</param>
    /// <param name="descriptor">Matching descriptor.</param>
    /// <returns><see langword="true" /> when the fixed catalog contains the command.</returns>
    public static bool TryGet(string name, out InteractiveCommandDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (ByName.TryGetValue(name, out var match))
        {
            descriptor = match;
            return true;
        }

        descriptor = null;
        return false;
    }

    /// <summary>Formats the exact ordered help projection.</summary>
    /// <returns>Help text including the ordinary-input instruction.</returns>
    public static string FormatHelp()
    {
        var output = new StringBuilder();
        foreach (var entry in Entries)
        {
            if (entry.Usage.Length >= DescriptionColumn)
            {
                output.AppendLine(entry.Usage);
                output.Append(' ', DescriptionColumn);
            }
            else
            {
                output.Append(entry.Usage.PadRight(DescriptionColumn, ' '));
            }

            output.AppendLine(entry.Description);
        }

        output.AppendLine("Submit any other text to Threadsmith.");
        return output.ToString();
    }
}
