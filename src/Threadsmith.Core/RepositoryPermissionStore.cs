namespace Threadsmith.Core;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>A user-owned repository permission document, detached from repository-controlled files.</summary>
public sealed record RepositoryPermissionSnapshot(
    ImmutableDictionary<string, JsonElement> Settings,
    bool Exists,
    long Revision);

/// <summary>Routes repository permission reads and edits through the shared configuration document and writer.</summary>
public sealed class RepositoryPermissionStore
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _userDirectory;

    /// <summary>Initializes a new instance of the <see cref="RepositoryPermissionStore"/> class beneath the host-selected user configuration directory.</summary>
    public RepositoryPermissionStore(string userConfigurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userConfigurationPath);
        _userDirectory = Path.GetDirectoryName(Path.GetFullPath(userConfigurationPath))
            ?? throw new ArgumentException("User configuration requires a directory.", nameof(userConfigurationPath));
    }

    /// <summary>Gets the entire protected permission tree, including future files.</summary>
    public string DirectoryPath => Path.Combine(_userDirectory, "repositories");

    /// <summary>Returns the stable local identity, using the repository filesystem's case semantics.</summary>
    public static string GetRepositoryIdentity(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (OperatingSystem.IsWindows())
        {
            if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = @"\\" + normalized[8..];
            }
            else if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[4..];
            }
        }

        return RepositoryPathPolicy.GetPathComparer(normalized).Equals("a", "A") ? normalized.ToUpperInvariant() : normalized;
    }

    /// <summary>Resolves a host-owned filename; repository data never supplies a destination path.</summary>
    public string GetPath(string repositoryPath)
    {
        var identity = GetRepositoryIdentity(repositoryPath);
        var name = new string([.. Path.GetFileName(identity).Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(40)]);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(DirectoryPath, $"{(name.Length == 0 ? "repository" : name)}-{hash}", "permissions.json");
    }

    /// <summary>Reads an identity-checked document without creating or migrating anything.</summary>
    public RepositoryPermissionSnapshot Read(string repositoryPath)
    {
        var path = GetPath(repositoryPath);
        EnsureOwnedPath(path);
        return ParseSnapshot(repositoryPath, ConfigurationDocument.ReadContent(path, MaximumBytes));
    }

    /// <summary>Publishes one explicit local permission choice, preserving unrelated fields and detecting concurrent replacement.</summary>
    public Task SetAsync(string repositoryPath, string key, JsonNode? value, CancellationToken cancellationToken = default)
        => SetManyAsync(repositoryPath, new Dictionary<string, JsonNode?> { [key] = value }, cancellationToken);

    /// <summary>Publishes related permission fields atomically through the same validated writer.</summary>
    public Task SetManyAsync(string repositoryPath, IReadOnlyDictionary<string, JsonNode?> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Keys.Any(key => !ConfigurationPermissionCatalog.IsPermission(key) || !ConfigurationPermissionCatalog.Allows(key, PermissionConfigurationSources.UserRepository)))
        {
            throw new ArgumentException("The property is not a declared permission setting.", nameof(values));
        }

        return UpdateAsync(
            repositoryPath,
            root =>
            {
                var settings = root["settings"] as JsonObject ?? throw new InvalidDataException("Permission settings must be an object.");
                foreach (var (key, value) in values)
                {
                    ConfigurationDocument.Set(settings, key, value?.DeepClone());
                }
            },
            cancellationToken);
    }

    private static RepositoryPermissionSnapshot ParseSnapshot(string repositoryPath, byte[]? content)
    {
        if (content is null)
        {
            return new(ImmutableDictionary.Create<string, JsonElement>(StringComparer.OrdinalIgnoreCase), false, 0);
        }

        var document = ConfigurationDocument.Parse(content);
        if (!document.TryGetValue("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1
            || !document.TryGetValue("repositoryIdentity", out var storedIdentity) || storedIdentity.ValueKind != JsonValueKind.String
            || !string.Equals(storedIdentity.GetString(), GetRepositoryIdentity(repositoryPath), StringComparison.Ordinal)
            || !document.TryGetValue("revision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.Number || !revisionValue.TryGetInt64(out var revision) || revision < 1
            || !document.TryGetValue("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Repository permission document has an unsupported schema or mismatched repository identity.");
        }

        var values = ConfigurationDocument.Parse(Encoding.UTF8.GetBytes(settings.GetRawText()));
        foreach (var (key, value) in values)
        {
            if (!ConfigurationPermissionCatalog.Allows(key, PermissionConfigurationSources.UserRepository)
                || (!ConfigurationPermissionCatalog.IsPermission(key)
                    && !(value.ValueKind == JsonValueKind.Object && ConfigurationPermissionCatalog.Paths.Any(permission => permission.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))))
            {
                throw new InvalidDataException($"'{key}' is not a permission setting; keep project preferences in repository configuration.");
            }
        }

        foreach (var property in ConfigurationPermissionCatalog.Properties)
        {
            if (!values.TryGetValue(property.Path, out var value))
            {
                continue;
            }

            var valid = property.Shape switch
            {
                PermissionInputShape.List => value.ValueKind == JsonValueKind.Array
                    && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(item.GetString()) && !(item.GetString() ?? string.Empty).Any(char.IsControl)),
                PermissionInputShape.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                PermissionInputShape.Scalar => value.ValueKind is JsonValueKind.String or JsonValueKind.Number,
                _ => true,
            };
            if (!valid)
            {
                throw new InvalidDataException($"Permission '{property.Path}' has an invalid value shape.");
            }
        }

        return new(values, true, revision);
    }

    private async Task UpdateAsync(string repositoryPath, Action<JsonObject> edit, CancellationToken cancellationToken)
    {
        var path = GetPath(repositoryPath);
        var published = await RepositorySettingsCoordinator.UpdateAsync(
            path,
            token =>
            {
                token.ThrowIfCancellationRequested();
                EnsureOwnedPath(path);
                var previous = ConfigurationDocument.ReadContent(path, MaximumBytes);
                var snapshot = ParseSnapshot(repositoryPath, previous);

                var root = previous is null
                    ? new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true })
                    {
                        ["schemaVersion"] = 1,
                        ["repositoryIdentity"] = GetRepositoryIdentity(repositoryPath),
                        ["settings"] = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true }),
                    }
                    : JsonNode.Parse(previous.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? previous.AsSpan(3) : previous.AsSpan(), new JsonNodeOptions { PropertyNameCaseInsensitive = true }, RepositorySettingsCoordinator.DocumentOptions) as JsonObject
                        ?? throw new InvalidDataException("Repository permissions must be an object.");
                root["revision"] = checked(snapshot.Revision + 1);
                edit(root);
                var content = JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions);
                if (content.Length > MaximumBytes)
                {
                    throw new InvalidDataException("Repository permissions exceed their byte limit.");
                }

                _ = ParseSnapshot(repositoryPath, content);
                EnsureOwnedPath(path);
                return Task.FromResult<RepositorySettingsUpdate?>(new(content)
                {
                    ExpectedContent = previous is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(previous),
                    Overwrite = previous is not null,
                });
            },
            cancellationToken: cancellationToken);
        if (!published)
        {
            throw new IOException("Repository permissions changed concurrently; reload and retry the choice.");
        }
    }

    private void EnsureOwnedPath(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("Repository permission paths cannot contain links or reparse points.");
            }

            if (RepositoryPathPolicy.GetPathComparer(_userDirectory).Equals(current, _userDirectory))
            {
                return;
            }
        }

        throw new UnauthorizedAccessException("Repository permission path escaped the user configuration directory.");
    }
}
