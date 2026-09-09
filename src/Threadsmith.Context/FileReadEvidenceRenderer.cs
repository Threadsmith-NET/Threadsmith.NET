namespace Threadsmith.Context;

using System.Text.Json;

/// <summary>Renders host-attributed file reads as source lines while retaining range and truncation metadata.</summary>
internal static class FileReadEvidenceRenderer
{
    /// <summary>Decodes only the known file-read shape; all other evidence remains verbatim.</summary>
    public static string Render(Evidence evidence)
    {
        if (evidence.Kind != EvidenceKind.ToolResult
            || evidence.Provenance.Source != "tool:read_file"
            || evidence.Provenance.ToolInvocationId is null)
        {
            return evidence.Content;
        }

        try
        {
            using var document = JsonDocument.Parse(evidence.Content);
            var result = document.RootElement;
            if (result.ValueKind != JsonValueKind.Object)
            {
                return evidence.Content;
            }

            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in result.EnumerateObject())
            {
                if (property.Name is not ("Path" or "StartLine" or "EndLine" or "TotalLines" or "Lines"
                    or "IsTruncated" or "NextStartLine" or "TruncationReason")
                    || !properties.Add(property.Name))
                {
                    return evidence.Content;
                }
            }

            if (properties.Count != 8
                || result.GetProperty("Path").ValueKind != JsonValueKind.String
                || result.GetProperty("Lines").ValueKind != JsonValueKind.Array)
            {
                return evidence.Content;
            }

            var lines = new List<string>();
            foreach (var line in result.GetProperty("Lines").EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.String || line.GetString() is not { } text)
                {
                    return evidence.Content;
                }

                lines.Add(text);
            }

            // Preserve all metadata, including truncation. The outer evidence
            // frame still carries the original digest, provenance, and distrust.
            var metadata = JsonSerializer.Serialize(result.EnumerateObject()
                .Where(property => property.Name != "Lines")
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal));
            return metadata + "\nSource lines (line endings shown as LF):\n" + string.Join('\n', lines);
        }
        catch (JsonException)
        {
            return evidence.Content;
        }
    }
}
