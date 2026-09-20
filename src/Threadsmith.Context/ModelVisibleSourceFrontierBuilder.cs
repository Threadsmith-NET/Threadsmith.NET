namespace Threadsmith.Context;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Builds request-local source visibility metadata from canonical model messages.</summary>
public static class ModelVisibleSourceFrontierBuilder
{
    private const int MaximumEntries = 256;

    /// <summary>Derives exact code-explore source ranges retained in the current model request after host output sanitization.</summary>
    public static ModelVisibleSourceFrontier Build(
        IReadOnlyList<ModelMessage> messages,
        string repositoryPath,
        WorkspaceId? workspaceId,
        long frontierGeneration,
        int maximumEntries = 256)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        var normalizedRepositoryPath = NormalizeRepositoryPath(repositoryPath);
        var entries = new List<ModelVisibleSourceEntry>();
        foreach (var message in messages)
        {
            if (entries.Count >= maximumEntries)
            {
                break;
            }

            if (message.Role != ModelMessageRole.Tool
                || !string.Equals(message.ToolName, "code_explore", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                continue;
            }

            foreach (var content in message.Content.Where(part => part.Kind == ModelContentPartKind.Json))
            {
                AddJsonCodeExploreEntries(
                    content.Content,
                    content.IsModelVisible,
                    message,
                    normalizedRepositoryPath,
                    workspaceId,
                    entries,
                    maximumEntries);
            }
        }

        return new ModelVisibleSourceFrontier(
            normalizedRepositoryPath,
            workspaceId,
            frontierGeneration,
            entries,
            entries.Count,
            entries.Count,
            entries.Sum(entry => entry.EmittedCharacters));
    }

    private static void AddJsonCodeExploreEntries(
        string content,
        bool isModelVisible,
        ModelMessage message,
        string normalizedRepositoryPath,
        WorkspaceId? workspaceId,
        List<ModelVisibleSourceEntry> entries,
        int maximumEntries)
    {
        if (entries.Count >= maximumEntries || !TryDeserializeCodeExploreResult(content, out var result))
        {
            return;
        }

        var fileSections = result.FileSections;
        if (!HasUsableCodeExploreResultShape(result, fileSections))
        {
            return;
        }

        foreach (var section in fileSections)
        {
            if (entries.Count >= maximumEntries)
            {
                break;
            }

            if (!TryCreateEntry(
                section,
                message,
                normalizedRepositoryPath,
                workspaceId,
                result.WorkspaceGeneration,
                out var entry))
            {
                continue;
            }

            // Markdown may have dropped a section that remains in the structured sidecar.
            if (!isModelVisible
                && section.Source.Completeness == CodeExploreSourceCompleteness.Partial
                && !message.Content.Any(part => part.IsModelVisible
                    && ContainsVisibleSection(part.Content, section, fileSections)))
            {
                continue;
            }

            entries.Add(entry);
        }
    }

    private static bool ContainsVisibleSection(
        string content,
        CodeExploreFileSection section,
        IReadOnlyList<CodeExploreFileSection> fileSections)
    {
        var normalizedContent = content.ReplaceLineEndings("\n");
        var header = CreateMarkdownSectionHeader(section.FilePath);
        var source = string.Join('\n', section.Source.NumberedLines);
        var sectionHeaders = fileSections
            .Select(item => CreateMarkdownSectionHeader(item.FilePath))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var searchIndex = 0;
        while ((searchIndex = normalizedContent.IndexOf(header, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            var sectionStart = searchIndex + header.Length;
            var sectionEnd = normalizedContent.Length;
            foreach (var otherHeader in sectionHeaders)
            {
                var nextHeader = normalizedContent.IndexOf(otherHeader, sectionStart, StringComparison.Ordinal);
                if (nextHeader >= 0)
                {
                    sectionEnd = Math.Min(sectionEnd, nextHeader);
                }
            }

            if (normalizedContent.AsSpan(sectionStart, sectionEnd - sectionStart).Contains(source, StringComparison.Ordinal))
            {
                return true;
            }

            searchIndex = sectionStart;
        }

        return false;
    }

    private static string CreateMarkdownSectionHeader(string filePath)
    {
        var cleanPath = string.Join(' ', filePath.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var maximumRun = 0;
        var currentRun = 0;
        foreach (var character in cleanPath)
        {
            if (character == '`')
            {
                currentRun++;
                maximumRun = Math.Max(maximumRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        var delimiter = new string('`', Math.Max(1, maximumRun + 1));
        var codeSpan = cleanPath.StartsWith('`') || cleanPath.EndsWith('`')
            ? $"{delimiter} {cleanPath} {delimiter}"
            : $"{delimiter}{cleanPath}{delimiter}";
        return $"**{codeSpan}**";
    }

    private static bool TryDeserializeCodeExploreResult(
        string content,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CodeExploreResult? result)
    {
        try
        {
            result = JsonSerializer.Deserialize<CodeExploreResult>(content);
            return result is not null;
        }
        catch (JsonException)
        {
            result = null;
            return false;
        }
    }

    private static bool HasUsableCodeExploreResultShape(
        CodeExploreResult result,
        IReadOnlyList<CodeExploreFileSection>? fileSections)
    {
        return result.ResolvedAnchors is not null
            && fileSections is not null
            && result.Coverage is not null
            && result.Coverage.Omissions is not null
            && result.Omissions is not null
            && result.ContinuationTargets is not null;
    }

    private static bool TryCreateEntry(
        CodeExploreFileSection? section,
        ModelMessage message,
        string normalizedRepositoryPath,
        WorkspaceId? workspaceId,
        long workspaceGeneration,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ModelVisibleSourceEntry? entry)
    {
        entry = null;
        var toolCallId = message.ToolCallId;
        if (string.IsNullOrWhiteSpace(toolCallId)
            || section is null
            || string.IsNullOrWhiteSpace(section.FilePath)
            || section.SemanticIdentities is null
            || section.Source is null
            || section.Source.NumberedLines is null
            || section.Source.OmittedRanges is null
            || section.Source.Completeness is not (CodeExploreSourceCompleteness.Complete or CodeExploreSourceCompleteness.Partial)
            || string.IsNullOrWhiteSpace(section.Source.FileSha256)
            || section.Source.NumberedLines.Count == 0)
        {
            return false;
        }

        // Partial describes the requested envelope, not the exact lines actually returned.
        if (section.Source.Completeness == CodeExploreSourceCompleteness.Partial
            && (string.IsNullOrWhiteSpace(section.Source.RangeSha256)
                || section.Source.Range.EndLine - section.Source.Range.StartLine + 1 != section.Source.NumberedLines.Count))
        {
            return false;
        }

        entry = new ModelVisibleSourceEntry(
            message.SectionId,
            toolCallId,
            normalizedRepositoryPath,
            workspaceId,
            workspaceGeneration,
            NormalizeRepositoryRelativePath(section.FilePath),
            section.Source.Range,
            section.Source.FileSha256,
            section.Source.RangeSha256,
            CountEmittedCharacters(section.Source.NumberedLines));
        return true;
    }

    private static int CountEmittedCharacters(IReadOnlyList<string> numberedLines)
    {
        var total = 0;
        for (var index = 0; index < numberedLines.Count; index++)
        {
            total += numberedLines[index].Length;
            if (index > 0)
            {
                total += Environment.NewLine.Length;
            }
        }

        return total;
    }

    private static string NormalizeRepositoryPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeRepositoryRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
