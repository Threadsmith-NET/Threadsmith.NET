namespace Threadsmith.DotNet;

using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Threadsmith.Core;

/// <summary>Runs bounded advanced C# queries against snapshots from the existing semantic workspace.</summary>
public sealed class AdvancedSemanticQueryService : IAdvancedSemanticQueryService
{
    private static readonly HashSet<string> AllowedModifiers = new(
        ["public", "private", "protected", "internal", "static", "abstract", "virtual", "override", "sealed", "partial", "async", "readonly", "required", "unsafe", "extern", "new"],
        StringComparer.Ordinal);

    private readonly SemanticEngineRegistry _registry;

    /// <summary>Initializes a new instance of the <see cref="AdvancedSemanticQueryService"/> class.</summary>
    public AdvancedSemanticQueryService(SemanticEngineRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public async Task<CallHierarchyResult> QueryCallHierarchyAsync(
        WorkspaceId workspaceId,
        CallHierarchyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSymbolId(request.SymbolId);
        ValidateLimits(request.Limits);
        var engine = _registry.GetEngine(workspaceId);
        var snapshot = engine.CaptureAdvancedSnapshot();
        var root = await ResolveSymbolAsync(snapshot.Solution, request.SymbolId, cancellationToken);
        var projection = new SemanticSourceProjection(snapshot.Solution, cancellationToken);
        using var timeout = CreateTimeout(request.Limits.TimeoutMilliseconds, cancellationToken);
        var nodes = new Dictionary<string, CallHierarchyNode>(StringComparer.Ordinal);
        var edges = new List<CallHierarchyEdge>();
        var pending = new Queue<(ISymbol Symbol, int Depth)>();
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        AddNode(nodes, root, 0, snapshot, projection);
        pending.Enqueue((root, 0));
        var depthReached = false;
        var nodeReached = false;
        var edgeReached = false;
        var timeReached = false;

        try
        {
            while (pending.Count > 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                (var symbol, var depth) = pending.Dequeue();
                var symbolId = CreateIdentity(symbol).Id;
                if (!expanded.Add(symbolId))
                {
                    continue;
                }

                if (depth > request.Limits.MaximumDepth)
                {
                    depthReached = true;
                    continue;
                }

                var discovered = new List<(ISymbol Caller, ISymbol Callee, Location? Site)>();
                if (request.Direction is CallHierarchyDirection.Incoming or CallHierarchyDirection.Both)
                {
                    var callers = await SymbolFinder.FindCallersAsync(
                        symbol,
                        snapshot.Solution,
                        timeout.Token);
                    discovered.AddRange(callers.SelectMany(
                        caller => caller.Locations.DefaultIfEmpty(),
                        (caller, location) => (caller.CallingSymbol, symbol, location)));
                }

                if (request.Direction is CallHierarchyDirection.Outgoing or CallHierarchyDirection.Both)
                {
                    discovered.AddRange(await FindOutgoingAsync(symbol, snapshot.Solution, timeout.Token));
                }

                foreach ((var caller, var callee, var site) in discovered
                    .OrderBy(item => CreateIdentity(item.Caller).Id, StringComparer.Ordinal)
                    .ThenBy(item => CreateIdentity(item.Callee).Id, StringComparer.Ordinal)
                    .ThenBy(item => item.Site?.SourceSpan.Start ?? -1))
                {
                    if (edges.Count >= request.Limits.MaximumEdges)
                    {
                        edgeReached = true;
                        break;
                    }

                    var callerId = CreateIdentity(caller).Id;
                    var calleeId = CreateIdentity(callee).Id;
                    var traversedEndpoint = callerId == symbolId ? callee : caller;
                    var traversedEndpointId = CreateIdentity(traversedEndpoint).Id;
                    var cycle = expanded.Contains(traversedEndpointId);
                    ISymbol[] endpoints = [caller, callee];
                    ISymbol[] missingEndpoints = [.. endpoints
                        .Where(endpoint => !nodes.ContainsKey(CreateIdentity(endpoint).Id))
                        .Distinct(SymbolEqualityComparer.Default)];
                    if (nodes.Count + missingEndpoints.Length > request.Limits.MaximumNodes)
                    {
                        nodeReached = true;
                        break;
                    }

                    var callSite = site is null
                        ? null
                        : await CreateLocationAsync(snapshot, projection, site, timeout.Token);
                    foreach (var endpoint in missingEndpoints)
                    {
                        AddNode(nodes, endpoint, depth + 1, snapshot, projection);
                    }

                    edges.Add(new CallHierarchyEdge(
                        callerId,
                        calleeId,
                        ClassifyDispatch(callee),
                        callSite,
                        IsAmbiguousDispatch(callee),
                        cycle));
                    if (depth < request.Limits.MaximumDepth && !expanded.Contains(traversedEndpointId))
                    {
                        pending.Enqueue((traversedEndpoint, depth + 1));
                    }
                    else if (depth >= request.Limits.MaximumDepth)
                    {
                        depthReached = true;
                    }
                }

                if (edgeReached || nodeReached)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            timeReached = true;
        }

        EnsureCurrent(engine, snapshot.Generation);
        var omissions = BuildOmissions(depthReached, nodeReached, edgeReached, timeReached);
        return new CallHierarchyResult(
            snapshot.Generation,
            snapshot.Confidence,
            nodes.Values.OrderBy(node => node.Depth).ThenBy(node => node.Symbol.Id, StringComparer.Ordinal).ToArray(),
            edges,
            new SemanticTraversalSummary(
                expanded.Count,
                edges.Count,
                omissions.Length == 0,
                depthReached,
                nodeReached,
                edgeReached,
                timeReached,
                omissions));
    }

    /// <inheritdoc />
    public async Task<SymbolImpactResult> QuerySymbolImpactAsync(
        WorkspaceId workspaceId,
        SymbolImpactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSymbolId(request.SymbolId);
        ValidateLimits(request.Limits);
        var engine = _registry.GetEngine(workspaceId);
        var snapshot = engine.CaptureAdvancedSnapshot();
        var root = await ResolveSymbolAsync(snapshot.Solution, request.SymbolId, cancellationToken);
        var projection = new SemanticSourceProjection(snapshot.Solution, cancellationToken);
        using var timeout = CreateTimeout(request.Limits.TimeoutMilliseconds, cancellationToken);
        var rootIdentity = CreateIdentity(root);
        var nodes = new Dictionary<string, ImpactNode>(StringComparer.Ordinal)
        {
            [rootIdentity.Id] = new(rootIdentity.Id, rootIdentity.DisplayName, ImpactKind.RootSymbol, null, null),
        };
        var edges = new List<ImpactEdge>();
        var omissions = new List<string>();
        var depthReached = false;
        var nodeReached = false;
        var edgeReached = false;
        var timeReached = false;

        try
        {
            var references = await SymbolFinder.FindReferencesAsync(root, snapshot.Solution, timeout.Token);
            foreach (var reference in references.SelectMany(item => item.Locations))
            {
                var location = await CreateLocationAsync(snapshot, projection, reference.Location, timeout.Token);
                var referenceId = location is null
                    ? string.Empty
                    : $"reference:{location.FilePath}:{location.Range.StartLine}:{location.Range.StartColumn}";
                if (location is null || !TryAddImpact(
                    nodes,
                    edges,
                    request.Limits,
                    rootIdentity.Id,
                    referenceId,
                    rootIdentity.DisplayName,
                    ImpactKind.Reference,
                    location,
                    location.ProjectName,
                    "Source references the selected symbol."))
                {
                    UpdateImpactBounds(nodes, edges, request.Limits, ref nodeReached, ref edgeReached);
                    omissions.Add("Reference results exceeded the graph bounds or could not be projected.");
                    break;
                }
            }

            var implementations = await SymbolFinder.FindImplementationsAsync(
                root,
                snapshot.Solution,
                cancellationToken: timeout.Token);
            foreach (var implementation in implementations)
            {
                var identity = CreateIdentity(implementation);
                var location = await FirstLocationAsync(snapshot, projection, implementation, timeout.Token);
                if (!TryAddImpact(
                    nodes,
                    edges,
                    request.Limits,
                    rootIdentity.Id,
                    identity.Id,
                    identity.DisplayName,
                    ImpactKind.Implementation,
                    location,
                    location?.ProjectName,
                    "Symbol implements or overrides the selected contract."))
                {
                    UpdateImpactBounds(nodes, edges, request.Limits, ref nodeReached, ref edgeReached);
                    omissions.Add("Implementation results exceeded the graph bounds.");
                    break;
                }
            }

            var callers = await SymbolFinder.FindCallersAsync(root, snapshot.Solution, timeout.Token);
            foreach (var caller in callers)
            {
                var identity = CreateIdentity(caller.CallingSymbol);
                var location = await FirstLocationAsync(snapshot, projection, caller.CallingSymbol, timeout.Token);
                if (!TryAddImpact(
                    nodes,
                    edges,
                    request.Limits,
                    rootIdentity.Id,
                    identity.Id,
                    identity.DisplayName,
                    ImpactKind.Caller,
                    location,
                    location?.ProjectName,
                    "Caller directly invokes the selected symbol."))
                {
                    UpdateImpactBounds(nodes, edges, request.Limits, ref nodeReached, ref edgeReached);
                    omissions.Add("Caller results exceeded the graph bounds.");
                    break;
                }
            }

            var projectDepths = nodes.Values
                .Select(node => node.ProjectName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
            var pendingProjects = new Queue<(string ProjectName, int Depth)>(projectDepths
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => (item.Key, item.Value)));
            var projectBoundsReached = false;
            while (pendingProjects.Count > 0 && !projectBoundsReached)
            {
                timeout.Token.ThrowIfCancellationRequested();
                (var impactedProject, var depth) = pendingProjects.Dequeue();
                Project[] dependents = [.. snapshot.Solution.Projects
                    .Where(project => project.ProjectReferences.Any(reference =>
                        string.Equals(snapshot.Solution.GetProject(reference.ProjectId)?.Name, impactedProject, StringComparison.Ordinal)))
                    .OrderBy(project => project.Name, StringComparer.Ordinal)];
                if (depth >= request.Limits.MaximumDepth)
                {
                    depthReached |= dependents.Any(project => !projectDepths.ContainsKey(project.Name));
                    continue;
                }

                foreach (var project in dependents.Where(project => !projectDepths.ContainsKey(project.Name)))
                {
                    var kind = IsTestProject(project) ? ImpactKind.Test : ImpactKind.Project;
                    var id = $"project:{project.Id.Id:D}";
                    var reason = kind == ImpactKind.Test
                        ? "Test project depends on an impacted project."
                        : "Project depends on an impacted project.";
                    if (!TryAddImpact(
                        nodes,
                        edges,
                        request.Limits,
                        rootIdentity.Id,
                        id,
                        project.Name,
                        kind,
                        null,
                        project.Name,
                        reason))
                    {
                        UpdateImpactBounds(nodes, edges, request.Limits, ref nodeReached, ref edgeReached);
                        omissions.Add("Dependent project/test results exceeded the graph bounds.");
                        projectBoundsReached = true;
                        break;
                    }

                    var projectDepth = depth + 1;
                    projectDepths.Add(project.Name, projectDepth);
                    pendingProjects.Enqueue((project.Name, projectDepth));
                }
            }

            foreach (var node in nodes.Values.Where(node => node.Location is { IsGenerated: true } or { IsLinked: true }).ToArray())
            {
                var location = node.Location
                    ?? throw new InvalidOperationException("A classified impact node requires a source location.");
                var kind = location.IsGenerated ? ImpactKind.GeneratedDocument : ImpactKind.LinkedDocument;
                var id = $"{kind}:{location.FilePath}";
                var reason = location.IsGenerated
                    ? "Impacted symbol evidence is generated source."
                    : "Impacted symbol evidence is linked source.";
                if (!TryAddImpact(
                    nodes,
                    edges,
                    request.Limits,
                    node.Id,
                    id,
                    Path.GetFileName(location.FilePath),
                    kind,
                    location,
                    location.ProjectName,
                    reason))
                {
                    UpdateImpactBounds(nodes, edges, request.Limits, ref nodeReached, ref edgeReached);
                    omissions.Add("Generated or linked document results exceeded the graph bounds.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            timeReached = true;
            omissions.Add("The traversal time limit was reached.");
        }

        omissions.Add("Runtime reflection, dynamic dispatch, execution traces, and diagnostics outside the loaded semantic snapshot are not inferred.");
        EnsureCurrent(engine, snapshot.Generation);
        if (depthReached)
        {
            omissions.Add("The traversal depth limit was reached.");
        }

        return new SymbolImpactResult(
            snapshot.Generation,
            snapshot.Confidence,
            nodes.Values.OrderBy(node => node.Kind).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray(),
            edges.OrderBy(edge => edge.Kind).ThenBy(edge => edge.ToId, StringComparer.Ordinal).ToArray(),
            new SemanticTraversalSummary(
                nodes.Count,
                edges.Count,
                !depthReached && !nodeReached && !edgeReached && !timeReached,
                depthReached,
                nodeReached,
                edgeReached,
                timeReached,
                omissions.Distinct(StringComparer.Ordinal).ToArray()));
    }

    /// <inheritdoc />
    public async Task<CSharpPatternSearchResult> SearchCSharpPatternAsync(
        WorkspaceId workspaceId,
        CSharpPatternSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Pattern);
        ValidatePattern(request);
        var engine = _registry.GetEngine(workspaceId);
        var snapshot = engine.CaptureAdvancedSnapshot();
        using var timeout = CreateTimeout(request.TimeoutMilliseconds, cancellationToken);
        var projection = new SemanticSourceProjection(snapshot.Solution, timeout.Token);
        var matches = new List<CSharpPatternMatch>();
        var timeReached = false;
        var normalizedScope = NormalizeScope(request.Path, snapshot.RepositoryPath);
        try
        {
            foreach (var project in snapshot.Solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
            {
                foreach (var document in project.Documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (!IsInScope(document.FilePath, normalizedScope))
                    {
                        continue;
                    }

                    var root = await document.GetSyntaxRootAsync(timeout.Token);
                    if (root is null)
                    {
                        continue;
                    }

                    foreach (var node in root.DescendantNodesAndSelf().Where(node => IsPatternKind(node, request.Pattern.Kind)))
                    {
                        if (!MatchesPattern(node, request.Pattern))
                        {
                            continue;
                        }

                        var location = CreateDocumentLocation(
                            document,
                            node.SyntaxTree,
                            node.Span,
                            projection);
                        CSharpPatternCapture[] captures = request.Pattern.Capture is null
                            ? []
                            : [new CSharpPatternCapture(request.Pattern.Capture, location.Range, BoundText(node.ToString(), 1024))];
                        matches.Add(new CSharpPatternMatch(request.Pattern.Kind, location, captures));
                        if (matches.Count >= request.MaximumMatches)
                        {
                            EnsureCurrent(engine, snapshot.Generation);
                            return new(
                                snapshot.Generation,
                                snapshot.Confidence,
                                matches,
                                false,
                                ["The maximum match count was reached."]);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            timeReached = true;
        }

        EnsureCurrent(engine, snapshot.Generation);
        return new(
            snapshot.Generation,
            snapshot.Confidence,
            matches,
            !timeReached,
            timeReached ? ["The query time limit was reached."] : []);
    }

    /// <inheritdoc />
    public async Task<GeneratedCodeResult> QueryGeneratedCodeAsync(
        WorkspaceId workspaceId,
        GeneratedCodeQuery request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGeneratedQuery(request);
        var engine = _registry.GetEngine(workspaceId);
        var snapshot = engine.CaptureAdvancedSnapshot();
        var projection = new SemanticSourceProjection(snapshot.Solution, cancellationToken);
        var normalizedScope = NormalizeScope(request.Path, snapshot.RepositoryPath);
        var documents = new List<GeneratedDocumentInfo>();
        var truncated = false;
        foreach (var project in snapshot.Solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectFileScoped = normalizedScope is not null
                && project.FilePath is not null
                && Path.GetFullPath(project.FilePath).Equals(normalizedScope, PathComparison);
            var candidates = new List<(Document Document, GeneratedCodeOrigin Origin, string? OriginName)>();
            candidates.AddRange(project.Documents
                .Where(document => IsGeneratedPath(document.FilePath ?? document.Name))
                .Select(document => (document, GeneratedCodeOrigin.FileConvention, (string?)null)));
            IEnumerable<Document> generated = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
            candidates.AddRange(generated.Select(document => (document, GeneratedCodeOrigin.SourceGenerator, (string?)document.Name)));
            foreach ((var document, var origin, var originName) in candidates
                .GroupBy(item => item.Document.Id)
                .Select(group => group.OrderByDescending(item => item.Origin).First())
                .OrderBy(item => item.Document.FilePath ?? item.Document.Name, StringComparer.Ordinal))
            {
                if (!projectFileScoped && !IsInScope(document.FilePath, normalizedScope))
                {
                    continue;
                }

                if (documents.Count >= request.MaximumDocuments)
                {
                    truncated = true;
                    break;
                }

                var text = await document.GetTextAsync(cancellationToken);
                var content = request.IncludeContent ? text.ToString() : null;
                var contentTruncated = content is { Length: var length } && length > request.MaximumContentCharacters;
                if (contentTruncated)
                {
                    content = content?[..request.MaximumContentCharacters];
                }

                var filePath = document.FilePath ?? document.Name;
                documents.Add(new GeneratedDocumentInfo(
                    document.Id.Id.ToString("D"),
                    document.Name,
                    project.Name,
                    filePath,
                    projection.IsLinked(filePath),
                    origin,
                    originName,
                    content,
                    contentTruncated));
            }

            if (truncated)
            {
                break;
            }
        }

        EnsureCurrent(engine, snapshot.Generation);
        return new(
            snapshot.Generation,
            snapshot.Confidence,
            documents,
            !truncated,
            truncated ? ["The maximum generated-document count was reached."] : []);
    }

    private static CancellationTokenSource CreateTimeout(int milliseconds, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
        return timeout;
    }

    private static void ValidateSymbolId(string symbolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        if (symbolId.Length > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolId), "Symbol ids are limited to 2,048 characters.");
        }
    }

    private static void ValidateLimits(SemanticTraversalLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumDepth is < 0 or > 8 || limits.MaximumNodes is < 1 or > 1000
            || limits.MaximumEdges is < 1 or > 5000 || limits.TimeoutMilliseconds is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Semantic traversal bounds are outside host limits.");
        }
    }

    private static void ValidatePattern(CSharpPatternSearchRequest request)
    {
        if (request.Pattern.Version != 1)
        {
            throw new NotSupportedException($"C# pattern version {request.Pattern.Version} is not supported.");
        }

        if (request.MaximumMatches is < 1 or > 1000 || request.TimeoutMilliseconds is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Pattern-search bounds are outside host limits.");
        }

        foreach (var value in request.Pattern.RequiredModifiers)
        {
            if (!AllowedModifiers.Contains(value))
            {
                throw new ArgumentException($"Unsupported C# modifier '{value}'.", nameof(request));
            }
        }

        foreach (var value in new[] { request.Pattern.Name, request.Pattern.ContainingType, request.Pattern.Capture }
            .Concat(request.Pattern.RequiredAttributes))
        {
            if (value is { Length: > 256 } || (value is not null && !IsSafeName(value)))
            {
                throw new ArgumentException("Pattern names must be bounded C# identifiers.", nameof(request));
            }
        }

        if (request.Pattern.RequiredModifiers.Count > 16 || request.Pattern.RequiredAttributes.Count > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Pattern predicate counts exceed host limits.");
        }
    }

    private static void ValidateGeneratedQuery(GeneratedCodeQuery request)
    {
        if (request.MaximumDocuments is < 1 or > 500 || request.MaximumContentCharacters is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Generated-code bounds are outside host limits.");
        }
    }

    private static bool IsSafeName(string value)
    {
        return value.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .All(part => SyntaxFacts.IsValidIdentifier(part));
    }

    private static async Task<ISymbol> ResolveSymbolAsync(Solution solution, string symbolId, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var symbol = compilation is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, compilation);
            if (symbol is not null)
            {
                return symbol;
            }
        }

        throw new KeyNotFoundException($"Semantic symbol '{symbolId}' is not loaded.");
    }

    private static async Task<IReadOnlyList<(ISymbol Caller, ISymbol Callee, Location? Site)>> FindOutgoingAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var calls = new List<(ISymbol Caller, ISymbol Callee, Location? Site)>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var declaration = await reference.GetSyntaxAsync(cancellationToken);
            var document = solution.GetDocument(declaration.SyntaxTree);
            if (document is null)
            {
                continue;
            }

            var model = await document.GetSemanticModelAsync(cancellationToken)
                ?? throw new InvalidOperationException("The semantic model became unavailable.");
            var expressions = declaration.DescendantNodes()
                .OfType<ExpressionSyntax>()
                .Where(node => node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax)
                .Where(node => BelongsToDeclaration(node, declaration));
            foreach (var expression in expressions)
            {
                var info = model.GetSymbolInfo(expression, cancellationToken);
                var target = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (target is IMethodSymbol method)
                {
                    calls.Add((symbol, method.OriginalDefinition, expression.GetLocation()));
                }
                else if (target is IPropertySymbol property)
                {
                    calls.Add((symbol, property.OriginalDefinition, expression.GetLocation()));
                }
            }
        }

        return calls;
    }

    private static bool BelongsToDeclaration(SyntaxNode node, SyntaxNode declaration)
    {
        var containingCallable = node.Ancestors().FirstOrDefault(ancestor => ancestor is
            BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
            or AnonymousFunctionExpressionSyntax);
        return ReferenceEquals(containingCallable, declaration);
    }

    private static void AddNode(
        Dictionary<string, CallHierarchyNode> nodes,
        ISymbol symbol,
        int depth,
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection)
    {
        var identity = CreateIdentity(symbol);
        var locations = symbol.Locations.Where(location => location.IsInSource)
            .Select(location => CreateLocation(snapshot, projection, location))
            .Where(location => location is not null)
            .Select(location => location ?? throw new InvalidOperationException("Projected location cannot be null."))
            .ToArray();
        nodes[identity.Id] = new CallHierarchyNode(identity, locations, depth);
    }

    private static SemanticSymbolIdentity CreateIdentity(ISymbol symbol)
    {
        var id = symbol.GetDocumentationCommentId()
            ?? $"{symbol.Kind}:{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
        return new(id, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), symbol.Kind.ToString());
    }

    private static CallDispatchKind ClassifyDispatch(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
        {
            return CallDispatchKind.Constructor;
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction })
        {
            return CallDispatchKind.LocalFunction;
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke })
        {
            return CallDispatchKind.Delegate;
        }

        if (symbol is IMethodSymbol { IsExtensionMethod: true })
        {
            return CallDispatchKind.Extension;
        }

        if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
        {
            return CallDispatchKind.Interface;
        }

        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride)
        {
            return CallDispatchKind.Virtual;
        }

        return symbol.IsStatic ? CallDispatchKind.Static : CallDispatchKind.Direct;
    }

    private static bool IsAmbiguousDispatch(ISymbol symbol)
    {
        return ClassifyDispatch(symbol) is CallDispatchKind.Interface or CallDispatchKind.Virtual
            or CallDispatchKind.Delegate or CallDispatchKind.Unknown;
    }

    private static async Task<SemanticSourceLocation?> FirstLocationAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var location = symbol.Locations.FirstOrDefault(item => item.IsInSource);
        return location is null ? null : await CreateLocationAsync(snapshot, projection, location, cancellationToken);
    }

    private static Task<SemanticSourceLocation?> CreateLocationAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        Location location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateLocation(snapshot, projection, location));
    }

    private static SemanticSourceLocation? CreateLocation(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        Location location)
    {
        if (!location.IsInSource || location.SourceTree is null)
        {
            return null;
        }

        var document = snapshot.Solution.GetDocument(location.SourceTree);
        if (document is null)
        {
            return null;
        }

        return CreateDocumentLocation(document, location.SourceTree, location.SourceSpan, projection);
    }

    private static SemanticSourceLocation CreateDocumentLocation(
        Document document,
        SyntaxTree syntaxTree,
        TextSpan span,
        SemanticSourceProjection projection)
    {
        var lineSpan = syntaxTree.GetLineSpan(span);
        var filePath = document.FilePath ?? document.Name;
        var range = new SourceRange(
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1);
        return new(
            document.Project.Name,
            projection.GetTargetFramework(document.Project.Id),
            filePath,
            range,
            IsGeneratedPath(filePath),
            projection.IsLinked(filePath));
    }

    private static bool TryAddImpact(
        Dictionary<string, ImpactNode> nodes,
        List<ImpactEdge> edges,
        SemanticTraversalLimits limits,
        string fromId,
        string id,
        string displayName,
        ImpactKind kind,
        SemanticSourceLocation? location,
        string? projectName,
        string reason)
    {
        if ((!nodes.ContainsKey(id) && nodes.Count >= limits.MaximumNodes) || edges.Count >= limits.MaximumEdges)
        {
            return false;
        }

        nodes.TryAdd(id, new ImpactNode(id, displayName, kind, location, projectName));
        edges.Add(new ImpactEdge(fromId, id, kind, reason));
        return true;
    }

    private static void UpdateImpactBounds(
        IReadOnlyDictionary<string, ImpactNode> nodes,
        IReadOnlyCollection<ImpactEdge> edges,
        SemanticTraversalLimits limits,
        ref bool nodeReached,
        ref bool edgeReached)
    {
        nodeReached |= nodes.Count >= limits.MaximumNodes;
        edgeReached |= edges.Count >= limits.MaximumEdges;
    }

    private static bool IsTestProject(Project project)
    {
        return project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || project.MetadataReferences.Any(reference => reference.Display?.Contains("xunit", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsPatternKind(SyntaxNode node, CSharpPatternKind kind)
    {
        return kind switch
        {
            CSharpPatternKind.Declaration => node is MemberDeclarationSyntax,
            CSharpPatternKind.TypeDeclaration => node is TypeDeclarationSyntax,
            CSharpPatternKind.MethodDeclaration => node is MethodDeclarationSyntax,
            CSharpPatternKind.PropertyDeclaration => node is PropertyDeclarationSyntax,
            CSharpPatternKind.FieldDeclaration => node is FieldDeclarationSyntax,
            CSharpPatternKind.Attribute => node is AttributeSyntax,
            CSharpPatternKind.Invocation => node is InvocationExpressionSyntax,
            CSharpPatternKind.ObjectCreation => node is ObjectCreationExpressionSyntax,
            CSharpPatternKind.MemberAccess => node is MemberAccessExpressionSyntax,
            _ => false,
        };
    }

    private static bool MatchesPattern(SyntaxNode node, CSharpPattern pattern)
    {
        var name = GetSimpleName(node);
        if (pattern.Name is not null && !string.Equals(pattern.Name, name, StringComparison.Ordinal))
        {
            return false;
        }

        if (pattern.ContainingType is not null)
        {
            var containing = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
            if (!string.Equals(pattern.ContainingType, containing, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var modifiers = GetModifiers(node);
        if (pattern.RequiredModifiers.Any(required => !modifiers.Any(token => token.ValueText == required)))
        {
            return false;
        }

        var attributes = GetAttributes(node);
        return pattern.RequiredAttributes.All(required => attributes.Any(actual => AttributeNamesEqual(required, actual)));
    }

    private static string? GetSimpleName(SyntaxNode node)
    {
        return node switch
        {
            BaseTypeDeclarationSyntax declaration => declaration.Identifier.ValueText,
            MethodDeclarationSyntax declaration => declaration.Identifier.ValueText,
            PropertyDeclarationSyntax declaration => declaration.Identifier.ValueText,
            VariableDeclaratorSyntax declaration => declaration.Identifier.ValueText,
            FieldDeclarationSyntax declaration => declaration.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
            AttributeSyntax attribute => attribute.Name.ToString().Split('.').Last(),
            InvocationExpressionSyntax invocation => GetExpressionName(invocation.Expression),
            ObjectCreationExpressionSyntax creation => creation.Type.ToString().Split('.').Last(),
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        };
    }

    private static string? GetExpressionName(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => null,
        };
    }

    private static SyntaxTokenList GetModifiers(SyntaxNode node)
    {
        return node switch
        {
            MemberDeclarationSyntax member => member.Modifiers,
            LocalFunctionStatementSyntax local => local.Modifiers,
            _ => default,
        };
    }

    private static IReadOnlyList<string> GetAttributes(SyntaxNode node)
    {
        var lists = node switch
        {
            MemberDeclarationSyntax member => member.AttributeLists,
            ParameterSyntax parameter => parameter.AttributeLists,
            _ => default,
        };
        return lists.SelectMany(list => list.Attributes).Select(attribute => attribute.Name.ToString().Split('.').Last()).ToArray();
    }

    private static bool AttributeNamesEqual(string expected, string actual)
    {
        static string TrimSuffix(string value) => value.EndsWith("Attribute", StringComparison.Ordinal)
            ? value[..^"Attribute".Length]
            : value;
        return string.Equals(TrimSuffix(expected), TrimSuffix(actual), StringComparison.Ordinal);
    }

    private static string? NormalizeScope(string? path, string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path, repositoryPath);
        if (!fullPath.Equals(repositoryPath, PathComparison)
            && !fullPath.StartsWith(repositoryPath + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new UnauthorizedAccessException("Semantic query scope escapes the repository root.");
        }

        return fullPath;
    }

    private static bool IsInScope(string? filePath, string? scope)
    {
        if (scope is null)
        {
            return true;
        }

        return filePath is not null && (filePath.Equals(scope, PathComparison)
            || filePath.StartsWith(scope + Path.DirectorySeparatorChar, PathComparison));
    }

    private static bool IsGeneratedPath(string path)
    {
        return path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string BoundText(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }

    private static string[] BuildOmissions(bool depth, bool nodes, bool edges, bool time)
    {
        var omissions = new List<string>();
        if (depth)
        {
            omissions.Add("The traversal depth limit was reached.");
        }

        if (nodes)
        {
            omissions.Add("The traversal node limit was reached.");
        }

        if (edges)
        {
            omissions.Add("The traversal edge limit was reached.");
        }

        if (time)
        {
            omissions.Add("The traversal time limit was reached.");
        }

        omissions.Add("Dynamic, reflection, and runtime-only call targets are not resolved.");
        return [.. omissions];
    }

    private static void EnsureCurrent(SemanticEngine engine, long generation)
    {
        if (!engine.IsCurrentGeneration(generation))
        {
            throw new InvalidOperationException("The semantic workspace changed while the query was running; stale results were discarded.");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

/// <summary>Caches stable source-projection metadata for one semantic query.</summary>
internal sealed class SemanticSourceProjection
{
    private readonly IReadOnlyDictionary<ProjectId, string> _targetFrameworks;
    private readonly IReadOnlyDictionary<string, int> _documentPathCounts;

    /// <summary>Initializes a new instance of the <see cref="SemanticSourceProjection"/> class.</summary>
    internal SemanticSourceProjection(Solution solution, CancellationToken cancellationToken)
    {
        var targetFrameworks = new Dictionary<ProjectId, string>();
        var documentPathCounts = new Dictionary<string, int>(PathComparer);
        foreach (var project in solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetFrameworks[project.Id] = ReadTargetFramework(project.FilePath);
            foreach (var document in project.Documents)
            {
                if (document.FilePath is { } filePath)
                {
                    documentPathCounts[filePath] = documentPathCounts.GetValueOrDefault(filePath) + 1;
                }
            }
        }

        _targetFrameworks = targetFrameworks;
        _documentPathCounts = documentPathCounts;
    }

    /// <summary>Gets the captured target framework for a project.</summary>
    internal string GetTargetFramework(ProjectId projectId)
    {
        return _targetFrameworks.GetValueOrDefault(projectId, string.Empty);
    }

    /// <summary>Returns whether a source path was loaded by more than one project.</summary>
    internal bool IsLinked(string filePath)
    {
        return _documentPathCounts.GetValueOrDefault(filePath) > 1;
    }

    private static string ReadTargetFramework(string? projectFilePath)
    {
        if (projectFilePath is null || !File.Exists(projectFilePath))
        {
            return string.Empty;
        }

        var document = XDocument.Load(projectFilePath, LoadOptions.None);
        return document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "TargetFramework")?.Value.Trim()
            ?? document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "TargetFrameworks")?.Value
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim()
            ?? string.Empty;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

/// <summary>Immutable compiler-aware state captured for one advanced query.</summary>
internal sealed record AdvancedSemanticSnapshot(
    Solution Solution,
    IReadOnlySet<ProjectId> CompiledProjects,
    SemanticConfidenceLevel Confidence,
    string RepositoryPath,
    long Generation);
