namespace Threadsmith.Core;

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Reads configuration with bounded bytes, unambiguous names, and preserved null/empty presence.</summary>
public static class ConfigurationDocument
{
    /// <summary>Reads one optional JSON object. Missing files are empty; malformed or oversized files are errors.</summary>
    public static ImmutableDictionary<string, JsonElement> Read(string path, long maximumBytes = 1024 * 1024)
    {
        var content = ReadContent(path, maximumBytes);
        return content is null ? ImmutableDictionary.Create<string, JsonElement>(StringComparer.OrdinalIgnoreCase) : Parse(content);
    }

    /// <summary>Reads editable JSON through the same bounded parser and alias validation.</summary>
    public static JsonObject ReadObject(string path, long maximumBytes = 1024 * 1024)
    {
        var content = ReadContent(path, maximumBytes);
        if (content is null)
        {
            return new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        }

        _ = Parse(content, validatePermissionParents: false);
        return JsonNode.Parse(
            content.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? content.AsSpan(3) : content.AsSpan(),
            new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            RepositorySettingsCoordinator.DocumentOptions) as JsonObject
            ?? throw new InvalidDataException("Configuration must contain a JSON object.");
    }

    /// <summary>Reads exact bounded bytes for coordinated compare-and-replace; null means missing.</summary>
    public static byte[]? ReadContent(string path, long maximumBytes = 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes || stream.Length > int.MaxValue)
            {
                throw new InvalidDataException($"Configuration '{path}' exceeds its byte limit.");
            }

            var content = new byte[(int)stream.Length];
            stream.ReadExactly(content);
            if (stream.ReadByte() != -1)
            {
                throw new IOException("Configuration changed during reading; retry.");
            }

            return content;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Parses once, rejects case-alias duplicates, and retains each object and its descendants.</summary>
    public static ImmutableDictionary<string, JsonElement> Parse(ReadOnlyMemory<byte> content, bool validatePermissionParents = true)
    {
        if (content.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            content = content[3..];
        }

        using var document = JsonDocument.Parse(content, RepositorySettingsCoordinator.DocumentOptions);
        ValidateNames(document.RootElement, string.Empty);
        var result = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        Visit(document.RootElement, string.Empty);
        return result.ToImmutable();

        static void ValidateNames(JsonElement node, string prefix)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in node.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;
                    if ((property.Name.Contains(':') && (ConfigurationPermissionCatalog.IsPermission(path)
                        || ConfigurationPermissionCatalog.Paths.Any(permission => permission.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase))))
                        || !names.Add(property.Name))
                    {
                        throw new InvalidDataException("Permission property names cannot use flattened ':' aliases, and configuration cannot contain case-alias duplicates.");
                    }

                    ValidateNames(property.Value, path);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    ValidateNames(item, prefix);
                }
            }
        }

        void Visit(JsonElement node, string prefix)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Configuration must contain a JSON object.");
            }

            foreach (var property in node.EnumerateObject())
            {
                var path = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;
                if (!result.TryAdd(path, property.Value.Clone()))
                {
                    throw new InvalidDataException($"Configuration contains ambiguous duplicate key '{path}'.");
                }

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    Visit(property.Value, path);
                }
                else if (validatePermissionParents && property.Value.ValueKind != JsonValueKind.Null
                    && ConfigurationPermissionCatalog.Paths.Any(key => key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Configuration '{path}' must be an object containing permission settings.");
                }
            }
        }
    }

    /// <summary>Flattens a validated source, excluding repository-owned permission values before merging.</summary>
    public static Dictionary<string, string?> Flatten(IReadOnlyDictionary<string, JsonElement> values, bool projectPreferencesOnly = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (value.ValueKind != JsonValueKind.Object && (!projectPreferencesOnly || (!ConfigurationPermissionCatalog.IsPermission(key)
                && !ConfigurationPermissionCatalog.Paths.Any(path => path.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))))
            {
                Add(value, key);
            }
        }

        return result;

        void Add(JsonElement node, string key)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                result[key] = null;
                var index = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Add(item, key + ":" + (index++).ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in node.EnumerateObject())
                {
                    Add(property.Value, key + ":" + property.Name);
                }
            }
            else
            {
                result[key] = node.ValueKind == JsonValueKind.Null ? null : node.ToString();
            }
        }
    }

    /// <summary>Applies a single property edit while preserving unrelated JSON and case-insensitive names.</summary>
    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = path.Split(':');
        var current = root;
        foreach (var segment in segments[..^1])
        {
            if (current[segment] is null)
            {
                current[segment] = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            }

            current = current[segment] as JsonObject
                ?? throw new InvalidDataException($"Configuration '{path}' has a non-object parent.");
        }

        current[segments[^1]] = value;
    }

    /// <summary>Removes permission examples from first-run scaffolding using the same property ownership metadata.</summary>
    public static void RemovePermissionProperties(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Remove(root, string.Empty);

        static void Remove(JsonObject node, string prefix)
        {
            foreach (var property in node.ToArray())
            {
                var path = prefix.Length == 0 ? property.Key : prefix + ":" + property.Key;
                if (ConfigurationPermissionCatalog.IsPermission(path))
                {
                    node.Remove(property.Key);
                }
                else if (property.Value is JsonObject child)
                {
                    Remove(child, path);
                }
            }
        }
    }
}
