namespace Threadsmith.Skills;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Defines the maintained documentation skill identity, its confined tool scope, and cited-answer checks.</summary>
public static class PackagedDocumentationPolicy
{
    /// <summary>Maintained skill id used for local Threadsmith documentation questions.</summary>
    public const string SkillId = "threadsmith-docs-help";

    /// <summary>Application-relative directory containing the curated documentation bundle.</summary>
    public const string BundleDirectoryName = "ThreadsmithDocs";

    /// <summary>Returns whether a selector identifies the maintained documentation skill.</summary>
    public static bool IsDocumentationSkillSelector(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var identity = selector;
        var scopeSeparator = identity.IndexOf(':');
        if (scopeSeparator >= 0)
        {
            identity = identity[(scopeSeparator + 1)..];
        }

        var qualifier = identity.IndexOfAny(['@', '+']);
        if (qualifier >= 0)
        {
            identity = identity[..qualifier];
        }

        return string.Equals(identity, SkillId, StringComparison.Ordinal);
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

    /// <summary>Validates that a completed docs answer cites exact bounded evidence from the packaged bundle.</summary>
    public static async Task ValidateAnswerAsync(
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
        if (string.Equals(status, "unavailable", StringComparison.Ordinal)
            && citationsElement.GetArrayLength() != 0)
        {
            throw new InvalidDataException("Unavailable documentation answers cannot cite evidence.");
        }

        if (!string.Equals(status, "unavailable", StringComparison.Ordinal)
            && citationsElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Documentation answers must cite packaged documentation evidence.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        foreach (var citation in citationsElement.EnumerateArray())
        {
            await ValidateCitationAsync(citation, normalizedRoot, cancellationToken);
        }
    }

    private static async Task ValidateCitationAsync(
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
        for (int index = lineStart - 1; index >= 0; index--)
        {
            var candidate = lines[index].Trim();
            if (candidate.Length > 1
                && candidate[0] == '#'
                && candidate.SkipWhile(character => character == '#').FirstOrDefault() == ' ')
            {
                governingHeading = candidate;
                break;
            }
        }

        if (!string.Equals(governingHeading, heading.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Documentation citation heading does not govern the cited line range.");
        }

        var citedText = string.Join('\n', lines[(lineStart - 1)..lineEnd]);
        if (!citedText.Contains(snippet, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Documentation citation snippet is not present in the cited range.");
        }
    }
}
