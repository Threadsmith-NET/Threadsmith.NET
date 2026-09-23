namespace Threadsmith.Interaction.Coordination;

using System.Globalization;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;

/// <summary>Manages skill verification and availability through existing shared command authority.</summary>
public sealed partial class InteractionCoordinator
{
    private async Task<T?> RunCancellableSkillOperationAsync<T>(
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(execute);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeInput = _surface.BeginActiveRunInput(_timeProvider);
        var activeInputTask = activeInput?.ReadAsync(operation.Token);
        try
        {
            if (activeInput is not null && !_surface.Surface.Capabilities.SupportsRetainedRunHints)
            {
                await _surface.WriteAsync(
                    "ESC-ESC to cancel\n",
                    PresentationTextRole.Status,
                    operation.Token);
            }

            var operationTask = execute(operation.Token);
            while (!operationTask.IsCompleted && activeInputTask is not null)
            {
                var completed = await Task.WhenAny(operationTask, activeInputTask);
                if (completed == operationTask)
                {
                    break;
                }

                var signal = await activeInputTask;
                if (signal == ActiveRunInputSignal.CancellationRequested)
                {
                    await operation.CancelAsync();
                    break;
                }

                activeInputTask = activeInput!.ReadAsync(operation.Token);
            }

            try
            {
                return await operationTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await _surface.WriteAsync(
                    "Skill command cancelled.\n",
                    PresentationTextRole.Status,
                    CancellationToken.None);
                return null;
            }
        }
        finally
        {
            await DisposeActiveRunInputAsync(activeInput, activeInputTask);
        }
    }

    private async Task ManageSkillsAsync(InteractionController controller, CancellationToken cancellationToken)
    {
        var catalog = await controller.ListSkillsAsync(new SkillCatalogQuery { MaximumResults = int.MaxValue }, cancellationToken);
        if (catalog.Count == 0)
        {
            await _surface.WriteAsync("No skills are available. Use /skills refresh to rediscover packages.\n", PresentationTextRole.Status, cancellationToken);
            return;
        }

        var candidates = catalog
            .OrderBy(SkillFormat, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Provenance.Scope == SkillScope.Maintained ? -1 : (int)candidate.Provenance.Scope)
            .ThenBy(candidate => candidate.Metadata.DisplayName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Metadata.Version, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Provenance.Source, StringComparer.Ordinal)
            .Select((candidate, index) => (Id: index.ToString(CultureInfo.InvariantCulture), Candidate: candidate))
            .ToDictionary(item => item.Id, item => item.Candidate, StringComparer.Ordinal);
        var request = new InteractionToggleRequest(
            "Skills - checked means enabled",
            candidates.Select(item => SkillToggleOption(item.Key, item.Value)).ToArray())
        {
            AllowGroupActions = true,
        };
        if (_surface.Surface is IInteractionActionToggleSurface actions)
        {
            await actions.SelectActionTogglesAsync(
                request,
                (id, enabled, token) => ApplyAsync(id, enabled, token),
                (id, action, token) => action == "verify"
                    ? ApplyAsync(id, null, token)
                    : Task.FromResult(new InteractionToggleResult(candidates[id].Enabled, "Unknown skill action.")),
                cancellationToken);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var entries = candidates.ToArray();
            var selected = await _surface.SelectAsync(
                "Skills - choose a package:",
                [.. entries.Select(item => $"{SkillFormat(item.Value)} / {item.Value.Provenance.Scope}: {SkillToggleOption(item.Key, item.Value).Label}"), "Back"],
                cancellationToken);
            if (selected < 0 || selected >= entries.Length)
            {
                return;
            }

            var entry = entries[selected];
            var action = await _surface.SelectAsync(
                SkillToggleOption(entry.Key, entry.Value).Label,
                ["Verify", entry.Value.Enabled ? "Disable" : "Enable", "Back"],
                cancellationToken);
            if (action is 0 or 1)
            {
                var result = await ApplyAsync(entry.Key, action == 0 ? null : !entry.Value.Enabled, cancellationToken);
                await _surface.WriteAsync(result.Reason + Environment.NewLine, PresentationTextRole.Status, cancellationToken);
            }
        }

        async Task<InteractionToggleResult> ApplyAsync(string id, bool? enabled, CancellationToken token)
        {
            var expected = candidates[id];
            var current = expected;
            string? failure = null;
            try
            {
                current = await FindCurrentAsync(expected, token);
                if (current is null)
                {
                    return ChangedCatalog(id, expected);
                }

                var selector = SkillActionSelector(current);
                current = enabled is { } desired
                    ? await controller.SetSkillEnabledAsync(selector, desired, token)
                    : await controller.VerifySkillAsync(selector, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                failure = "Skill action cancelled; showing current state.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure = "Skill action failed; showing current state. Use /skills inspect for details.";
            }

            if (failure is not null)
            {
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                refresh.CancelAfter(TimeSpan.FromSeconds(5));
                current = await FindCurrentAsync(expected, refresh.Token);
            }

            if (current is null)
            {
                return ChangedCatalog(id, expected);
            }

            candidates[id] = current;
            var reason = failure ?? $"{current.Metadata.DisplayName}: [{current.Verification}] {(current.Enabled ? "enabled" : "disabled")}. {current.VerificationReason}";
            return new InteractionToggleResult(current.Enabled, reason)
            {
                UpdatedOption = SkillToggleOption(id, current),
            };
        }

        async Task<SkillCatalogCandidate?> FindCurrentAsync(SkillCatalogCandidate expected, CancellationToken token)
        {
            var matches = await controller.ListSkillsAsync(
                new SkillCatalogQuery { SkillId = expected.Metadata.SkillId, Scope = expected.Provenance.Scope, MaximumResults = int.MaxValue }, token);
            return matches.SingleOrDefault(candidate => candidate.Identity == expected.Identity
                && candidate.Provenance.Source == expected.Provenance.Source
                && candidate.Provenance.PackageRoot == expected.Provenance.PackageRoot);
        }
    }

    private static InteractionToggleResult ChangedCatalog(string id, SkillCatalogCandidate candidate)
    {
        const string reason = "The skill catalog changed; close and reopen Skills.";
        return new InteractionToggleResult(false, reason)
        {
            UpdatedOption = SkillToggleOption(id, candidate) with
            {
                Label = candidate.Metadata.DisplayName + " [catalog changed]",
                Enabled = false,
                Locked = true,
                Reason = reason,
                Actions = [],
            },
        };
    }

    private static InteractionToggleOption SkillToggleOption(string id, SkillCatalogCandidate candidate)
    {
        var format = SkillFormat(candidate);
        var scope = candidate.Provenance.Scope.ToString();
        var name = candidate.Metadata.DisplayName == candidate.Metadata.SkillId.Value
            ? candidate.Metadata.DisplayName
            : $"{candidate.Metadata.DisplayName} ({candidate.Metadata.SkillId.Value})";
        return new InteractionToggleOption(
            id,
            $"{name}@{candidate.Metadata.Version} [{candidate.Verification}] {(candidate.Enabled ? "enabled" : "disabled")}",
            $"{format} / {scope}",
            candidate.Enabled,
            Reason: $"{candidate.Metadata.Description}\n{candidate.VerificationReason}\nSource: {candidate.Provenance.Source}\nSelector: {SkillActionSelector(candidate)}\nDigest: {candidate.Identity.Digest.Value}")
        {
            GroupPath = [format, scope],
            Actions = [new("verify", "Verify")],
        };
    }

    private static string SkillFormat(SkillCatalogCandidate candidate) =>
        candidate.Provenance.Source.StartsWith("claude:", StringComparison.Ordinal)
            && candidate.Metadata.SkillId.Value.StartsWith("claude.", StringComparison.Ordinal)
            ? "Claude"
            : "Native";

    private static string SkillActionSelector(SkillCatalogCandidate candidate)
    {
        var selector = $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}@{candidate.Metadata.Version}";

        // Claude discovery has no exact digest until the first explicit activation/verification.
        return SkillFormat(candidate) == "Claude" && candidate.Identity.Digest.Value.All(character => character == '0')
            ? selector
            : $"{selector}+{candidate.Identity.Digest.Value}";
    }
}
