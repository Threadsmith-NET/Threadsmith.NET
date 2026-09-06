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
        long frontierGeneration)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var normalizedRepositoryPath = NormalizeRepositoryPath(repositoryPath);
        var entries = new List<ModelVisibleSourceEntry>();
        foreach (var message in messages)
        {
            if (entries.Count >= MaximumEntries)
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
                    message,
                    normalizedRepositoryPath,
                    workspaceId,
                    entries);
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
        ModelMessage message,
        string normalizedRepositoryPath,
        WorkspaceId? workspaceId,
        List<ModelVisibleSourceEntry> entries)
    {
        if (entries.Count >= MaximumEntries || !TryDeserializeCodeExploreResult(content, out var result))
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
            if (entries.Count >= MaximumEntries)
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

            entries.Add(entry);
        }
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
            || section.Source.Completeness != CodeExploreSourceCompleteness.Complete
            || string.IsNullOrWhiteSpace(section.Source.FileSha256)
            || section.Source.NumberedLines.Count == 0)
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
