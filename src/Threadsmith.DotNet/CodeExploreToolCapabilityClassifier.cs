namespace Threadsmith.DotNet;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Extracts repository-neutral agent-tool capability evidence from Roslyn symbols.</summary>
internal static class CodeExploreToolCapabilityClassifier
{
    private const string ToolTypeSuffix = "Tool";
    private const string ToolDefinitionTypeName = "ToolDefinition";

    /// <summary>Classifies one declaration and records the tool contract it belongs to, if any.</summary>
    public static CodeExploreToolCapabilityEvidence Classify(
        ISymbol symbol,
        SyntaxNode declaration,
        SemanticModel semanticModel)
    {
        var toolType = symbol is INamedTypeSymbol namedType && IsConcreteToolType(namedType)
            ? namedType
            : symbol.ContainingType;

        if (toolType is null || !IsConcreteToolType(toolType))
        {
            return CodeExploreToolCapabilityEvidence.None;
        }

        var role = symbol switch
        {
            INamedTypeSymbol => CodeExploreToolCapabilityRole.ToolType,
            IFieldSymbol field when IsToolDefinitionType(field.Type) => CodeExploreToolCapabilityRole.Definition,
            IPropertySymbol property when IsToolDefinitionType(property.Type) => CodeExploreToolCapabilityRole.Definition,
            _ => CodeExploreToolCapabilityRole.Member,
        };
        var toolTypeName = toolType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var declaredToolId = role == CodeExploreToolCapabilityRole.Definition
            ? FindDeclaredToolId(declaration, semanticModel)
            : null;
        return new CodeExploreToolCapabilityEvidence(
            role,
            toolTypeName,
            declaredToolId,
            CreateFamilyTerms(declaredToolId ?? toolType.Name),
            GetRelatedContractTypeNames(toolType));
    }

    /// <summary>Creates canonical family concepts from a declared tool id or tool type name.</summary>
    public static IReadOnlyList<string> CreateFamilyTerms(string value)
    {
        var terms = SplitIdentifierTerms(value).ToList();
        if (terms.Count > 0 && terms[^1].Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            terms.RemoveAt(terms.Count - 1);
        }

        if (terms.Count > 0 && terms[^1].Equals("query", StringComparison.OrdinalIgnoreCase))
        {
            terms.RemoveAt(terms.Count - 1);
        }

        return terms
            .Where(term => term.Length >= 2)
            .Select(term => term.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Creates a stable posting key from canonical family concepts.</summary>
    public static string CreateFamilyKey(IEnumerable<string> terms)
    {
        return string.Join('_', terms.Select(term => term.ToLowerInvariant()));
    }

    private static bool IsConcreteToolType(INamedTypeSymbol type)
    {
        if (type.IsAbstract)
        {
            return false;
        }

        if (type.GetMembers().Any(member => member switch
            {
                IFieldSymbol field => IsToolDefinitionType(field.Type),
                IPropertySymbol property => IsToolDefinitionType(property.Type),
                _ => false,
            }))
        {
            return true;
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name.Equals(ToolTypeSuffix, StringComparison.Ordinal)
                && current.TypeArguments.Length >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsToolDefinitionType(ITypeSymbol type)
    {
        return type.Name.Equals(ToolDefinitionTypeName, StringComparison.Ordinal);
    }

    private static string? FindDeclaredToolId(SyntaxNode declaration, SemanticModel semanticModel)
    {
        var operation = semanticModel.GetOperation(declaration);
        if (operation is null)
        {
            return null;
        }

        foreach (var candidate in EnumerateOperations(operation))
        {
            if (candidate is not IArgumentOperation argument
                || argument.Parameter?.Name is not { } parameterName
                || !IsToolIdParameter(parameterName)
                || !argument.Value.ConstantValue.HasValue
                || argument.Value.ConstantValue.Value is not string value
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            return value;
        }

        return null;
    }

    private static IEnumerable<IOperation> EnumerateOperations(IOperation operation)
    {
        yield return operation;
        foreach (var child in operation.ChildOperations)
        {
            foreach (var descendant in EnumerateOperations(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsToolIdParameter(string parameterName)
    {
        return parameterName.Equals("id", StringComparison.OrdinalIgnoreCase)
            || parameterName.Equals("name", StringComparison.OrdinalIgnoreCase)
            || parameterName.Equals("toolId", StringComparison.OrdinalIgnoreCase)
            || parameterName.Equals("toolName", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetRelatedContractTypeNames(INamedTypeSymbol toolType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = toolType.BaseType; current is not null; current = current.BaseType)
        {
            if (current.TypeArguments.Length < 2)
            {
                continue;
            }

            foreach (var typeArgument in current.TypeArguments.OfType<INamedTypeSymbol>())
            {
                names.Add(typeArgument.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            }
        }

        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> SplitIdentifierTerms(string value)
    {
        var current = new List<char>();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsLetterOrDigit(character))
            {
                if (current.Count > 0)
                {
                    yield return new string([.. current]).ToLowerInvariant();
                    current.Clear();
                }

                continue;
            }

            var startsBoundary = current.Count > 0
                && char.IsUpper(character)
                && (char.IsLower(value[index - 1])
                    || (index + 1 < value.Length && char.IsLower(value[index + 1])));
            if (startsBoundary
                && current.Count == 1
                && char.IsUpper(current[0])
                && index + 1 < value.Length
                && char.IsLower(value[index + 1]))
            {
                startsBoundary = false;
            }

            if (startsBoundary)
            {
                yield return new string([.. current]).ToLowerInvariant();
                current.Clear();
            }

            current.Add(character);
        }

        if (current.Count > 0)
        {
            yield return new string([.. current]).ToLowerInvariant();
        }
    }
}

/// <summary>Structural role of one declaration in an agent-facing tool capability.</summary>
internal enum CodeExploreToolCapabilityRole
{
    None,
    ToolType,
    Definition,
    DataContract,
    Member,
}

/// <summary>Catalog-time structural evidence for an agent-facing tool capability.</summary>
internal sealed record CodeExploreToolCapabilityEvidence(
    CodeExploreToolCapabilityRole Role,
    string? ToolTypeName,
    string? DeclaredToolId,
    IReadOnlyList<string> FamilyTerms,
    IReadOnlyList<string> RelatedContractTypeNames)
{
    /// <summary>Gets an empty capability-evidence value.</summary>
    public static CodeExploreToolCapabilityEvidence None { get; } = new(
        CodeExploreToolCapabilityRole.None,
        null,
        null,
        [],
        []);

    /// <summary>Gets the canonical capability families linked to this declaration.</summary>
    public IReadOnlyList<string> Families { get; init; } = [];
}
