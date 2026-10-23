namespace Threadsmith.Core;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

/// <summary>One immutable application authorization ceiling. This is not an OS sandbox.</summary>
public sealed record EffectiveSessionPolicy
{
    /// <summary>Gets the host binding identity.</summary>
    public required Guid BindingId { get; init; }

    /// <summary>Gets the monotonically increasing binding generation.</summary>
    public required long Generation { get; init; }

    /// <summary>Gets the normalized repository identity.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>Gets the nonsecret normalized authority fingerprint.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Gets the parent snapshot fingerprint for an assignment-derived ceiling.</summary>
    public string? ParentFingerprint { get; init; }

    /// <summary>Gets the host-established repository trust ceiling.</summary>
    public RepositoryTrustLevel MaximumTrust { get; init; } = RepositoryTrustLevel.FullyTrustedAutomation;

    /// <summary>Gets an opaque identity for specialized adapters whose configuration is fixed until host restart.</summary>
    public string StartupAuthorityFingerprint { get; init; } = string.Empty;

    /// <summary>Gets executable basenames admitted by the trusted ceiling and all filters.</summary>
    public ImmutableArray<string> AllowedExecutables { get; init; } = [];

    /// <summary>Gets static host preauthorizations; an empty list does not revoke scoped fetch consent.</summary>
    public ImmutableArray<string> AllowedNetworkHosts { get; init; } = [];

    /// <summary>Gets mandatory outbound filters; a default array means unrestricted.</summary>
    public ImmutableArray<string> NetworkHostRestriction { get; init; }

    /// <summary>Gets allowed tools; a default array explicitly represents unrestricted tools.</summary>
    public ImmutableArray<string> AllowedTools { get; init; }

    /// <summary>Gets mandatory tool denials.</summary>
    public ImmutableArray<string> DeniedTools { get; init; } = [];

    /// <summary>Checks the shared immutable tool-name ceiling for presentation and execution.</summary>
    public bool AllowsTool(string toolId) => !DeniedTools.Contains(toolId, StringComparer.OrdinalIgnoreCase)
        && (AllowedTools.IsDefault || AllowedTools.Contains(toolId, StringComparer.OrdinalIgnoreCase));

    /// <summary>Gets mandatory tool approval requirements.</summary>
    public ImmutableArray<string> ReviewTools { get; init; } = [];

    /// <summary>Gets normalized direct artifact write roots.</summary>
    public ImmutableArray<string> WriteFolders { get; init; } = [];

    /// <summary>Gets normalized mutation roots, independent of ordinary read roots.</summary>
    public ImmutableArray<string> EditableRoots { get; init; } = [];

    /// <summary>Gets inherited repository path prohibitions.</summary>
    public ImmutableArray<string> ProhibitedPaths { get; init; } = [];

    /// <summary>Gets exact authority-control paths protected from generic mutations.</summary>
    public ImmutableArray<string> ControlPaths { get; init; } = [];

    /// <summary>Gets whether shell execution requires the existing user approval.</summary>
    public bool RequireShellApproval { get; init; } = true;

    /// <summary>Gets whether independent tool calls may execute concurrently.</summary>
    public bool ParallelToolsEnabled { get; init; } = true;

    /// <summary>Gets the trusted mutation automatic-application ceiling.</summary>
    public MutationApprovalPolicy MutationCeiling { get; init; } = MutationApprovalPolicy.ReviewAll;

    /// <summary>Gets the effective large-diff risk threshold.</summary>
    public int LargeDiffThreshold { get; init; } = 500;

    /// <summary>Gets resolved resource ceilings indexed by canonical configuration key.</summary>
    public ImmutableDictionary<string, int> ResourceLimits { get; init; } = [];

    /// <summary>Gets exact assembly names admitted for the existing script worker.</summary>
    public ImmutableArray<string> ScriptAssemblies { get; init; } = [];

    /// <summary>Gets provenance descriptors without configuration values.</summary>
    public ImmutableArray<PolicyInputSource> Sources { get; init; } = [];

    /// <summary>Gets deduplicated source diagnostics for publication at the binding boundary.</summary>
    public ImmutableArray<string> Diagnostics { get; init; } = [];
}

/// <summary>Value-free provenance of an explicitly present policy input.</summary>
public sealed record PolicyInputSource(string Key, string Kind, string Location);

/// <summary>Owns atomic publication and stale-admission rejection outside immutable policy values.</summary>
public sealed class SessionPolicyBinding
{
    private EffectiveSessionPolicy? _current;
    private int _maximumTrust = (int)RepositoryTrustLevel.FullyTrustedAutomation;

    /// <summary>Gets the currently executable policy, or rejects unavailable authority.</summary>
    public EffectiveSessionPolicy Current => Volatile.Read(ref _current)
        ?? throw new UnauthorizedAccessException("PolicyGenerationStale: policy is unavailable; reopen the repository after correcting configuration.");

    /// <summary>Publishes an already validated candidate at the serialized repository boundary.</summary>
    public void Publish(EffectiveSessionPolicy candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var ceiling = (RepositoryTrustLevel)Volatile.Read(ref _maximumTrust);
        var trust = candidate.MaximumTrust < ceiling ? candidate.MaximumTrust : ceiling;
        Volatile.Write(ref _current, candidate with
        {
            MaximumTrust = trust,
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Fingerprint + ":trust:" + trust))),
        });
    }

    /// <summary>Sets host-established trust while execution is unavailable, before candidate publication.</summary>
    public void PrepareRepositoryTrust(RepositoryTrustLevel maximumTrust)
    {
        Invalidate();
        Volatile.Write(ref _maximumTrust, (int)maximumTrust);
    }

    /// <summary>Blocks new operations after a failed authority transition.</summary>
    public void Invalidate() => Volatile.Write(ref _current, null);

    /// <summary>Rejects retained or restored baselines without current host authority.</summary>
    public void ValidateBaseline(WorkspaceBaseline baseline, RepositoryTrustLevel minimumTrust)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var captured = baseline.SecurityPolicy
            ?? throw new UnauthorizedAccessException("PolicyGenerationStale: recapture the workspace baseline under current policy.");
        Validate(captured);
        if (captured.MaximumTrust < minimumTrust || baseline.TrustLevel < minimumTrust)
        {
            throw new UnauthorizedAccessException("Repository trust is insufficient for validation.");
        }
    }

    /// <summary>Rejects old queued operations and approvals before a new effect starts.</summary>
    public void Validate(EffectiveSessionPolicy captured)
    {
        var current = Current;
        if (captured.BindingId != current.BindingId || captured.Generation != current.Generation || captured.MaximumTrust > current.MaximumTrust)
        {
            throw new UnauthorizedAccessException("PolicyGenerationStale: policy changed; retry under the current repository policy.");
        }
    }
}
