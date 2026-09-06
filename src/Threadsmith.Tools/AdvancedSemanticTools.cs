namespace Threadsmith.Tools;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Returns a bounded compiler-aware incoming/outgoing call hierarchy.</summary>
public sealed class CallHierarchyTool : AdvancedSemanticTool<CallHierarchyInput, CallHierarchyResult>
{
    /// <summary>Initializes a new instance of the <see cref="CallHierarchyTool"/> class.</summary>
    public CallHierarchyTool(IAdvancedSemanticQueryService service, IPromptLoader promptLoader)
        : base(
            service,
            CreateDefinition<CallHierarchyInput, CallHierarchyResult>(
                "call_hierarchy",
                promptLoader,
                PromptFileNames.ToolCallHierarchyDescription) with
            {
                PreferStrictArguments = true,
            },
            promptLoader)
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
    /// <summary>Initializes a new instance of the <see cref="SymbolImpactTool"/> class.</summary>
    public SymbolImpactTool(IAdvancedSemanticQueryService service, IPromptLoader promptLoader)
        : base(
            service,
            CreateDefinition<SymbolImpactInput, SymbolImpactResult>(
                "symbol_impact",
                promptLoader,
                PromptFileNames.ToolSymbolImpactDescription) with
            {
                PreferStrictArguments = true,
            },
            promptLoader)
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
    private readonly IPromptLoader _promptLoader;

    /// <summary>Initializes a new instance of the <see cref="CSharpPatternSearchTool"/> class.</summary>
    public CSharpPatternSearchTool(IAdvancedSemanticQueryService service, IPromptLoader promptLoader)
        : base(service, CreatePatternDefinition(promptLoader), promptLoader)
    {
        ArgumentNullException.ThrowIfNull(promptLoader);
        _promptLoader = promptLoader;
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
            ? _promptLoader.Get(PromptFileNames.CorrectionCsharpPatternSearchKindSchemaMismatch)
            : base.CreateSchemaMismatchMessage(exception, argumentsJson);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        CSharpPatternSearchInput input,
        ToolInvocationContext context)
    {
        return [input.Path ?? context.RepositoryPath];
    }

    private static ToolDefinition CreatePatternDefinition(IPromptLoader promptLoader)
    {
        var definition = CreateDefinition<CSharpPatternSearchInput, CSharpPatternSearchResult>(
            "csharp_pattern_search",
            promptLoader,
            PromptFileNames.ToolCsharpPatternSearchDescription);
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
    /// <summary>Initializes a new instance of the <see cref="GeneratedCodeTool"/> class.</summary>
    public GeneratedCodeTool(IAdvancedSemanticQueryService service, IPromptLoader promptLoader)
        : base(
            service,
            CreateDefinition<GeneratedCodeQuery, GeneratedCodeResult>(
                "generated_code_query",
                promptLoader,
                PromptFileNames.ToolGeneratedCodeQueryDescription),
            promptLoader)
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
    private readonly IPromptLoader _promptLoader;

    /// <summary>Initializes a new instance of the <see cref="AdvancedSemanticTool{TInput, TOutput}"/> class.</summary>
    protected AdvancedSemanticTool(
        IAdvancedSemanticQueryService service,
        ToolDefinition definition,
        IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(promptLoader);
        Service = service;
        _definition = definition;
        _promptLoader = promptLoader;
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
        result = Confine(result, context.Invocation, _promptLoader);
        return new(
            result,
            [new ToolProvenanceSource("semantic-workspace", workspaceId.Value.ToString("D"))],
            IsTruncated(result),
            CreateModelResultContent(input, result, _promptLoader));
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
    protected static ToolDefinition CreateDefinition<TRequest, TResult>(
        string id,
        IPromptLoader promptLoader,
        string promptFileName)
    {
        ArgumentNullException.ThrowIfNull(promptLoader);
        return ToolDefinitionFactory.Create<TRequest, TResult>(
            id,
            promptLoader.Get(promptFileName),
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

    private static TOutput Confine(
        TOutput result,
        ToolInvocationContext context,
        IPromptLoader promptLoader)
    {
        object confined = result switch
        {
            CallHierarchyResult hierarchy => Confine(hierarchy, context, promptLoader),
            SymbolImpactResult impact => Confine(impact, context, promptLoader),
            CSharpPatternSearchResult pattern => Confine(pattern, context, promptLoader),
            GeneratedCodeResult generated => Confine(generated, context, promptLoader),
            _ => result,
        };
        return (TOutput)confined;
    }

    private static CallHierarchyResult Confine(
        CallHierarchyResult result,
        ToolInvocationContext context,
        IPromptLoader promptLoader)
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
            Traversal = Confine(result.Traversal, nodes.Length, edges.Length, omitted, promptLoader),
        };
    }

    private static SymbolImpactResult Confine(
        SymbolImpactResult result,
        ToolInvocationContext context,
        IPromptLoader promptLoader)
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
            Traversal = Confine(result.Traversal, nodes.Length, edges.Length, omitted, promptLoader),
        };
    }

    private static CSharpPatternSearchResult Confine(
        CSharpPatternSearchResult result,
        ToolInvocationContext context,
        IPromptLoader promptLoader)
    {
        CSharpPatternMatch[] matches = [.. result.Matches.Where(match =>
            IsAllowed(match.Location.FilePath, context))];
        var omitted = matches.Length != result.Matches.Count;
        return result with
        {
            Matches = matches,
            IsComplete = result.IsComplete && !omitted,
            Omissions = AddPolicyOmission(result.Omissions, omitted, promptLoader),
        };
    }

    private static GeneratedCodeResult Confine(
        GeneratedCodeResult result,
        ToolInvocationContext context,
        IPromptLoader promptLoader)
    {
        GeneratedDocumentInfo[] documents = [.. result.Documents.Where(document =>
            IsAllowed(document.FilePath, context))];
        var omitted = documents.Length != result.Documents.Count;
        return result with
        {
            Documents = documents,
            IsComplete = result.IsComplete && !omitted,
            Omissions = AddPolicyOmission(result.Omissions, omitted, promptLoader),
        };
    }

    private static SemanticTraversalSummary Confine(
        SemanticTraversalSummary traversal,
        int nodeCount,
        int edgeCount,
        bool omitted,
        IPromptLoader promptLoader)
    {
        return traversal with
        {
            VisitedNodes = Math.Min(traversal.VisitedNodes, nodeCount),
            ReturnedEdges = edgeCount,
            IsComplete = traversal.IsComplete && !omitted,
            Omissions = AddPolicyOmission(traversal.Omissions, omitted, promptLoader),
        };
    }

    private static IReadOnlyList<string> AddPolicyOmission(
        IReadOnlyList<string> omissions,
        bool omitted,
        IPromptLoader promptLoader)
    {
        return omitted
            ? [.. omissions, GetPromptValue(promptLoader, PromptFileNames.ToolAdvancedSemanticPathPolicyOmission)]
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

    private static string? CreateModelResultContent(
        TInput input,
        TOutput result,
        IPromptLoader promptLoader)
    {
        return (input, result) switch
        {
            (CallHierarchyInput hierarchyInput, CallHierarchyResult hierarchyResult) =>
                AdvancedSemanticMarkdownRenderer.Render(hierarchyInput, hierarchyResult, promptLoader),
            (SymbolImpactInput impactInput, SymbolImpactResult impactResult) =>
                AdvancedSemanticMarkdownRenderer.Render(impactInput, impactResult, promptLoader),
            (CSharpPatternSearchInput patternInput, CSharpPatternSearchResult patternResult) =>
                AdvancedSemanticMarkdownRenderer.Render(patternInput, patternResult, promptLoader),
            (GeneratedCodeQuery generatedInput, GeneratedCodeResult generatedResult) =>
                AdvancedSemanticMarkdownRenderer.Render(generatedInput, generatedResult, promptLoader),
            _ => null,
        };
    }

    private static string GetPromptValue(IPromptLoader promptLoader, string promptFileName)
    {
        return promptLoader.Get(promptFileName).TrimEnd('\r', '\n');
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
    private const int MaximumGeneratedDocuments = 12;
    private const int MaximumGeneratedContentCharacters = 4_096;
    private const int MaximumOmissions = 8;

    /// <summary>Renders one compact call-hierarchy result.</summary>
    internal static string Render(
        CallHierarchyInput input,
        CallHierarchyResult result,
        IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(promptLoader);
        var builder = new StringBuilder();
        var depth = input.Depth is null
            ? "host default"
            : input.Depth.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AppendPromptBlock(
            builder,
            promptLoader.Render(
                PromptFileNames.ToolCallHierarchyResultHeader,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SymbolId"] = FormatCodeSpan(input.SymbolId),
                    ["Direction"] = input.Direction.ToString(),
                    ["Depth"] = depth,
                    ["SymbolCount"] = result.Nodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["SymbolPlural"] = Pluralize(result.Nodes.Count),
                    ["RelationshipCount"] = result.Edges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["RelationshipPlural"] = Pluralize(result.Edges.Count),
                }));
        var nodes = result.Nodes.ToDictionary(node => node.Symbol.Id, StringComparer.Ordinal);
        if (result.Edges.Count > 0)
        {
            builder.AppendLine();
            var items = result.Edges
                .Take(MaximumCallEdges)
                .Select(edge => FormatCallEdge(edge, nodes, promptLoader));
            AppendPromptBlock(
                builder,
                promptLoader.Render(
                    PromptFileNames.ToolCallHierarchyCallsSection,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Items"] = string.Join(Environment.NewLine, items),
                    }));
            AppendHiddenCount(
                builder,
                promptLoader,
                result.Edges.Count,
                MaximumCallEdges,
                PromptFileNames.ToolCallHierarchyHiddenCallRelationships);
        }
        else if (result.Nodes.Count > 0)
        {
            builder.AppendLine();
            var items = result.Nodes
                .Take(MaximumCallSymbols)
                .Select(node =>
                {
                    var location = node.Locations.Count == 0 ? null : node.Locations[0];
                    return TrimPromptValue(promptLoader.Render(
                        PromptFileNames.ToolCallHierarchySymbolItem,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["SymbolName"] = FormatCodeSpan(node.Symbol.DisplayName),
                            ["SymbolKind"] = node.Symbol.Kind,
                            ["Depth"] = node.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["Location"] = FormatOptionalLocation(location),
                        }));
                });
            AppendPromptBlock(
                builder,
                promptLoader.Render(
                    PromptFileNames.ToolCallHierarchySymbolsSection,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Items"] = string.Join(Environment.NewLine, items),
                    }));
            AppendHiddenCount(
                builder,
                promptLoader,
                result.Nodes.Count,
                MaximumCallSymbols,
                PromptFileNames.ToolCallHierarchyHiddenSymbols);
        }

        AppendOmissions(builder, result.Traversal.Omissions, promptLoader);
        return builder.ToString().TrimEnd();
    }

    /// <summary>Renders one compact symbol-impact result.</summary>
    internal static string Render(
        SymbolImpactInput input,
        SymbolImpactResult result,
        IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(promptLoader);
        var builder = new StringBuilder();
        AppendPromptBlock(
            builder,
            promptLoader.Render(
                PromptFileNames.ToolSymbolImpactResultHeader,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SymbolId"] = FormatCodeSpan(input.SymbolId),
                    ["NodeCount"] = result.Nodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["NodePlural"] = Pluralize(result.Nodes.Count),
                    ["RelationshipCount"] = result.Edges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["RelationshipPlural"] = Pluralize(result.Edges.Count),
                }));
        var items = CreateRankedImpactItems(result).ToArray();
        if (items.Length > 0)
        {
            builder.AppendLine();
            var rank = 1;
            var rows = new List<string>();
            foreach (var item in items.Take(MaximumImpactItems))
            {
                rows.Add(
                    $"{rank.ToString(System.Globalization.CultureInfo.InvariantCulture)}. **{item.Node.Kind}:** {FormatCodeSpan(item.Node.DisplayName)}{FormatImpactLocation(item.Node)}{FormatReason(item.Reason)}");
                rank++;
            }

            AppendPromptBlock(
                builder,
                promptLoader.Render(
                    PromptFileNames.ToolSymbolImpactRankedSection,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Items"] = string.Join(Environment.NewLine, rows),
                    }));
            AppendHiddenCount(
                builder,
                promptLoader,
                items.Length,
                MaximumImpactItems,
                PromptFileNames.ToolSymbolImpactHiddenItems);
        }

        AppendOmissions(builder, result.Traversal.Omissions, promptLoader);
        return builder.ToString().TrimEnd();
    }

    /// <summary>Renders one compact C# pattern-search result.</summary>
    internal static string Render(
        CSharpPatternSearchInput input,
        CSharpPatternSearchResult result,
        IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(promptLoader);
        var builder = new StringBuilder();
        var scope = string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path;
        var name = string.IsNullOrWhiteSpace(input.Name) ? "*" : input.Name;
        AppendPromptBlock(
            builder,
            promptLoader.Render(
                PromptFileNames.ToolCsharpPatternSearchResultHeader,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Kind"] = FormatCodeSpan(input.Kind.ToString()),
                    ["Name"] = FormatCodeSpan(name),
                    ["Scope"] = FormatCodeSpan(scope),
                    ["MatchCount"] = result.Matches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["MatchPlural"] = Pluralize(result.Matches.Count),
                }));
        builder.AppendLine();
        foreach (var match in result.Matches.Take(MaximumPatternMatches))
        {
            builder.Append("- ");
            builder.Append(FormatLocation(match.Location));
            builder.Append(" — ");
            builder.Append(match.Kind);
            builder.AppendLine();
        }

        AppendHiddenCount(
            builder,
            promptLoader,
            result.Matches.Count,
            MaximumPatternMatches,
            PromptFileNames.ToolCsharpPatternSearchHiddenMatches);
        AppendOmissions(builder, result.Omissions, promptLoader);
        return builder.ToString().TrimEnd();
    }

    /// <summary>Renders one compact generated-code result.</summary>
    internal static string Render(
        GeneratedCodeQuery input,
        GeneratedCodeResult result,
        IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(promptLoader);
        var builder = new StringBuilder();
        var scope = string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path;
        AppendPromptBlock(
            builder,
            promptLoader.Render(
                PromptFileNames.ToolGeneratedCodeQueryResultHeader,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Scope"] = FormatCodeSpan(scope),
                    ["DocumentCount"] = result.Documents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["DocumentPlural"] = Pluralize(result.Documents.Count),
                }));
        if (result.Documents.Count > 0)
        {
            builder.AppendLine();
            foreach (var document in result.Documents.Take(MaximumGeneratedDocuments))
            {
                builder.Append($"- {FormatCodeSpan(document.FilePath)} ({document.Origin}, {FormatCodeSpan(document.ProjectName)})");
                if (!string.IsNullOrWhiteSpace(document.OriginName))
                {
                    builder.Append($" — {BoundInline(document.OriginName, 160)}");
                }

                builder.AppendLine();
                if (input.IncludeContent && document.Content is not null)
                {
                    var projectedContent = document.Content.Length <= MaximumGeneratedContentCharacters
                        ? document.Content
                        : document.Content[..MaximumGeneratedContentCharacters];
                    AppendCodeBlock(builder, projectedContent);
                    if (document.ContentTruncated)
                    {
                        AppendPromptBlock(
                            builder,
                            promptLoader.Get(PromptFileNames.ToolGeneratedCodeQueryContentHostTruncation));
                    }

                    if (projectedContent.Length < document.Content.Length)
                    {
                        AppendPromptBlock(
                            builder,
                            promptLoader.Get(PromptFileNames.ToolGeneratedCodeQueryContentProjectionTruncation));
                    }
                }
            }

            AppendHiddenCount(
                builder,
                promptLoader,
                result.Documents.Count,
                MaximumGeneratedDocuments,
                PromptFileNames.ToolGeneratedCodeQueryHiddenDocuments);
        }

        AppendOmissions(builder, result.Omissions, promptLoader);
        return builder.ToString().TrimEnd();
    }

    private static void AppendCodeBlock(StringBuilder builder, string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var maximumRun = 0;
        var currentRun = 0;
        foreach (var character in normalized)
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

        var delimiter = new string('`', Math.Max(3, maximumRun + 1));
        builder.Append("  ");
        builder.Append(delimiter);
        builder.AppendLine("csharp");
        foreach (var line in normalized.Split('\n'))
        {
            builder.Append("  ");
            builder.AppendLine(line);
        }

        builder.Append("  ");
        builder.AppendLine(delimiter);
    }

    private static string FormatCallEdge(
        CallHierarchyEdge edge,
        IReadOnlyDictionary<string, CallHierarchyNode> nodes,
        IPromptLoader promptLoader)
    {
        var caller = FormatSymbolName(edge.CallerSymbolId, nodes);
        var callee = FormatSymbolName(edge.CalleeSymbolId, nodes);
        return $"- {FormatCodeSpan(caller)} → {FormatCodeSpan(callee)} ({edge.DispatchKind}){FormatOptionalLocation(edge.CallSite)}{FormatCallFlags(edge, promptLoader)}";
    }

    private static string FormatSymbolName(
        string symbolId,
        IReadOnlyDictionary<string, CallHierarchyNode> nodes)
    {
        return nodes.TryGetValue(symbolId, out var node)
            ? node.Symbol.DisplayName
            : symbolId;
    }

    private static string FormatCallFlags(CallHierarchyEdge edge, IPromptLoader promptLoader)
    {
        var flags = new List<string>();
        if (edge.IsAmbiguous)
        {
            flags.Add(TrimPromptValue(promptLoader.Get(
                PromptFileNames.ToolCallHierarchyCallFlagAmbiguousDispatch)));
        }

        if (edge.ClosesCycle)
        {
            flags.Add(TrimPromptValue(promptLoader.Get(
                PromptFileNames.ToolCallHierarchyCallFlagCycle)));
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

    private static void AppendOmissions(
        StringBuilder builder,
        IReadOnlyList<string> omissions,
        IPromptLoader promptLoader)
    {
        if (omissions.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        var items = omissions
            .Take(MaximumOmissions)
            .Select(omission => $"- {BoundInline(omission, 240)}");
        AppendPromptBlock(
            builder,
            promptLoader.Render(
                PromptFileNames.ToolAdvancedSemanticOmissionsSection,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Items"] = string.Join(Environment.NewLine, items),
                }));
        AppendHiddenCount(
            builder,
            promptLoader,
            omissions.Count,
            MaximumOmissions,
            PromptFileNames.ToolAdvancedSemanticHiddenOmissions);
    }

    private static void AppendHiddenCount(
        StringBuilder builder,
        IPromptLoader promptLoader,
        int total,
        int shown,
        string promptFileName)
    {
        if (total > shown)
        {
            var hiddenCount = total - shown;
            AppendPromptBlock(
                builder,
                promptLoader.Render(
                    promptFileName,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["HiddenCount"] = hiddenCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["Plural"] = Pluralize(hiddenCount),
                    }));
        }
    }

    private static void AppendPromptBlock(StringBuilder builder, string content)
    {
        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        foreach (var line in normalized.Split('\n'))
        {
            builder.AppendLine(line);
        }
    }

    private static string TrimPromptValue(string value)
    {
        return value.TrimEnd('\r', '\n');
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
