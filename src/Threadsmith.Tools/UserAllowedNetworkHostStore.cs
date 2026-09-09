namespace Threadsmith.Tools;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Reads and atomically updates the ordinary user configuration's exact network-host list.</summary>
public sealed class UserAllowedNetworkHostStore
{
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _configurationPath;

    /// <summary>Initializes a new instance of the <see cref="UserAllowedNetworkHostStore"/> class with a host-owned user path.</summary>
    public UserAllowedNetworkHostStore(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        _configurationPath = Path.GetFullPath(configurationPath);
    }

    /// <summary>Reads the current exact hostname grants so manual removals take effect without restart.</summary>
    public IReadOnlySet<string> ReadHosts()
    {
        var root = ReadRoot();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root["tools"] is JsonObject tools && tools["allowedNetworkHosts"] is JsonArray hosts)
        {
            foreach (var node in hosts)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var host)
                    && Uri.CheckHostName(host) == UriHostNameType.Dns)
                {
                    result.Add(new Uri($"https://{host}/").IdnHost.ToLowerInvariant());
                }
            }
        }

        return result;
    }

    /// <summary>Adds an exact public-HTTPS hostname while preserving unrelated configuration values.</summary>
    public async Task AddAsync(string hostname, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        if (Uri.CheckHostName(hostname) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException("The user allowed list requires an exact DNS hostname.");
        }

        var normalized = WebFetchUrlPolicy.Normalize($"https://{hostname}/", 8192).IdnHost.ToLowerInvariant();
        await RepositorySettingsCoordinator.ExecuteWriteAsync(
            _configurationPath,
            async token =>
            {
                var root = ReadRoot();
                if (root["tools"] is not (null or JsonObject))
                {
                    throw new InvalidOperationException("User configuration tools must be an object.");
                }

                var tools = root["tools"] as JsonObject ?? new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
                if (root["tools"] is null)
                {
                    root["tools"] = tools;
                }

                if (tools["allowedNetworkHosts"] is not (null or JsonArray))
                {
                    throw new InvalidOperationException("User allowed network hosts must be an array.");
                }

                var hosts = tools["allowedNetworkHosts"] as JsonArray ?? new JsonArray();
                if (tools["allowedNetworkHosts"] is null)
                {
                    tools["allowedNetworkHosts"] = hosts;
                }

                if (hosts.Any(node => node is JsonValue value && value.TryGetValue<string>(out var host)
                    && string.Equals(host, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                hosts.Add(normalized);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions);
                if (bytes.Length > MaximumConfigurationBytes)
                {
                    throw new InvalidOperationException("The updated user configuration exceeds the supported size.");
                }

                var directory = Path.GetDirectoryName(_configurationPath)
                    ?? throw new InvalidOperationException("The user configuration path has no parent directory.");
                Directory.CreateDirectory(directory);
                var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, bytes, token);
                    token.ThrowIfCancellationRequested();
                    RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(_configurationPath);
                    File.Move(temporaryPath, _configurationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            },
            cancellationToken);
    }

    private JsonObject ReadRoot()
    {
        RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(_configurationPath);
        if (!File.Exists(_configurationPath))
        {
            return new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        }

        using var stream = new FileStream(_configurationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumConfigurationBytes)
        {
            throw new InvalidOperationException("The user configuration exceeds the supported size.");
        }

        return JsonNode.Parse(
            stream,
            new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            RepositorySettingsCoordinator.DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("User configuration must contain a JSON object.");
    }
}
