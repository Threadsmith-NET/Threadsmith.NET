namespace Threadsmith.Skills;

using System.Text.Json;
using Threadsmith.Core;

/// <summary>Provides repository-excluding skill signer, allowlist, revocation, and enablement policy.</summary>
public interface ISkillTrustPolicyProvider
{
    /// <summary>Gets the current immutable policy snapshot.</summary>
    SkillTrustPolicySnapshot Snapshot { get; }

    /// <summary>Explicitly allowlists/enables or disables one exact package outside repository control.</summary>
    Task SetEnabledAsync(
        SkillCatalogCandidate candidate,
        bool enabled,
        CancellationToken cancellationToken = default);
}

/// <summary>Immutable policy provider useful for organization policy and tests.</summary>
public sealed class FixedSkillTrustPolicyProvider : ISkillTrustPolicyProvider
{
    /// <summary>Initializes a new instance of the <see cref="FixedSkillTrustPolicyProvider"/> class.</summary>
    public FixedSkillTrustPolicyProvider(SkillTrustPolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }

    /// <inheritdoc />
    public SkillTrustPolicySnapshot Snapshot { get; }

    /// <inheritdoc />
    public Task SetEnabledAsync(
        SkillCatalogCandidate candidate,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("This skill trust policy provider is immutable.");
    }
}

/// <summary>Atomically persists user-authorized exact digests and disabled selectors outside repositories.</summary>
public sealed class FileSkillTrustPolicyProvider : ISkillTrustPolicyProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SkillTrustPolicySnapshot _basePolicy;
    private readonly Lock _gate = new();
    private readonly string _path;
    private UserSkillPolicy _userPolicy;

    /// <summary>Initializes a new instance of the <see cref="FileSkillTrustPolicyProvider"/> class.</summary>
    public FileSkillTrustPolicyProvider(
        string path,
        SkillTrustPolicySnapshot basePolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(basePolicy);
        _path = Path.GetFullPath(path);
        _basePolicy = basePolicy;
        _userPolicy = Load(_path);
    }

    /// <inheritdoc />
    public SkillTrustPolicySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return Merge(_basePolicy, _userPolicy);
            }
        }
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(
        SkillCatalogCandidate candidate,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        var selector = FormatSelector(candidate);
        var allowlistEntry = FormatAllowlistEntry(candidate);
        UserSkillPolicy next;
        lock (_gate)
        {
            var allowlisted = _userPolicy.AllowlistedPackages.ToHashSet(StringComparer.Ordinal);
            var enabledSelectors = _userPolicy.EnabledSelectors.ToHashSet(StringComparer.Ordinal);
            var disabled = _userPolicy.DisabledSelectors.ToHashSet(StringComparer.Ordinal);
            if (enabled)
            {
                allowlisted.Add(allowlistEntry);
                enabledSelectors.Add(selector);
                disabled.Remove(selector);
            }
            else
            {
                enabledSelectors.Remove(selector);
                disabled.Add(selector);
            }

            next = new UserSkillPolicy
            {
                SchemaVersion = 1,
                AllowlistedPackages = [.. allowlisted.OrderBy(item => item, StringComparer.Ordinal)],
                EnabledSelectors = [.. enabledSelectors.OrderBy(item => item, StringComparer.Ordinal)],
                DisabledSelectors = [.. disabled.OrderBy(item => item, StringComparer.Ordinal)],
            };
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(next, JsonOptions) + Environment.NewLine,
                cancellationToken);
            File.Move(temporary, _path, overwrite: true);
            lock (_gate)
            {
                _userPolicy = next;
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static UserSkillPolicy Load(string path)
    {
        if (!File.Exists(path))
        {
            return new UserSkillPolicy();
        }

        var info = new FileInfo(path);
        if (info.Length > 1024 * 1024)
        {
            throw new InvalidDataException("User skill policy exceeds its byte limit.");
        }

        UserSkillPolicy policy = JsonSerializer.Deserialize<UserSkillPolicy>(File.ReadAllText(path))
            ?? throw new InvalidDataException("User skill policy is empty.");
        if (policy.SchemaVersion != 1
            || policy.AllowlistedPackages.Count > 2048
            || policy.EnabledSelectors.Count > 2048
            || policy.DisabledSelectors.Count > 2048
            || policy.AllowlistedPackages
                .Concat(policy.EnabledSelectors)
                .Concat(policy.DisabledSelectors)
                .Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 1024))
        {
            throw new InvalidDataException("User skill policy is invalid or exceeds its bounds.");
        }

        return policy;
    }

    private static SkillTrustPolicySnapshot Merge(
        SkillTrustPolicySnapshot basePolicy,
        UserSkillPolicy userPolicy)
    {
        return basePolicy with
        {
            AllowlistedDigests = basePolicy.AllowlistedDigests
                .Concat(userPolicy.AllowlistedPackages)
                .ToHashSet(StringComparer.Ordinal),
            EnabledSelectors = basePolicy.EnabledSelectors
                .Concat(userPolicy.EnabledSelectors)
                .ToHashSet(StringComparer.Ordinal),
            DisabledSelectors = basePolicy.DisabledSelectors
                .Concat(userPolicy.DisabledSelectors)
                .ToHashSet(StringComparer.Ordinal),
        };
    }

    private static string FormatSelector(SkillCatalogCandidate candidate)
    {
        return $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}"
            + $"@{candidate.Metadata.Version}+{candidate.Identity.Digest.Value}";
    }

    private static string FormatAllowlistEntry(SkillCatalogCandidate candidate)
    {
        return string.Join(
            '|',
            candidate.Identity.Digest.Value,
            candidate.Metadata.Publisher,
            candidate.Provenance.Source);
    }

    private sealed record UserSkillPolicy
    {
        public int SchemaVersion { get; init; } = 1;

        public IReadOnlyList<string> AllowlistedPackages { get; init; } = [];

        public IReadOnlyList<string> EnabledSelectors { get; init; } = [];

        public IReadOnlyList<string> DisabledSelectors { get; init; } = [];
    }
}
