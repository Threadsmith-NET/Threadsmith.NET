namespace Threadsmith.Core;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Repository preference for workload resolution during semantic design-time loads.</summary>
public static class RepositoryWorkloadResolverSetting
{
    private const string Key = "solution:workloadResolverEnabled";

    /// <summary>Reads the saved choice, or null to inherit the SDK default.</summary>
    public static bool? Read(string repositoryPath, int maximumBytes)
    {
        var path = GetPath(repositoryPath);
        RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(path);
        return Read(ConfigurationDocument.Read(path, maximumBytes));
    }

    /// <summary>Reads the preference from an already loaded repository configuration.</summary>
    public static bool? Read(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.TryGetValue(Key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Repository setting '{Key}' must be a boolean."),
        };
    }

    /// <summary>Saves a repository choice through the coordinated settings writer.</summary>
    public static async Task SaveAsync(string repositoryPath, bool enabled, int maximumBytes, CancellationToken cancellationToken)
    {
        var path = GetPath(repositoryPath);
        var published = await RepositorySettingsCoordinator.UpdateAsync(
            path,
            token =>
            {
                token.ThrowIfCancellationRequested();
                var previous = ConfigurationDocument.ReadContent(path, maximumBytes);
                var root = previous is null
                    ? new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true })
                    : ConfigurationDocument.ParseObject(previous, path);
                if (root.ContainsKey(Key))
                {
                    root[Key] = enabled;
                }
                else
                {
                    ConfigurationDocument.Set(root, Key, JsonValue.Create(enabled));
                }

                var content = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
                if (content.Length > maximumBytes)
                {
                    throw new InvalidDataException("The updated repository configuration exceeds its configured byte limit.");
                }

                return Task.FromResult<RepositorySettingsUpdate?>(new(content)
                {
                    Overwrite = previous is not null,
                    ExpectedContent = previous is null ? (ReadOnlyMemory<byte>?)null : previous.AsMemory(),
                });
            },
            cancellationToken: cancellationToken);
        if (!published)
        {
            throw new IOException("The repository configuration changed while saving the workload resolver setting.");
        }
    }

    private static string GetPath(string repositoryPath) => Path.Combine(repositoryPath, ".threadsmith", "config.json");
}
