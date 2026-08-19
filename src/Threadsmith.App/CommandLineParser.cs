namespace Threadsmith.App;

using Threadsmith.Core;

/// <summary>Parses process arguments into host-owned startup options without performing startup side effects.</summary>
internal static class CommandLineParser
{
    /// <summary>Parses repository, trust, terminal, request, and layered configuration arguments.</summary>
    internal static CommandLineParseResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? requestedRepository = null;
        string? requestedSolution = null;
        RepositoryTrustLevel? requestedTrust = null;
        var repositoryOptionsSpecified = false;
        var useInteractiveTerminal = false;
        var showHelp = false;
        var showVersion = false;
        string? codexAuthenticationAction = null;
        string? mcpAction = null;
        string? rawModelLogPath = null;
        var mcpConfirmed = false;
        var mcpAllowLocalCleanup = false;
        var mcpRevokeCurrentIdentity = false;
        var mcpModeRequested = args.Any(argument => string.Equals(
            argument,
            "--mcp",
            StringComparison.OrdinalIgnoreCase));
        var requestArguments = new List<string>();

        // Host switches are removed from the conversational request. Configuration overrides remain
        // available to ConfigurationBootstrap but likewise never become model input.
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--tui", StringComparison.OrdinalIgnoreCase))
            {
                useInteractiveTerminal = true;
                continue;
            }

            if (string.Equals(args[index], "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[index], "-h", StringComparison.OrdinalIgnoreCase))
            {
                showHelp = true;
                continue;
            }

            if (string.Equals(args[index], "--version", StringComparison.OrdinalIgnoreCase))
            {
                showVersion = true;
                continue;
            }

            if (string.Equals(args[index], "--codex-login", StringComparison.OrdinalIgnoreCase))
            {
                codexAuthenticationAction = "login";
                continue;
            }

            if (string.Equals(args[index], "--codex-status", StringComparison.OrdinalIgnoreCase))
            {
                codexAuthenticationAction = "status";
                continue;
            }

            if (string.Equals(args[index], "--codex-logout", StringComparison.OrdinalIgnoreCase))
            {
                codexAuthenticationAction = "logout";
                continue;
            }

            if (string.Equals(args[index], "--raw-model-log", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadValue(args, ref index, out var logPath))
                {
                    return CommandLineParseResult.Failure("--raw-model-log requires a file path.");
                }

                rawModelLogPath = logPath;
                continue;
            }

            if (string.Equals(args[index], "--mcp", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadValue(args, ref index, out var action))
                {
                    return CommandLineParseResult.Failure("--mcp requires an action.");
                }

                mcpAction = action;
                continue;
            }

            if (mcpModeRequested
                && string.Equals(args[index], "--confirm", StringComparison.OrdinalIgnoreCase))
            {
                mcpConfirmed = true;
                continue;
            }

            if (mcpModeRequested
                && string.Equals(args[index], "--allow-local-cleanup", StringComparison.OrdinalIgnoreCase))
            {
                mcpAllowLocalCleanup = true;
                continue;
            }

            if (mcpModeRequested
                && string.Equals(args[index], "--revoke-current", StringComparison.OrdinalIgnoreCase))
            {
                mcpRevokeCurrentIdentity = true;
                continue;
            }

            if (string.Equals(args[index], "--repository", StringComparison.OrdinalIgnoreCase))
            {
                repositoryOptionsSpecified = true;
                if (!TryReadValue(args, ref index, out var repository))
                {
                    return CommandLineParseResult.Failure("--repository requires a path.");
                }

                requestedRepository = repository;
                continue;
            }

            if (string.Equals(args[index], "--solution", StringComparison.OrdinalIgnoreCase))
            {
                repositoryOptionsSpecified = true;
                if (!TryReadValue(args, ref index, out var solution))
                {
                    return CommandLineParseResult.Failure("--solution requires a path.");
                }

                requestedSolution = solution;
                continue;
            }

            if (string.Equals(args[index], "--trust", StringComparison.OrdinalIgnoreCase))
            {
                repositoryOptionsSpecified = true;
                if (!TryReadValue(args, ref index, out var trustText))
                {
                    return CommandLineParseResult.Failure("--trust requires a trust level.");
                }

                if (!Enum.TryParse(trustText, ignoreCase: true, out RepositoryTrustLevel trust)
                    || !Enum.IsDefined(trust))
                {
                    return CommandLineParseResult.Failure(
                        $"Unknown repository trust level '{trustText}'.");
                }

                requestedTrust = trust;
                continue;
            }

            if (!args[index].StartsWith("--set:", StringComparison.Ordinal))
            {
                requestArguments.Add(args[index]);
            }
        }

        if (mcpAction is not null
            && ValidateMcpArguments(mcpAction, requestArguments) is { } mcpError)
        {
            return CommandLineParseResult.Failure(mcpError);
        }

        return CommandLineParseResult.Success(new CommandLineOptions
        {
            RequestedRepository = requestedRepository,
            RequestedSolution = requestedSolution,
            RequestedTrust = requestedTrust,
            RepositoryOptionsSpecified = repositoryOptionsSpecified,
            UseInteractiveTerminal = useInteractiveTerminal,
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            CodexAuthenticationAction = codexAuthenticationAction,
            McpAction = mcpAction,
            McpConfirmed = mcpConfirmed,
            McpAllowLocalCleanup = mcpAllowLocalCleanup,
            McpRevokeCurrentIdentity = mcpRevokeCurrentIdentity,
            RawModelLogPath = rawModelLogPath,
            RequestArguments = requestArguments,
        });
    }

    private static string? ValidateMcpArguments(string action, IReadOnlyList<string> arguments)
    {
        var normalized = action.ToLowerInvariant();
        int minimum;
        int maximum;
        var keyValueArguments = false;
        switch (normalized)
        {
            case "list":
                minimum = 0;
                maximum = 0;
                break;
            case "inspect":
            case "connect":
            case "disconnect":
            case "reconnect":
            case "auth":
            case "logout":
            case "revoke":
            case "switch-account":
            case "diagnose":
                minimum = 1;
                maximum = 1;
                break;
            case "capabilities":
                minimum = 1;
                maximum = 2;
                break;
            case "capability":
            case "enable":
            case "disable":
                minimum = 2;
                maximum = 2;
                break;
            case "resource-read":
            case "prompt-get":
                minimum = 2;
                maximum = 34;
                keyValueArguments = true;
                break;
            default:
                return $"Unknown MCP action '{action}'.";
        }

        if (arguments.Count < minimum || arguments.Count > maximum)
        {
            return $"MCP action '{action}' requires between {minimum} and {maximum} argument(s).";
        }

        if (normalized == "capabilities" && arguments.Count == 2)
        {
            var kind = arguments[1].ToLowerInvariant();
            if (kind is not ("tool" or "tools" or "resource" or "resources"
                or "resource-template" or "resource-templates" or "prompt" or "prompts"))
            {
                return $"Unknown MCP capability kind '{arguments[1]}'.";
            }
        }

        if (keyValueArguments)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var argument in arguments.Skip(2))
            {
                var pair = argument.Split('=', 2);
                if (pair.Length != 2
                    || pair[0].Length is 0 or > 128
                    || pair[1].Length > 16 * 1024
                    || !keys.Add(pair[0]))
                {
                    return "MCP resource and prompt arguments must use bounded exact key=value syntax.";
                }
            }
        }

        return null;
    }

    /// <summary>Consumes one required switch value while rejecting a missing value or another switch.</summary>
    private static bool TryReadValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }
}

/// <summary>Immutable command-line options used by later startup phases.</summary>
internal sealed record CommandLineOptions
{
    /// <summary>Gets the explicitly requested repository, or <see langword="null"/> for the current directory.</summary>
    internal string? RequestedRepository { get; init; }

    /// <summary>Gets the explicitly requested solution path.</summary>
    internal string? RequestedSolution { get; init; }

    /// <summary>Gets the explicitly requested repository trust.</summary>
    internal RepositoryTrustLevel? RequestedTrust { get; init; }

    /// <summary>Gets whether any repository discovery option was explicitly supplied.</summary>
    internal bool RepositoryOptionsSpecified { get; init; }

    /// <summary>Gets whether the interactive terminal was requested.</summary>
    internal bool UseInteractiveTerminal { get; init; }

    /// <summary>Gets whether side-effect-free command help was requested.</summary>
    internal bool ShowHelp { get; init; }

    /// <summary>Gets whether side-effect-free product version output was requested.</summary>
    internal bool ShowVersion { get; init; }

    /// <summary>Gets the requested standalone Codex authentication action.</summary>
    internal string? CodexAuthenticationAction { get; init; }

    /// <summary>Gets the requested exact headless MCP lifecycle action.</summary>
    internal string? McpAction { get; init; }

    /// <summary>Gets whether the caller confirmed a sensitive MCP identity mutation.</summary>
    internal bool McpConfirmed { get; init; }

    /// <summary>Gets whether local identity cleanup may follow unconfirmed remote revocation.</summary>
    internal bool McpAllowLocalCleanup { get; init; }

    /// <summary>Gets whether switch-account should remotely revoke the current identity first.</summary>
    internal bool McpRevokeCurrentIdentity { get; init; }

    /// <summary>Gets the explicit non-persistent model exchange JSONL log path, when requested.</summary>
    internal string? RawModelLogPath { get; init; }

    /// <summary>Gets conversational arguments after host switches were removed.</summary>
    internal required IReadOnlyList<string> RequestArguments { get; init; }
}

/// <summary>Represents either parsed options or one user-facing command-line error.</summary>
internal sealed record CommandLineParseResult
{
    /// <summary>Gets parsed options when parsing succeeded.</summary>
    internal CommandLineOptions? Options { get; init; }

    /// <summary>Gets the actionable error when parsing failed.</summary>
    internal string? Error { get; init; }

    /// <summary>Creates a successful parse result.</summary>
    internal static CommandLineParseResult Success(CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CommandLineParseResult { Options = options };
    }

    /// <summary>Creates a failed parse result without partial options.</summary>
    internal static CommandLineParseResult Failure(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new CommandLineParseResult { Error = error };
    }
}
