namespace Threadsmith.Interaction.Coordination;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Tools;

/// <summary>Reconciles retained availability requests through the existing tool authority.</summary>
public sealed partial class InteractionCoordinator
{
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
