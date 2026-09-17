namespace Threadsmith.Execution;

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Shared semantic-first admission policy for parent and child tool batches.</summary>
internal static class SemanticFirstSearchPolicy
{
    /// <summary>Rejects broad active-workspace C# discovery until an advertised semantic tool has been tried.</summary>
    public static bool TryCreateCorrection(
        ToolRequestModelOutput tool,
        ToolInvocationContext? invocationContext,
        bool semanticToolAttempted,
        IReadOnlyList<ModelToolDefinition> modelTools,
        CorrectiveMessageFactory corrections,
        [NotNullWhen(true)] out string? content)
    {
        content = null;
        if (invocationContext?.WorkspaceId is null
            || semanticToolAttempted
            || !string.Equals(tool.ToolName, "search", StringComparison.OrdinalIgnoreCase)
            || !TryGetDiscoverySearchQuery(tool.ArgumentsJson, invocationContext.RepositoryPath, out var query)
            || !LooksLikeCSharpSymbolOrFileQuery(query))
        {
            return false;
        }

        var hasCodeExplore = modelTools.Any(static definition => string.Equals(
            definition.Name,
            "code_explore",
            StringComparison.OrdinalIgnoreCase));
        var hasFindSymbol = modelTools.Any(static definition => string.Equals(
            definition.Name,
            "find_symbol",
            StringComparison.OrdinalIgnoreCase));
        if (!hasCodeExplore && !hasFindSymbol)
        {
            return false;
        }

        var boundedQuery = BoundSingleLine(query, 160);
        var isFileQuery = boundedQuery.Contains(".cs", StringComparison.OrdinalIgnoreCase);
        var isExactPathQuery = isFileQuery && LooksLikeExactCSharpPathQuery(boundedQuery);
        var isExactSymbolQuery = !isFileQuery && LooksLikeExactCSharpSymbolQuery(boundedQuery);
        if (!isExactPathQuery && !isExactSymbolQuery && !hasFindSymbol)
        {
            return false;
        }

        var suggestedQuery = isExactPathQuery || isExactSymbolQuery
            ? boundedQuery
            : isFileQuery
                ? BoundSingleLine(GetCSharpFileSymbolQuery(query), 160)
                : BoundSingleLine(GetDiscoverableSymbolQuery(query), 160);
        var suggestedTool = hasCodeExplore && (isExactPathQuery || isExactSymbolQuery)
            ? "code_explore"
            : "find_symbol";
        content = corrections.CreateSemanticFirstSearchReason(
            suggestedTool,
            suggestedQuery,
            boundedQuery,
            isExactPathQuery,
            isExactSymbolQuery);
        return true;
    }

    /// <summary>Identifies semantic inspection tools for accepted-call tracking.</summary>
    public static bool IsSemanticInspectionTool(string toolName)
    {
        return string.Equals(toolName, "code_explore", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "find_symbol", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "find_references", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "find_implementations", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "call_hierarchy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "symbol_impact", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "csharp_pattern_search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "generated_code_query", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetDiscoverySearchQuery(string argumentsJson, string repositoryPath, [NotNullWhen(true)] out string? query)
    {
        query = null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("query", out var queryElement)
                || queryElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("path", out var pathElement)
                && pathElement.ValueKind == JsonValueKind.String
                && pathElement.GetString() is { } path
                && !string.IsNullOrWhiteSpace(path))
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
                var scope = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, root));
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!scope.Equals(root, comparison))
                {
                    return false;
                }
            }

            query = queryElement.GetString();
            return !string.IsNullOrWhiteSpace(query);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool LooksLikeCSharpSymbolOrFileQuery(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Contains(".cs", StringComparison.OrdinalIgnoreCase)
            || ContainsDeclarationKeyword(trimmed))
        {
            return true;
        }

        foreach (var token in ExtractIdentifierTokens(trimmed))
        {
            if (token.Length >= 3
                && token.Any(char.IsUpper)
                && token.Any(char.IsLower))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDeclarationKeyword(string query)
    {
        return query.Contains("class", StringComparison.OrdinalIgnoreCase)
            || query.Contains("interface", StringComparison.OrdinalIgnoreCase)
            || query.Contains("record", StringComparison.OrdinalIgnoreCase)
            || query.Contains("struct", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExactCSharpPathQuery(string query)
    {
        var trimmed = query.Trim();
        return trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Contains('/', StringComparison.Ordinal)
                || trimmed.Contains('\\', StringComparison.Ordinal)
                || trimmed.StartsWith(".", StringComparison.Ordinal));
    }

    private static bool LooksLikeExactCSharpSymbolQuery(string query)
    {
        var trimmed = query.Trim();
        var parameterStart = trimmed.IndexOf('(');
        var name = parameterStart < 0 ? trimmed : trimmed[..parameterStart];
        return !string.IsNullOrWhiteSpace(name)
            && !name.Any(char.IsWhiteSpace)
            && LooksLikeQualifiedIdentifier(name)
            && (parameterStart < 0 || trimmed.EndsWith(")", StringComparison.Ordinal));
    }

    private static bool LooksLikeQualifiedIdentifier(string value)
    {
        var parts = value.Split('.');
        return parts.Length > 0 && parts.All(IsIdentifierLike);
    }

    private static bool IsIdentifierLike(string value)
    {
        if (value.Length == 0 || (!char.IsLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        return value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static string GetCSharpFileSymbolQuery(string query)
    {
        var trimmed = query.Trim();
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        var fileName = separator < 0 ? trimmed : trimmed[(separator + 1)..];
        return StripCSharpExtension(fileName);
    }

    private static string GetDiscoverableSymbolQuery(string query)
    {
        var token = ExtractIdentifierTokens(query)
            .LastOrDefault(token => token.Any(char.IsUpper) && token.Any(char.IsLower));
        return token ?? StripCSharpExtension(query);
    }

    private static IEnumerable<string> ExtractIdentifierTokens(string query)
    {
        var builder = new StringBuilder(query.Length);
        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string StripCSharpExtension(string query)
    {
        var trimmed = query.Trim();
        return trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3]
            : trimmed;
    }

    private static string BoundSingleLine(string value, int maximumCharacters)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters];
    }
}
