namespace Threadsmith.Hooks;

using System.Security.Cryptography;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Normalizes declarations and computes immutable configuration fingerprints.</summary>
public static class HookDescriptorValidator
{
    private const int MaximumHandlers = 64;

    /// <summary>Validates, fingerprints, and deterministically orders declarations.</summary>
    public static IReadOnlyList<HookHandlerDescriptor> Normalize(
        IEnumerable<HookHandlerDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        HookHandlerDescriptor[] source = [.. descriptors];
        if (source.Length > MaximumHandlers)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptors), "At most 64 hook handlers may be configured.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HookHandlerDescriptor[] normalized = [.. source.Select(NormalizeOne)
            .OrderBy(descriptor => descriptor.Priority)
            .ThenBy(descriptor => descriptor.Scope)
            .ThenBy(descriptor => descriptor.Identity.Id.Value, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Identity.Version, StringComparer.Ordinal)
            .Select(descriptor => ids.Add(descriptor.Identity.Id.Value)
                ? descriptor
                : throw new ArgumentException($"Duplicate hook handler id '{descriptor.Identity.Id}'.", nameof(descriptors)))];
        var aggregateSeconds = normalized.Where(descriptor => descriptor.Enabled)
            .Sum(descriptor => descriptor.Limits.Timeout.TotalSeconds * (descriptor.Limits.MaximumRetries + 1));
        if (aggregateSeconds > TimeSpan.FromMinutes(2).TotalSeconds)
        {
            throw new ArgumentException("The aggregate configured hook run budget exceeds two minutes.", nameof(descriptors));
        }

        return normalized;
    }

    /// <summary>Computes the stable digest over authority-relevant normalized configuration.</summary>
    public static HookConfigurationDigest ComputeDigest(HookHandlerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var digestInput = new
        {
            descriptor.SchemaVersion,
            Id = descriptor.Identity.Id.Value,
            descriptor.Identity.Version,
            descriptor.Scope,
            descriptor.AdapterKind,
            descriptor.Enabled,
            HookPoints = descriptor.HookPoints.OrderBy(point => point).ToArray(),
            descriptor.Target,
            descriptor.RequestedAuthority,
            descriptor.RequestedFailureMode,
            descriptor.Limits,
            descriptor.RequestedDataScope,
            SecretReferences = descriptor.SecretReferences.Order(StringComparer.Ordinal).ToArray(),
            descriptor.Idempotent,
            descriptor.Priority,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(digestInput);
        return new HookConfigurationDigest(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>Reports whether a point is an eligible managed-blocking pre-action boundary.</summary>
    public static bool AllowsBlocking(HookPoint point)
    {
        return point is HookPoint.BeforeModelRequest
        or HookPoint.BeforeToolInvocation
        or HookPoint.PlanProposed
        or HookPoint.MutationStaged
        or HookPoint.BeforeValidation;
    }

    private static HookHandlerDescriptor NormalizeOne(HookHandlerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Identity.Id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Identity.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Target);
        if (descriptor.SchemaVersion != 1 || descriptor.HookPoints.Count is < 1 or > 16)
        {
            throw new ArgumentException("A hook declaration must use schema 1 and contain 1-16 hook points.", nameof(descriptor));
        }

        if (descriptor.Target.Length > 2048
            || descriptor.Identity.Id.Value.Length > 128
            || descriptor.Identity.Version.Length > 64
            || descriptor.SecretReferences.Count > 16
            || descriptor.SecretReferences.Any(secret => secret.Length > 128 || !secret.StartsWith("secrets:", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Hook identity, target, or secret-reference bounds are invalid.", nameof(descriptor));
        }

        HookHandlerLimits limits = descriptor.Limits;
        if (limits.Timeout < TimeSpan.FromMilliseconds(100)
            || limits.Timeout > TimeSpan.FromMinutes(2)
            || limits.MaximumInputBytes is < 1024 or > 1024 * 1024
            || limits.MaximumOutputBytes is < 1024 or > 1024 * 1024
            || limits.MaximumConcurrency is < 1 or > 8
            || limits.MaximumRetries is < 0 or > 2)
        {
            throw new ArgumentException("Hook resource limits are outside compiled safety bounds.", nameof(descriptor));
        }

        HookPoint[] points = [.. descriptor.HookPoints.Distinct().OrderBy(point => point)];
        string[] secrets = [.. descriptor.SecretReferences.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        HookHandlerDescriptor normalized = descriptor with
        {
            HookPoints = points,
            SecretReferences = secrets,
            RequestedAuthority = descriptor.Scope == HookHandlerScope.Repository
                ? HookAuthority.Advisory
                : descriptor.RequestedAuthority,
            RequestedFailureMode = descriptor.Scope == HookHandlerScope.Repository
                ? HookFailureMode.FailOpen
                : descriptor.RequestedFailureMode,
        };
        return normalized with
        {
            Identity = normalized.Identity with { ConfigurationDigest = ComputeDigest(normalized) },
        };
    }
}

/// <summary>Evaluates repository approval and externally trusted managed-policy grants.</summary>
public sealed class HookPolicyEvaluator
{
    private readonly IReadOnlyList<HookManagedPolicyGrant> _grants;
    private readonly IHookStore _store;

    /// <summary>Initializes a new instance of the <see cref="HookPolicyEvaluator"/> class.</summary>
    public HookPolicyEvaluator(IHookStore store, IEnumerable<HookManagedPolicyGrant>? grants = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _grants = [.. (grants ?? []).OrderBy(grant => grant.AuthoritySource, StringComparer.Ordinal)];
    }

    /// <summary>Evaluates one declaration against exact repository approval and managed grants.</summary>
    internal async Task<HookEligibilityDecision> EvaluateAsync(
        HookHandlerDescriptor descriptor,
        HookPoint point,
        string? repositoryIdentity,
        CancellationToken cancellationToken)
    {
        if (!descriptor.Enabled || !descriptor.HookPoints.Contains(point))
        {
            return HookEligibilityDecision.Ineligible("disabled-or-point-not-declared");
        }

        if (descriptor.Scope == HookHandlerScope.Repository)
        {
            if (string.IsNullOrWhiteSpace(repositoryIdentity))
            {
                return HookEligibilityDecision.Ineligible("repository-identity-required");
            }

            HookRepositoryApproval? approval = await _store.GetApprovalAsync(
                repositoryIdentity,
                descriptor.Identity,
                cancellationToken);
            if (approval is null
                || !string.Equals(approval.Target, descriptor.Target, StringComparison.Ordinal)
                || !approval.HookPoints.SequenceEqual(descriptor.HookPoints)
                || !approval.SecretReferences.SequenceEqual(descriptor.SecretReferences, StringComparer.Ordinal))
            {
                return HookEligibilityDecision.Ineligible("exact-repository-approval-required");
            }

            return HookEligibilityDecision.Advisory(
                descriptor.RequestedDataScope & ~HookDataScope.SensitiveContent,
                descriptor.SecretReferences);
        }

        HookManagedPolicyGrant? grant = _grants.FirstOrDefault(candidate =>
            candidate.HandlerIdentity == descriptor.Identity
            && candidate.HookPoints.Contains(point));
        if (grant is null || !HookDescriptorValidator.AllowsBlocking(point))
        {
            return HookEligibilityDecision.Advisory(
                descriptor.RequestedDataScope & ~HookDataScope.SensitiveContent,
                descriptor.SecretReferences);
        }

        return new HookEligibilityDecision(
            true,
            HookAuthority.ManagedBlocking,
            grant.FailureMode,
            descriptor.RequestedDataScope & grant.DataScope,
            [.. descriptor.SecretReferences.Intersect(grant.SecretReferences, StringComparer.Ordinal)],
            grant.AuthoritySource,
            grant.AllowedDenialCodes,
            null);
    }
}

/// <summary>Internal immutable effective eligibility and authority decision.</summary>
internal sealed record HookEligibilityDecision(
    bool Eligible,
    HookAuthority Authority,
    HookFailureMode FailureMode,
    HookDataScope DataScope,
    IReadOnlyList<string> SecretReferences,
    string? AuthoritySource,
    IReadOnlyList<string> AllowedDenialCodes,
    string? Reason)
{
    /// <summary>Creates an ineligible decision.</summary>
    public static HookEligibilityDecision Ineligible(string reason)
    {
        return new(false, HookAuthority.Advisory, HookFailureMode.FailOpen, HookDataScope.Metadata, [], null, [], reason);
    }

    /// <summary>Creates an advisory eligible decision.</summary>
    public static HookEligibilityDecision Advisory(
        HookDataScope dataScope,
        IReadOnlyList<string>? secretReferences = null)
    {
        return new(
            true,
            HookAuthority.Advisory,
            HookFailureMode.FailOpen,
            dataScope,
            secretReferences ?? [],
            null,
            [],
            null);
    }
}
