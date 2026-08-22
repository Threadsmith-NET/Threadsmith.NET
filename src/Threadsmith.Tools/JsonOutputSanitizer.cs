namespace Threadsmith.Tools;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Sanitizes structured JSON values without changing their enclosing syntax.</summary>
internal static class JsonOutputSanitizer
{
    /// <summary>Sanitizes one complete JSON value and returns a valid compact representation.</summary>
    public static string Sanitize(string json, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(sanitizer);
        var node = JsonNode.Parse(json);
        return SanitizeNode(node, sanitizer)?.ToJsonString() ?? "null";
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
            path["text"] = rawPath;
        }

        return sanitized?.ToJsonString() ?? "null";
    }

    private static JsonNode? SanitizeNode(JsonNode? node, IOutputSanitizer sanitizer)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            return value.TryGetValue<string>(out var text)
                ? JsonValue.Create(sanitizer.Sanitize(text))
                : value.DeepClone();
        }

        if (node is JsonArray array)
        {
            var sanitized = new JsonArray();
            foreach (var item in array)
            {
                sanitized.Add(SanitizeNode(item, sanitizer));
            }

            return sanitized;
        }

        var result = new JsonObject();
        foreach ((var key, var child) in node.AsObject())
        {
            result[key] = SanitizeNode(child, sanitizer);
        }

        return result;
    }
}
