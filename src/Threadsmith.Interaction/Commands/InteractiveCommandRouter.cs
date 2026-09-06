namespace Threadsmith.Interaction.Commands;

/// <summary>Represents one parsed slash-command invocation.</summary>
/// <param name="Descriptor">Catalog descriptor.</param>
/// <param name="CommandText">Trimmed exact command text.</param>
/// <param name="Argument">Trimmed text following the command name.</param>
public sealed record InteractiveCommandInvocation(
    InteractiveCommandDescriptor Descriptor,
    string CommandText,
    string Argument);

/// <summary>Classifies fixed interactive commands without invoking host behavior.</summary>
public static class InteractiveCommandRouter
{
    /// <summary>Parses a slash command with the existing case and whitespace rules.</summary>
    /// <param name="commandText">Trimmed candidate command.</param>
    /// <param name="invocation">Parsed invocation for a known command.</param>
    /// <returns><see langword="true" /> when the command is known.</returns>
    public static bool TryParse(string commandText, out InteractiveCommandInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        if (!commandText.StartsWith('/'))
        {
            invocation = null;
            return false;
        }

        var separator = commandText.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = separator < 0 ? commandText : commandText[..separator];
        if (!InteractiveCommandCatalog.TryGet(name, out var descriptor) || descriptor is null)
        {
            invocation = null;
            return false;
        }

        var argument = separator < 0 ? string.Empty : commandText[separator..].Trim();
        invocation = new InteractiveCommandInvocation(descriptor, commandText, argument);
        return true;
    }
}
