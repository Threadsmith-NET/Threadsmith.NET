namespace Threadsmith.Interaction.Coordination;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Tools;

/// <summary>Reconciles retained availability requests through the existing tool authority.</summary>
public sealed partial class InteractionCoordinator
{
    private static Task ManageHookTogglesAsync(
        InteractionController controller,
        IInteractionToggleSurface surface,
        IReadOnlyList<HookHandlerDescriptor> catalog,
        CancellationToken cancellationToken)
    {
        var identities = catalog.ToDictionary(handler => handler.Identity.Id.Value, handler => handler.Identity, StringComparer.Ordinal);
        return surface.SelectTogglesAsync(
            new InteractionToggleRequest("Hooks - checked means enabled", catalog.Select(handler => new InteractionToggleOption(
                handler.Identity.Id.Value, handler.Identity.Id.Value, handler.Scope.ToString(), handler.Enabled, Reason: $"{handler.AdapterKind}; {string.Join(", ", handler.HookPoints)}; enabling does not grant repository approval")).ToArray()),
            async (id, enabled, token) =>
            {
                var current = await controller.InspectHookAsync(new HookHandlerId(id), token);
                if (current is null || current.Identity != identities[id])
                {
                    return new InteractionToggleResult(current?.Enabled == true, "The hook catalog changed; reopen Hooks.");
                }

                string? failure = null;
                if (current.Enabled != enabled)
                {
                    try
                    {
                        if (!await controller.SetHookEnabledAsync(current.Identity.Id, enabled, token))
                        {
                            failure = "The hook setting was not changed.";
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failure = "The hook change failed; showing current state.";
                    }
                }

                var refreshed = await controller.InspectHookAsync(current.Identity.Id, token);
                return new InteractionToggleResult(refreshed?.Enabled == true, failure ?? (refreshed?.Enabled == enabled ? null : "The hook did not reach the requested state."));
            },
            cancellationToken);
    }

    private static Task ManageMcpConnectionTogglesAsync(
        InteractionController controller,
        IInteractionToggleSurface surface,
        IReadOnlyList<McpProfileSummary> catalog,
        CancellationToken cancellationToken)
    {
        var request = new InteractionToggleRequest("MCP connections - checked means connected", catalog.Select(McpConnectionOption).ToArray());
        return surface is IInteractionActionToggleSurface actions
            ? actions.SelectActionTogglesAsync(request, ChangeAsync, AuthenticateAsync, cancellationToken)
            : surface.SelectTogglesAsync(request, ChangeAsync, cancellationToken);

        async Task<InteractionToggleResult> ChangeAsync(string id, bool enabled, CancellationToken token)
        {
            var current = await controller.ManageMcpAsync(new McpManagementRequest { Action = McpManagementAction.Inspect, ProfileId = id }, token);
            if (!current.Succeeded || current.Profile is null)
            {
                return new InteractionToggleResult(current.Profile?.Summary.State == "Connected", current.Message);
            }

            string? failure = null;
            if ((current.Profile.Summary.State == "Connected") != enabled)
            {
                try
                {
                    var result = await controller.ManageMcpAsync(
                        new McpManagementRequest
                    {
                        Action = enabled ? McpManagementAction.Connect : McpManagementAction.Disconnect,
                        ProfileId = id,
                    },
                        token);
                    if (!result.Succeeded)
                    {
                        failure = result.Message;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failure = "The MCP connection change failed; showing current state.";
                }
            }

            var refreshed = await controller.ManageMcpAsync(new McpManagementRequest { Action = McpManagementAction.Inspect, ProfileId = id }, token);
            var connected = refreshed.Profile?.Summary.State == "Connected";
            return new InteractionToggleResult(connected, failure ?? (!refreshed.Succeeded ? refreshed.Message : connected == enabled ? null : "The MCP connection did not reach the requested state."))
            {
                UpdatedOption = refreshed.Profile is { } detail ? McpConnectionOption(detail.Summary) : null,
            };
        }

        async Task<InteractionToggleResult> AuthenticateAsync(string id, string actionId, CancellationToken token)
        {
            string? notice = null;
            try
            {
                var current = await controller.ManageMcpAsync(new McpManagementRequest { Action = McpManagementAction.Inspect, ProfileId = id }, token);
                if (actionId != "authenticate" || !current.Succeeded || current.Profile is not { } profile
                    || profile.Summary.AuthenticationState == McpAuthenticationState.NotApplicable || !profile.Summary.Eligible)
                {
                    return new InteractionToggleResult(current.Profile?.Summary.State == "Connected", "Authentication is unavailable for this profile.");
                }

                var result = await controller.ManageMcpAsync(new McpManagementRequest { Action = McpManagementAction.Authenticate, ProfileId = id }, token);
                notice = token.IsCancellationRequested ? "Authentication cancelled." : result.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && token.IsCancellationRequested)
            {
                notice = "Authentication cancelled.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                notice = "Authentication failed; showing current state.";
            }

            using var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            refresh.CancelAfter(TimeSpan.FromSeconds(5));
            var refreshed = await controller.ManageMcpAsync(new McpManagementRequest { Action = McpManagementAction.Inspect, ProfileId = id }, refresh.Token);
            return new InteractionToggleResult(refreshed.Profile?.Summary.State == "Connected", notice)
            {
                UpdatedOption = refreshed.Profile is { } detail ? McpConnectionOption(detail.Summary) : null,
            };
        }
    }

    private static InteractionToggleOption McpConnectionOption(McpProfileSummary profile)
    {
        var authentication = profile.AuthenticationState switch
        {
            McpAuthenticationState.NotApplicable => string.Empty,
            McpAuthenticationState.SignedOut => " [sign-in required]",
            McpAuthenticationState.Cached => " [credentials cached]",
            McpAuthenticationState.Authenticated => " [authenticated]",
            _ => $" [{profile.AuthenticationState}]",
        };
        return new InteractionToggleOption(
            profile.ProfileId,
            $"{profile.DisplayName} ({profile.ProfileId}){authentication}",
            "Connections",
            profile.State == "Connected",
            Locked: !profile.Eligible && profile.State != "Connected",
            Reason: $"{profile.Transport}; {profile.EndpointIdentity}; tool enablement is managed with /mcp capabilities" + (profile.Eligible ? string.Empty : "; profile is not eligible to connect"))
        {
            Actions = profile.Eligible && profile.AuthenticationState != McpAuthenticationState.NotApplicable
                ? [new("authenticate", "Sign in / Authenticate")]
                : [],
        };
    }

    private static Task ManageExtensionTogglesAsync(
        IExtensionManager manager,
        SessionId sessionId,
        IInteractionToggleSurface surface,
        IReadOnlyList<ExtensionSummary> catalog,
        CancellationToken cancellationToken)
    {
        return surface.SelectTogglesAsync(
            new InteractionToggleRequest("Extensions - checked means loaded", catalog.Select(extension => new InteractionToggleOption(
                extension.ExtensionId, $"{extension.Name} ({extension.Version})", "Extensions", extension.IsLoaded, Reason: extension.ExtensionId)).ToArray()),
            async (id, enabled, token) =>
            {
                var current = (await manager.DiscoverAsync(token)).FirstOrDefault(extension => extension.ExtensionId == id);
                if (current is null)
                {
                    return new InteractionToggleResult(false, "The extension is no longer available; reopen Extensions.");
                }

                string? failure = null;
                if (current.IsLoaded != enabled)
                {
                    try
                    {
                        if (enabled)
                        {
                            _ = await manager.LoadAsync(id, sessionId, token);
                        }
                        else
                        {
                            _ = await manager.UnloadAsync(id, sessionId, token);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failure = "The extension change failed; showing current state.";
                    }
                }

                var refreshed = (await manager.DiscoverAsync(token)).FirstOrDefault(extension => extension.ExtensionId == id);
                return new InteractionToggleResult(refreshed?.IsLoaded == true, failure ?? (refreshed?.IsLoaded == enabled ? null : $"The extension did not reach the requested state ({refreshed?.State ?? "unavailable"})."));
            },
            cancellationToken);
    }

    private static Task ManageMcpTogglesAsync(
        InteractionController controller,
        IInteractionToggleSurface surface,
        string profileId,
        IReadOnlyList<McpCapabilityDescriptor> catalog,
        CancellationToken cancellationToken)
    {
        var tools = catalog.Where(capability => capability.Enabled is not null).ToDictionary(capability => capability.CapabilityId, StringComparer.Ordinal);
        return surface.SelectTogglesAsync(
            new InteractionToggleRequest("MCP tool enablement (connection is separate)", tools.Values.Select(capability =>
                new InteractionToggleOption(capability.CapabilityId, capability.Name, profileId, capability.Enabled == true, Reason: capability.Description)).ToArray()),
            async (id, enabled, token) =>
            {
                var current = await controller.ManageMcpAsync(new McpManagementRequest { Action = McpManagementAction.ListCapabilities, ProfileId = profileId }, token);
                var actual = current.Capabilities.FirstOrDefault(capability => capability.CapabilityId == id);
                if (actual is null || actual.Digest != tools[id].Digest)
                {
                    return new InteractionToggleResult(actual?.Enabled == true, "The connection or capability catalog changed; reopen this selector.");
                }

                var result = await controller.ManageMcpAsync(
                    new McpManagementRequest { Action = enabled ? McpManagementAction.EnableTool : McpManagementAction.DisableTool, ProfileId = profileId, CapabilityId = id, ExpectedCapabilityDigest = tools[id].Digest },
                    token);
                var refreshed = await controller.ManageMcpAsync(new McpManagementRequest { Action = McpManagementAction.ListCapabilities, ProfileId = profileId }, token);
                return new InteractionToggleResult(
                    refreshed.Capabilities.FirstOrDefault(capability => capability.CapabilityId == id)?.Enabled == true,
                    result.Succeeded ? null : result.Message);
            },
            cancellationToken);
    }

    private async Task<InteractionToggleResult> ApplyToolToggleAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        var manager = _toolStateManager ?? throw new InvalidOperationException("Tool management is unavailable.");
        var state = manager.GetAllStates().FirstOrDefault(item => item.Id == id);
        if (state is null)
        {
            return new(false, "The tool catalog changed; reopen Tools.");
        }

        if (state.Essential)
        {
            return new(state.Enabled, "Essential tools cannot be disabled.");
        }

        if (state.Enabled == enabled)
        {
            return new(state.Enabled);
        }

        try
        {
            if (!enabled)
            {
                await manager.DisableAsync(id, cancellationToken);
            }
            else if (state.ConsentRequired)
            {
                var confirmation = await _surface.SelectAsync(
                    "Web Search may send query text to the configured provider. Selected results may be retrieved. An exact public HTTPS URL in your current request may be contacted only if the model invokes web_fetch; model-proposed destinations require separate inline approval. Fetched content is untrusted and supplied to the model. Grant this repository-bound consent?",
                    ["No — keep disabled", "Yes — grant consent and enable"],
                    cancellationToken);
                if (confirmation != 1)
                {
                    return new(manager.IsEnabled(id), "Consent declined; setting unchanged.");
                }

                await manager.GrantConsentAndEnableAsync(
                    id,
                    retrievalDisclosureAcknowledged: true,
                    currentMessageUrlDisclosureAcknowledged: true,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await manager.EnableAsync(id, cancellationToken);
            }

            return new(manager.IsEnabled(id));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(manager.IsEnabled(id), "Host rejected the availability change; setting reflects actual state.");
        }
    }
}
