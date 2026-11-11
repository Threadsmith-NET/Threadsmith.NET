namespace Threadsmith.Interaction.Coordination;

using Threadsmith.Interaction.Presentation;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Tools;

/// <summary>Projects host-permitted command approval choices through the existing modal owner.</summary>
public sealed partial class InteractionCoordinator
{
    private async Task HandleCommandPolicyAsync(string argument, CancellationToken cancellationToken)
    {
        try
        {
            var coordinator = _commandPolicies ?? throw new InvalidOperationException("Command policy is unavailable.");
            var parts = argument.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                await _surface.WriteAsync("Usage: /command-policy <shell> [inspect|revoke <grant-id>|reload|reset-session]\n", PresentationTextRole.Warning, cancellationToken);
                return;
            }

            var operation = parts.Length == 1 ? "inspect" : parts[1];
            if (operation == "reload")
            {
                var reloadConfiguration = coordinator.Configuration ?? throw new InvalidOperationException("Command policy update is unavailable.");
                var platform = OperatingSystem.IsWindows() ? CommandPolicyNames.Windows : OperatingSystem.IsMacOS() ? CommandPolicyNames.MacOS : CommandPolicyNames.Linux;
                var reloadReceipt = await reloadConfiguration.UpdateAsync(new PolicyUpdateRequest(PolicyDestination.User, platform, parts[0], "reload", PolicyUpdateOperation.Reload, "reload"), cancellationToken);
                await _surface.WriteAsync($"Command policy reload: activated={reloadReceipt.Activated}, status={reloadReceipt.ReasonCode}.\n", reloadReceipt.Activated ? PresentationTextRole.Status : PresentationTextRole.Error, cancellationToken);
                return;
            }

            var policy = coordinator.Inspect();
            if (!policy.CommandPolicies.TryGetValue(parts[0], out var snapshot))
            {
                await _surface.WriteAsync("No active profile for that host shell.\n", PresentationTextRole.Warning, cancellationToken);
                return;
            }

            if (operation == "inspect")
            {
                // This is a manual surface write, not a conversation, event, diagnostic or model-tool result.
                await _surface.WriteAsync(InteractionPresentationFormatter.FormatCommandPolicyInspection(snapshot, coordinator.Grants.Epoch, coordinator.Grants.InspectSession()), PresentationTextRole.Status, cancellationToken);
                return;
            }

            if (operation == "reset-session")
            {
                coordinator.Reset();
                await _surface.WriteAsync("Live command approvals revoked. Persistent approvals are unchanged.\n", PresentationTextRole.Status, cancellationToken);
                return;
            }

            if (operation != "revoke" || parts.Length != 3)
            {
                throw new ArgumentException("Use inspect, revoke <grant-id>, reload, or reset-session.");
            }

            var configuration = coordinator.Configuration ?? throw new InvalidOperationException("Command policy update is unavailable.");
            var request = new PolicyUpdateRequest(
                PolicyDestination.User,
                snapshot.Platform,
                snapshot.Shell,
                snapshot.ConfigurationRevision,
                PolicyUpdateOperation.Revoke,
                parts[2]);
            var receipt = await configuration.UpdateAsync(request, cancellationToken);
            await _surface.WriteAsync($"Command policy: disk published={receipt.DiskPublished}, activated={receipt.Activated}, status={receipt.ReasonCode}.\n", receipt.Activated ? PresentationTextRole.Status : PresentationTextRole.Error, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            await _surface.WriteAsync("Command policy authority is unavailable. Correct the configuration and reload or reopen the repository before managing or executing commands.\n", PresentationTextRole.Error, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            await _surface.WriteAsync("Command policy management failed: " + exception.Message + "\n", PresentationTextRole.Error, cancellationToken);
        }
    }

    private Task ReportCommandApprovalCompletionAsync(CommandApprovalCompletion completion, CancellationToken cancellationToken)
    {
        // Direct user-surface output is not a conversation message, event or retrievable diagnostic.
        var message = completion.Receipt switch
        {
            null => "No completed persistent approval save receipt is available. This invocation is denied. Inspect saved approvals before retrying.",
            { DiskPublished: false, Activated: false } => "Persistent command approval was not saved or activated. This invocation is denied.",
            { Activated: false } => "Persistent command approval was saved, but policy activation failed. Command execution remains blocked. Correct the configuration and reload or reopen the repository to validate all authority.",
            _ when !completion.ContinuationApproved => "Persistent command approval is saved and active, but this invocation was revoked. Resetting live approvals does not remove saved approvals; revoke the saved approval to remove it.",
            _ => "Persistent command approval is saved and active. The invocation remains subject to all execution checks.",
        };
        return _surface.WriteAsync(message + "\n", completion.ContinuationApproved ? PresentationTextRole.Status : PresentationTextRole.Warning, cancellationToken);
    }

    private async Task<PolicyApprovalResponse> PromptForCommandApprovalAsync(PolicyApprovalRequest request, CancellationToken cancellationToken)
    {
        await _modalGate.WaitAsync(cancellationToken);
        try
        {
            var labels = request.Choices.Select(choice => choice switch
            {
                PolicyApprovalChoice.Once => "Approve Once",
                PolicyApprovalChoice.Session => "Approve for This Session",
                PolicyApprovalChoice.Always => "Approve Always Going Forward",
                _ => "Deny",
            }).ToArray();
            var selected = await _surface.SelectAsync(
                request.Summary + "\nRequester: " + request.Requester + "\nRules: " + string.Join(", ", request.RuleIds)
                + "\nRemembered approval covers only this exact command, targets, arguments, directory, environment and policy. Identity: " + request.ContextFingerprint,
                labels,
                cancellationToken);
            return new PolicyApprovalResponse(request.RequestId, selected is { } index && index >= 0 && index < request.Choices.Length ? request.Choices[index] : PolicyApprovalChoice.Deny);
        }
        finally
        {
            _modalGate.Release();
        }
    }
}
