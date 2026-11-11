namespace Threadsmith.App;

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;

/// <summary>Uses the existing settings and authority publication owners for command-policy updates.</summary>
internal sealed partial class RepositoryScopedBindingCoordinator : IPolicyConfigurationHost
{
    /// <inheritdoc />
    public async Task<PolicyUpdateReceipt> UpdateAsync(PolicyUpdateRequest request, Func<PolicyConfigurationInputs, PolicyDocument> transform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transform);
        if (request.Operation == PolicyUpdateOperation.Reload)
        {
            // Manual reload reuses the existing idle-only, complete repository binding lifecycle.
            // Recovery needs no executable fallback or retained authority comparison snapshot.
            try
            {
                await BindRepositoryAsync(_currentRepositoryRoot, cancellationToken);
                var reloaded = _policyBinding?.Current ?? throw new InvalidOperationException("Command policy binding is unavailable.");
                var revision = reloaded.CommandPolicies.GetValueOrDefault(request.Shell)?.ConfigurationRevision ?? string.Empty;
                return new PolicyUpdateReceipt(false, true, revision, "PolicyActivated");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
            {
                return new PolicyUpdateReceipt(false, false, string.Empty, "PolicyActivationFailed");
            }
        }

        await _gate.WaitAsync(cancellationToken);
        var diskPublished = false;
        try
        {
            var binding = _policyBinding ?? throw new InvalidOperationException("Command policy binding is unavailable.");
            var prior = binding.Current;
            var paths = _configurationPaths(_currentRepositoryRoot);
            var path = request.Destination == PolicyDestination.Machine ? paths.MachineConfiguration : paths.UserConfiguration;
            diskPublished = await RepositorySettingsCoordinator.UpdateAsync(
                path,
                token =>
                {
                    ConfigurationRoot machine;
                    ConfigurationRoot user;
                    PolicyConfigurationInputs inputs;
                    try
                    {
                        machine = ReadRoot(paths.MachineConfiguration, token);
                        user = ReadRoot(paths.UserConfiguration, token);
                        var machineDocument = Extract(machine.Root);
                        var userDocument = Extract(user.Root);
                        var baseline = ShippedCommandPolicy.CreateDefaults();
                        _ = Threadsmith.Policies.Evaluation.PolicyCompiler.CompileAll(baseline, machineDocument, userDocument);
                        inputs = new PolicyConfigurationInputs(baseline, machineDocument, userDocument, PolicyJson.Fingerprint(new { machine = machineDocument, user = userDocument }));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or JsonException)
                    {
                        // Invalid current authority is different from a stale update request or a failed save.
                        binding.Invalidate();
                        throw;
                    }

                    var candidate = transform(inputs);
                    var selected = request.Destination == PolicyDestination.Machine ? machine : user;
                    var tools = Object(selected.Root, "tools");
                    var process = Object(tools, "runProcess");
                    process["commandPolicy"] = JsonNode.Parse(PolicyJson.Serialize(candidate));
                    var bytes = Encoding.UTF8.GetBytes(selected.Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

                    // Validate the actual indented representation, not a compact reserialization.
                    // Candidate rejection must leave the existing file and live authority untouched.
                    if (bytes.Length > SecurityPolicyConfigurationLoader.TrustedConfigurationMaximumBytes)
                    {
                        throw new InvalidDataException("PolicyConfigurationTooLarge");
                    }

                    var persistedValues = ConfigurationDocument.Parse(bytes);
                    var persisted = PolicyJson.Parse(persistedValues["tools:runProcess:commandPolicy"].GetRawText());
                    _ = Threadsmith.Policies.Evaluation.PolicyCompiler.CompileAll(
                        inputs.Baseline,
                        request.Destination == PolicyDestination.Machine ? persisted : inputs.Machine,
                        request.Destination == PolicyDestination.User ? persisted : inputs.User);
                    return Task.FromResult<RepositorySettingsUpdate?>(new RepositorySettingsUpdate(bytes)
                    {
                        ExpectedContent = selected.Existed ? selected.Bytes : (ReadOnlyMemory<byte>?)null,
                        Overwrite = selected.Existed,
                        AuthorizedPolicyKeys = ["tools:runProcess:commandPolicy"],
                    });
                },
                cancellationToken: cancellationToken);
            if (!diskPublished)
            {
                return new PolicyUpdateReceipt(false, false, request.ExpectedRevision, "PolicyPublicationConflict");
            }

            // Disk publication does not authorize an effect. Invalidate first and publish only a validated live candidate.
            binding.Invalidate(request.AuthorityTransitionId);
            var next = SecurityPolicyConfigurationLoader.Load(paths, _policyBindingId, ++_policyGeneration);
            next = next with { MaximumTrust = next.MaximumTrust < prior.MaximumTrust ? next.MaximumTrust : prior.MaximumTrust };
            if (CommonAuthority(next) != CommonAuthority(prior))
            {
                return new PolicyUpdateReceipt(true, false, request.ExpectedRevision, "PolicyFullRebindRequired");
            }

            binding.Publish(next);
            var revision = next.CommandPolicies.GetValueOrDefault(request.Shell)?.ConfigurationRevision ?? string.Empty;
            return new PolicyUpdateReceipt(diskPublished, true, revision, "PolicyActivated");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            if (diskPublished)
            {
                _policyBinding?.Invalidate();
                return new PolicyUpdateReceipt(diskPublished, false, string.Empty, "PolicyActivationFailed");
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CommonAuthority(EffectiveSessionPolicy policy)
    {
        var normalized = policy with
        {
            Fingerprint = string.Empty,
            Generation = 0,
            Sources = [],
            Diagnostics = [],
            AllowedTools = policy.AllowedTools.IsDefault ? [] : policy.AllowedTools,
            NetworkHostRestriction = policy.NetworkHostRestriction.IsDefault ? [] : policy.NetworkHostRestriction,
        };
        return PolicyJson.Fingerprint(new { ToolsRestricted = !policy.AllowedTools.IsDefault, NetworkRestricted = !policy.NetworkHostRestriction.IsDefault, normalized });
    }

    private static PolicyDocument? Extract(JsonObject root)
    {
        var values = ConfigurationDocument.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));
        return values.TryGetValue("tools:runProcess:commandPolicy", out var document) ? PolicyJson.Parse(document.GetRawText()) : null;
    }

    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject present)
        {
            return present;
        }

        if (parent[name] is not null)
        {
            throw new InvalidDataException("PolicyConfigurationShapeInvalid");
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static ConfigurationRoot ReadRoot(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(path);
        var bytes = ConfigurationDocument.ReadContent(path, SecurityPolicyConfigurationLoader.TrustedConfigurationMaximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is null)
        {
            return new ConfigurationRoot([], [], false);
        }

        var root = ConfigurationDocument.ParseObject(bytes, path);
        cancellationToken.ThrowIfCancellationRequested();
        return new ConfigurationRoot(root, bytes, true);
    }

    private sealed record ConfigurationRoot(JsonObject Root, byte[] Bytes, bool Existed);
}
