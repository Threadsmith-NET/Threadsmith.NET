namespace Threadsmith.Skills;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Defines the maintained documentation skill identity, its confined tool scope, and cited-answer checks.</summary>
public static class PackagedDocumentationPolicy
{
    /// <summary>Maintained skill id used for local Threadsmith documentation questions.</summary>
    public const string SkillId = "threadsmith-docs-help";

    /// <summary>Application-relative directory containing the curated documentation bundle.</summary>
    public const string BundleDirectoryName = "ThreadsmithDocs";

    /// <summary>Returns whether a resolved package is the maintained documentation skill.</summary>
    public static bool IsDocumentationSkill(SkillScope scope, string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        return scope == SkillScope.Maintained
            && string.Equals(skillId, SkillId, StringComparison.Ordinal);
    }

    /// <summary>Rebinds a tool invocation to the application-owned documentation bundle with read-only authority.</summary>
    public static ToolInvocationContext BindToBundle(
        ToolInvocationContext context,
        string applicationBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        var root = Path.GetFullPath(Path.Combine(applicationBaseDirectory, BundleDirectoryName));
        return context with
        {
            WorkspaceId = null,
            RepositoryPath = root,
            ApprovedRoots = ["."],
            ProhibitedPaths =
            [
                "implementation-plans",
                "implementation-plans/**",
                "features",
                "features/**",
                "docs/implementation-plans",
                "docs/implementation-plans/**",
                "docs/features",
                "docs/features/**",
            ],
            AllowedExecutables = [],
            AllowedNetworkHosts = [],
            AllowedSecretReferences = [],
        };
    }

    /// <summary>Validates confined citation ranges and normalizes model-supplied citation presentation.</summary>
    public static async Task<string> ValidateAnswerAsync(
        string outputJson,
        string bundleRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleRoot);
        using var document = JsonDocument.Parse(outputJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("citations", out var citationsElement)
            || citationsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Documentation skill output is missing its status or citations array.");
        }

        var status = statusElement.GetString();
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        var normalizedCitations = new List<(string Heading, string Snippet)>();
        foreach (var citation in citationsElement.EnumerateArray())
        {
            normalizedCitations.Add(await ValidateCitationAsync(citation, normalizedRoot, cancellationToken));
        }

        var normalizedNode = JsonNode.Parse(outputJson);
        if (normalizedNode is not JsonObject normalizedObject
            || normalizedObject["citations"] is not JsonArray normalizedCitationNodes
            || normalizedCitationNodes.Count != normalizedCitations.Count)
        {
            throw new InvalidDataException("Documentation skill output cannot be normalized.");
        }

        var hasCitations = normalizedCitations.Count != 0;
        if (string.Equals(status, "unavailable", StringComparison.Ordinal) && hasCitations)
        {
            normalizedObject["status"] = "partial";
        }
        else if (!string.Equals(status, "unavailable", StringComparison.Ordinal) && !hasCitations)
        {
            normalizedObject["status"] = "unavailable";
        }

        for (var index = 0; index < normalizedCitationNodes.Count; index++)
        {
            if (normalizedCitationNodes[index] is not JsonObject normalizedCitation)
            {
                throw new InvalidDataException("Documentation citation cannot be normalized.");
            }

            normalizedCitation["heading"] = normalizedCitations[index].Heading;
            normalizedCitation["snippet"] = normalizedCitations[index].Snippet;
        }

        return normalizedObject.ToJsonString();
    }

    private static async Task<(string Heading, string Snippet)> ValidateCitationAsync(
        JsonElement citation,
        string normalizedRoot,
        CancellationToken cancellationToken)
    {
        if (!citation.TryGetProperty("path", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String
            || !citation.TryGetProperty("heading", out var headingElement)
            || headingElement.ValueKind != JsonValueKind.String
            || !citation.TryGetProperty("lineStart", out var startElement)
            || !startElement.TryGetInt32(out var lineStart)
            || !citation.TryGetProperty("lineEnd", out var endElement)
            || !endElement.TryGetInt32(out var lineEnd)
            || !citation.TryGetProperty("snippet", out var snippetElement)
            || snippetElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Documentation citation fields are incomplete.");
        }

        var relativePath = pathElement.GetString();
        var heading = headingElement.GetString();
        var snippet = snippetElement.GetString();
        if (string.IsNullOrWhiteSpace(relativePath)
            || string.IsNullOrWhiteSpace(heading)
            || string.IsNullOrWhiteSpace(snippet)
            || Path.IsPathRooted(relativePath)
            || lineStart < 1
            || lineEnd < lineStart)
        {
            throw new InvalidDataException("Documentation citation values are invalid.");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var relativeSegments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (relativeSegments.Any(segment =>
            string.Equals(segment, "implementation-plans", comparison)
            || string.Equals(segment, "features", comparison)))
        {
            throw new InvalidDataException("Documentation citations cannot use excluded planning content.");
        }

        var path = Path.GetFullPath(relativePath, normalizedRoot);
        if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
            || !File.Exists(path))
        {
            throw new InvalidDataException("Documentation citation escapes or is absent from the packaged bundle.");
        }

        var current = normalizedRoot;
        foreach (var segment in Path.GetRelativePath(normalizedRoot, path)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Documentation citations cannot traverse links or reparse points.");
            }
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lineEnd > lines.Length)
        {
            throw new InvalidDataException("Documentation citation line range is not present.");
        }

        string? governingHeading = null;
        for (var index = lineStart - 1; index >= 0; index--)
        {
            if (TryGetMarkdownHeading(lines[index], out var candidate))
            {
                governingHeading = candidate;
                break;
            }
        }

        if (governingHeading is null)
        {
            for (var index = lineStart - 1; index < lineEnd; index++)
            {
                if (TryGetMarkdownHeading(lines[index], out var candidate))
                {
                    governingHeading = candidate;
                    break;
                }
            }
        }

        var citedText = string.Join('\n', lines[(lineStart - 1)..lineEnd]);
        return (governingHeading ?? heading.Trim(), NormalizeSnippet(snippet, citedText));
    }

    private static string NormalizeSnippet(string snippet, string citedText)
    {
        if (citedText.Contains(snippet, StringComparison.Ordinal))
        {
            return snippet;
        }

        var elidedSnippet = snippet.Replace("…", "...", StringComparison.Ordinal);
        if (!elidedSnippet.Contains("...", StringComparison.Ordinal))
        {
            return snippet;
        }

        var fragments = elidedSnippet.Split("...", StringSplitOptions.RemoveEmptyEntries);
        if (fragments.Length < 2)
        {
            return snippet;
        }

        var start = -1;
        var end = 0;
        foreach (var fragment in fragments)
        {
            var index = citedText.IndexOf(fragment, end, StringComparison.Ordinal);
            if (index < 0)
            {
                return snippet;
            }

            start = start < 0 ? index : start;
            end = index + fragment.Length;
        }

        var canonical = citedText[start..end];
        return canonical.Length <= 500 ? canonical : snippet;
    }

    private static bool TryGetMarkdownHeading(string line, out string heading)
    {
        var candidate = line.Trim();
        var markerLength = 0;
        while (markerLength < candidate.Length && candidate[markerLength] == '#')
        {
            markerLength++;
        }

        if (markerLength is >= 1 and <= 6
            && markerLength < candidate.Length
            && candidate[markerLength] == ' ')
        {
            heading = candidate;
            return true;
        }

        heading = string.Empty;
        return false;
    }
}
