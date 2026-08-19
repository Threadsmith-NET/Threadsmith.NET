namespace Threadsmith.Skills;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Threadsmith.Core;

/// <summary>One trusted host-provided catalog location.</summary>
public sealed record SkillCatalogSource(
    SkillScope Scope,
    string RootPath,
    string Source,
    bool IsRepositoryControlled = false,
    bool IsMaintained = false);

/// <summary>Bounded metadata discovery policy.</summary>
public sealed record SkillCatalogOptions
{
    /// <summary>Maximum manifest bytes.</summary>
    public int MaximumManifestBytes { get; init; } = 256 * 1024;

    /// <summary>Maximum packages across all sources.</summary>
    public int MaximumPackages { get; init; } = 512;

    /// <summary>Maximum assets declared by one package.</summary>
    public int MaximumAssetsPerPackage { get; init; } = 64;

    /// <summary>Maximum metadata text length.</summary>
    public int MaximumTextCharacters { get; init; } = 4_096;
}

/// <summary>Repository-excluding signer, digest, revocation, and enablement policy.</summary>
public sealed record SkillTrustPolicySnapshot
{
    /// <summary>Trusted ECDSA public keys in PEM format keyed by signer id.</summary>
    public IReadOnlyDictionary<string, string> TrustedSignerPublicKeys { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Exact lowercase SHA-256 package digests allowlisted outside repositories.</summary>
    public IReadOnlySet<string> AllowlistedDigests { get; init; } = new HashSet<string>();

    /// <summary>Revoked exact package digests.</summary>
    public IReadOnlySet<string> RevokedDigests { get; init; } = new HashSet<string>();

    /// <summary>Organization-denied normalized skill ids across every lower scope.</summary>
    public IReadOnlySet<string> DeniedSkillIds { get; init; } = new HashSet<string>();

    /// <summary>Organization-denied publishers across every lower scope.</summary>
    public IReadOnlySet<string> DeniedPublishers { get; init; } = new HashSet<string>();

    /// <summary>Revoked signer ids.</summary>
    public IReadOnlySet<string> RevokedSigners { get; init; } = new HashSet<string>();

    /// <summary>Explicit enabled scope-qualified selectors for trusted signed packages.</summary>
    public IReadOnlySet<string> EnabledSelectors { get; init; } = new HashSet<string>();

    /// <summary>Explicit disabled scope-qualified selectors.</summary>
    public IReadOnlySet<string> DisabledSelectors { get; init; } = new HashSet<string>();

    /// <summary>Exact maintained-package digests compiled with the host.</summary>
    public IReadOnlySet<string> MaintainedDigests { get; init; } = new HashSet<string>();
}

/// <summary>Discovers bounded metadata without opening declared package assets.</summary>
public sealed partial class SkillCatalog : ISkillCatalog, IUpdatableSkillCatalog
{
    private const string ManifestFileName = "skill.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SkillCatalogOptions _options;
    private readonly Lock _gate = new();
    private IReadOnlyList<SkillCatalogSource> _sources;
    private SkillCatalogSnapshot _snapshot = new()
    {
        CreatedAt = DateTimeOffset.MinValue,
    };

    /// <summary>Initializes a new instance of the <see cref="SkillCatalog"/> class.</summary>
    public SkillCatalog(
        IEnumerable<SkillCatalogSource> sources,
        SkillCatalogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();
        _options = options ?? new SkillCatalogOptions();
        ValidateOptions(_options);
        if (_sources.Count == 0)
        {
            throw new ArgumentException("At least one skill catalog source is required.", nameof(sources));
        }

        foreach (var source in _sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.RootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Source);
            if (source.IsRepositoryControlled && source.Scope != SkillScope.Repository)
            {
                throw new ArgumentException(
                    "Only repository scope may be marked repository-controlled.",
                    nameof(sources));
            }
        }
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

    /// <summary>Rebinds repository-controlled catalog roots and refreshes metadata before the repository switch completes.</summary>
    public async Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var canonicalRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var previous = _sources;
        SkillCatalogSource[] rebound = [.. previous.Select(source => source.IsRepositoryControlled
            ? source with { RootPath = Path.Combine(canonicalRepository, ".threadsmith", "skills") }
            : source)];
        _sources = rebound;
        try
        {
            _ = await RefreshAsync(cancellationToken);
        }
        catch
        {
            _sources = previous;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new List<SkillCatalogCandidate>();
        foreach (var source in _sources
            .OrderBy(item => item.Scope)
            .ThenBy(item => item.Source, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source.RootPath))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(source.RootPath)
                .OrderBy(item => item, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= _options.MaximumPackages)
                {
                    throw new InvalidDataException("The visible skill catalog exceeds its package limit.");
                }

                var manifestPath = Path.Combine(directory, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var metadata = await ReadBoundedMetadataAsync(manifestPath, cancellationToken);
                ValidateMetadata(metadata, _options);
                var digest = SkillCanonicalJson.ComputeManifestDigest(metadata);
                candidates.Add(new SkillCatalogCandidate
                {
                    Metadata = metadata,
                    Identity = new SkillPackageIdentity(
                        metadata.SkillId,
                        metadata.PackageId,
                        metadata.Version,
                        new SkillDigest("sha256", digest),
                        metadata.Publisher),
                    Provenance = new SkillPackageProvenance
                    {
                        Scope = source.IsMaintained ? SkillScope.Maintained : source.Scope,
                        Source = source.Source,
                        PackageRoot = Path.GetFullPath(directory),
                        DiscoveredAt = DateTimeOffset.UtcNow,
                    },
                    Verification = SkillVerificationState.Unverified,
                    VerificationReason = "metadata discovered; package content not loaded",
                    Enabled = false,
                });
            }
        }

        RejectDuplicateSourceIdentities(candidates);
        SkillCatalogSnapshot next;
        lock (_gate)
        {
            next = new SkillCatalogSnapshot
            {
                Generation = checked(_snapshot.Generation + 1),
                Candidates = candidates
                    .OrderBy(item => item.Metadata.SkillId.Value, StringComparer.Ordinal)
                    .ThenByDescending(item => item.Metadata.Version, SemanticVersionComparer.Instance)
                    .ThenBy(item => item.Provenance.Scope)
                    .ThenBy(item => item.Identity.Digest.Value, StringComparer.Ordinal)
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

        var text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();
        return Snapshot.Candidates
            .Where(candidate =>
                (query.SkillId is null
                    || string.Equals(
                        candidate.Metadata.SkillId.Value,
                        query.SkillId.Value,
                        StringComparison.Ordinal))
                && (query.Scope is null || candidate.Provenance.Scope == query.Scope)
                && (text is null || Matches(candidate, text)))
            .Take(query.MaximumResults)
            .ToArray();
    }

    /// <inheritdoc />
    public SkillCatalogCandidate Resolve(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var parsed = SkillSelector.Parse(selector);
        SkillCatalogCandidate[] matches =
        [
            .. Snapshot.Candidates
                .Where(item =>
                    string.Equals(item.Metadata.SkillId.Value, parsed.SkillId, StringComparison.Ordinal)
                    && (parsed.Version is null
                        || string.Equals(item.Metadata.Version, parsed.Version, StringComparison.Ordinal))
                    && (parsed.Scope is null || item.Provenance.Scope == parsed.Scope)
                    && (parsed.Digest is null
                        || string.Equals(
                            item.Identity.Digest.Value,
                            parsed.Digest,
                            StringComparison.OrdinalIgnoreCase))),
        ];
        if (matches.Length == 0)
        {
            throw new KeyNotFoundException($"Skill selector '{selector}' was not found.");
        }

        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Skill selector '{selector}' is ambiguous; select scope, version, and digest explicitly.");
        }

        return matches[0];
    }

    /// <summary>Replaces one candidate state without rediscovering package metadata.</summary>
    public SkillCatalogCandidate UpdateCandidate(SkillCatalogCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            var index = _snapshot.Candidates
                .Select((item, position) => (item, position))
                .Where(pair => SameCandidate(pair.item, candidate))
                .Select(pair => pair.position)
                .SingleOrDefault(-1);
            if (index < 0)
            {
                throw new KeyNotFoundException("The verified skill candidate is no longer in the catalog snapshot.");
            }

            SkillCatalogCandidate[] updated = [.. _snapshot.Candidates];
            updated[index] = candidate;
            _snapshot = _snapshot with { Candidates = updated };
            return candidate;
        }
    }

    private static async Task<SkillManifestMetadata> DeserializeMetadataAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var metadata = await JsonSerializer.DeserializeAsync<SkillManifestMetadata>(
            stream,
            JsonOptions,
            cancellationToken) ?? throw new InvalidDataException("Skill manifest is empty.");
        return metadata;
    }

    private async Task<SkillManifestMetadata> ReadBoundedMetadataAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(manifestPath);
        if (info.Length is < 2 || info.Length > int.MaxValue || info.Length > _options.MaximumManifestBytes)
        {
            throw new InvalidDataException("Skill manifest exceeds the bounded metadata size.");
        }

        return await DeserializeMetadataAsync(manifestPath, cancellationToken);
    }

    private static void ValidateMetadata(SkillManifestMetadata metadata, SkillCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.SchemaVersion != 1)
        {
            throw new NotSupportedException($"Skill manifest schema {metadata.SchemaVersion} is unsupported.");
        }

        ValidateId(metadata.SkillId.Value, "skill id");
        ValidateId(metadata.PackageId, "package id");
        if (!SemanticVersionRegex().IsMatch(metadata.Version))
        {
            throw new InvalidDataException("Skill version must be bounded semantic version text.");
        }

        ValidateText(metadata.DisplayName, nameof(metadata.DisplayName), options.MaximumTextCharacters);
        ValidateText(metadata.Description, nameof(metadata.Description), options.MaximumTextCharacters);
        ValidateText(metadata.Publisher, nameof(metadata.Publisher), options.MaximumTextCharacters);
        ValidateText(metadata.License, nameof(metadata.License), 256);
        if (metadata.Tags.Count > 32
            || metadata.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64))
        {
            throw new InvalidDataException("Skill tags exceed their bounds.");
        }

        if (metadata.Assets.Count is < 3 || metadata.Assets.Count > 64
            || metadata.Assets.Count > options.MaximumAssetsPerPackage)
        {
            throw new InvalidDataException("Skill asset count is outside the supported bound.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in metadata.Assets)
        {
            SkillPathPolicy.ValidateRelativePath(asset.Path);
            if (!paths.Add(asset.Path)
                || asset.Bytes < 0
                || asset.Bytes > 4 * 1024 * 1024
                || !Sha256Regex().IsMatch(asset.Sha256)
                || string.IsNullOrWhiteSpace(asset.Kind))
            {
                throw new InvalidDataException("Skill asset metadata is invalid or duplicated.");
            }
        }

        SkillManifestValidator.ValidateRequirements(metadata.Requirements);
        SkillManifestValidator.ValidateBudget(metadata.Budget);
        SkillManifestValidator.ValidateWorkflow(metadata.Workflow, metadata.Assets);
        SkillManifestValidator.ValidateWorkflowBudget(metadata.Workflow, metadata.Budget);
        SkillManifestValidator.ValidateAgents(metadata.Agents, metadata.Assets, metadata.Budget);
        if (metadata.Signature is not null)
        {
            ValidateText(metadata.Signature.SignerId, "signer id", 256);
            if (!string.Equals(metadata.Signature.Algorithm, "ecdsa-p256-sha256", StringComparison.Ordinal)
                || metadata.Signature.Signature.Length is < 32 or > 1024)
            {
                throw new InvalidDataException("Skill signature envelope is unsupported or malformed.");
            }
        }
    }

    private static void RejectDuplicateSourceIdentities(IReadOnlyList<SkillCatalogCandidate> candidates)
    {
        foreach (var group in candidates.GroupBy(
            item => (
                item.Provenance.Scope,
                Id: item.Metadata.SkillId.Value,
                item.Metadata.Version),
            SkillSourceIdentityComparer.Instance))
        {
            if (group.Count() > 1)
            {
                throw new InvalidDataException(
                    $"Skill {group.Key.Id}@{group.Key.Version} is duplicated in {group.Key.Scope} scope.");
            }
        }
    }

    private static bool Matches(SkillCatalogCandidate candidate, string text)
    {
        return candidate.Metadata.SkillId.Value.Contains(text, StringComparison.OrdinalIgnoreCase)
            || candidate.Metadata.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || candidate.Metadata.Description.Contains(text, StringComparison.OrdinalIgnoreCase)
            || candidate.Metadata.Tags.Any(tag => tag.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameCandidate(SkillCatalogCandidate left, SkillCatalogCandidate right)
    {
        return left.Provenance.Scope == right.Provenance.Scope
            && string.Equals(left.Provenance.Source, right.Provenance.Source, StringComparison.Ordinal)
            && string.Equals(left.Identity.Digest.Value, right.Identity.Digest.Value, StringComparison.Ordinal);
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || !IdRegex().IsMatch(value))
        {
            throw new InvalidDataException($"Skill {name} is invalid.");
        }
    }

    private static void ValidateText(string value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new InvalidDataException($"Skill {name} is empty or exceeds its bound.");
        }
    }

    private static void ValidateOptions(SkillCatalogOptions options)
    {
        if (options.MaximumManifestBytes is < 1024 or > 4 * 1024 * 1024
            || options.MaximumPackages is < 1 or > 10_000
            || options.MaximumAssetsPerPackage is < 3 or > 256
            || options.MaximumTextCharacters is < 128 or > 32_768)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Skill catalog limits are invalid.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    private sealed class SkillSourceIdentityComparer
        : IEqualityComparer<(SkillScope Scope, string Id, string Version)>
    {
        internal static SkillSourceIdentityComparer Instance { get; } = new();

        public bool Equals(
            (SkillScope Scope, string Id, string Version) left,
            (SkillScope Scope, string Id, string Version) right)
        {
            return left.Scope == right.Scope
                && string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && string.Equals(left.Version, right.Version, StringComparison.Ordinal);
        }

        public int GetHashCode((SkillScope Scope, string Id, string Version) value)
        {
            return HashCode.Combine(
                value.Scope,
                StringComparer.Ordinal.GetHashCode(value.Id),
                StringComparer.Ordinal.GetHashCode(value.Version));
        }
    }
}

/// <summary>Canonical JSON and digest operations used by discovery and verification.</summary>
public static class SkillCanonicalJson
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Computes the canonical manifest digest without the detached signature value.</summary>
    public static string ComputeManifestDigest(SkillManifestMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var unsigned = metadata with { Signature = null };
        var node = JsonSerializer.SerializeToNode(unsigned, SerializeOptions)
            ?? throw new InvalidDataException("Skill manifest cannot be canonicalized.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, node);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    /// <summary>Returns canonical bounded JSON for schema-validated invocation values.</summary>
    public static string CanonicalizeValue(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        }) ?? throw new InvalidDataException("JSON value is empty.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, node);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject objectNode:
                writer.WriteStartObject();
                foreach (var property in objectNode
                    .OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value:
                value.WriteTo(writer);
                break;
            default:
                throw new InvalidDataException("Unsupported canonical JSON node.");
        }
    }
}

/// <summary>Normalized explicit skill selector.</summary>
/// <param name="SkillId">Normalized skill id.</param>
/// <param name="Version">Optional semantic version.</param>
/// <param name="Scope">Optional installation scope.</param>
/// <param name="Digest">Optional exact digest.</param>
internal sealed record SkillSelector(
    string SkillId,
    string? Version,
    SkillScope? Scope,
    string? Digest)
{
    /// <summary>Parses a bounded scope/id/version/digest selector.</summary>
    internal static SkillSelector Parse(string selector)
    {
        var value = selector.Trim();
        SkillScope? scope = null;
        var scopeSeparator = value.IndexOf(':', StringComparison.Ordinal);
        if (scopeSeparator > 0
            && Enum.TryParse(value[..scopeSeparator], ignoreCase: true, out SkillScope parsedScope))
        {
            scope = parsedScope;
            value = value[(scopeSeparator + 1)..];
        }

        string? digest = null;
        var digestSeparator = value.IndexOf('+', StringComparison.Ordinal);
        if (digestSeparator > 0)
        {
            digest = value[(digestSeparator + 1)..];
            value = value[..digestSeparator];
        }

        string? version = null;
        var versionSeparator = value.LastIndexOf('@');
        if (versionSeparator > 0)
        {
            version = value[(versionSeparator + 1)..];
            value = value[..versionSeparator];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Skill selector is invalid.", nameof(selector));
        }

        return new SkillSelector(value.ToLowerInvariant(), version, scope, digest);
    }
}

/// <summary>Semantic version ordering used only after strict version validation.</summary>
internal sealed class SemanticVersionComparer : IComparer<string>
{
    /// <summary>Gets the shared stateless comparer.</summary>
    internal static SemanticVersionComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftParts = ParseCore(left);
        var rightParts = ParseCore(right);
        for (var index = 0; index < 3; index++)
        {
            var result = leftParts[index].CompareTo(rightParts[index]);
            if (result != 0)
            {
                return result;
            }
        }

        var leftPrerelease = left.Contains('-', StringComparison.Ordinal);
        var rightPrerelease = right.Contains('-', StringComparison.Ordinal);
        if (leftPrerelease != rightPrerelease)
        {
            return leftPrerelease ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static int[] ParseCore(string version)
    {
        var core = version.Split('-', 2)[0];
        return [.. core.Split('.').Select(int.Parse)];
    }
}
