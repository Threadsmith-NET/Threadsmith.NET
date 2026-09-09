namespace Threadsmith.Context;

using System.Text.Json;

/// <summary>Proves a file-read excerpt is already visible in the current instruction bundle.</summary>
internal sealed class InstructionEvidenceMatcher
{
    private readonly RepositoryInstructionBundle _bundle;
    private readonly Dictionary<string, string[]> _linesByPath;

    /// <summary>Initializes a new instance of the <see cref="InstructionEvidenceMatcher"/> class.</summary>
    public InstructionEvidenceMatcher(RepositoryInstructionBundle bundle)
    {
        _bundle = bundle;
        _linesByPath = new Dictionary<string, string[]>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var source in bundle.Sources)
        {
            if (GetCanonicalPath(source.RelativePath) is not { } path)
            {
                continue;
            }

            var lines = new List<string>();
            using var reader = new StringReader(source.Content.TrimStart('\uFEFF'));
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            _linesByPath[path] = [.. lines];
        }
    }

    /// <summary>Checks exact host-attributed read content without consulting disk or provider history.</summary>
    public bool IsAlreadyIncluded(Evidence evidence)
    {
        if (evidence.Kind != EvidenceKind.ToolResult
            || evidence.Provenance.Source != "tool:read_file"
            || evidence.Provenance.ToolInvocationId is null
            || evidence.Provenance.SourcePath is not { } sourcePath
            || GetCanonicalPath(sourcePath) is not { } canonicalPath
            || !_linesByPath.TryGetValue(canonicalPath, out var expectedLines))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(evidence.Content);
            var result = document.RootElement;
            if (result.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Unknown or duplicate fields may carry additional evidence. Keep it.
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in result.EnumerateObject())
            {
                if (property.Name is not ("Path" or "StartLine" or "EndLine" or "TotalLines" or "Lines"
                    or "IsTruncated" or "NextStartLine" or "TruncationReason")
                    || !properties.Add(property.Name))
                {
                    return false;
                }
            }

            if (properties.Count != 8
                || result.GetProperty("Path").ValueKind != JsonValueKind.String
                || GetCanonicalPath(result.GetProperty("Path").GetString()) is not { } payloadPath
                || !_linesByPath.Comparer.Equals(canonicalPath, payloadPath)
                || !TryReadInt(result, "StartLine", out var startLine)
                || !TryReadInt(result, "EndLine", out var endLine)
                || !TryReadInt(result, "TotalLines", out var totalLines)
                || totalLines != expectedLines.Length
                || startLine < 1 || endLine < startLine || endLine > totalLines
                || result.GetProperty("Lines").ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var truncated = result.GetProperty("IsTruncated");
            var next = result.GetProperty("NextStartLine");
            var reason = result.GetProperty("TruncationReason");
            if (truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || truncated.GetBoolean() != (endLine < totalLines)
                || (endLine < totalLines
                    ? next.ValueKind != JsonValueKind.Number || !next.TryGetInt32(out var nextLine) || nextLine != endLine + 1
                    : next.ValueKind != JsonValueKind.Null)
                || (endLine == totalLines && reason.ValueKind != JsonValueKind.Null)
                || (endLine < totalLines && reason.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)))
            {
                return false;
            }

            var lines = result.GetProperty("Lines");
            if (lines.GetArrayLength() != endLine - startLine + 1)
            {
                return false;
            }

            var index = startLine - 1;
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.String
                    || !string.Equals(line.GetString(), expectedLines[index++], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadInt(JsonElement value, string name, out int number)
    {
        number = 0;
        var property = value.GetProperty(name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out number);
    }

    private string? GetCanonicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), _bundle.RepositoryRoot);
            var prefix = Path.TrimEndingDirectorySeparator(_bundle.RepositoryRoot) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
