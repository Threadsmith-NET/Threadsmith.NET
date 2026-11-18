namespace Threadsmith.Tools;

using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;

/// <summary>Confines legacy semantic results and creates bounded, flat model-facing projections.</summary>
internal static class LegacySemanticToolOutput
{
    /// <summary>Returns whether a semantic location is within the invocation path policy.</summary>
    internal static bool IsAllowed(
        SemanticSourceLocation location,
        ToolInvocationContext context)
    {
        try
        {
            _ = ToolPathRules.NormalizeAndValidate(location.FilePath, context);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Projects symbol declarations.</summary>
    internal static string Render(IReadOnlyList<SymbolResult> results, bool truncated, int maximumResults)
    {
        return Render(
            results.Select(static result => CreateItem(result.Symbol, result.Location)),
            results.Count,
            truncated,
            maximumResults);
    }

    /// <summary>Projects symbol references.</summary>
    internal static string Render(IReadOnlyList<ReferenceResult> results, bool truncated, int maximumResults)
    {
        return Render(
            results.Select(static result => CreateItem(result.Symbol, result.Location)),
            results.Count,
            truncated,
            maximumResults);
    }

    /// <summary>Projects symbol implementations.</summary>
    internal static string Render(IReadOnlyList<ImplementationResult> results, bool truncated, int maximumResults)
    {
        return Render(
            results.Select(static result => CreateItem(result.Symbol, result.Location)),
            results.Count,
            truncated,
            maximumResults);
    }

    private static string Render(
        IEnumerable<LegacySemanticProjectionItem> results,
        int resultCount,
        bool truncated,
        int maximumResults)
    {
        var selected = results.Take(maximumResults).ToArray();
        return JsonSerializer.Serialize(new
        {
            results = selected,
            truncated = truncated || resultCount > selected.Length,
        });
    }

    private static LegacySemanticProjectionItem CreateItem(
        SemanticSymbolIdentity symbol,
        SemanticSourceLocation location)
    {
        return new(
            symbol.Id,
            symbol.DisplayName,
            symbol.Kind,
            location.FilePath,
            location.Range.StartLine);
    }

    private sealed record LegacySemanticProjectionItem(
        [property: JsonPropertyName("symbolId")] string SymbolId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("line")] int Line);
}
