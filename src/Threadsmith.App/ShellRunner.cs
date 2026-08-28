namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Extensions.Runtime;
using Threadsmith.Tools;
using Threadsmith.Tui;
using Threadsmith.Workspaces;

/// <summary>Runs the selected terminal projection and owns process-global cancellation registration.</summary>
internal static class ShellRunner
{
    /// <summary>Runs interactive or headless commands and translates Ctrl+C into a clean process result.</summary>
    internal static async Task<int> RunAsync(
        ShellRunContext context,
        CancellationTokenSource processCancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(processCancellation);

        // CancelKeyPress is process-global, so unregister it before disposing the source. The handler tolerates
        // the narrow shutdown race where a second Ctrl+C arrives while resources are being released.
        void OnCancelKeyPress(object? _, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            try
            {
                processCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown already owns cancellation and no active operation remains.
            }
        }

        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            if (context.CommandLine.McpAction is not null)
            {
                var mcpShell = new HeadlessShell(
                    context.Dispatcher,
                    context.Projections,
                    Console.Out,
                    context.WebFetchAuthorization,
                    context.Paths.RepositoryRoot);
                var mcpRequest = ParseMcpRequest(context.CommandLine);
                return await mcpShell.WriteMcpResultAsync(mcpRequest, processCancellation.Token);
            }

            if (context.CommandLine.UseInteractiveTerminal)
            {
                var extensionHost = context.ExtensionHost
                    ?? throw new InvalidOperationException("Interactive startup requires the extension host.");
                await new ConversationalShell(
                    new TuiPresenter(context.Dispatcher, context.Projections),
                    context.Events,
                    context.Models.Catalog,
                    context.Applications.EffectiveStartupProfileId,
                    context.Applications.SessionModelPreferences,
                    extensionHost,
                    context.Configuration,
                    context.Applications.SessionUsage,
                    context.ToolStateManager,
                    context.Applications.MutationApprovalPolicy,
                    context.Applications.PlanApprovalPolicy,
                    context.Models.ActiveModels is not null,
                    context.Applications.ClaudeSkillCatalog,
                    sessionLifecycleAvailable: true,
                    gitQueries: new GitQueryService(),
                    webFetchAuthorization: context.WebFetchAuthorization,
                    directFetchApprovalPrompt: context.DirectFetchApprovalPrompt,
                    userConfigurationPath: context.Paths.UserConfiguration,
                    validationStages: context.Applications.ValidationStages,
                    codeExploreOutputOptions: context.CodeExploreOutputOptions).RunAsync(
                        context.Paths.RepositoryRoot,
                        context.CommandLine.RequestedTrust,
                        context.CommandLine.RequestedSolution,
                        context.Models.Status,
                        context.Paths.RepositoryConfigurationDirectoryExistedAtStartup,
                        processCancellation.Token);
                return 0;
            }

            var headlessShell = new HeadlessShell(
                context.Dispatcher,
                context.Projections,
                Console.Out,
                context.WebFetchAuthorization,
                context.Paths.RepositoryRoot);
            var request = string.Join(' ', context.CommandLine.RequestArguments);
            if (context.CommandLine.RepositoryOptionsSpecified)
            {
                if (context.CommandLine.RequestArguments.Count > 0)
                {
                    return await headlessShell.RunRepositoryRequestAsync(
                        "Headless",
                        context.Paths.RepositoryRoot,
                        context.CommandLine.RequestedTrust ?? RepositoryTrustLevel.UntrustedInspection,
                        context.CommandLine.RequestedSolution,
                        request,
                        processCancellation.Token);
                }

                return await headlessShell.InspectRepositoryAsync(
                    "Repository discovery",
                    context.Paths.RepositoryRoot,
                    context.CommandLine.RequestedTrust ?? RepositoryTrustLevel.UntrustedInspection,
                    context.CommandLine.RequestedSolution,
                    processCancellation.Token);
            }

            if (context.CommandLine.RequestArguments.Count == 0)
            {
                return await headlessShell.InspectRepositoryAsync(
                    "Repository discovery",
                    context.Paths.RepositoryRoot,
                    context.CommandLine.RequestedTrust ?? RepositoryTrustLevel.UntrustedInspection,
                    context.CommandLine.RequestedSolution,
                    processCancellation.Token);
            }

            return await headlessShell.RunAsync("Headless", request, processCancellation.Token);
        }
        catch (OperationCanceledException) when (processCancellation.IsCancellationRequested)
        {
            // User cancellation is a normal terminal outcome rather than an unhandled task-cancellation error.
            await Console.Out.WriteLineAsync("Cancelled.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    private static McpManagementRequest ParseMcpRequest(CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var actionText = options.McpAction
            ?? throw new InvalidOperationException("A headless MCP action is required.");
        var action = actionText.ToLowerInvariant() switch
        {
            "list" => McpManagementAction.List,
            "inspect" => McpManagementAction.Inspect,
            "connect" => McpManagementAction.Connect,
            "disconnect" => McpManagementAction.Disconnect,
            "reconnect" => McpManagementAction.Reconnect,
            "capabilities" => McpManagementAction.ListCapabilities,
            "capability" => McpManagementAction.InspectCapability,
            "enable" => McpManagementAction.EnableTool,
            "disable" => McpManagementAction.DisableTool,
            "resource-read" => McpManagementAction.ReadResource,
            "prompt-get" => McpManagementAction.GetPrompt,
            "auth" => McpManagementAction.Authenticate,
            "logout" => McpManagementAction.Logout,
            "revoke" => McpManagementAction.Revoke,
            "switch-account" => McpManagementAction.SwitchAccount,
            "diagnose" => McpManagementAction.Diagnose,
            _ => throw new InvalidOperationException($"Unknown MCP action '{actionText}'."),
        };
        var profileId = options.RequestArguments.ElementAtOrDefault(0);
        var capabilityId = action is McpManagementAction.InspectCapability
            or McpManagementAction.EnableTool
            or McpManagementAction.DisableTool
            or McpManagementAction.ReadResource
            or McpManagementAction.GetPrompt
            ? options.RequestArguments.ElementAtOrDefault(1)
            : null;
        var argumentStart = capabilityId is null ? 1 : 2;
        McpManagedCapabilityKind? capabilityKind = null;
        if (action == McpManagementAction.ListCapabilities
            && options.RequestArguments.ElementAtOrDefault(1) is { } kindText)
        {
            capabilityKind = kindText.ToLowerInvariant() switch
            {
                "tool" or "tools" => McpManagedCapabilityKind.Tool,
                "resource" or "resources" => McpManagedCapabilityKind.Resource,
                "resource-template" or "resource-templates" => McpManagedCapabilityKind.ResourceTemplate,
                "prompt" or "prompts" => McpManagedCapabilityKind.Prompt,
                _ => throw new InvalidOperationException($"Unknown MCP capability kind '{kindText}'."),
            };
            argumentStart = 2;
        }

        if (action is not (McpManagementAction.ReadResource or McpManagementAction.GetPrompt)
            && options.RequestArguments.Count > argumentStart)
        {
            throw new InvalidOperationException("This MCP action does not accept additional arguments.");
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in options.RequestArguments.Skip(argumentStart))
        {
            var pair = argument.Split('=', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]))
            {
                throw new InvalidOperationException(
                    "MCP resource and prompt arguments must use exact key=value syntax.");
            }

            if (!arguments.TryAdd(pair[0], pair[1]))
            {
                throw new InvalidOperationException(
                    $"MCP argument '{pair[0]}' was supplied more than once.");
            }
        }

        return new McpManagementRequest
        {
            Action = action,
            ProfileId = profileId,
            CapabilityId = capabilityId,
            CapabilityKind = capabilityKind,
            Arguments = arguments,
            Confirmed = options.McpConfirmed,
            AllowLocalCleanupAfterUnconfirmedRevocation = options.McpAllowLocalCleanup,
            RevokeCurrentIdentityBeforeSwitch = options.McpRevokeCurrentIdentity,
        };
    }
}

/// <summary>Collects immutable shell dependencies produced by earlier startup phases.</summary>
internal sealed record ShellRunContext
{
    /// <summary>Gets parsed host command-line intent.</summary>
    internal required CommandLineOptions CommandLine { get; init; }

    /// <summary>Gets normalized repository and configuration paths.</summary>
    internal required ConfigurationPaths Paths { get; init; }

    /// <summary>Gets the effective normal-layer configuration.</summary>
    internal required IConfiguration Configuration { get; init; }

    /// <summary>Gets the host command dispatcher shared by terminal modes.</summary>
    internal required CommandDispatcher Dispatcher { get; init; }

    /// <summary>Gets domain projections used by presenters.</summary>
    internal required InMemoryProjectionStore Projections { get; init; }

    /// <summary>Gets the event stream observed by the interactive projection.</summary>
    internal required DomainEventStream Events { get; init; }

    /// <summary>Gets model catalog, startup selection, and status state.</summary>
    internal required ModelServices Models { get; init; }

    /// <summary>Gets composed application policy and session projections.</summary>
    internal required ApplicationServices Applications { get; init; }

    /// <summary>Gets the repository-scoped extension manager, absent for machine-oriented MCP management.</summary>
    internal ExtensionHost? ExtensionHost { get; init; }

    /// <summary>Gets mutable tool availability state used by slash commands.</summary>
    internal required ToolStateManager ToolStateManager { get; init; }

    /// <summary>Gets host-owned per-session code_explore output presentation state.</summary>
    internal required CodeExploreOutputOptions CodeExploreOutputOptions { get; init; }

    /// <summary>Gets transient direct web-fetch authorization owned by user command surfaces.</summary>
    internal required WebFetchAuthorizationAuthority WebFetchAuthorization { get; init; }

    /// <summary>Gets the serialized direct-fetch approval prompt router.</summary>
    internal required DirectFetchApprovalPromptRouter DirectFetchApprovalPrompt { get; init; }
}
