namespace Threadsmith.Core;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Sanitizes structured JSON values without changing their enclosing syntax.</summary>
public static class JsonOutputSanitizer
{
    /// <summary>Sanitizes one complete JSON value and returns a valid compact representation.</summary>
    public static string Sanitize(string json, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(sanitizer);
        var node = JsonNode.Parse(json);
        return SanitizeNode(node, sanitizer)?.ToJsonString() ?? "null";
    }

    /// <summary>Sanitizes JSON values without corrupting their syntax, or ordinary text when the input is not JSON.</summary>
    public static string SanitizeJsonOrText(string content, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(sanitizer);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
            MaterializeContainers(node);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return sanitizer.Sanitize(content);
        }

        var original = node?.ToJsonString() ?? "null";
        var sanitized = SanitizeNode(node, sanitizer)?.ToJsonString() ?? "null";
        return string.Equals(original, sanitized, StringComparison.Ordinal) ? content : sanitized;
    }

    /// <summary>
    /// Sanitizes complete ripgrep JSON records while preserving raw path metadata for host authorization,
    /// and drops only an incomplete host-truncated final record.
    /// </summary>
    public static string SanitizeRipgrepLines(
        string jsonLines,
        bool finalRecordMayBeTruncated,
        IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        ArgumentNullException.ThrowIfNull(sanitizer);
        var records = jsonLines.Split('\n');
        var result = new StringBuilder(jsonLines.Length);
        for (var index = 0; index < records.Length; index++)
        {
            var terminated = index < records.Length - 1;
            var record = records[index].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(record))
            {
                if (terminated)
                {
                    result.Append('\n');
                }

                continue;
            }

            string sanitized;
            try
            {
                sanitized = SanitizeRipgrepRecord(record, sanitizer);
            }
            catch (JsonException) when (!terminated && finalRecordMayBeTruncated)
            {
                break;
            }

            result.Append(sanitized);
            if (terminated)
            {
                result.Append('\n');
            }
        }

        return result.ToString();
    }

    private static void MaterializeContainers(JsonNode? node)
    {
        // JsonNode delays duplicate-property errors until object enumeration, before any sanitizer runs.
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                MaterializeContainers(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                MaterializeContainers(item);
            }
        }
    }

    private static string SanitizeRipgrepRecord(string record, IOutputSanitizer sanitizer)
    {
        var node = JsonNode.Parse(record);
        var rawPath = node?["data"]?["path"]?["text"]?.GetValue<string>();
        var sanitized = SanitizeNode(node, sanitizer);
        if (rawPath is not null
            && sanitized is JsonObject root
            && root["data"] is JsonObject data
            && data["path"] is JsonObject path)
        {
            var sanitizedPath = path["text"]?.GetValue<string>() ?? rawPath;
            path["sanitizedText"] = sanitizedPath;
            path["text"] = rawPath;
        }

        return sanitized?.ToJsonString() ?? "null";
    }

    private static JsonNode? SanitizeNode(JsonNode? node, IOutputSanitizer sanitizer, string? propertyName = null)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return JsonValue.Create(SanitizeStringValue(text, propertyName, sanitizer));
        }

        // Apply the existing field-name policy without serializing entire containers to inspect their names.
        if (propertyName is not null
            && !string.Equals(SanitizeStringValue("0", propertyName, sanitizer), sanitizer.Sanitize("0"), StringComparison.Ordinal))
        {
            return JsonValue.Create("[REDACTED]");
        }

        if (node is JsonValue)
        {
            return node.DeepClone();
        }

        if (node is JsonArray array)
        {
            var sanitized = new JsonArray();
            foreach (var item in array)
            {
                sanitized.Add(SanitizeNode(item, sanitizer, propertyName));
            }

            return sanitized;
        }

        var result = new JsonObject();
        foreach ((var key, var child) in node.AsObject())
        {
            var sanitizedKey = sanitizer.Sanitize(key);
            result[sanitizedKey] = SanitizeNode(child, sanitizer, key);
        }

        return result;
    }

    private static string SanitizeStringValue(
        string value,
        string? propertyName,
        IOutputSanitizer sanitizer)
    {
        var sanitizedValue = sanitizer.Sanitize(value);
        if (propertyName is null)
        {
            return sanitizedValue;
        }

        var sanitizedKey = sanitizer.Sanitize(propertyName);
        var contextual = JsonSerializer.Serialize(propertyName) + ": " + value;
        var independentlySanitized = JsonSerializer.Serialize(sanitizedKey) + ": " + sanitizedValue;
        return string.Equals(sanitizer.Sanitize(contextual), independentlySanitized, StringComparison.Ordinal)
            ? sanitizedValue
            : "[REDACTED]";
    }
}
