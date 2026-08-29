namespace Threadsmith.Tools;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Returns a bounded compiler-aware incoming/outgoing call hierarchy.</summary>
public sealed class CallHierarchyTool : AdvancedSemanticTool<CallHierarchyInput, CallHierarchyResult>
{
    private static readonly ToolDefinition _definition = CreateDefinition<CallHierarchyInput, CallHierarchyResult>(
        "call_hierarchy",
        "Primary compiler-aware tool for incoming/outgoing C# call relationships. Arguments use the flat shape {symbolId,direction?,depth?}. depth is the only model-visible traversal hint; host owns node/edge counts and time bounds. MUST use before search and fall back only if this tool fails or reports incomplete evidence.")
    with
    {
        PreferStrictArguments = true,
    };

    /// <summary>Initializes a new instance of the <see cref="CallHierarchyTool"/> class.</summary>
    public CallHierarchyTool(IAdvancedSemanticQueryService service)
        : base(service, _definition)
    {
    }

    /// <inheritdoc />
    protected override Task<CallHierarchyResult> QueryAsync(
        WorkspaceId workspaceId,
        CallHierarchyInput input,
        CancellationToken cancellationToken)
    {
        var limits = input.Depth is { } depth
            ? new SemanticTraversalLimits { MaximumDepth = depth }
            : new SemanticTraversalLimits();
        return Service.QueryCallHierarchyAsync(
            workspaceId,
            new CallHierarchyRequest
            {
                SymbolId = input.SymbolId,
                Direction = input.Direction,
                Limits = limits,
            },
            cancellationToken);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(CallHierarchyInput input)
    {
        return $"{input.Direction}: {input.SymbolId}";
    }

    /// <inheritdoc />
    protected override void ValidateInput(CallHierarchyInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SymbolId);
        if (input.Depth is < 0 or > 8)
        {
            throw new ToolArgumentValidationException("call_hierarchy depth must be between 0 and 8.");
        }
    }
}

/// <summary>Model-facing simplified input for <c>call_hierarchy</c>.</summary>
public sealed record CallHierarchyInput
{
    /// <summary>Stable semantic symbol id returned by semantic discovery.</summary>
    public required string SymbolId { get; init; }

    /// <summary>Requested traversal direction.</summary>
    public CallHierarchyDirection Direction { get; init; } = CallHierarchyDirection.Both;

    /// <summary>Optional graph depth hint from 0 through 8; host owns all other traversal limits.</summary>
    public int? Depth { get; init; }
}

/// <summary>Returns an explainable bounded impact graph for a semantic symbol.</summary>
public sealed class SymbolImpactTool : AdvancedSemanticTool<SymbolImpactInput, SymbolImpactResult>
{
    private static readonly ToolDefinition _definition = CreateDefinition<SymbolImpactInput, SymbolImpactResult>(
        "symbol_impact",
        "Primary compiler-aware tool for bounded reference, caller, implementation, project, test, and classified-source impact. Arguments use the flat shape {symbolId}. Host owns traversal depth, node/edge counts, and time bounds. MUST use before search and fall back only if this tool fails or reports incomplete evidence.")
    with
    {
        PreferStrictArguments = true,
    };

    /// <summary>Initializes a new instance of the <see cref="SymbolImpactTool"/> class.</summary>
    public SymbolImpactTool(IAdvancedSemanticQueryService service)
        : base(service, _definition)
    {
    }

    /// <inheritdoc />
    protected override Task<SymbolImpactResult> QueryAsync(
        WorkspaceId workspaceId,
        SymbolImpactInput input,
        CancellationToken cancellationToken)
    {
        return Service.QuerySymbolImpactAsync(
            workspaceId,
            new SymbolImpactRequest { SymbolId = input.SymbolId },
            cancellationToken);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(SymbolImpactInput input)
    {
        return input.SymbolId;
    }

    /// <inheritdoc />
    protected override void ValidateInput(SymbolImpactInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SymbolId);
    }
}

/// <summary>Model-facing minimal input for <c>symbol_impact</c>.</summary>
public sealed record SymbolImpactInput
{
    /// <summary>Stable semantic symbol id returned by semantic discovery.</summary>
    public required string SymbolId { get; init; }
}

/// <summary>Searches C# syntax with a flat closed inert pattern schema.</summary>
public sealed class CSharpPatternSearchTool : AdvancedSemanticTool<CSharpPatternSearchInput, CSharpPatternSearchResult>
{
    private static readonly ToolDefinition _definition = CreatePatternDefinition();

    /// <summary>Initializes a new instance of the <see cref="CSharpPatternSearchTool"/> class.</summary>
    public CSharpPatternSearchTool(IAdvancedSemanticQueryService service)
        : base(service, _definition)
    {
    }

    /// <inheritdoc />
    protected override Task<CSharpPatternSearchResult> QueryAsync(
        WorkspaceId workspaceId,
        CSharpPatternSearchInput input,
        CancellationToken cancellationToken)
    {
        return Service.SearchCSharpPatternAsync(
            workspaceId,
            new CSharpPatternSearchRequest
            {
                Pattern = new CSharpPattern
                {
                    Kind = input.Kind,
                    Name = input.Name,
                    ContainingType = input.ContainingType,
                    RequiredModifiers = input.Modifiers,
                    RequiredAttributes = input.Attributes,
                },
                Path = input.Path,
            },
            cancellationToken);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(CSharpPatternSearchInput input)
    {
        var scope = string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path;
        var name = string.IsNullOrWhiteSpace(input.Name) ? "*" : input.Name;
        return $"{input.Kind} {name} in {scope}";
    }

    /// <inheritdoc />
    protected override void ValidateInput(CSharpPatternSearchInput input)
    {
        if (input.Modifiers is { Count: > CSharpPatternConstraints.MaximumPredicateValues }
            || input.Attributes is { Count: > CSharpPatternConstraints.MaximumPredicateValues })
        {
            throw new ToolArgumentValidationException("Pattern modifier or attribute counts exceed host limits.");
        }

        foreach (var modifier in input.Modifiers ?? [])
        {
            if (!CSharpPatternConstraints.AllowedModifiers.Contains(modifier))
            {
                throw new ToolArgumentValidationException($"Unsupported C# modifier '{modifier}'.");
            }
        }

        foreach (var value in new[] { input.Name, input.ContainingType }.Concat(input.Attributes ?? []))
        {
            if (value is { Length: > CSharpPatternConstraints.MaximumNameCharacters }
                || (value is not null && !CSharpPatternConstraints.IsValidDottedIdentifierName(value)))
            {
                throw new ToolArgumentValidationException("Pattern names must be bounded C# identifiers.");
            }
        }
    }

    /// <inheritdoc />
    protected override string CreateSchemaMismatchMessage(JsonException exception, string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);
        return string.Equals(exception.Path, "$.kind", StringComparison.OrdinalIgnoreCase)
            ? "Tool arguments do not match the declared input schema. $.kind expected string enum Declaration|TypeDeclaration|MethodDeclaration|PropertyDeclaration|FieldDeclaration|Attribute|Invocation|ObjectCreation|MemberAccess; use MethodDeclaration for methods, not Method."
            : base.CreateSchemaMismatchMessage(exception, argumentsJson);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        CSharpPatternSearchInput input,
        ToolInvocationContext context)
    {
        return [input.Path ?? context.RepositoryPath];
    }

    private static ToolDefinition CreatePatternDefinition()
    {
        var definition = CreateDefinition<CSharpPatternSearchInput, CSharpPatternSearchResult>(
            "csharp_pattern_search",
            "Primary semantic tool for C# declaration and expression shapes when an exact symbol query is insufficient. Arguments use a flat shape {kind,name?,containingType?,path?,modifiers?,attributes?}. kind must be a JSON string enum value: Declaration, TypeDeclaration, MethodDeclaration, PropertyDeclaration, FieldDeclaration, Attribute, Invocation, ObjectCreation, or MemberAccess; there is no Method kind, use MethodDeclaration for methods. Host owns capture names, result counts, and time bounds. MUST use before search and fall back only if this tool fails or reports incomplete evidence.");
        var schema = JsonNode.Parse(definition.InputSchema.JsonSchema)
            ?? throw new InvalidOperationException("The generated pattern-search schema was empty.");
        var required = schema["required"] as JsonArray;
        if (required is null)
        {
            required = [];
            schema["required"] = required;
        }

        if (!required.Any(item => item is JsonValue value
            && value.TryGetValue<string>(out var name)
            && string.Equals(name, "kind", StringComparison.OrdinalIgnoreCase)))
        {
            required.Add("kind");
        }

        return definition with
        {
            PreferStrictArguments = true,
            InputSchema = definition.InputSchema with
            {
                JsonSchema = schema.ToJsonString(),
            },
        };
    }
}

/// <summary>Model-facing flat input for <c>csharp_pattern_search</c>.</summary>
public sealed record CSharpPatternSearchInput
{
    /// <summary>Required closed C# syntax shape.</summary>
    public required CSharpPatternKind Kind { get; init; }

    /// <summary>Optional exact simple identifier or dotted name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional exact containing type name.</summary>
    public string? ContainingType { get; init; }

    /// <summary>Optional repository-relative file or directory scope.</summary>
    public string? Path { get; init; }

    /// <summary>Optional required C# modifiers, such as <c>public</c>, <c>static</c>, or <c>async</c>.</summary>
    public IReadOnlyList<string>? Modifiers { get; init; }

    /// <summary>Optional required exact attribute simple names, with or without the Attribute suffix.</summary>
    public IReadOnlyList<string>? Attributes { get; init; }
}

/// <summary>Inventories and optionally reads bounded content from already-loaded generated documents.</summary>
public sealed class GeneratedCodeTool : AdvancedSemanticTool<GeneratedCodeQuery, GeneratedCodeResult>
{
    private static readonly ToolDefinition _definition = CreateDefinition<GeneratedCodeQuery, GeneratedCodeResult>(
        "generated_code_query",
        "Primary semantic tool for generated C# documents already present in the semantic workspace. MUST use before search and fall back only if this tool fails or reports incomplete evidence.");

    /// <summary>Initializes a new instance of the <see cref="GeneratedCodeTool"/> class.</summary>
    public GeneratedCodeTool(IAdvancedSemanticQueryService service)
        : base(service, _definition)
    {
    }

    /// <inheritdoc />
    protected override Task<GeneratedCodeResult> QueryAsync(
        WorkspaceId workspaceId,
        GeneratedCodeQuery input,
        CancellationToken cancellationToken)
    {
        return Service.QueryGeneratedCodeAsync(workspaceId, input, cancellationToken);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(GeneratedCodeQuery input)
    {
        var scope = string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path;
        return input.IncludeContent ? $"content in {scope}" : $"inventory in {scope}";
    }

    /// <inheritdoc />
    protected override void ValidateInput(GeneratedCodeQuery input)
    {
        if (input.MaximumDocuments is < 1 or > 500 || input.MaximumContentCharacters is < 1 or > 65_536)
        {
            throw new ToolArgumentValidationException("generated document/content bounds are outside host limits.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GeneratedCodeQuery input,
        ToolInvocationContext context)
    {
        return [input.Path ?? context.RepositoryPath];
    }
}

/// <summary>Common policy and provenance adapter for Plan-43 semantic tools.</summary>
public abstract class AdvancedSemanticTool<TInput, TOutput> : Tool<TInput, TOutput>
    where TInput : class
    where TOutput : class
{
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="AdvancedSemanticTool{TInput, TOutput}"/> class.</summary>
    protected AdvancedSemanticTool(IAdvancedSemanticQueryService service, ToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(definition);
        Service = service;
        _definition = definition;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <summary>Gets the compiler-aware query service.</summary>
    protected IAdvancedSemanticQueryService Service { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<TOutput>> ExecuteAsync(
        TInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var workspaceId = context.Invocation.WorkspaceId
            ?? throw new InvalidOperationException("Advanced semantic inspection requires an opened workspace.");
        var result = await QueryAsync(workspaceId, input, cancellationToken);
        result = Confine(result, context.Invocation);
        return new(
            result,
            [new ToolProvenanceSource("semantic-workspace", workspaceId.Value.ToString("D"))],
            IsTruncated(result),
            CreateModelResultContent(input, result));
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(TInput input, ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }

    /// <summary>Executes one closed advanced semantic query.</summary>
    protected abstract Task<TOutput> QueryAsync(
        WorkspaceId workspaceId,
        TInput input,
        CancellationToken cancellationToken);

    /// <summary>Creates a common read-only semantic tool definition.</summary>
    protected static ToolDefinition CreateDefinition<TRequest, TResult>(string id, string description)
    {
        return ToolDefinitionFactory.Create<TRequest, TResult>(
            id,
            description,
            ToolCategory.SemanticSearch,
            RepositoryTrustLevel.TrustedBuild,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            TimeSpan.FromSeconds(60),
            1024 * 1024);
    }

    /// <summary>Validates common graph limits.</summary>
    protected static void ValidateLimits(SemanticTraversalLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumDepth is < 0 or > 8 || limits.MaximumNodes is < 1 or > 1000
            || limits.MaximumEdges is < 1 or > 5000 || limits.TimeoutMilliseconds is < 1 or > 60_000)
        {
            throw new ToolArgumentValidationException("semantic traversal bounds are outside host limits.");
        }
    }

    private static TOutput Confine(TOutput result, ToolInvocationContext context)
    {
        object confined = result switch
        {
            CallHierarchyResult hierarchy => Confine(hierarchy, context),
            SymbolImpactResult impact => Confine(impact, context),
            CSharpPatternSearchResult pattern => Confine(pattern, context),
            GeneratedCodeResult generated => Confine(generated, context),
            _ => result,
        };
        return (TOutput)confined;
    }

    private static CallHierarchyResult Confine(
        CallHierarchyResult result,
        ToolInvocationContext context)
    {
        CallHierarchyNode[] nodes = [.. result.Nodes
            .Select(node => node with
            {
                Locations = [.. node.Locations.Where(location => IsAllowed(location.FilePath, context))],
            })
            .Where(node => node.Locations.Count > 0)];
        HashSet<string> nodeIds = [.. nodes.Select(node => node.Symbol.Id)];
        CallHierarchyEdge[] edges = [.. result.Edges.Where(edge =>
            nodeIds.Contains(edge.CallerSymbolId)
            && nodeIds.Contains(edge.CalleeSymbolId)
            && (edge.CallSite is null || IsAllowed(edge.CallSite.FilePath, context)))];
        var omitted = nodes.Length != result.Nodes.Count || edges.Length != result.Edges.Count;
        return result with
        {
            Nodes = nodes,
            Edges = edges,
            Traversal = Confine(result.Traversal, nodes.Length, edges.Length, omitted),
        };
    }

    private static SymbolImpactResult Confine(
        SymbolImpactResult result,
        ToolInvocationContext context)
    {
        ImpactNode[] nodes = [.. result.Nodes.Where(node =>
            node.Location is null || IsAllowed(node.Location.FilePath, context))];
        HashSet<string> nodeIds = [.. nodes.Select(node => node.Id)];
        ImpactEdge[] edges = [.. result.Edges.Where(edge =>
            nodeIds.Contains(edge.FromId) && nodeIds.Contains(edge.ToId))];
        var omitted = nodes.Length != result.Nodes.Count || edges.Length != result.Edges.Count;
        return result with
        {
            Nodes = nodes,
            Edges = edges,
            Traversal = Confine(result.Traversal, nodes.Length, edges.Length, omitted),
        };
    }

    private static CSharpPatternSearchResult Confine(
        CSharpPatternSearchResult result,
        ToolInvocationContext context)
    {
        CSharpPatternMatch[] matches = [.. result.Matches.Where(match =>
            IsAllowed(match.Location.FilePath, context))];
        var omitted = matches.Length != result.Matches.Count;
        return result with
        {
            Matches = matches,
            IsComplete = result.IsComplete && !omitted,
            Omissions = AddPolicyOmission(result.Omissions, omitted),
        };
    }

    private static GeneratedCodeResult Confine(
        GeneratedCodeResult result,
        ToolInvocationContext context)
    {
        GeneratedDocumentInfo[] documents = [.. result.Documents.Where(document =>
            IsAllowed(document.FilePath, context))];
        var omitted = documents.Length != result.Documents.Count;
        return result with
        {
            Documents = documents,
            IsComplete = result.IsComplete && !omitted,
            Omissions = AddPolicyOmission(result.Omissions, omitted),
        };
    }

    private static SemanticTraversalSummary Confine(
        SemanticTraversalSummary traversal,
        int nodeCount,
        int edgeCount,
        bool omitted)
    {
        return traversal with
        {
            VisitedNodes = Math.Min(traversal.VisitedNodes, nodeCount),
            ReturnedEdges = edgeCount,
            IsComplete = traversal.IsComplete && !omitted,
            Omissions = AddPolicyOmission(traversal.Omissions, omitted),
        };
    }

    private static IReadOnlyList<string> AddPolicyOmission(
        IReadOnlyList<string> omissions,
        bool omitted)
    {
        return omitted
            ? [.. omissions, "Results outside the invocation path policy were omitted."]
            : omissions;
    }

    private static bool IsAllowed(string path, ToolInvocationContext context)
    {
        try
        {
            _ = ToolPathRules.NormalizeAndValidate(path, context);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? CreateModelResultContent(TInput input, TOutput result)
    {
        return (input, result) switch
        {
            (CallHierarchyInput hierarchyInput, CallHierarchyResult hierarchyResult) =>
                AdvancedSemanticMarkdownRenderer.Render(hierarchyInput, hierarchyResult),
            (SymbolImpactInput impactInput, SymbolImpactResult impactResult) =>
                AdvancedSemanticMarkdownRenderer.Render(impactInput, impactResult),
            (CSharpPatternSearchInput patternInput, CSharpPatternSearchResult patternResult) =>
                AdvancedSemanticMarkdownRenderer.Render(patternInput, patternResult),
            _ => null,
        };
    }

    private static bool IsTruncated(TOutput result)
    {
        return result switch
        {
            CallHierarchyResult hierarchy => !hierarchy.Traversal.IsComplete,
            SymbolImpactResult impact => !impact.Traversal.IsComplete,
            CSharpPatternSearchResult pattern => !pattern.IsComplete,
            GeneratedCodeResult generated => !generated.IsComplete,
            _ => false,
        };
    }
}

/// <summary>Renders compact model-facing output for advanced semantic tools while preserving rich host DTOs.</summary>
internal static class AdvancedSemanticMarkdownRenderer
{
    private const int MaximumCallEdges = 32;
    private const int MaximumCallSymbols = 32;
    private const int MaximumImpactItems = 32;
    private const int MaximumPatternMatches = 40;
    private const int MaximumOmissions = 8;

    /// <summary>Renders one compact call-hierarchy result.</summary>
    internal static string Render(CallHierarchyInput input, CallHierarchyResult result)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        var depth = input.Depth is null ? "host default" : input.Depth.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        builder.AppendLine($"**Call hierarchy:** {FormatCodeSpan(input.SymbolId)} ({input.Direction}, depth {depth})");
        builder.AppendLine(
            $"Found {result.Nodes.Count} symbol{Pluralize(result.Nodes.Count)} and {result.Edges.Count} call relationship{Pluralize(result.Edges.Count)}.");
        var nodes = result.Nodes.ToDictionary(node => node.Symbol.Id, StringComparer.Ordinal);
        if (result.Edges.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("**Calls**");
            foreach (var edge in result.Edges.Take(MaximumCallEdges))
            {
                builder.AppendLine(FormatCallEdge(edge, nodes));
            }

            AppendHiddenCount(builder, result.Edges.Count, MaximumCallEdges, "call relationship");
        }
        else if (result.Nodes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("**Symbols**");
            foreach (var node in result.Nodes.Take(MaximumCallSymbols))
            {
                var location = node.Locations.Count == 0 ? null : node.Locations[0];
                builder.AppendLine(
                    $"- {FormatCodeSpan(node.Symbol.DisplayName)} ({node.Symbol.Kind}, depth {node.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture)}){FormatOptionalLocation(location)}");
            }

            AppendHiddenCount(builder, result.Nodes.Count, MaximumCallSymbols, "symbol");
        }

        AppendOmissions(builder, result.Traversal.Omissions);
        return builder.ToString().TrimEnd();
    }

    /// <summary>Renders one compact symbol-impact result.</summary>
    internal static string Render(SymbolImpactInput input, SymbolImpactResult result)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        builder.AppendLine($"**Symbol impact:** {FormatCodeSpan(input.SymbolId)}");
        builder.AppendLine(
            $"Found {result.Nodes.Count} impact node{Pluralize(result.Nodes.Count)} and {result.Edges.Count} relationship{Pluralize(result.Edges.Count)}.");
        var items = CreateRankedImpactItems(result).ToArray();
        if (items.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("**Ranked impact**");
            var rank = 1;
            foreach (var item in items.Take(MaximumImpactItems))
            {
                builder.AppendLine(
                    $"{rank.ToString(System.Globalization.CultureInfo.InvariantCulture)}. **{item.Node.Kind}:** {FormatCodeSpan(item.Node.DisplayName)}{FormatImpactLocation(item.Node)}{FormatReason(item.Reason)}");
                rank++;
            }

            AppendHiddenCount(builder, items.Length, MaximumImpactItems, "impact item");
        }

        AppendOmissions(builder, result.Traversal.Omissions);
        return builder.ToString().TrimEnd();
    }

    /// <summary>Renders one compact C# pattern-search result.</summary>
    internal static string Render(CSharpPatternSearchInput input, CSharpPatternSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        var scope = string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path;
        var name = string.IsNullOrWhiteSpace(input.Name) ? "*" : input.Name;
        builder.AppendLine($"**C# pattern search:** {FormatCodeSpan(input.Kind.ToString())} {FormatCodeSpan(name)} in {FormatCodeSpan(scope)}");
        builder.AppendLine($"Found {result.Matches.Count} match{Pluralize(result.Matches.Count)}.");
        builder.AppendLine();
        foreach (var match in result.Matches.Take(MaximumPatternMatches))
        {
            builder.Append("- ");
            builder.Append(FormatLocation(match.Location));
            builder.Append(" — ");
            builder.Append(match.Kind);
            builder.AppendLine();
        }

        AppendHiddenCount(builder, result.Matches.Count, MaximumPatternMatches, "match");
        AppendOmissions(builder, result.Omissions);
        return builder.ToString().TrimEnd();
    }

    private static string FormatCallEdge(
        CallHierarchyEdge edge,
        IReadOnlyDictionary<string, CallHierarchyNode> nodes)
    {
        var caller = FormatSymbolName(edge.CallerSymbolId, nodes);
        var callee = FormatSymbolName(edge.CalleeSymbolId, nodes);
        return $"- {FormatCodeSpan(caller)} → {FormatCodeSpan(callee)} ({edge.DispatchKind}){FormatOptionalLocation(edge.CallSite)}{FormatCallFlags(edge)}";
    }

    private static string FormatSymbolName(
        string symbolId,
        IReadOnlyDictionary<string, CallHierarchyNode> nodes)
    {
        return nodes.TryGetValue(symbolId, out var node)
            ? node.Symbol.DisplayName
            : symbolId;
    }

    private static string FormatCallFlags(CallHierarchyEdge edge)
    {
        var flags = new List<string>();
        if (edge.IsAmbiguous)
        {
            flags.Add("ambiguous dispatch");
        }

        if (edge.ClosesCycle)
        {
            flags.Add("cycle");
        }

        return flags.Count == 0 ? string.Empty : " — " + string.Join(", ", flags);
    }

    private static string FormatOptionalLocation(SemanticSourceLocation? location)
    {
        return location is null ? string.Empty : " — " + FormatLocation(location);
    }

    private static IEnumerable<RankedImpactItem> CreateRankedImpactItems(SymbolImpactResult result)
    {
        var reasons = result.Edges
            .GroupBy(edge => edge.ToId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Reason, StringComparer.Ordinal);
        return result.Nodes
            .Where(static node => node.Kind != ImpactKind.RootSymbol)
            .OrderBy(static node => GetImpactRank(node.Kind))
            .ThenBy(static node => node.ProjectName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static node => node.DisplayName, StringComparer.Ordinal)
            .ThenBy(static node => node.Id, StringComparer.Ordinal)
            .Select(node => new RankedImpactItem(
                node,
                reasons.GetValueOrDefault(node.Id)));
    }

    private static int GetImpactRank(ImpactKind kind)
    {
        return kind switch
        {
            ImpactKind.Caller => 0,
            ImpactKind.Implementation => 1,
            ImpactKind.Reference => 2,
            ImpactKind.Test => 3,
            ImpactKind.Project => 4,
            ImpactKind.Diagnostic => 5,
            ImpactKind.GeneratedDocument => 6,
            ImpactKind.LinkedDocument => 7,
            _ => 8,
        };
    }

    private static string FormatImpactLocation(ImpactNode node)
    {
        if (node.Location is { } location)
        {
            return " — " + FormatLocation(location);
        }

        return string.IsNullOrWhiteSpace(node.ProjectName)
            ? string.Empty
            : $" — project {FormatCodeSpan(node.ProjectName)}";
    }

    private static string FormatReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : $" — {BoundInline(reason, 240)}";
    }

    private static void AppendOmissions(StringBuilder builder, IReadOnlyList<string> omissions)
    {
        if (omissions.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("**Omissions**");
        foreach (var omission in omissions.Take(MaximumOmissions))
        {
            builder.AppendLine($"- {BoundInline(omission, 240)}");
        }

        AppendHiddenCount(builder, omissions.Count, MaximumOmissions, "omission");
    }

    private static void AppendHiddenCount(StringBuilder builder, int total, int shown, string noun)
    {
        if (total > shown)
        {
            builder.AppendLine($"- … {total - shown} more {noun}{Pluralize(total - shown)} hidden by the model projection.");
        }
    }

    private static string FormatLocation(SemanticSourceLocation location)
    {
        return $"{FormatCodeSpan(location.FilePath)}:{FormatRange(location.Range)} ({FormatCodeSpan(location.ProjectName)})";
    }

    private static string FormatRange(SourceRange range)
    {
        return range.StartLine == range.EndLine
            ? range.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{range.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{range.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string BoundInline(string value, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var compact = CleanInline(value);
        return compact.Length <= maximumCharacters
            ? compact
            : compact[..Math.Max(0, maximumCharacters - 1)] + "…";
    }

    private static string FormatCodeSpan(string value)
    {
        var clean = CleanInline(value);
        var maximumRun = 0;
        var currentRun = 0;
        foreach (var character in clean)
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
        return clean.StartsWith('`') || clean.EndsWith('`')
            ? $"{delimiter} {clean} {delimiter}"
            : $"{delimiter}{clean}{delimiter}";
    }

    private static string CleanInline(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Pluralize(int count)
    {
        return count == 1 ? string.Empty : "s";
    }

    private sealed record RankedImpactItem(ImpactNode Node, string? Reason);
}
