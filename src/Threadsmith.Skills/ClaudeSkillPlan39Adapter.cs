namespace Threadsmith.Skills;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Threadsmith.Core;
using Threadsmith.Telemetry;

/// <summary>Projects native and Claude-style candidates through one Plan-39 catalog boundary.</summary>
public sealed class CompatibleSkillCatalog : ISkillCatalog, IAsyncSkillCatalog, IUpdatableSkillCatalog
{
    private const string PendingDigest = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Version = "0.0.0-claude-v1";
    private readonly ConcurrentDictionary<string, SkillCatalogCandidate> _exactCandidates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ClaudeSkillSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly IClaudeSkillCompatibilityCatalog _claude;
    private readonly ISkillCatalog _native;
    private readonly Lock _gate = new();
    private SkillCatalogSnapshot _snapshot = new() { CreatedAt = DateTimeOffset.MinValue };

    /// <summary>Initializes a new instance of the <see cref="CompatibleSkillCatalog"/> class.</summary>
    public CompatibleSkillCatalog(ISkillCatalog native, IClaudeSkillCompatibilityCatalog claude)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(claude);
        _native = native;
        _claude = claude;
    }

    /// <inheritdoc />
    public SkillCatalogSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <inheritdoc />
    public async Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var native = await _native.RefreshAsync(cancellationToken);
        var claude = await _claude.RefreshAsync(cancellationToken);
        SkillCatalogSnapshot next;
        lock (_gate)
        {
            next = new SkillCatalogSnapshot
            {
                Generation = checked(_snapshot.Generation + 1),
                Candidates = native.Candidates
                    .Concat(claude.Select(ProjectMetadata))
                    .OrderBy(item => item.Metadata.SkillId.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.Provenance.Scope)
                    .ThenBy(item => item.Provenance.Source, StringComparer.Ordinal)
                    .ToArray(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _snapshot = next;
        }

        return next;
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillCatalogCandidate> Search(SkillCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MaximumResults is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Skill search result limit must be 1-500.");
        }

        string? text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();
        return Snapshot.Candidates
            .Where(item => query.SkillId is null
                || string.Equals(item.Metadata.SkillId.Value, query.SkillId.Value, StringComparison.Ordinal))
            .Where(item => query.Scope is null || item.Provenance.Scope == query.Scope)
            .Where(item => text is null
                || item.Metadata.SkillId.Value.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.Metadata.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.Metadata.Description.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(query.MaximumResults)
            .ToArray();
    }

    /// <inheritdoc />
    public SkillCatalogCandidate Resolve(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        string normalized = NormalizeClaudeSelector(selector);
        SkillSelector parsed = SkillSelector.Parse(normalized);
        SkillCatalogCandidate[] matches =
        [
            .. Snapshot.Candidates
                .Where(item => string.Equals(
                    item.Metadata.SkillId.Value,
                    parsed.SkillId,
                    StringComparison.Ordinal))
                .Where(item => parsed.Version is null
                    || string.Equals(item.Metadata.Version, parsed.Version, StringComparison.Ordinal))
                .Where(item => parsed.Scope is null || item.Provenance.Scope == parsed.Scope)
                .Where(item => parsed.Digest is null
                    || string.Equals(item.Identity.Digest.Value, parsed.Digest, StringComparison.OrdinalIgnoreCase)),
        ];
        if (matches.Length == 0)
        {
            return _native.Resolve(selector);
        }

        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Skill selector '{selector}' is ambiguous; select format, scope, version, and digest explicitly.");
        }

        return matches[0];
    }

    /// <inheritdoc />
    public async Task<SkillCatalogCandidate> ResolveAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeClaudeSelector(selector);
        SkillSelector parsed = SkillSelector.Parse(normalized);
        if (!parsed.SkillId.StartsWith("claude.", StringComparison.Ordinal))
        {
            return _native is IAsyncSkillCatalog asynchronous
                ? await asynchronous.ResolveAsync(selector, cancellationToken)
                : _native.Resolve(selector);
        }

        var source = ResolveClaudeSource(parsed);
        var activated = await _claude.ActivateAsync(source, cancellationToken);
        string digest = activated.Candidate.Identity.Digest
            ?? throw new InvalidDataException("Claude skill activation did not produce an exact digest.");
        if (parsed.Digest is not null
            && !string.Equals(parsed.Digest, digest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Claude skill source changed after exact selection.");
        }

        var candidate = ProjectSnapshot(activated);
        _snapshots[digest] = activated;
        _exactCandidates[digest] = candidate;
        return candidate;
    }

    /// <inheritdoc />
    public SkillCatalogCandidate UpdateCandidate(SkillCatalogCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!IsClaude(candidate))
        {
            return _native is IUpdatableSkillCatalog updatable
                ? updatable.UpdateCandidate(candidate)
                : candidate;
        }

        string key = candidate.Identity.Digest.Value;
        if (!_snapshots.ContainsKey(key))
        {
            throw new KeyNotFoundException("The activated Claude skill snapshot is no longer available.");
        }

        _exactCandidates[key] = candidate;
        lock (_gate)
        {
            SkillCatalogCandidate[] updated =
            [
                .. _snapshot.Candidates.Select(item =>
                    item.Provenance.Scope == candidate.Provenance.Scope
                    && string.Equals(
                        item.Metadata.SkillId.Value,
                        candidate.Metadata.SkillId.Value,
                        StringComparison.Ordinal)
                    && string.Equals(
                        item.Provenance.Source,
                        candidate.Provenance.Source,
                        StringComparison.Ordinal)
                        ? candidate
                        : item),
            ];
            _snapshot = _snapshot with { Candidates = updated };
        }

        return candidate;
    }

    /// <summary>Reactivates one projected Claude candidate and rejects source or adapter changes.</summary>
    public async Task<(SkillCatalogCandidate Candidate, ClaudeSkillSnapshot Snapshot)> RevalidateAsync(
        SkillCatalogCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string selector = string.Equals(
            candidate.Identity.Digest.Value,
            PendingDigest,
            StringComparison.Ordinal)
            ? $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}"
            : FormatSelector(candidate);
        var exact = await ResolveAsync(selector, cancellationToken);
        string digest = exact.Identity.Digest.Value;
        var snapshot = _snapshots[digest];
        return (exact, snapshot);
    }

    /// <summary>Gets a cached immutable snapshot after exact verification.</summary>
    public ClaudeSkillSnapshot GetSnapshot(SkillCatalogCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return _snapshots.TryGetValue(candidate.Identity.Digest.Value, out var snapshot)
            ? snapshot
            : throw new InvalidOperationException("Claude skill content was not activated at this boundary.");
    }

    /// <summary>Returns whether a projected package is a Claude compatibility candidate.</summary>
    public static bool IsClaude(SkillCatalogCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Metadata.SkillId.Value.StartsWith("claude.", StringComparison.Ordinal)
            && candidate.Provenance.Source.StartsWith("claude:", StringComparison.Ordinal);
    }

    private ClaudeSkillCandidate ResolveClaudeSource(SkillSelector selector)
    {
        string name = selector.SkillId["claude.".Length..];
        ClaudeSkillCandidate[] matches =
        [
            .. _claude.Candidates
                .Where(item => string.Equals(item.Identity.Name, name, StringComparison.Ordinal))
                .Where(item => selector.Scope is null || item.Identity.Scope == selector.Scope),
        ];
        return matches.Length switch
        {
            0 => throw new KeyNotFoundException("Claude skill selector was not found."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                "Claude skill selector is ambiguous; select the source scope explicitly."),
        };
    }

    private static SkillCatalogCandidate ProjectMetadata(ClaudeSkillCandidate source)
    {
        return Project(source, PendingDigest, packageRoot: string.Empty, files: []);
    }

    private static SkillCatalogCandidate ProjectSnapshot(ClaudeSkillSnapshot snapshot)
    {
        string digest = snapshot.Candidate.Identity.Digest
            ?? throw new InvalidDataException("Claude skill snapshot has no digest.");
        return Project(snapshot.Candidate, digest, snapshot.PackageRoot, snapshot.Files);
    }

    private static SkillCatalogCandidate Project(
        ClaudeSkillCandidate source,
        string digest,
        string packageRoot,
        IReadOnlyList<ClaudeSkillSnapshotFile> files)
    {
        string skillId = $"claude.{source.Identity.Name}";
        SkillAssetMetadata[] assets =
        [
            .. files.Select(file => new SkillAssetMetadata
            {
                Path = file.Path,
                Bytes = file.Bytes,
                Sha256 = file.Sha256,
                Required = string.Equals(file.Path, "SKILL.md", StringComparison.Ordinal),
                Kind = string.Equals(file.Path, "SKILL.md", StringComparison.Ordinal)
                    ? "instructions"
                    : "reference",
            }),
        ];
        var step = new SkillWorkflowStep
        {
            StepId = "invoke",
            Kind = SkillWorkflowStepKind.InvokeProcedure,
            InstructionAsset = "SKILL.md",
        };
        return new SkillCatalogCandidate
        {
            Metadata = new SkillManifestMetadata
            {
                SkillId = new SkillId(skillId),
                PackageId = skillId,
                Version = Version,
                DisplayName = source.Identity.Name,
                Description = source.Description,
                Publisher = "claude-compatibility",
                License = "unspecified",
                Assets = assets,
                Requirements = new SkillRequirementSet
                {
                    OptionalTools = source.MappedTools,
                    MinimumTrust = source.Identity.Scope == SkillScope.Repository
                        ? RepositoryTrustLevel.TrustedRead
                        : RepositoryTrustLevel.UntrustedInspection,
                    MinimumHostVersion = "1.0.0",
                    MaximumHostVersion = "1.0.0",
                    Model = new SkillModelRequirements
                    {
                        RequiresToolCalls = source.MappedTools.Count > 0,
                        RequiresStructuredOutput = true,
                    },
                },
                Workflow = new SkillWorkflowDefinition
                {
                    WorkflowId = "claude-instruction",
                    Steps = [step],
                },
            },
            Identity = new SkillPackageIdentity(
                new SkillId(skillId),
                skillId,
                Version,
                new SkillDigest("sha256", digest),
                "claude-compatibility"),
            Provenance = new SkillPackageProvenance
            {
                Scope = source.Identity.Scope,
                Source = $"claude:{source.Identity.Source}",
                PackageRoot = packageRoot,
                DiscoveredAt = DateTimeOffset.UtcNow,
            },
            Verification = SkillVerificationState.Unverified,
            VerificationReason = source.Status == ClaudeSkillCompatibilityStatus.Unsupported
                ? "Claude compatibility requires unsupported runtime behavior"
                : "Claude compatibility metadata discovered; exact external enablement required",
            Enabled = false,
        };
    }

    private static string NormalizeClaudeSelector(string selector)
    {
        string value = selector.Trim();
        if (!value.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        string[] parts = value.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !Enum.TryParse(parts[1], ignoreCase: true, out SkillScope scope))
        {
            throw new ArgumentException("Claude skill selector must be claude:<scope>:<name>.", nameof(selector));
        }

        return $"{scope}:claude.{parts[2]}";
    }

    private static string FormatSelector(SkillCatalogCandidate candidate)
    {
        return $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}"
            + $"@{candidate.Metadata.Version}+{candidate.Identity.Digest.Value}";
    }
}

/// <summary>Verifies native packages or exact Claude source snapshots through one external policy.</summary>
public sealed class CompatibleSkillPackageVerifier : ISkillPackageVerifier
{
    private readonly CompatibleSkillCatalog _catalog;
    private readonly ISkillPackageVerifier _native;
    private readonly ISkillTrustPolicyProvider _policy;

    /// <summary>Initializes a new instance of the <see cref="CompatibleSkillPackageVerifier"/> class.</summary>
    public CompatibleSkillPackageVerifier(
        ISkillPackageVerifier native,
        CompatibleSkillCatalog catalog,
        ISkillTrustPolicyProvider policy)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(policy);
        _native = native;
        _catalog = catalog;
        _policy = policy;
    }

    /// <inheritdoc />
    public async Task<SkillCatalogCandidate> VerifyAsync(
        SkillCatalogCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!CompatibleSkillCatalog.IsClaude(candidate))
        {
            return await _native.VerifyAsync(candidate, cancellationToken);
        }

        try
        {
            (var exact, _) = await _catalog.RevalidateAsync(candidate, cancellationToken);
            var policy = _policy.Snapshot;
            string digest = exact.Identity.Digest.Value;
            if (exact.VerificationReason.Contains("unsupported runtime behavior", StringComparison.Ordinal))
            {
                return exact with
                {
                    Verification = SkillVerificationState.Invalid,
                    VerificationReason = "Claude skill requires unsupported runtime behavior",
                    Enabled = false,
                };
            }

            string allowlist = string.Join(
                '|',
                digest,
                exact.Metadata.Publisher,
                exact.Provenance.Source);
            string selector = $"{exact.Provenance.Scope}:{exact.Metadata.SkillId.Value}"
                + $"@{exact.Metadata.Version}+{digest}";
            if (policy.DeniedSkillIds.Contains(exact.Metadata.SkillId.Value)
                || policy.DeniedPublishers.Contains(exact.Metadata.Publisher)
                || policy.RevokedDigests.Contains(digest))
            {
                return exact with
                {
                    Verification = SkillVerificationState.Revoked,
                    VerificationReason = "Claude source identity or exact digest is denied/revoked",
                    Enabled = false,
                };
            }

            bool allowlisted = policy.AllowlistedDigests.Contains(allowlist);
            bool enabled = allowlisted && !policy.DisabledSelectors.Contains(selector);
            return exact with
            {
                Verification = allowlisted
                    ? SkillVerificationState.DigestAllowlisted
                    : SkillVerificationState.Unverified,
                VerificationReason = allowlisted
                    ? "exact Claude source digest externally allowlisted"
                    : "exact Claude source digest requires external enablement",
                Enabled = enabled,
            };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or CryptographicException)
        {
            return candidate with
            {
                Verification = SkillVerificationState.Invalid,
                VerificationReason = $"{exception.GetType().Name}: Claude source verification failed",
                Enabled = false,
            };
        }
    }
}

/// <summary>Loads exact Claude instructions/resources or delegates native package content loading.</summary>
public sealed class CompatibleSkillContentLoader : ISkillContentLoader
{
    private readonly CompatibleSkillCatalog _catalog;
    private readonly ISkillContentLoader _native;
    private readonly SecretOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="CompatibleSkillContentLoader"/> class.</summary>
    public CompatibleSkillContentLoader(
        ISkillContentLoader native,
        CompatibleSkillCatalog catalog,
        SecretOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _native = native;
        _catalog = catalog;
        _sanitizer = sanitizer;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillContextSegment>> LoadAsync(
        SkillCatalogCandidate candidate,
        SkillWorkflowStep step,
        int maximumTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(step);
        if (!CompatibleSkillCatalog.IsClaude(candidate))
        {
            return await _native.LoadAsync(candidate, step, maximumTokens, cancellationToken);
        }

        (var exact, var snapshot) = await _catalog.RevalidateAsync(
            candidate,
            cancellationToken);
        if (!string.Equals(
                exact.Identity.Digest.Value,
                candidate.Identity.Digest.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Claude skill source changed before content loading.");
        }

        var segments = new List<SkillContextSegment>();
        int remaining = maximumTokens;
        AddSegment("SKILL.md", snapshot.Instructions, required: true);
        foreach (var resource in snapshot.Resources.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            AddSegment(resource.Key, resource.Value, required: false);
        }

        return segments;

        void AddSegment(string path, string content, bool required)
        {
            string sanitized = _sanitizer.Sanitize(content);
            int tokens = Math.Max(1, (sanitized.Length + 3) / 4);
            if (tokens > remaining)
            {
                if (required)
                {
                    throw new InvalidOperationException("Required Claude skill instructions exceed the content budget.");
                }

                return;
            }

            var file = snapshot.Files.Single(item =>
                string.Equals(item.Path, path, StringComparison.Ordinal));
            segments.Add(new SkillContextSegment
            {
                Package = candidate.Identity,
                StepId = step.StepId,
                AssetPath = path,
                Sha256 = file.Sha256,
                Content = sanitized,
                EstimatedTokens = tokens,
                Required = required,
            });
            remaining -= tokens;
        }
    }
}
