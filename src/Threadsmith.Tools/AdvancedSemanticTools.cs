namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Returns a bounded compiler-aware incoming/outgoing call hierarchy.</summary>
public sealed class CallHierarchyTool : AdvancedSemanticTool<CallHierarchyRequest, CallHierarchyResult>
{
    private static readonly ToolDefinition _definition = CreateDefinition<CallHierarchyRequest, CallHierarchyResult>(
        "call_hierarchy",
        "Primary compiler-aware tool for incoming/outgoing C# call relationships. MUST use before search and fall back only if this tool fails or reports incomplete evidence.");

    /// <summary>Initializes a new instance of the <see cref="CallHierarchyTool"/> class.</summary>
    public CallHierarchyTool(IAdvancedSemanticQueryService service)
        : base(service, _definition)
    {
    }

    /// <inheritdoc />
    protected override Task<CallHierarchyResult> QueryAsync(
        WorkspaceId workspaceId,
        CallHierarchyRequest input,
        CancellationToken cancellationToken)
    {
        return Service.QueryCallHierarchyAsync(workspaceId, input, cancellationToken);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(CallHierarchyRequest input)
    {
        return $"{input.Direction}: {input.SymbolId}";
    }

    /// <inheritdoc />
    protected override void ValidateInput(CallHierarchyRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SymbolId);
        ValidateLimits(input.Limits);
    }
}

/// <summary>Returns an explainable bounded impact graph for a semantic symbol.</summary>
public sealed class SymbolImpactTool : AdvancedSemanticTool<SymbolImpactRequest, SymbolImpactResult>
{
    private static readonly ToolDefinition _definition = CreateDefinition<SymbolImpactRequest, SymbolImpactResult>(
        "symbol_impact",
        "Primary compiler-aware tool for bounded reference, caller, implementation, project, test, and classified-source impact. MUST use before search and fall back only if this tool fails or reports incomplete evidence.");

    /// <summary>Initializes a new instance of the <see cref="SymbolImpactTool"/> class.</summary>
    public SymbolImpactTool(IAdvancedSemanticQueryService service)
        : base(service, _definition)
    {
    }

    /// <inheritdoc />
    protected override Task<SymbolImpactResult> QueryAsync(
        WorkspaceId workspaceId,
        SymbolImpactRequest input,
        CancellationToken cancellationToken)
    {
        return Service.QuerySymbolImpactAsync(workspaceId, input, cancellationToken);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(SymbolImpactRequest input)
    {
        return input.SymbolId;
    }

    /// <inheritdoc />
    protected override void ValidateInput(SymbolImpactRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SymbolId);
        ValidateLimits(input.Limits);
    }
}

/// <summary>Searches C# syntax with a closed inert versioned pattern schema.</summary>
public sealed class CSharpPatternSearchTool : AdvancedSemanticTool<CSharpPatternSearchRequest, CSharpPatternSearchResult>
{
    private static readonly ToolDefinition _definition = CreateDefinition<CSharpPatternSearchRequest, CSharpPatternSearchResult>(
        "csharp_pattern_search",
        "Primary semantic tool for C# declaration and expression shapes when an exact symbol query is insufficient. MUST use before search and fall back only if this tool fails or reports incomplete evidence.");

    /// <summary>Initializes a new instance of the <see cref="CSharpPatternSearchTool"/> class.</summary>
    public CSharpPatternSearchTool(IAdvancedSemanticQueryService service)
        : base(service, _definition)
    {
    }

    /// <inheritdoc />
    protected override Task<CSharpPatternSearchResult> QueryAsync(
        WorkspaceId workspaceId,
        CSharpPatternSearchRequest input,
        CancellationToken cancellationToken)
    {
        return Service.SearchCSharpPatternAsync(workspaceId, input, cancellationToken);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(CSharpPatternSearchRequest input)
    {
        var scope = string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path;
        var name = string.IsNullOrWhiteSpace(input.Pattern.Name) ? "*" : input.Pattern.Name;
        return $"{input.Pattern.Kind} {name} in {scope}";
    }

    /// <inheritdoc />
    protected override void ValidateInput(CSharpPatternSearchRequest input)
    {
        ArgumentNullException.ThrowIfNull(input.Pattern);
        if (input.Pattern.Version != 1 || input.MaximumMatches is < 1 or > 1000
            || input.TimeoutMilliseconds is < 1 or > 60_000)
        {
            throw new ToolArgumentValidationException("pattern version or result/time bounds are unsupported.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        CSharpPatternSearchRequest input,
        ToolInvocationContext context)
    {
        return [input.Path ?? context.RepositoryPath];
    }
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
        WorkspaceId workspaceId = context.Invocation.WorkspaceId
            ?? throw new InvalidOperationException("Advanced semantic inspection requires an opened workspace.");
        TOutput result = await QueryAsync(workspaceId, input, cancellationToken);
        result = Confine(result, context.Invocation);
        return new(
            result,
            [new ToolProvenanceSource("semantic-workspace", workspaceId.Value.ToString("D"))],
            IsTruncated(result));
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
