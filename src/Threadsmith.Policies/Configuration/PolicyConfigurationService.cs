namespace Threadsmith.Policies.Configuration;

using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;

/// <summary>Identifies an authority destination without exposing host paths.</summary>
public enum PolicyDestination
{
    /// <summary>The privileged machine document.</summary>
    Machine,

    /// <summary>The selected user document.</summary>
    User,
}

/// <summary>Identifies one supported, typed policy-list transformation.</summary>
public enum PolicyUpdateOperation
{
    /// <summary>Adds a new stable rule identity.</summary>
    Add,

    /// <summary>Replaces a complete existing source rule.</summary>
    Replace,

    /// <summary>Removes a source rule.</summary>
    Remove,

    /// <summary>Disables an inherited ordinary rule.</summary>
    Disable,

    /// <summary>Adds an exact persistent approval.</summary>
    Grant,

    /// <summary>Removes an exact persistent approval.</summary>
    Revoke,

    /// <summary>Validates manually edited sources and refreshes authority without rewriting them.</summary>
    Reload,
}

/// <summary>Requests a bounded transformation, never an arbitrary JSON patch.</summary>
public sealed record PolicyUpdateRequest(
    PolicyDestination Destination,
    string Platform,
    string Shell,
    string ExpectedRevision,
    PolicyUpdateOperation Operation,
    string Id,
    PolicyRule? Rule = null,
    PolicyGrant? Grant = null,
    Guid? AuthorityTransitionId = null);

/// <summary>Provides raw typed sources read under the existing settings coordinator gate.</summary>
public sealed record PolicyConfigurationInputs(PolicyDocument Baseline, PolicyDocument? Machine, PolicyDocument? User, string Revision);

/// <summary>Separates a disk publication from effective-authority activation.</summary>
public sealed record PolicyUpdateReceipt(bool DiskPublished, bool Activated, string Revision, string ReasonCode);

/// <summary>Owns authorized host IO, coordinated publication and serialized binding activation.</summary>
public interface IPolicyConfigurationHost
{
    /// <summary>Runs the transformation under the existing coordinated settings update and publishes effective authority.</summary>
    Task<PolicyUpdateReceipt> UpdateAsync(
        PolicyUpdateRequest request,
        Func<PolicyConfigurationInputs, PolicyDocument> transform,
        CancellationToken cancellationToken = default);
}

/// <summary>Owns every host-managed transformation of command rules and persistent approval lists.</summary>
public sealed class PolicyConfigurationService
{
    private readonly IPolicyConfigurationHost _host;

    /// <summary>Initializes a new instance of the <see cref="PolicyConfigurationService"/> class.</summary>
    public PolicyConfigurationService(IPolicyConfigurationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Applies a typed transformation, validating effective authority before publication.</summary>
    public Task<PolicyUpdateReceipt> UpdateAsync(PolicyUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Destination) || !Enum.IsDefined(request.Operation) || string.IsNullOrWhiteSpace(request.Id)
            || request.Id.Length > 128 || string.IsNullOrWhiteSpace(request.ExpectedRevision)
            || ((request.Operation is PolicyUpdateOperation.Add or PolicyUpdateOperation.Replace) != (request.Rule is not null))
            || ((request.Operation == PolicyUpdateOperation.Grant) != (request.Grant is not null))
            || (request.AuthorityTransitionId.HasValue && (request.AuthorityTransitionId == Guid.Empty || request.Operation != PolicyUpdateOperation.Grant)))
        {
            throw new ArgumentException("PolicyInvalidUpdate", nameof(request));
        }

        return _host.UpdateAsync(request, inputs => Transform(request, inputs), cancellationToken);
    }

    private static PolicyDocument Transform(PolicyUpdateRequest request, PolicyConfigurationInputs inputs)
    {
        if (request.Operation == PolicyUpdateOperation.Reload)
        {
            _ = PolicyCompiler.CompileAll(inputs.Baseline, inputs.Machine, inputs.User);
            return (request.Destination == PolicyDestination.Machine ? inputs.Machine : inputs.User) ?? new PolicyDocument();
        }

        if (request.ExpectedRevision != inputs.Revision)
        {
            throw new InvalidDataException("PolicyRevisionConflict");
        }

        var document = (request.Destination == PolicyDestination.Machine ? inputs.Machine : inputs.User) ?? new PolicyDocument();
        var platform = document.Platforms.GetValueOrDefault(request.Platform) ?? new PolicyProfile();
        var profile = platform.Shells.GetValueOrDefault(request.Shell) ?? new PolicyProfile();
        var exists = profile.Rules.Any(rule => rule.Id == request.Id);
        switch (request.Operation)
        {
            case PolicyUpdateOperation.Add:
            case PolicyUpdateOperation.Replace:
            {
                if (request.Rule is not { } rule || rule.Id != request.Id || exists != (request.Operation == PolicyUpdateOperation.Replace))
                {
                    throw new InvalidDataException("PolicyRuleUpdateConflict");
                }

                profile = profile with { Rules = [.. profile.Rules.Where(item => item.Id != request.Id), rule] };
                break;
            }

            case PolicyUpdateOperation.Remove:
            {
                profile = profile with { Rules = [.. profile.Rules.Where(item => item.Id != request.Id)] };
                break;
            }

            case PolicyUpdateOperation.Disable:
            {
                profile = profile with { DisabledRuleIds = [.. profile.DisabledRuleIds.Append(request.Id).Distinct(StringComparer.Ordinal)] };
                break;
            }

            case PolicyUpdateOperation.Grant:
            {
                if (request.Destination != PolicyDestination.User || request.Grant is not { } grant || grant.Id != request.Id)
                {
                    throw new InvalidDataException("PolicyInvalidGrantUpdate");
                }

                profile = profile with { AlwaysAllow = [.. profile.AlwaysAllow.Where(item => item.Id != request.Id), grant] };
                break;
            }

            case PolicyUpdateOperation.Revoke:
            {
                document = document with { Common = document.Common with { AlwaysAllow = [.. document.Common.AlwaysAllow.Where(item => item.Id != request.Id)] } };
                platform = platform with { AlwaysAllow = [.. platform.AlwaysAllow.Where(item => item.Id != request.Id)] };
                profile = profile with { AlwaysAllow = [.. profile.AlwaysAllow.Where(item => item.Id != request.Id)] };
                break;
            }

            default:
                throw new InvalidDataException("PolicyUnknownUpdate");
        }

        document = document with { Platforms = document.Platforms.SetItem(request.Platform, platform with { Shells = platform.Shells.SetItem(request.Shell, profile) }) };
        _ = PolicyCompiler.Compile(
            inputs.Baseline,
            request.Destination == PolicyDestination.Machine ? document : inputs.Machine,
            request.Destination == PolicyDestination.User ? document : inputs.User,
            request.Platform,
            request.Shell);
        return document;
    }
}
