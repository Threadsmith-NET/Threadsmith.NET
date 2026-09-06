namespace Threadsmith.Skills;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Telemetry;

/// <summary>Verifies package asset hashes, undeclared files, signatures, exact allowlists, and revocation.</summary>
public sealed class SkillPackageVerifier : ISkillPackageVerifier
{
    private static readonly HashSet<string> MetadataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "skill.json",
    };

    private readonly ISkillTrustPolicyProvider _policyProvider;

    /// <summary>Initializes a new instance of the <see cref="SkillPackageVerifier"/> class.</summary>
    public SkillPackageVerifier(SkillTrustPolicySnapshot policy)
        : this(new FixedSkillTrustPolicyProvider(policy))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SkillPackageVerifier"/> class with refreshable policy.</summary>
    public SkillPackageVerifier(ISkillTrustPolicyProvider policyProvider)
    {
        ArgumentNullException.ThrowIfNull(policyProvider);
        _policyProvider = policyProvider;
    }

    /// <inheritdoc />
    public async Task<SkillCatalogCandidate> VerifyAsync(
        SkillCatalogCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var policy = _policyProvider.Snapshot;
        var digest = candidate.Identity.Digest.Value;
        if (policy.DeniedSkillIds.Contains(candidate.Metadata.SkillId.Value)
            || policy.DeniedPublishers.Contains(candidate.Metadata.Publisher)
            || policy.RevokedDigests.Contains(digest)
            || (candidate.Metadata.Signature is { } signature
                && policy.RevokedSigners.Contains(signature.SignerId)))
        {
            return candidate with
            {
                Verification = SkillVerificationState.Revoked,
                VerificationReason = "package id, publisher, digest, or signer is denied/revoked",
                Enabled = false,
            };
        }

        try
        {
            await VerifyManifestUnchangedAsync(candidate, cancellationToken);
            await VerifyAssetsAsync(candidate, cancellationToken);
            (var state, var reason) = VerifyTrust(candidate, policy);
            var selector = FormatSelector(candidate);
            var enabled = state switch
            {
                SkillVerificationState.Maintained => true,
                SkillVerificationState.DigestAllowlisted => true,
                SkillVerificationState.SignedTrusted => policy.EnabledSelectors.Contains(selector),
                _ => false,
            };
            enabled &= !policy.DisabledSelectors.Contains(selector);
            return candidate with
            {
                Verification = state,
                VerificationReason = reason,
                Enabled = enabled,
            };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or CryptographicException
            or FormatException)
        {
            return candidate with
            {
                Verification = SkillVerificationState.Invalid,
                VerificationReason = $"{exception.GetType().Name}: package verification failed",
                Enabled = false,
            };
        }
    }

    private static async Task VerifyManifestUnchangedAsync(
        SkillCatalogCandidate candidate,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(candidate.Provenance.PackageRoot)
            ?? throw new InvalidDataException("Skill package root has no catalog parent.");
        var catalog = new SkillCatalog(
            [
                new SkillCatalogSource(
                    candidate.Provenance.Scope,
                    parent,
                    candidate.Provenance.Source,
                    IsRepositoryControlled: candidate.Provenance.Scope == SkillScope.Repository,
                    IsMaintained: candidate.Provenance.Scope == SkillScope.Maintained),
            ]);
        var snapshot = await catalog.RefreshAsync(cancellationToken);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fresh = snapshot.Candidates.SingleOrDefault(item =>
            string.Equals(
                item.Provenance.PackageRoot,
                candidate.Provenance.PackageRoot,
                comparison))
            ?? throw new InvalidDataException("Skill package disappeared during verification.");
        if (!string.Equals(
                fresh.Identity.Digest.Value,
                candidate.Identity.Digest.Value,
                StringComparison.Ordinal)
            || fresh.Metadata.Signature != candidate.Metadata.Signature)
        {
            throw new InvalidDataException("Skill manifest changed after discovery.");
        }
    }

    private static async Task VerifyAssetsAsync(
        SkillCatalogCandidate candidate,
        CancellationToken cancellationToken)
    {
        var declared = candidate.Metadata.Assets
            .Select(item => item.Path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in candidate.Metadata.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SkillPathPolicy.ResolveConfined(candidate.Provenance.PackageRoot, asset.Path);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Declared skill asset '{asset.Path}' is missing.");
            }

            var info = new FileInfo(path);
            if (info.Length != asset.Bytes)
            {
                throw new InvalidDataException($"Skill asset '{asset.Path}' length does not match its manifest.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            if (!string.Equals(Convert.ToHexStringLower(hash), asset.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Skill asset '{asset.Path}' hash does not match its manifest.");
            }
        }

        foreach (var file in Directory.EnumerateFiles(
            candidate.Provenance.PackageRoot,
            "*",
            SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Skill package contains a link or reparse point.");
            }

            var relative = Path.GetRelativePath(candidate.Provenance.PackageRoot, file).Replace('\\', '/');
            if (!declared.Contains(relative) && !MetadataFiles.Contains(relative))
            {
                throw new InvalidDataException($"Skill package contains undeclared file '{relative}'.");
            }
        }
    }

    private static (SkillVerificationState State, string Reason) VerifyTrust(
        SkillCatalogCandidate candidate,
        SkillTrustPolicySnapshot policy)
    {
        var digest = candidate.Identity.Digest.Value;
        if (candidate.Provenance.Scope == SkillScope.Maintained)
        {
            return (SkillVerificationState.Maintained, "immutable host-shipped package integrity verified");
        }

        var allowlistEntry = string.Join(
            '|',
            digest,
            candidate.Metadata.Publisher,
            candidate.Provenance.Source);
        if (policy.AllowlistedDigests.Contains(allowlistEntry))
        {
            return (SkillVerificationState.DigestAllowlisted, "exact digest/publisher/source tuple allowlisted");
        }

        var signature = candidate.Metadata.Signature;
        if (signature is null)
        {
            return (SkillVerificationState.Unverified, "no trusted signature or exact external allowlist");
        }

        if (!policy.TrustedSignerPublicKeys.TryGetValue(signature.SignerId, out var publicKeyPem))
        {
            return (SkillVerificationState.Unverified, "signature signer is not trusted by external policy");
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var digestBytes = Convert.FromHexString(digest);
        var signatureBytes = Convert.FromBase64String(signature.Signature);
        var valid = key.VerifyHash(
            digestBytes,
            signatureBytes,
            DSASignatureFormat.Rfc3279DerSequence);
        return valid
            ? (SkillVerificationState.SignedTrusted, "detached signature matched trusted signer")
            : (SkillVerificationState.Invalid, "detached signature is invalid");
    }

    private static string FormatSelector(SkillCatalogCandidate candidate)
    {
        return $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}"
            + $"@{candidate.Metadata.Version}+{candidate.Identity.Digest.Value}";
    }
}

/// <summary>Loads strict UTF-8, hash-verified, sanitized, token-bounded step content.</summary>
public sealed class SkillContentLoader : ISkillContentLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly SecretOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="SkillContentLoader"/> class.</summary>
    public SkillContentLoader(SecretOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
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
        if (!candidate.Enabled
            || candidate.Verification is not (SkillVerificationState.SignedTrusted
                or SkillVerificationState.DigestAllowlisted
                or SkillVerificationState.Maintained))
        {
            throw new UnauthorizedAccessException("Only enabled verified skill content may be loaded.");
        }

        if (maximumTokens is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        }

        string[] selectedPaths =
        [
            .. new[] { step.InstructionAsset, step.InputSchemaAsset, step.OutputSchemaAsset }
                .Where(item => item is not null)
                .Select(item => item ?? string.Empty),
            .. candidate.Metadata.Assets
                .Where(item => !item.Required && item.Kind.Equals("reference", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Path),
        ];
        var segments = new List<SkillContextSegment>();
        var usedTokens = 0;
        foreach (var path in selectedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var asset = candidate.Metadata.Assets.Single(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            var fullPath = SkillPathPolicy.ResolveConfined(candidate.Provenance.PackageRoot, asset.Path);
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            if (bytes.LongLength != asset.Bytes
                || !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    asset.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Skill content changed after verification.");
            }

            var content = _sanitizer.Sanitize(StrictUtf8.GetString(bytes));
            var tokens = Threadsmith.Context.TokenEstimator.Estimate(content);
            if (usedTokens + tokens > maximumTokens)
            {
                if (asset.Required)
                {
                    throw new InvalidOperationException(
                        $"Required skill content '{asset.Path}' does not fit the authorized context budget.");
                }

                continue;
            }

            segments.Add(new SkillContextSegment
            {
                Package = candidate.Identity,
                StepId = step.StepId,
                AssetPath = asset.Path,
                Sha256 = asset.Sha256,
                Content = content,
                EstimatedTokens = tokens,
                Required = asset.Required,
            });
            usedTokens += tokens;
        }

        return segments;
    }
}

/// <summary>Hard bounds for package import and quarantine extraction.</summary>
public sealed record SkillInstallerOptions
{
    /// <summary>Maximum archive bytes.</summary>
    public long MaximumArchiveBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Maximum extracted bytes.</summary>
    public long MaximumExtractedBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Maximum archive entries.</summary>
    public int MaximumFiles { get; init; } = 256;
}

/// <summary>Imports packages through quarantine without executing package content.</summary>
public sealed class SkillPackageInstaller
{
    private readonly string _contentStoreRoot;
    private readonly string _quarantineRoot;
    private readonly SkillInstallerOptions _options;

    /// <summary>Initializes a new instance of the <see cref="SkillPackageInstaller"/> class.</summary>
    public SkillPackageInstaller(
        string contentStoreRoot,
        string quarantineRoot,
        SkillInstallerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentStoreRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        _contentStoreRoot = Path.GetFullPath(contentStoreRoot);
        _quarantineRoot = Path.GetFullPath(quarantineRoot);
        _options = options ?? new SkillInstallerOptions();
        if (!string.Equals(
            Path.GetPathRoot(_contentStoreRoot),
            Path.GetPathRoot(_quarantineRoot),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Skill quarantine and content store must share one volume.");
        }

        if (_options.MaximumArchiveBytes is < 1 or > 1024L * 1024 * 1024
            || _options.MaximumExtractedBytes < _options.MaximumArchiveBytes
            || _options.MaximumFiles is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Skill installer limits are invalid.");
        }
    }

    /// <summary>Extracts a bounded archive to quarantine and atomically installs by verified digest.</summary>
    public async Task<string> InstallArchiveAsync(
        string archivePath,
        ISkillPackageVerifier verifier,
        SkillScope scope,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var archiveInfo = new FileInfo(archivePath);
        if (!archiveInfo.Exists || archiveInfo.Length > _options.MaximumArchiveBytes)
        {
            throw new InvalidDataException("Skill archive is missing or exceeds its compressed-size limit.");
        }

        Directory.CreateDirectory(_quarantineRoot);
        Directory.CreateDirectory(_contentStoreRoot);
        var quarantine = Path.Combine(_quarantineRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(quarantine);
        try
        {
            await ExtractAsync(archivePath, quarantine, cancellationToken);
            var catalog = new SkillCatalog(
                [new SkillCatalogSource(scope, quarantine, source)]);
            var snapshot = await catalog.RefreshAsync(cancellationToken);
            var candidate = AssertSingle(snapshot.Candidates);
            var verified = await verifier.VerifyAsync(candidate, cancellationToken);
            if (verified.Verification is not (SkillVerificationState.SignedTrusted
                or SkillVerificationState.DigestAllowlisted))
            {
                throw new UnauthorizedAccessException("Imported skill package is not trusted for installation.");
            }

            var destination = Path.Combine(_contentStoreRoot, verified.Identity.Digest.Value);
            if (Directory.Exists(destination))
            {
                return destination;
            }

            var packageDirectory = verified.Provenance.PackageRoot;
            Directory.Move(packageDirectory, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(quarantine))
            {
                Directory.Delete(quarantine, recursive: true);
            }
        }
    }

    /// <summary>Removes one exact user package from the content-addressed catalog.</summary>
    public Task<bool> UninstallAsync(
        SkillCatalogCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.Provenance.Scope != SkillScope.User)
        {
            throw new UnauthorizedAccessException("Only user-scope packages may be uninstalled.");
        }

        var packageRoot = Path.GetFullPath(candidate.Provenance.PackageRoot);
        var expectedRoot = Path.Combine(_contentStoreRoot, candidate.Identity.Digest.Value);
        if (!string.Equals(
            packageRoot,
            expectedRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("User package is outside the content-addressed store.");
        }

        if (!Directory.Exists(packageRoot))
        {
            return Task.FromResult(false);
        }

        if ((File.GetAttributes(packageRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Skill package root cannot be a reparse point.");
        }

        Directory.Delete(packageRoot, recursive: true);
        return Task.FromResult(true);
    }

    private async Task ExtractAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is < 1 || archive.Entries.Count > _options.MaximumFiles)
        {
            throw new InvalidDataException("Skill archive entry count is outside its bound.");
        }

        long extracted = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            SkillPathPolicy.ValidateRelativePath(entry.FullName);
            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
            {
                throw new InvalidDataException("Skill archives cannot contain symbolic links.");
            }

            extracted = checked(extracted + entry.Length);
            if (extracted > _options.MaximumExtractedBytes)
            {
                throw new InvalidDataException("Skill archive exceeds its extracted-size limit.");
            }

            var output = SkillPathPolicy.ResolveConfined(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(output)
                ?? throw new InvalidDataException("Skill archive output has no parent directory."));
            await using var input = await entry.OpenAsync(cancellationToken);
            await using var outputStream = new FileStream(
                output,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous);
            await input.CopyToAsync(outputStream, cancellationToken);
        }
    }

    private static SkillCatalogCandidate AssertSingle(IReadOnlyList<SkillCatalogCandidate> candidates)
    {
        if (candidates.Count != 1)
        {
            throw new InvalidDataException("A skill archive must contain exactly one package directory.");
        }

        return candidates[0];
    }
}
