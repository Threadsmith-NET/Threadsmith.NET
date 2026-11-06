namespace Threadsmith.Core;

using System.Collections.ObjectModel;

/// <summary>Detaches inspection collections at host ownership boundaries.</summary>
public static class ContextInspectionSnapshots
{
    /// <summary>Copies the supported inspection graph, sharing only immutable values and records.</summary>
    public static ContextInspectionProjection Copy(ContextInspectionProjection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        return inspection with
        {
            TokensByCategory = new ReadOnlyDictionary<string, int>(
                inspection.TokensByCategory.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
            Evidence = inspection.Evidence.ToArray(),
            PromptAssets = inspection.PromptAssets.ToArray(),
            ModelRationale = inspection.ModelRationale.ToArray(),
            Reductions = inspection.Reductions.ToArray(),
            ConversationItems = inspection.ConversationItems.Select(item => item with
            {
                Images = item.Images.ToArray(),
                SourceMessageIds = item.SourceMessageIds.ToArray(),
                SourceRunIds = item.SourceRunIds.ToArray(),
                SourceEvidenceIds = item.SourceEvidenceIds.ToArray(),
            }).ToArray(),
            RepositoryMemoryItems = inspection.RepositoryMemoryItems.Select(CopyMemory).ToArray(),
            RepositoryMemorySearch = CopySearch(inspection.RepositoryMemorySearch),
            RepositoryIntelligenceSearch = CopySearch(inspection.RepositoryIntelligenceSearch),
            RepositoryMemoryDispatch = inspection.RepositoryMemoryDispatch is { } dispatch
                ? dispatch with { Inclusions = dispatch.Inclusions.ToArray() }
                : null,
            RequestUsage = inspection.RequestUsage is { } usage
                ? usage with
                {
                    Memories = usage.Memories.Select(CopyMemory).ToArray(),
                    Components = usage.Components.Select(CopyComponent).ToArray(),
                }
                : null,
        };
    }

    private static RepositoryMemoryContextItemProjection CopyMemory(RepositoryMemoryContextItemProjection memory)
    {
        return memory with { Supports = memory.Supports.ToArray() };
    }

    private static RepositoryMemorySearchDetails? CopySearch(RepositoryMemorySearchDetails? search)
    {
        return search is null ? null : search with
        {
            Candidates = search.Candidates.Select(candidate => candidate with
            {
                MatchedTerms = candidate.MatchedTerms.ToArray(),
            }).ToArray(),
            LexicalExpansions = search.LexicalExpansions.ToArray(),
        };
    }

    private static ContextUsageComponent CopyComponent(ContextUsageComponent component)
    {
        return component with { Children = component.Children.Select(CopyComponent).ToArray() };
    }
}
