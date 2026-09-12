namespace Threadsmith.Models.Anthropic;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Versioned secret-free metadata snapshot for bounded offline fallback.</summary>
public sealed record AnthropicModelCatalogCacheEntry
{
    /// <summary>Current detached cache schema.</summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>UTC completion time of the last complete authenticated discovery.</summary>
    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>Provider instance identity.</summary>
    public required string ProviderId { get; init; }

    /// <summary>One-way logical-reference identity, never a credential or key hash.</summary>
    public required string SecretReferenceIdentity { get; init; }

    /// <summary>Complete detached model metadata.</summary>
    public required IReadOnlyList<AnthropicDiscoveredModel> Models { get; init; }
}

/// <summary>Bounded atomic cache with confined hashed filenames and reparse checks.</summary>
public sealed class AnthropicModelCatalogCache
{
    /// <summary>Maximum serialized cache bytes, checked during reading.</summary>
    public const int MaximumBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly AnthropicResourceLimits _limits;
    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="AnthropicModelCatalogCache"/> class with an absolute user-owned path.</summary>
    public AnthropicModelCatalogCache(string path, AnthropicResourceLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Anthropic metadata cache paths must be absolute.", nameof(path));
        }

        _limits = limits ?? new();
        _limits.Validate();
        _path = Path.GetFullPath(path);
        ValidatePath();
    }

    /// <summary>Creates a confined filename independent of provider-ID path syntax.</summary>
    public static AnthropicModelCatalogCache ForProvider(string directory, string providerId, AnthropicResourceLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("Anthropic cache directory must be absolute.", nameof(directory));
        }

        var identity = SHA256.HashData(Encoding.UTF8.GetBytes(providerId.Trim().ToLowerInvariant()));
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, "anthropic-models-" + Convert.ToHexString(identity) + ".json"));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Anthropic cache path escapes its user-owned directory.");
        }

        return new AnthropicModelCatalogCache(path, limits);
    }

    /// <summary>Loads one validated provider/reference snapshot, returning null for corrupt or unavailable data.</summary>
    public async Task<AnthropicModelCatalogCacheEntry?> LoadAsync(
        string providerId, string secretReference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidatePath();
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            using var memory = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, _limits.MaximumMetadataBytes + 1L - memory.Length)), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (memory.Length > _limits.MaximumMetadataBytes)
                {
                    return null;
                }
            }

            using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (HasDuplicateProperties(document.RootElement))
            {
                return null;
            }

            var entry = document.RootElement.Deserialize<AnthropicModelCatalogCacheEntry>(SerializerOptions);
            return IsValid(entry, providerId, CreateSecretReferenceIdentity(secretReference)) ? entry : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or inaccessible cache is a defined miss; no raw path or content is logged.
            return null;
        }
    }

    /// <summary>Atomically replaces only this cache after a complete detached snapshot validates.</summary>
    public async Task StoreAsync(AnthropicModelCatalogCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsValid(entry, entry.ProviderId, entry.SecretReferenceIdentity))
        {
            throw new ArgumentException("The Anthropic metadata cache entry is invalid.", nameof(entry));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, SerializerOptions);
        if (bytes.Length > _limits.MaximumMetadataBytes)
        {
            throw new ModelProviderException("Anthropic cache metadata exceeds its byte limit.");
        }

        ValidatePath();
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Cache directory is missing.");
        Directory.CreateDirectory(directory);
        ValidatePath();
        var temporary = Path.Combine(directory, Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidatePath();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Invalidates account eligibility after explicit credential rejection without removing secrets.</summary>
    public Task InvalidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath();
        File.Delete(_path);
        return Task.CompletedTask;
    }

    /// <summary>Hashes only the logical secret-reference name, preserving key privacy.</summary>
    public static string CreateSecretReferenceIdentity(string secretReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secretReference.Trim().ToLowerInvariant())));
    }

    private bool IsValid(AnthropicModelCatalogCacheEntry? entry, string providerId, string identity)
    {
        return entry is { SchemaVersion: 2 } && entry.Models.Count <= _limits.MaximumDiscoveredModels
            && entry.FetchedAt >= DateTimeOffset.UnixEpoch && entry.FetchedAt <= DateTimeOffset.UtcNow
            && string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && AnthropicCatalogHydrator.IsSafeIdentity(entry.ProviderId)
            && entry.SecretReferenceIdentity is { Length: 64 }
            && entry.SecretReferenceIdentity.All(char.IsAsciiHexDigit)
            && string.Equals(entry.SecretReferenceIdentity, identity, StringComparison.Ordinal)
            && entry.Models.All(AnthropicCatalogHydrator.IsSafeMetadata)
            && entry.Models.Select(model => model.ModelId).Distinct(StringComparer.Ordinal).Count() == entry.Models.Count;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return element.EnumerateObject().Any(property => !names.Add(property.Name) || HasDuplicateProperties(property.Value));
        }

        return element.ValueKind == JsonValueKind.Array && element.EnumerateArray().Any(HasDuplicateProperties);
    }

    private void ValidatePath()
    {
        for (var path = _path; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Anthropic metadata cache cannot traverse a filesystem link.");
            }
        }
    }
}
