namespace Threadsmith.DotNet;

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Threadsmith.Core;

/// <summary>Runs bounded advanced C# queries against snapshots from the existing semantic workspace.</summary>
public sealed class AdvancedSemanticQueryService : IAdvancedSemanticQueryService, ICodeExploreService
{
    private const int MaximumCurrentSourceFileBytes = 1024 * 1024;
    private const int MaximumCodeExploreCatalogEntries = 50_000;
    private const int MaximumCodeExploreCatalogs = 4;
    private const int MaximumNaturalLanguageCandidateSummaries = 64;
    private const int MaximumNaturalLanguageGraphSeeds = 8;
    private const int MaximumNaturalLanguageGraphEdges = 128;
    private const int MinimumUsefulSourceCharacters = 256;

    private static readonly TimeSpan NonCooperativeCompilationBackstop = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> AllowedModifiers = new(
        ["public", "private", "protected", "internal", "static", "abstract", "virtual", "override", "sealed", "partial", "async", "readonly", "required", "unsafe", "extern", "new"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> NaturalLanguageStopWords = new(
        ["a", "an", "and", "are", "as", "at", "be", "been", "being", "by", "can", "could", "did", "do", "does", "for", "from", "has", "have", "how", "i", "in", "into", "is", "it", "its", "me", "of", "on", "or", "please", "show", "that", "the", "their", "this", "through", "to", "was", "were", "what", "when", "where", "which", "who", "why", "with", "without"],
        StringComparer.OrdinalIgnoreCase);

    private readonly Lock _catalogGate = new();
    private readonly Dictionary<string, CodeExploreDeclarationCatalog> _codeExploreCatalogs = new(StringComparer.Ordinal);
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

    /// <inheritdoc />
    public async Task<CodeExploreResult> QueryCodeExploreAsync(
        WorkspaceId workspaceId,
        CodeExploreRequest request,
        ICodeExploreSourceReader sourceReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceReader);
        ValidateCodeExploreRequest(request);
        var engine = _registry.GetEngine(workspaceId);
        var snapshot = engine.CaptureAdvancedSnapshot();
        using var timeout = CreateTimeout(request.Limits.TimeoutMilliseconds, cancellationToken);
        var queryInterpretation = InterpretCodeExploreQuery(request.Query);
        var anchors = BuildCodeExploreAnchors(request).ToList();
        var resolutions = new List<CodeExploreAnchorResolution>();
        var candidates = new List<CodeExploreSectionCandidate>();
        var omissions = new List<string>();
        var continuations = new List<CodeExploreContinuationTarget>();
        var allocationFiles = new List<CodeExploreAllocationFileSummary>();
        var candidateSummaries = Array.Empty<CodeExploreCandidateSummary>();
        CodeExploreDiscoverySummary? discovery = null;
        CodeExploreFlow? flow = null;
        CodeExploreBlastRadius? blastRadius = null;
        var alternativesCapped = false;
        var timeReached = false;
        SemanticSourceProjection? projection = null;
        try
        {
            projection = new SemanticSourceProjection(snapshot.Solution, timeout.Token);
            if (anchors.Count == 0 && ShouldUseNaturalLanguageDiscovery(queryInterpretation))
            {
                var naturalLanguage = await DiscoverNaturalLanguageCodeExploreAsync(
                    workspaceId,
                    snapshot,
                    projection,
                    sourceReader,
                    request,
                    queryInterpretation,
                    timeout.Token);
                anchors.AddRange(naturalLanguage.Anchors);
                queryInterpretation = queryInterpretation with { UnresolvedTerms = naturalLanguage.UnresolvedTerms };
                discovery = naturalLanguage.Discovery;
                candidateSummaries = naturalLanguage.Candidates;
                omissions.AddRange(naturalLanguage.Omissions);
            }

            if (anchors.Count == 0)
            {
                EnsureCurrent(engine, snapshot.Generation);
                return CreateUnanchoredCodeExploreResult(
                    snapshot,
                    request.Query,
                    queryInterpretation,
                    discovery,
                    candidateSummaries);
            }

            foreach (var anchor in anchors)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (anchor.Kind == CodeExploreAnchorKind.SymbolId)
                {
                    var symbolIdResult = await ResolveCodeExploreSymbolIdAsync(
                        snapshot,
                        projection,
                        sourceReader,
                        request.PathAnchors,
                        request.Limits.MaximumAlternatives,
                        anchor,
                        resolutions,
                        candidates,
                        timeout.Token);
                    alternativesCapped |= symbolIdResult.AlternativesCapped;
                    continue;
                }

                if (anchor.Kind == CodeExploreAnchorKind.Path || QueryLooksLikePath(anchor.Value))
                {
                    var pathResult = await ResolveCodeExplorePathAsync(
                        snapshot,
                        projection,
                        sourceReader,
                        request.Limits.MaximumAlternatives,
                        anchor,
                        resolutions,
                        candidates,
                        timeout.Token);
                    alternativesCapped |= pathResult.AlternativesCapped;
                    continue;
                }

                var result = await ResolveCodeExploreSymbolNameAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    request,
                    anchor,
                    resolutions,
                    candidates,
                    timeout.Token);
                alternativesCapped |= result.AlternativesCapped;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            timeReached = true;
            omissions.Add("The code exploration time limit was reached during anchor resolution.");
        }

        var selectedSections = new List<CodeExploreFileSection>();
        var seenSections = new HashSet<string>(StringComparer.Ordinal);
        var reservedCharacters = discovery is null
            ? 0
            : Math.Min(
                request.Limits.MaximumSourceCharacters,
                EstimateReservedCodeExploreCharacters(queryInterpretation, discovery));
        var availableSourceCharacters = Math.Max(0, request.Limits.MaximumSourceCharacters - reservedCharacters);
        var remainingSourceCharacters = availableSourceCharacters;
        var outputBoundReached = false;
        if (!timeReached && projection is not null)
        {
            await ProjectAvailableSourceAsync(
                candidate => candidate.Priority <= 1,
                "anchor source projection");
        }

        if (!timeReached && projection is not null)
        {
            try
            {
                var flowAnchors = await ResolveCodeExploreFlowAnchorsAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    request,
                    resolutions,
                    timeout.Token);
                if (ShouldBuildCodeExploreFlow(request, flowAnchors))
                {
                    flow = await BuildCodeExploreFlowAsync(
                        snapshot,
                        projection,
                        sourceReader,
                        request,
                        flowAnchors,
                        candidates,
                        timeout.Token);
                    omissions.AddRange(flow.Traversal.Omissions);
                }
                else if (request.Mode == CodeExploreMode.Flow && flowAnchors.Count < 2)
                {
                    omissions.Add("Flow mode requires at least two resolved source-bearing symbol anchors.");
                }

                if (ShouldBuildCodeExploreBlastRadius(request, flowAnchors))
                {
                    blastRadius = await BuildCodeExploreBlastRadiusAsync(
                        snapshot,
                        projection,
                        sourceReader,
                        request,
                        flowAnchors,
                        candidates,
                        timeout.Token);
                    omissions.AddRange(blastRadius.Omissions);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                timeReached = true;
                omissions.Add("The code exploration time limit was reached during flow composition.");
            }
        }

        if (!timeReached && projection is not null)
        {
            await ProjectAvailableSourceAsync(
                _ => true,
                "source projection");
        }

        async Task ProjectAvailableSourceAsync(
            Func<CodeExploreSectionCandidate, bool> predicate,
            string phase)
        {
            if (projection is null)
            {
                return;
            }

            var activeProjection = projection;
            try
            {
                foreach (var candidate in candidates
                    .Where(predicate)
                    .OrderBy(candidate => candidate.Priority)
                    .ThenBy(candidate => candidate.AllocationRank ?? int.MaxValue)
                    .ThenBy(candidate => IsFlowCallSiteCandidate(candidate) ? 0 : 1)
                    .ThenBy(candidate => candidate.FilePath, PathComparer)
                    .ThenBy(candidate => candidate.Location?.Range.StartLine ?? candidate.PreferredLine ?? 0)
                    .ThenBy(candidate => candidate.Identity?.Id ?? string.Empty, StringComparer.Ordinal))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var dedupeKey = CreateSectionKey(candidate);
                    if (!seenSections.Add(dedupeKey))
                    {
                        continue;
                    }

                    if (selectedSections.Count >= request.Limits.MaximumFiles)
                    {
                        outputBoundReached = true;
                        AddContinuationIfWithinLimit(
                            continuations,
                            CreateSkippedCandidateContinuation(
                                snapshot,
                                candidate,
                                "The maximum file-section count was reached."),
                            request.Limits.MaximumFiles);
                        break;
                    }

                    if (remainingSourceCharacters <= 0 && selectedSections.Count > 0)
                    {
                        outputBoundReached = true;
                        AddContinuationIfWithinLimit(
                            continuations,
                            CreateSkippedCandidateContinuation(
                                snapshot,
                                candidate,
                                "The maximum total source-character count was reached."),
                            request.Limits.MaximumFiles);
                        break;
                    }

                    var sourceAllowance = remainingSourceCharacters <= 0
                        ? 0
                        : Math.Min(
                            remainingSourceCharacters,
                            request.Limits.MaximumPerFileSourceCharacters);
                    var projected = await ProjectCodeExploreSectionAsync(
                        snapshot,
                        activeProjection,
                        sourceReader,
                        candidate,
                        sourceAllowance,
                        timeout.Token);
                    remainingSourceCharacters -= projected.SourceCharacters;
                    selectedSections.Add(projected.Section);
                    allocationFiles.Add(new CodeExploreAllocationFileSummary(
                        projected.Section.FilePath,
                        sourceAllowance,
                        projected.SourceCharacters,
                        projected.Section.Source.Completeness,
                        IsUsefulCodeExploreSection(projected.Section),
                        projected.Section.Source.OmittedRanges.FirstOrDefault()));
                    foreach (var continuation in projected.ContinuationTargets)
                    {
                        AddContinuationIfWithinLimit(continuations, continuation, request.Limits.MaximumFiles);
                    }

                    outputBoundReached |= projected.ContinuationTargets.Count > 0
                        || projected.Section.Source.OmittedRanges.Any(IsOutputBoundOmission);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                timeReached = true;
                omissions.Add($"The code exploration time limit was reached during {phase}.");
            }
        }

        var symbolResolutionComplete = !timeReached
            && resolutions.Count == anchors.Count
            && resolutions.All(resolution => resolution.Outcome == CodeExploreResolutionOutcome.Resolved);
        EnsureCurrent(engine, snapshot.Generation);
        var hasUnloadedSource = selectedSections.Any(section => string.IsNullOrWhiteSpace(section.ProjectName));
        if (snapshot.Confidence < SemanticConfidenceLevel.FullSemantic || hasUnloadedSource)
        {
            omissions.Add("Compiled-project coverage is partial; unloaded or uncompilable projects may be absent.");
        }

        if (alternativesCapped)
        {
            omissions.Add("One or more ambiguity alternative sets were capped by the request limits.");
        }

        if (outputBoundReached)
        {
            omissions.Add("Source or section output bounds were reached; use the continuation targets for more exact source.");
        }

        var missingSourceForResolvedAnchor = resolutions
            .Where(resolution => resolution.Outcome == CodeExploreResolutionOutcome.Resolved)
            .Any(resolution => !candidates.Any(candidate => candidate.AnchorKind == resolution.Kind
                && string.Equals(candidate.Anchor, resolution.Input, StringComparison.Ordinal)));
        if (missingSourceForResolvedAnchor)
        {
            omissions.Add("One or more resolved anchors had no source-bearing declaration or path section.");
        }

        var sourceComplete = !timeReached
            && !missingSourceForResolvedAnchor
            && selectedSections.All(section => section.Source.Completeness == CodeExploreSourceCompleteness.Complete)
            && selectedSections.Count == candidates.Select(CreateSectionKey).Distinct(StringComparer.Ordinal).Count();
        var sourceOmissions = selectedSections
            .SelectMany(section => section.Source.OmittedRanges)
            .Distinct(StringComparer.Ordinal);
        flow = AttachCodeExploreSourceSections(flow, selectedSections);
        var coverageOmissions = omissions
            .Concat(sourceOmissions)
            .Concat(resolutions
                .Where(resolution => resolution.Outcome != CodeExploreResolutionOutcome.Resolved)
                .Select(resolution => resolution.Reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var coverage = new CodeExploreCoverage(
            symbolResolutionComplete,
            snapshot.Confidence == SemanticConfidenceLevel.FullSemantic && !hasUnloadedSource,
            sourceComplete,
            !timeReached && !outputBoundReached && !alternativesCapped,
            coverageOmissions);
        var allocation = new CodeExploreAllocationSummary(
            request.Limits.MaximumSourceCharacters,
            reservedCharacters,
            availableSourceCharacters - remainingSourceCharacters,
            "request.maximumSourceCharacters after reserved metadata, plus request.maximumPerFileSourceCharacters",
            allocationFiles);
        return new CodeExploreResult(
            snapshot.Generation,
            snapshot.Confidence,
            resolutions,
            selectedSections,
            coverage,
            coverageOmissions,
            continuations.DistinctBy(target => $"{target.Kind}:{target.Anchor}:{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.StartAtLine}:{target.SelectionMode}:{target.ExpectedFileSha256}:{target.WorkspaceGeneration}:{target.Reason}").ToArray(),
            flow,
            blastRadius,
            queryInterpretation,
            discovery,
            candidateSummaries,
            allocation);
    }

    private static void AddContinuationIfWithinLimit(
        List<CodeExploreContinuationTarget> continuations,
        CodeExploreContinuationTarget target,
        int maximumContinuations)
    {
        if (continuations.Count < maximumContinuations)
        {
            continuations.Add(target);
        }
    }

    private static CodeExploreContinuationTarget CreateSkippedCandidateContinuation(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreSectionCandidate candidate,
        string reason)
    {
        var relativePath = ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath);
        var range = candidate.Location?.Range;
        var startLine = range?.StartLine ?? candidate.PreferredLine;
        var endLine = candidate.EndLine ?? range?.EndLine;
        var mode = candidate.SelectionMode != CodeExplorePathSelectionMode.Auto
            ? candidate.SelectionMode
            : startLine is null
                ? CodeExplorePathSelectionMode.WholeFile
                : endLine is null
                    ? CodeExplorePathSelectionMode.TailWindow
                    : CodeExplorePathSelectionMode.ExactLineRange;
        return new CodeExploreContinuationTarget(
            CodeExploreAnchorKind.Path,
            relativePath,
            relativePath,
            startLine,
            endLine,
            mode == CodeExplorePathSelectionMode.TailWindow,
            mode,
            candidate.ExpectedFileSha256,
            candidate.ExpectedWorkspaceGeneration ?? snapshot.Generation,
            $"{reason} Retry with this path anchor cursor instead of the original symbol anchor.");
    }

    private sealed record CodeExploreAnchor(
        CodeExploreAnchorKind Kind,
        string Value,
        int? Line,
        int? EndLine,
        bool StartAtLine,
        CodeExplorePathSelectionMode SelectionMode,
        string? ExpectedFileSha256,
        long? ExpectedWorkspaceGeneration,
        int? AllocationRank);

    private sealed record CodeExploreSymbolResolution(bool AlternativesCapped);

    private sealed record CodeExploreSymbolGroup(
        SemanticSymbolIdentity Identity,
        IReadOnlyList<ISymbol> Symbols);

    private sealed record CodeExploreLocatedSymbolGroup(
        CodeExploreSymbolGroup Group,
        CodeExploreLocation Location);

    private sealed record CodeExploreSectionCandidate(
        Document? Document,
        string FilePath,
        TextSpan? Span,
        SemanticSymbolIdentity? Identity,
        CodeExploreLocation? Location,
        CodeExploreAnchorKind AnchorKind,
        string Anchor,
        string SelectionReason,
        int Priority,
        int? PreferredLine,
        int? EndLine,
        bool StartAtLine,
        CodeExplorePathSelectionMode SelectionMode,
        string? ExpectedFileSha256,
        long? ExpectedWorkspaceGeneration,
        int? AllocationRank,
        SourceText? PreloadedText,
        string? PreloadedFileSha256);

    private sealed record CodeExploreDeclarationCatalog(
        string Key,
        long WorkspaceGeneration,
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> Entries,
        bool IsComplete,
        IReadOnlyList<string> Omissions);

    private sealed record CodeExploreDeclarationCatalogEntry(
        SemanticSymbolIdentity Identity,
        string Name,
        string MetadataName,
        string DisplayName,
        string FullyQualifiedName,
        string? ContainingType,
        string? ContainingNamespace,
        string Kind,
        string ProjectName,
        string TargetFramework,
        string FilePath,
        string RelativeFilePath,
        SourceRange Range,
        bool IsGenerated,
        bool IsLinked,
        bool IsTest,
        IReadOnlyList<string> Terms,
        IReadOnlyList<string> QualifiedNames);

    private sealed record CodeExploreRankedCandidate(
        CodeExploreDeclarationCatalogEntry Entry,
        CodeExploreCandidateTier Tier,
        CodeExploreSelectionReason Reasons,
        int Score,
        int CoveredTermCount,
        string AmbiguityGroup);

    private sealed record NaturalLanguageCodeExploreDiscovery(
        IReadOnlyList<CodeExploreAnchor> Anchors,
        CodeExploreQueryInterpretation Interpretation,
        CodeExploreDiscoverySummary Discovery,
        CodeExploreCandidateSummary[] Candidates,
        IReadOnlyList<string> UnresolvedTerms,
        IReadOnlyList<string> Omissions);

    private sealed record ProjectedCodeExploreSection(
        CodeExploreFileSection Section,
        int SourceCharacters,
        IReadOnlyList<CodeExploreContinuationTarget> ContinuationTargets);

    private sealed record CodeExploreFlowAnchor(
        SemanticSymbolIdentity Identity,
        ISymbol Symbol,
        CodeExploreLocation Location);

    private sealed record CodeExploreCallEvidence(
        ISymbol Caller,
        ISymbol Callee,
        Location? Site);

    private sealed record CodeExplorePathSearchResult(
        IReadOnlyList<CodeExploreCallEvidence> Edges,
        IReadOnlyList<CodeExploreCallEvidence> CycleEdges,
        bool IsComplete,
        bool DepthLimitReached,
        bool NodeLimitReached,
        bool EdgeLimitReached,
        string Reason);

    private sealed record CodeExplorePairPathCandidate(
        CodeExploreFlowAnchor From,
        CodeExploreFlowAnchor To,
        CodeExplorePathSearchResult Result);

    private sealed class CodeExploreFlowTraversalBudget
    {
        private readonly HashSet<string> _nodeIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);

        internal bool TryAddNode(string nodeId, int maximumNodes)
        {
            if (_nodeIds.Contains(nodeId))
            {
                return true;
            }

            if (_nodeIds.Count >= maximumNodes)
            {
                return false;
            }

            _nodeIds.Add(nodeId);
            return true;
        }

        internal bool TryAddEdge(string edgeKey, int maximumEdges)
        {
            if (_edgeKeys.Contains(edgeKey))
            {
                return true;
            }

            if (_edgeKeys.Count >= maximumEdges)
            {
                return false;
            }

            _edgeKeys.Add(edgeKey);
            return true;
        }

        internal bool HasEdgeCapacity(int maximumEdges)
        {
            return _edgeKeys.Count < maximumEdges;
        }
    }

    private sealed record CodeExploreFlowNodeDraft(
        ISymbol Symbol,
        CodeExploreFlowNodeRole Role,
        int Depth);

    private static async Task<IReadOnlyList<CodeExploreFlowAnchor>> ResolveCodeExploreFlowAnchorsAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreRequest request,
        IReadOnlyList<CodeExploreAnchorResolution> resolutions,
        CancellationToken cancellationToken)
    {
        var anchors = new List<CodeExploreFlowAnchor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resolution in resolutions.Where(resolution => resolution.Outcome == CodeExploreResolutionOutcome.Resolved
            && resolution.SelectedSymbol is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectedSymbol = resolution.SelectedSymbol
                ?? throw new InvalidOperationException("Resolved symbol metadata cannot be null.");
            var groups = await ResolveSymbolGroupsInSnapshotAsync(
                snapshot,
                selectedSymbol.Id,
                request.PathAnchors,
                cancellationToken);
            CodeExploreSymbolGroup? selectedGroup = null;
            CodeExploreLocation? selectedLocation = null;
            foreach (var group in groups)
            {
                var location = await FirstCodeExploreGroupLocationAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    group,
                    cancellationToken);
                if (location is null)
                {
                    continue;
                }

                selectedGroup ??= group;
                selectedLocation ??= location;
                if (CodeExploreLocationsEqual(location, resolution.SelectedLocation))
                {
                    selectedGroup = group;
                    selectedLocation = location;
                    break;
                }
            }

            var symbol = selectedGroup?.Symbols.FirstOrDefault();
            if (symbol is null || selectedLocation is null || !seen.Add(selectedSymbol.Id))
            {
                continue;
            }

            anchors.Add(new CodeExploreFlowAnchor(selectedSymbol, symbol, selectedLocation));
        }

        return anchors;
    }

    private static bool CodeExploreLocationsEqual(
        CodeExploreLocation left,
        CodeExploreLocation? right)
    {
        return right is not null
            && string.Equals(left.ProjectName, right.ProjectName, StringComparison.Ordinal)
            && string.Equals(left.TargetFramework, right.TargetFramework, StringComparison.Ordinal)
            && string.Equals(left.FilePath, right.FilePath, PathComparison)
            && left.Range.Equals(right.Range);
    }

    private static bool ShouldBuildCodeExploreFlow(
        CodeExploreRequest request,
        IReadOnlyList<CodeExploreFlowAnchor> anchors)
    {
        return anchors.Count >= 2 && request.Mode is CodeExploreMode.Auto
            or CodeExploreMode.Survey
            or CodeExploreMode.Flow
            or CodeExploreMode.Impact;
    }

    private static bool ShouldBuildCodeExploreBlastRadius(
        CodeExploreRequest request,
        IReadOnlyList<CodeExploreFlowAnchor> anchors)
    {
        return anchors.Count > 0 && (request.Mode is CodeExploreMode.Survey
            or CodeExploreMode.Flow
            or CodeExploreMode.Impact
            || (request.Mode == CodeExploreMode.Auto && anchors.Count >= 2));
    }

    private static async Task<CodeExploreFlow> BuildCodeExploreFlowAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreRequest request,
        IReadOnlyList<CodeExploreFlowAnchor> anchors,
        List<CodeExploreSectionCandidate> sourceCandidates,
        CancellationToken cancellationToken)
    {
        var orderedAnchors = anchors
            .OrderBy(anchor => anchor.Identity.Id, StringComparer.Ordinal)
            .ToArray();
        var namedIds = orderedAnchors
            .Select(anchor => anchor.Identity.Id)
            .ToHashSet(StringComparer.Ordinal);
        var retainedAnchors = orderedAnchors
            .Take(request.Limits.MaximumFlowNodes)
            .ToArray();
        var nodeDrafts = retainedAnchors.ToDictionary(
            anchor => anchor.Identity.Id,
            anchor => new CodeExploreFlowNodeDraft(anchor.Symbol, CodeExploreFlowNodeRole.NamedAnchor, 0),
            StringComparer.Ordinal);
        var resultNodeIds = nodeDrafts.Keys.ToHashSet(StringComparer.Ordinal);
        var candidateSymbolsAdded = new HashSet<string>(
            sourceCandidates
                .Select(candidate => candidate.Identity?.Id)
                .Where(id => id is not null)
                .Select(id => id ?? string.Empty),
            StringComparer.Ordinal);
        var bridgeIds = new HashSet<string>(StringComparer.Ordinal);
        var boundaryCallerIds = new HashSet<string>(StringComparer.Ordinal);
        var searchBudget = new CodeExploreFlowTraversalBudget();
        foreach (var anchor in retainedAnchors)
        {
            _ = searchBudget.TryAddNode(anchor.Identity.Id, request.Limits.MaximumFlowNodes);
        }

        var pathSearchCache = new Dictionary<string, CodeExplorePathSearchResult>(StringComparer.Ordinal);
        var paths = new List<CodeExploreFlowPath>();
        var deferredIncompletePaths = new List<CodeExploreFlowPath>();
        var edgeOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new List<CodeExploreFlowEdge>();
        var dispatchBranches = new List<CodeExploreDispatchBranch>();
        var boundaries = new List<CodeExploreFlowBoundary>();
        var omissions = new List<string>();
        var depthReached = false;
        var nodeReached = orderedAnchors.Length > retainedAnchors.Length;
        var edgeReached = false;
        var pathLimitReached = false;
        var evidenceOmitted = false;

        if (nodeReached)
        {
            omissions.Add("The flow node limit is smaller than the resolved anchor count; flow paths were omitted.");
        }
        else
        {
            var completeCandidates = new List<CodeExplorePairPathCandidate>();
            var frontierCandidates = new List<CodeExplorePairPathCandidate>();
            var incompleteCandidates = new List<CodeExplorePairPathCandidate>();
            for (var fromIndex = 0; fromIndex < orderedAnchors.Length; fromIndex++)
            {
                for (var toIndex = fromIndex + 1; toIndex < orderedAnchors.Length; toIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var forward = await FindCodeExploreFlowPathAsync(
                        snapshot,
                        orderedAnchors[fromIndex].Symbol,
                        orderedAnchors[toIndex].Symbol,
                        request.Limits,
                        searchBudget,
                        pathSearchCache,
                        cancellationToken);
                    var reverse = await FindCodeExploreFlowPathAsync(
                        snapshot,
                        orderedAnchors[toIndex].Symbol,
                        orderedAnchors[fromIndex].Symbol,
                        request.Limits,
                        searchBudget,
                        pathSearchCache,
                        cancellationToken);
                    depthReached |= forward.DepthLimitReached || reverse.DepthLimitReached;
                    nodeReached |= forward.NodeLimitReached || reverse.NodeLimitReached;
                    edgeReached |= forward.EdgeLimitReached || reverse.EdgeLimitReached;
                    var selected = SelectCodeExplorePairPathCandidate(
                        orderedAnchors[fromIndex],
                        orderedAnchors[toIndex],
                        forward,
                        reverse);
                    if (selected.Result.IsComplete)
                    {
                        completeCandidates.Add(selected);
                    }
                    else if (selected.Result.Edges.Count > 0)
                    {
                        frontierCandidates.Add(selected);
                    }
                    else
                    {
                        incompleteCandidates.Add(selected);
                    }
                }
            }

            foreach (var candidate in completeCandidates.OrderBy(candidate => candidate.Result.Edges.Count)
                .ThenBy(candidate => candidate.Result.Edges.Sum(edge => DispatchSortRank(ClassifyDispatch(edge.Callee))))
                .ThenBy(candidate => candidate.From.Identity.Id, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.To.Identity.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (paths.Count >= request.Limits.MaximumFlowPaths)
                {
                    pathLimitReached = true;
                    break;
                }

                _ = await TryAdmitPathAsync(candidate);
            }

            foreach (var candidate in frontierCandidates.OrderBy(candidate => candidate.Result.Edges.Count)
                .ThenBy(candidate => candidate.Result.Edges.Sum(edge => DispatchSortRank(ClassifyDispatch(edge.Callee))))
                .ThenBy(candidate => candidate.From.Identity.Id, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.To.Identity.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (paths.Count >= request.Limits.MaximumFlowPaths)
                {
                    pathLimitReached = true;
                    break;
                }

                _ = await TryAdmitPathAsync(candidate);
            }

            if (paths.Count < request.Limits.MaximumFlowPaths && !pathLimitReached)
            {
                foreach (var path in deferredIncompletePaths
                    .Concat(incompleteCandidates
                        .OrderBy(candidate => candidate.From.Identity.Id, StringComparer.Ordinal)
                        .ThenBy(candidate => candidate.To.Identity.Id, StringComparer.Ordinal)
                        .Select(CreateIncompleteCodeExploreFlowPath)))
                {
                    if (paths.Count >= request.Limits.MaximumFlowPaths)
                    {
                        pathLimitReached = true;
                        break;
                    }

                    paths.Add(path);
                }
            }

            pathLimitReached |= completeCandidates.Count > paths.Count(candidate => candidate.IsComplete);
        }

        if (pathLimitReached)
        {
            omissions.Add("The maximum selected flow-path count was reached.");
        }

        if (request.Limits.MaximumDispatchBranches > 0)
        {
            var dispatchLimitReached = await AddDispatchBranchesAsync(
                snapshot,
                projection,
                sourceReader,
                request,
                edges,
                nodeDrafts,
                candidateSymbolsAdded,
                sourceCandidates,
                dispatchBranches,
                omissions,
                cancellationToken);
            nodeReached |= dispatchLimitReached;
        }

        var nodes = new List<CodeExploreFlowNode>();
        foreach (var item in nodeDrafts
            .OrderBy(item => item.Value.Depth)
            .ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes.Count >= request.Limits.MaximumFlowNodes)
            {
                nodeReached = true;
                omissions.Add("The returned flow node limit was reached.");
                break;
            }

            var identity = CreateIdentity(item.Value.Symbol);
            var locations = await CreateCodeExploreLocationsAsync(
                snapshot,
                projection,
                sourceReader,
                item.Value.Symbol,
                cancellationToken);
            if (locations.Count == 0 && !item.Value.Role.Equals(CodeExploreFlowNodeRole.NamedAnchor) && HasSourceEvidence(item.Value.Symbol))
            {
                evidenceOmitted = true;
                omissions.Add("Flow symbols outside the invocation path policy were omitted.");
                continue;
            }

            nodes.Add(new CodeExploreFlowNode(
                identity,
                item.Value.Role,
                item.Value.Depth,
                null,
                namedIds.Contains(identity.Id),
                item.Value.Role == CodeExploreFlowNodeRole.Connector,
                locations));
        }

        omissions.Add("Dynamic, reflection, dependency-injection, and runtime-only call targets are not inferred unless Roslyn exposes an unresolved call-site boundary in returned source.");
        var distinctOmissions = omissions
            .Concat(BuildOmissions(depthReached, nodeReached, edgeReached, false))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hasIncompletePath = paths.Any(path => !path.IsComplete);
        var traversalComplete = !depthReached && !nodeReached && !edgeReached && !pathLimitReached && !evidenceOmitted && !hasIncompletePath;
        return new CodeExploreFlow(
            paths,
            nodes,
            edges,
            dispatchBranches,
            boundaries,
            new SemanticTraversalSummary(
                nodes.Count,
                edges.Count,
                traversalComplete,
                depthReached,
                nodeReached,
                edgeReached,
                false,
                distinctOmissions));

        async Task<bool> TryAdmitPathAsync(CodeExplorePairPathCandidate candidate)
        {
            var pathNodeIds = CreateFlowPathNodeIds(candidate.Result.Edges);
            var pathSymbols = candidate.Result.Edges
                .SelectMany(edge => new[] { edge.Caller, edge.Callee })
                .Distinct(SymbolEqualityComparer.Default)
                .ToArray();
            foreach (var symbol in pathSymbols)
            {
                if (!await IsPolicyAllowedFlowSymbolAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    symbol,
                    cancellationToken))
                {
                    evidenceOmitted = true;
                    omissions.Add("A compiler-proven path was omitted because one or more connector symbols are outside the invocation path policy.");
                    deferredIncompletePaths.Add(new CodeExploreFlowPath(
                        candidate.From.Identity.Id,
                        candidate.To.Identity.Id,
                        [candidate.From.Identity.Id, candidate.To.Identity.Id],
                        [],
                        false,
                        "The path crosses source outside the invocation path policy."));
                    return false;
                }
            }

            var pathBridgeIds = pathNodeIds
                .Where(id => !namedIds.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (bridgeIds.Count + pathBridgeIds.Count(id => !bridgeIds.Contains(id)) > request.Limits.MaximumFlowBridgeSymbols)
            {
                nodeReached = true;
                omissions.Add("A compiler-proven path was omitted because the unnamed connector limit was reached.");
                deferredIncompletePaths.Add(new CodeExploreFlowPath(
                    candidate.From.Identity.Id,
                    candidate.To.Identity.Id,
                    [candidate.From.Identity.Id, candidate.To.Identity.Id],
                    [],
                    false,
                    "The path requires more unnamed connector symbols than the request permits."));
                return false;
            }

            var newPathNodeIds = pathNodeIds
                .Where(id => !resultNodeIds.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (resultNodeIds.Count + newPathNodeIds.Length > request.Limits.MaximumFlowNodes)
            {
                nodeReached = true;
                omissions.Add("A compiler-proven path was omitted because the returned flow node limit was reached.");
                deferredIncompletePaths.Add(new CodeExploreFlowPath(
                    candidate.From.Identity.Id,
                    candidate.To.Identity.Id,
                    [candidate.From.Identity.Id, candidate.To.Identity.Id],
                    [],
                    false,
                    "The path would exceed the returned flow node limit."));
                return false;
            }

            var cycleEdges = candidate.Result.CycleEdges
                .Where(edge => pathNodeIds.Contains(CreateIdentity(edge.Caller).Id, StringComparer.Ordinal)
                    && pathNodeIds.Contains(CreateIdentity(edge.Callee).Id, StringComparer.Ordinal))
                .DistinctBy(CreateFlowEdgeKey)
                .ToArray();
            var newPathEdgeCount = candidate.Result.Edges
                .Concat(cycleEdges)
                .Select(CreateFlowEdgeKey)
                .Distinct(StringComparer.Ordinal)
                .Count(key => !edgeOrdinals.ContainsKey(key));
            if (edges.Count + newPathEdgeCount > request.Limits.MaximumFlowEdges)
            {
                edgeReached = true;
                omissions.Add("The returned flow edge limit was reached before a selected path could be emitted.");
                deferredIncompletePaths.Add(new CodeExploreFlowPath(
                    candidate.From.Identity.Id,
                    candidate.To.Identity.Id,
                    [candidate.From.Identity.Id, candidate.To.Identity.Id],
                    [],
                    false,
                    "The flow edge limit was reached before this path could be emitted."));
                return false;
            }

            foreach (var id in pathBridgeIds)
            {
                bridgeIds.Add(id);
            }

            foreach (var id in newPathNodeIds)
            {
                resultNodeIds.Add(id);
            }

            var pathEdgeOrdinals = new List<int>();
            for (var edgeIndex = 0; edgeIndex < candidate.Result.Edges.Count; edgeIndex++)
            {
                var evidence = candidate.Result.Edges[edgeIndex];
                AddOrUpdateFlowNode(nodeDrafts, evidence.Caller, namedIds, edgeIndex);
                AddOrUpdateFlowNode(nodeDrafts, evidence.Callee, namedIds, edgeIndex + 1);
                await AddFlowSourceCandidateAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    evidence.Caller,
                    candidateSymbolsAdded,
                    sourceCandidates,
                    "Compiler-proven flow declaration source.",
                    3,
                    cancellationToken);
                await AddFlowSourceCandidateAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    evidence.Callee,
                    candidateSymbolsAdded,
                    sourceCandidates,
                    "Compiler-proven flow declaration source.",
                    3,
                    cancellationToken);
                await AddFlowCallSiteSourceCandidateAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    evidence.Caller,
                    evidence.Site,
                    sourceCandidates,
                    cancellationToken);
                await AddUnresolvedCallBoundariesAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    evidence.Caller,
                    boundaryCallerIds,
                    boundaries,
                    omissions,
                    request.Limits.MaximumFlowEdges,
                    cancellationToken);
                pathEdgeOrdinals.Add(await AddCodeExploreFlowEdgeAsync(evidence, false));
            }

            foreach (var cycleEdge in cycleEdges)
            {
                AddOrUpdateFlowNode(nodeDrafts, cycleEdge.Caller, namedIds, IndexOfSymbolId(pathNodeIds, CreateIdentity(cycleEdge.Caller).Id));
                AddOrUpdateFlowNode(nodeDrafts, cycleEdge.Callee, namedIds, IndexOfSymbolId(pathNodeIds, CreateIdentity(cycleEdge.Callee).Id));
                await AddFlowCallSiteSourceCandidateAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    cycleEdge.Caller,
                    cycleEdge.Site,
                    sourceCandidates,
                    cancellationToken);
                _ = await AddCodeExploreFlowEdgeAsync(cycleEdge, true);
            }

            var pathReason = candidate.Result.IsComplete
                ? "Selected deterministic compiler-proven call path."
                : candidate.Result.Reason;
            paths.Add(new CodeExploreFlowPath(
                candidate.From.Identity.Id,
                candidate.To.Identity.Id,
                pathNodeIds,
                pathEdgeOrdinals,
                candidate.Result.IsComplete,
                pathReason));
            return true;
        }

        async Task<int> AddCodeExploreFlowEdgeAsync(CodeExploreCallEvidence evidence, bool closesCycle)
        {
            var edgeKey = CreateFlowEdgeKey(evidence);
            if (edgeOrdinals.TryGetValue(edgeKey, out var existingOrdinal))
            {
                return existingOrdinal;
            }

            var callerIdentity = CreateIdentity(evidence.Caller);
            var calleeIdentity = CreateIdentity(evidence.Callee);
            var callSite = await CreateCodeExploreLocationAsync(
                snapshot,
                projection,
                sourceReader,
                evidence.Site,
                cancellationToken);
            var dispatchKind = ClassifyDispatch(evidence.Callee);
            var isAmbiguousDispatch = IsAmbiguousDispatch(evidence.Callee);
            var proofKind = isAmbiguousDispatch
                ? CodeExploreEdgeProofKind.CompilerKnownDispatchBoundary
                : CodeExploreEdgeProofKind.CompilerProvenCall;
            var proof = closesCycle
                ? "The compiler resolved this call relationship and it closes a cycle among returned flow nodes."
                : isAmbiguousDispatch
                    ? "The compiler resolved this call site, but runtime dispatch may choose among compiler-known implementations."
                    : "The compiler resolved this call relationship from loaded source and symbols.";
            var ordinal = edges.Count;
            edgeOrdinals.Add(edgeKey, ordinal);
            edges.Add(new CodeExploreFlowEdge(
                ordinal,
                callerIdentity.Id,
                calleeIdentity.Id,
                dispatchKind,
                callSite,
                isAmbiguousDispatch,
                closesCycle,
                proofKind,
                proof));
            AddFlowBoundary(boundaries, evidence.Callee, dispatchKind, callSite);
            return ordinal;
        }
    }

    private static async Task<CodeExplorePathSearchResult> FindCodeExploreFlowPathAsync(
        AdvancedSemanticSnapshot snapshot,
        ISymbol source,
        ISymbol target,
        CodeExploreLimits limits,
        CodeExploreFlowTraversalBudget traversalBudget,
        Dictionary<string, CodeExplorePathSearchResult> pathSearchCache,
        CancellationToken cancellationToken)
    {
        var sourceIdentity = CreateIdentity(source);
        var targetIdentity = CreateIdentity(target);
        var cacheKey = $"{sourceIdentity.Id}|{targetIdentity.Id}";
        if (pathSearchCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (string.Equals(sourceIdentity.Id, targetIdentity.Id, StringComparison.Ordinal))
        {
            var sameSymbol = new CodeExplorePathSearchResult([], [], true, false, false, false, "The anchors resolve to the same semantic symbol.");
            pathSearchCache[cacheKey] = sameSymbol;
            return sameSymbol;
        }

        var pending = new Queue<ISymbol>();
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new Dictionary<string, ISymbol>(StringComparer.Ordinal)
        {
            [sourceIdentity.Id] = source,
        };
        var depths = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [sourceIdentity.Id] = 0,
        };
        var predecessors = new Dictionary<string, CodeExploreCallEvidence>(StringComparer.Ordinal);
        CodeExplorePathSearchResult? dispatchFrontier = null;
        var cycleEdges = new List<CodeExploreCallEvidence>();
        var cycleEdgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var searchedEdges = 0;
        var depthReached = false;
        var nodeReached = false;
        var edgeReached = false;
        pending.Enqueue(source);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            var currentId = CreateIdentity(current).Id;
            if (!expanded.Add(currentId))
            {
                continue;
            }

            if (!traversalBudget.TryAddNode(currentId, limits.MaximumFlowNodes))
            {
                nodeReached = true;
                break;
            }

            var depth = depths[currentId];
            if (depth >= limits.MaximumFlowDepth)
            {
                depthReached = true;
                continue;
            }

            var outgoing = await FindOutgoingAsync(current, snapshot.Solution, cancellationToken);
            foreach (var evidence in outgoing
                .Select(item => new CodeExploreCallEvidence(item.Caller, item.Callee, item.Site))
                .OrderBy(item => string.Equals(CreateIdentity(item.Callee).Id, targetIdentity.Id, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(item => DispatchSortRank(ClassifyDispatch(item.Callee)))
                .ThenBy(item => CreateIdentity(item.Callee).Id, StringComparer.Ordinal)
                .ThenBy(item => item.Site?.SourceTree?.FilePath ?? string.Empty, PathComparer)
                .ThenBy(item => item.Site?.SourceSpan.Start ?? -1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edgeKey = CreateFlowEdgeKey(evidence);
                if (searchedEdges >= limits.MaximumFlowEdges
                    || !traversalBudget.TryAddEdge(edgeKey, limits.MaximumFlowEdges))
                {
                    edgeReached = true;
                    break;
                }

                searchedEdges++;
                var calleeIdentity = CreateIdentity(evidence.Callee);
                if (!symbols.ContainsKey(calleeIdentity.Id))
                {
                    if (symbols.Count >= limits.MaximumFlowNodes
                        || !traversalBudget.TryAddNode(calleeIdentity.Id, limits.MaximumFlowNodes))
                    {
                        nodeReached = true;
                        break;
                    }

                    symbols.Add(calleeIdentity.Id, evidence.Callee);
                }

                if (depths.ContainsKey(calleeIdentity.Id))
                {
                    if (IsCycleEdge(currentId, calleeIdentity.Id, sourceIdentity.Id, predecessors))
                    {
                        var cycleKey = CreateFlowEdgeKey(evidence);
                        if (cycleEdgeKeys.Add(cycleKey))
                        {
                            cycleEdges.Add(evidence);
                        }
                    }

                    continue;
                }

                depths.Add(calleeIdentity.Id, depth + 1);
                predecessors.Add(calleeIdentity.Id, evidence);
                if (string.Equals(calleeIdentity.Id, targetIdentity.Id, StringComparison.Ordinal))
                {
                    var pathEdges = ReconstructCodeExplorePath(sourceIdentity.Id, targetIdentity.Id, predecessors);
                    var pathNodeIds = CreateFlowPathNodeIds(pathEdges).ToHashSet(StringComparer.Ordinal);
                    if (searchedEdges < limits.MaximumFlowEdges && traversalBudget.HasEdgeCapacity(limits.MaximumFlowEdges))
                    {
                        var targetOutgoing = await FindOutgoingAsync(evidence.Callee, snapshot.Solution, cancellationToken);
                        foreach (var targetEvidence in targetOutgoing
                            .Select(item => new CodeExploreCallEvidence(item.Caller, item.Callee, item.Site))
                            .OrderBy(item => DispatchSortRank(ClassifyDispatch(item.Callee)))
                            .ThenBy(item => CreateIdentity(item.Callee).Id, StringComparer.Ordinal)
                            .ThenBy(item => item.Site?.SourceTree?.FilePath ?? string.Empty, PathComparer)
                            .ThenBy(item => item.Site?.SourceSpan.Start ?? -1))
                        {
                            var targetEdgeKey = CreateFlowEdgeKey(targetEvidence);
                            if (searchedEdges >= limits.MaximumFlowEdges
                                || !traversalBudget.TryAddEdge(targetEdgeKey, limits.MaximumFlowEdges))
                            {
                                break;
                            }

                            searchedEdges++;
                            var targetCalleeId = CreateIdentity(targetEvidence.Callee).Id;
                            if (!pathNodeIds.Contains(targetCalleeId)
                                || !IsCycleEdge(calleeIdentity.Id, targetCalleeId, sourceIdentity.Id, predecessors))
                            {
                                continue;
                            }

                            var cycleKey = CreateFlowEdgeKey(targetEvidence);
                            if (cycleEdgeKeys.Add(cycleKey))
                            {
                                cycleEdges.Add(targetEvidence);
                            }
                        }
                    }

                    var found = new CodeExplorePathSearchResult(
                        pathEdges,
                        cycleEdges,
                        true,
                        depthReached,
                        nodeReached,
                        edgeReached,
                        "Compiler-proven path found.");
                    pathSearchCache[cacheKey] = found;
                    return found;
                }

                if (IsAmbiguousDispatch(evidence.Callee)
                    && await IsDispatchImplementationTargetAsync(evidence.Callee, target, snapshot.Solution, cancellationToken))
                {
                    var frontierPath = ReconstructCodeExplorePath(sourceIdentity.Id, calleeIdentity.Id, predecessors);
                    if (dispatchFrontier is null || frontierPath.Count < dispatchFrontier.Edges.Count)
                    {
                        dispatchFrontier = new CodeExplorePathSearchResult(
                            frontierPath,
                            cycleEdges,
                            false,
                            depthReached,
                            nodeReached,
                            edgeReached,
                            "The static path reaches a compiler-known dispatch boundary; the requested target is a possible implementation branch, not a proven runtime continuation.");
                    }
                }

                pending.Enqueue(evidence.Callee);
            }

            if (nodeReached || edgeReached)
            {
                break;
            }
        }

        if (dispatchFrontier is not null)
        {
            dispatchFrontier = dispatchFrontier with
            {
                DepthLimitReached = depthReached,
                NodeLimitReached = nodeReached,
                EdgeLimitReached = edgeReached,
            };
            pathSearchCache[cacheKey] = dispatchFrontier;
            return dispatchFrontier;
        }

        var reason = nodeReached || edgeReached || depthReached
            ? "No compiler-proven path was found before traversal limits were reached."
            : "No compiler-proven directed call path was found between the anchors in the loaded semantic snapshot.";
        var noPath = new CodeExplorePathSearchResult([], cycleEdges, false, depthReached, nodeReached, edgeReached, reason);
        pathSearchCache[cacheKey] = noPath;
        return noPath;
    }

    private static IReadOnlyList<CodeExploreCallEvidence> ReconstructCodeExplorePath(
        string sourceId,
        string targetId,
        IReadOnlyDictionary<string, CodeExploreCallEvidence> predecessors)
    {
        var edges = new List<CodeExploreCallEvidence>();
        var current = targetId;
        while (!string.Equals(current, sourceId, StringComparison.Ordinal))
        {
            if (!predecessors.TryGetValue(current, out var evidence))
            {
                return [];
            }

            edges.Add(evidence);
            current = CreateIdentity(evidence.Caller).Id;
        }

        edges.Reverse();
        return edges;
    }

    private static bool IsCycleEdge(
        string callerId,
        string calleeId,
        string sourceId,
        IReadOnlyDictionary<string, CodeExploreCallEvidence> predecessors)
    {
        if (string.Equals(callerId, calleeId, StringComparison.Ordinal))
        {
            return true;
        }

        var current = callerId;
        while (!string.Equals(current, sourceId, StringComparison.Ordinal))
        {
            if (!predecessors.TryGetValue(current, out var evidence))
            {
                return false;
            }

            current = CreateIdentity(evidence.Caller).Id;
            if (string.Equals(current, calleeId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return string.Equals(sourceId, calleeId, StringComparison.Ordinal);
    }

    private static async Task<bool> IsDispatchImplementationTargetAsync(
        ISymbol dispatchRoot,
        ISymbol target,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var targetId = CreateIdentity(target).Id;
        var implementations = await FindDispatchImplementationSymbolsAsync(dispatchRoot, solution, cancellationToken);
        return implementations.Any(implementation =>
            string.Equals(CreateIdentity(implementation).Id, targetId, StringComparison.Ordinal));
    }

    private static CodeExplorePairPathCandidate SelectCodeExplorePairPathCandidate(
        CodeExploreFlowAnchor first,
        CodeExploreFlowAnchor second,
        CodeExplorePathSearchResult forward,
        CodeExplorePathSearchResult reverse)
    {
        var forwardCandidate = new CodeExplorePairPathCandidate(first, second, forward);
        var reverseCandidate = new CodeExplorePairPathCandidate(second, first, reverse);
        return CompareCodeExplorePathCandidates(forwardCandidate, reverseCandidate) <= 0
            ? forwardCandidate
            : reverseCandidate;
    }

    private static int CompareCodeExplorePathCandidates(
        CodeExplorePairPathCandidate left,
        CodeExplorePairPathCandidate right)
    {
        var completeCompare = right.Result.IsComplete.CompareTo(left.Result.IsComplete);
        if (completeCompare != 0)
        {
            return completeCompare;
        }

        var evidenceCompare = (right.Result.Edges.Count > 0).CompareTo(left.Result.Edges.Count > 0);
        if (evidenceCompare != 0)
        {
            return evidenceCompare;
        }

        var lengthCompare = left.Result.Edges.Count.CompareTo(right.Result.Edges.Count);
        if (lengthCompare != 0)
        {
            return lengthCompare;
        }

        var dispatchCompare = left.Result.Edges.Sum(edge => DispatchSortRank(ClassifyDispatch(edge.Callee)))
            .CompareTo(right.Result.Edges.Sum(edge => DispatchSortRank(ClassifyDispatch(edge.Callee))));
        if (dispatchCompare != 0)
        {
            return dispatchCompare;
        }

        var fromCompare = string.Compare(left.From.Identity.Id, right.From.Identity.Id, StringComparison.Ordinal);
        return fromCompare != 0
            ? fromCompare
            : string.Compare(left.To.Identity.Id, right.To.Identity.Id, StringComparison.Ordinal);
    }

    private static CodeExploreFlowPath CreateIncompleteCodeExploreFlowPath(CodeExplorePairPathCandidate candidate)
    {
        return new CodeExploreFlowPath(
            candidate.From.Identity.Id,
            candidate.To.Identity.Id,
            [candidate.From.Identity.Id, candidate.To.Identity.Id],
            [],
            false,
            candidate.Result.Reason);
    }

    private static int DispatchSortRank(CallDispatchKind kind)
    {
        return kind switch
        {
            CallDispatchKind.Direct => 0,
            CallDispatchKind.Static => 1,
            CallDispatchKind.Constructor => 2,
            CallDispatchKind.Extension => 3,
            CallDispatchKind.LocalFunction => 4,
            CallDispatchKind.Interface => 5,
            CallDispatchKind.Virtual => 6,
            CallDispatchKind.Delegate => 7,
            _ => 8,
        };
    }

    private static IReadOnlyList<string> CreateFlowPathNodeIds(IReadOnlyList<CodeExploreCallEvidence> edges)
    {
        if (edges.Count == 0)
        {
            return [];
        }

        var nodes = new List<string> { CreateIdentity(edges[0].Caller).Id };
        nodes.AddRange(edges.Select(edge => CreateIdentity(edge.Callee).Id));
        return nodes;
    }

    private static int IndexOfSymbolId(IReadOnlyList<string> symbolIds, string symbolId)
    {
        for (var index = 0; index < symbolIds.Count; index++)
        {
            if (string.Equals(symbolIds[index], symbolId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return 0;
    }

    private static void AddOrUpdateFlowNode(
        Dictionary<string, CodeExploreFlowNodeDraft> nodes,
        ISymbol symbol,
        IReadOnlySet<string> namedIds,
        int depth)
    {
        var identity = CreateIdentity(symbol);
        var role = namedIds.Contains(identity.Id)
            ? CodeExploreFlowNodeRole.NamedAnchor
            : CodeExploreFlowNodeRole.Connector;
        if (nodes.TryGetValue(identity.Id, out var existing))
        {
            var effectiveRole = existing.Role == CodeExploreFlowNodeRole.NamedAnchor || role == CodeExploreFlowNodeRole.NamedAnchor
                ? CodeExploreFlowNodeRole.NamedAnchor
                : existing.Role;
            nodes[identity.Id] = existing with
            {
                Role = effectiveRole,
                Depth = Math.Min(existing.Depth, depth),
            };
            return;
        }

        nodes.Add(identity.Id, new CodeExploreFlowNodeDraft(symbol, role, depth));
    }

    private static async Task AddFlowSourceCandidateAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        ISymbol symbol,
        HashSet<string> candidateSymbolsAdded,
        List<CodeExploreSectionCandidate> sourceCandidates,
        string selectionReason,
        int priority,
        CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(symbol);
        if (!candidateSymbolsAdded.Add(identity.Id))
        {
            return;
        }

        var anchor = new CodeExploreAnchor(
            CodeExploreAnchorKind.SymbolId,
            identity.Id,
            null,
            null,
            false,
            CodeExplorePathSelectionMode.Auto,
            null,
            null,
            null);
        await AddSymbolSourceCandidatesAsync(
            snapshot,
            projection,
            sourceReader,
            symbol,
            anchor,
            selectionReason,
            priority,
            sourceCandidates,
            cancellationToken);
    }

    private static async Task AddFlowCallSiteSourceCandidateAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        ISymbol caller,
        Location? callSite,
        List<CodeExploreSectionCandidate> sourceCandidates,
        CancellationToken cancellationToken)
    {
        if (callSite?.SourceTree is null)
        {
            return;
        }

        var filePath = callSite.SourceTree.FilePath;
        if (string.IsNullOrWhiteSpace(filePath)
            || !Path.IsPathRooted(filePath)
            || !sourceReader.IsPathAllowed(filePath))
        {
            return;
        }

        var document = snapshot.Solution.GetDocument(callSite.SourceTree);
        if (document is null)
        {
            return;
        }

        var text = await document.GetTextAsync(cancellationToken);
        var safeStart = Math.Min(callSite.SourceSpan.Start, text.Length);
        var safeEnd = Math.Min(callSite.SourceSpan.End, text.Length);
        var startLine = text.Lines.GetLineFromPosition(safeStart);
        var endLine = text.Lines.GetLineFromPosition(Math.Max(safeEnd - 1, safeStart));
        var span = TextSpan.FromBounds(startLine.Start, endLine.EndIncludingLineBreak);
        var identity = CreateIdentity(caller);
        var location = ToCodeExploreLocation(
            CreateDocumentLocation(document, callSite.SourceTree, span, projection),
            snapshot.RepositoryPath);
        sourceCandidates.Add(new CodeExploreSectionCandidate(
            document,
            document.FilePath ?? filePath,
            span,
            identity,
            location,
            CodeExploreAnchorKind.SymbolId,
            identity.Id,
            "Compiler-proven flow call-site source.",
            1,
            startLine.LineNumber + 1,
            endLine.LineNumber + 1,
            false,
            CodeExplorePathSelectionMode.ExactLineRange,
            null,
            null,
            null,
            null,
            null));
    }

    private static async Task<bool> IsPolicyAllowedFlowSymbolAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        if (!HasSourceEvidence(symbol))
        {
            return true;
        }

        var location = await FirstCodeExploreLocationAsync(
            snapshot,
            projection,
            sourceReader,
            symbol,
            cancellationToken);
        return location is not null;
    }

    private static bool HasSourceEvidence(ISymbol symbol)
    {
        return symbol.DeclaringSyntaxReferences.Length > 0
            || symbol.Locations.Any(location => location.IsInSource);
    }

    private static string CreateFlowEdgeKey(CodeExploreCallEvidence evidence)
    {
        var callerId = CreateIdentity(evidence.Caller).Id;
        var calleeId = CreateIdentity(evidence.Callee).Id;
        var path = evidence.Site?.SourceTree?.FilePath ?? string.Empty;
        var span = evidence.Site?.SourceSpan ?? default;
        return $"{callerId}|{calleeId}|{path}|{span.Start}|{span.End}";
    }

    private static void AddFlowBoundary(
        List<CodeExploreFlowBoundary> boundaries,
        ISymbol callee,
        CallDispatchKind dispatchKind,
        CodeExploreLocation? callSite)
    {
        var kind = dispatchKind switch
        {
            CallDispatchKind.Interface or CallDispatchKind.Virtual => CodeExploreFlowBoundaryKind.RuntimeDispatch,
            CallDispatchKind.Delegate => CodeExploreFlowBoundaryKind.Delegate,
            CallDispatchKind.Unknown => CodeExploreFlowBoundaryKind.Unknown,
            _ => (CodeExploreFlowBoundaryKind?)null,
        };
        if (kind is null)
        {
            return;
        }

        var identity = CreateIdentity(callee);
        if (boundaries.Any(boundary => boundary.Kind == kind.Value
            && string.Equals(boundary.SymbolId, identity.Id, StringComparison.Ordinal)
            && Equals(boundary.CallSite, callSite)))
        {
            return;
        }

        var reason = kind.Value switch
        {
            CodeExploreFlowBoundaryKind.RuntimeDispatch => "Runtime dispatch may choose implementations not proven by this selected path; compiler-known branches are returned separately when available.",
            CodeExploreFlowBoundaryKind.Delegate => "Delegate invocation targets are runtime values and are not invented by code_explore.",
            _ => "The compiler could not classify a safe static continuation for this call site.",
        };
        boundaries.Add(new CodeExploreFlowBoundary(
            kind.Value,
            identity.Id,
            callSite,
            reason,
            [identity.Id]));
    }

    private static async Task AddUnresolvedCallBoundariesAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        ISymbol caller,
        HashSet<string> visitedCallerIds,
        List<CodeExploreFlowBoundary> boundaries,
        List<string> omissions,
        int maximumBoundaries,
        CancellationToken cancellationToken)
    {
        var callerIdentity = CreateIdentity(caller);
        if (!visitedCallerIds.Add(callerIdentity.Id))
        {
            return;
        }

        foreach (var reference in caller.DeclaringSyntaxReferences
            .OrderBy(reference => reference.SyntaxTree.FilePath, PathComparer)
            .ThenBy(reference => reference.Span.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntaxPath = reference.SyntaxTree.FilePath;
            if (string.IsNullOrWhiteSpace(syntaxPath)
                || !Path.IsPathRooted(syntaxPath)
                || !sourceReader.IsPathAllowed(syntaxPath))
            {
                continue;
            }

            var declaration = await reference.GetSyntaxAsync(cancellationToken);
            var document = snapshot.Solution.GetDocument(declaration.SyntaxTree);
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
                if (info.Symbol is not null)
                {
                    continue;
                }

                var callSite = await CreateCodeExploreLocationAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    expression.GetLocation(),
                    cancellationToken);
                if (callSite is null)
                {
                    continue;
                }

                if (boundaries.Count >= maximumBoundaries)
                {
                    omissions.Add("The flow boundary limit was reached; additional unresolved call-site boundaries were omitted.");
                    return;
                }

                var reason = info.CandidateSymbols.Length > 0
                    ? "The compiler reported candidate call targets at this call site; code_explore did not invent a compiler-proven edge."
                    : "The compiler did not resolve a static call target at this call site; code_explore stopped at an explicit boundary.";
                AddUnknownFlowBoundary(boundaries, callerIdentity.Id, callSite, reason);
            }
        }
    }

    private static void AddUnknownFlowBoundary(
        List<CodeExploreFlowBoundary> boundaries,
        string symbolId,
        CodeExploreLocation callSite,
        string reason)
    {
        if (boundaries.Any(boundary => boundary.Kind == CodeExploreFlowBoundaryKind.Unknown
            && string.Equals(boundary.SymbolId, symbolId, StringComparison.Ordinal)
            && Equals(boundary.CallSite, callSite)))
        {
            return;
        }

        boundaries.Add(new CodeExploreFlowBoundary(
            CodeExploreFlowBoundaryKind.Unknown,
            symbolId,
            callSite,
            reason,
            [symbolId]));
    }

    private static async Task<bool> AddDispatchBranchesAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreRequest request,
        IReadOnlyList<CodeExploreFlowEdge> edges,
        Dictionary<string, CodeExploreFlowNodeDraft> nodeDrafts,
        HashSet<string> candidateSymbolsAdded,
        List<CodeExploreSectionCandidate> sourceCandidates,
        List<CodeExploreDispatchBranch> dispatchBranches,
        List<string> omissions,
        CancellationToken cancellationToken)
    {
        var limitReached = false;
        var returnedTargets = 0;
        var branchRoots = edges
            .Where(edge => edge.DispatchKind is CallDispatchKind.Interface or CallDispatchKind.Virtual)
            .Select(edge => edge.CalleeSymbolId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var rootId in branchRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dispatchBranches.Count >= request.Limits.MaximumDispatchBranches
                || returnedTargets >= request.Limits.MaximumDispatchBranches)
            {
                omissions.Add("The dispatch-branch limit was reached.");
                return true;
            }

            var rootSymbol = nodeDrafts.TryGetValue(rootId, out var draft)
                ? draft.Symbol
                : await ResolveSymbolAsync(snapshot.Solution, rootId, cancellationToken);
            var implementations = await FindDispatchImplementationSymbolsAsync(
                rootSymbol,
                snapshot.Solution,
                cancellationToken);
            var targets = new List<CodeExploreDispatchTarget>();
            foreach (var implementation in implementations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (returnedTargets >= request.Limits.MaximumDispatchBranches)
                {
                    limitReached = true;
                    break;
                }

                var identity = CreateIdentity(implementation);
                var location = await FirstCodeExploreLocationAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    implementation,
                    cancellationToken);
                if (location is null && HasSourceEvidence(implementation))
                {
                    limitReached = true;
                    continue;
                }

                if (!nodeDrafts.ContainsKey(identity.Id) && nodeDrafts.Count >= request.Limits.MaximumFlowNodes)
                {
                    limitReached = true;
                    break;
                }

                targets.Add(new CodeExploreDispatchTarget(identity, location, null));
                returnedTargets++;
                if (nodeDrafts.TryGetValue(identity.Id, out var existingDraft))
                {
                    nodeDrafts[identity.Id] = existingDraft with
                    {
                        Role = existingDraft.Role == CodeExploreFlowNodeRole.NamedAnchor
                            ? CodeExploreFlowNodeRole.NamedAnchor
                            : CodeExploreFlowNodeRole.DispatchBranch,
                        Depth = Math.Min(existingDraft.Depth, 1),
                    };
                }
                else
                {
                    nodeDrafts.Add(identity.Id, new CodeExploreFlowNodeDraft(
                        implementation,
                        CodeExploreFlowNodeRole.DispatchBranch,
                        1));
                }

                await AddFlowSourceCandidateAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    implementation,
                    candidateSymbolsAdded,
                    sourceCandidates,
                    "Compiler-known dispatch branch source.",
                    4,
                    cancellationToken);
            }

            var rootEdge = edges.First(edge => string.Equals(edge.CalleeSymbolId, rootId, StringComparison.Ordinal));
            var branchOmissions = implementations.Count > targets.Count
                ? [$"{implementations.Count - targets.Count} compiler-known implementation or override branches were omitted by branch limits or path policy."]
                : Array.Empty<string>();
            if (branchOmissions.Length > 0)
            {
                limitReached = true;
            }

            dispatchBranches.Add(new CodeExploreDispatchBranch(
                CreateIdentity(rootSymbol),
                rootEdge.CallSite,
                targets,
                targets.Count,
                implementations.Count,
                branchOmissions));
        }

        if (limitReached)
        {
            omissions.Add("One or more compiler-known dispatch branches were omitted by branch, node, or path-policy limits.");
        }

        return limitReached;
    }

    private static async Task<IReadOnlyList<ISymbol>> FindDispatchImplementationSymbolsAsync(
        ISymbol rootSymbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var implementations = new List<ISymbol>();
        var directImplementations = await SymbolFinder.FindImplementationsAsync(
            rootSymbol,
            solution,
            cancellationToken: cancellationToken);
        implementations.AddRange(directImplementations);
        if (rootSymbol is IMethodSymbol method)
        {
            if (method.ContainingType?.TypeKind == TypeKind.Interface)
            {
                var typeImplementations = await SymbolFinder.FindImplementationsAsync(
                    method.ContainingType,
                    solution,
                    cancellationToken: cancellationToken);
                foreach (var type in typeImplementations.OfType<INamedTypeSymbol>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mapped = type.FindImplementationForInterfaceMember(method);
                    if (mapped is not null)
                    {
                        implementations.Add(mapped);
                        continue;
                    }

                    implementations.AddRange(type.GetMembers(method.Name)
                        .OfType<IMethodSymbol>()
                        .Where(candidate => MethodSignaturesMatch(method, candidate)));
                }
            }

            var overrideRoots = implementations
                .OfType<IMethodSymbol>()
                .Append(method)
                .Where(candidate => candidate.IsVirtual || candidate.IsAbstract || candidate.IsOverride)
                .Distinct(SymbolEqualityComparer.Default)
                .OfType<IMethodSymbol>()
                .ToArray();
            foreach (var overrideRoot in overrideRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var overrides = await SymbolFinder.FindOverridesAsync(
                    overrideRoot,
                    solution,
                    cancellationToken: cancellationToken);
                implementations.AddRange(overrides);
            }
        }

        return implementations
            .Select(symbol => symbol.OriginalDefinition)
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(symbol => CreateIdentity(symbol).Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MethodSignaturesMatch(IMethodSymbol expected, IMethodSymbol candidate)
    {
        return string.Equals(expected.Name, candidate.Name, StringComparison.Ordinal)
            && expected.TypeParameters.Length == candidate.TypeParameters.Length
            && expected.Parameters.Length == candidate.Parameters.Length
            && expected.Parameters.Zip(candidate.Parameters).All(pair =>
                pair.First.RefKind == pair.Second.RefKind
                && string.Equals(
                    pair.First.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    pair.Second.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    StringComparison.Ordinal));
    }

    private static async Task<CodeExploreBlastRadius> BuildCodeExploreBlastRadiusAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreRequest request,
        IReadOnlyList<CodeExploreFlowAnchor> anchors,
        List<CodeExploreSectionCandidate> sourceCandidates,
        CancellationToken cancellationToken)
    {
        var items = new List<CodeExploreBlastRadiusItem>();
        var omissions = new List<string>();
        var continuations = new List<CodeExploreContinuationTarget>();
        var candidateSymbolsAdded = new HashSet<string>(
            sourceCandidates
                .Select(candidate => candidate.Identity?.Id)
                .Where(id => id is not null)
                .Select(id => id ?? string.Empty),
            StringComparer.Ordinal);
        var countedProjectIds = new HashSet<ProjectId>();
        var countedTestProjectIds = new HashSet<ProjectId>();
        var returnedProjectIds = new HashSet<ProjectId>();
        var returnedTestProjectIds = new HashSet<ProjectId>();
        var totalCallers = 0;
        var totalImplementations = 0;
        var totalProjects = 0;
        var totalTests = 0;

        foreach (var anchor in anchors.OrderBy(anchor => anchor.Identity.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callers = (await SymbolFinder.FindCallersAsync(
                    anchor.Symbol,
                    snapshot.Solution,
                    cancellationToken))
                .Select(caller => caller.CallingSymbol)
                .Distinct(SymbolEqualityComparer.Default)
                .OrderBy(symbol => CreateIdentity(symbol).Id, StringComparer.Ordinal)
                .ToArray();
            totalCallers += callers.Length;
            foreach (var caller in callers)
            {
                if (!await TryAddBlastSymbolItemAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    request,
                    anchor.Identity.Id,
                    caller,
                    ImpactKind.Caller,
                    "Caller directly invokes a primary code_explore anchor.",
                    items,
                    omissions,
                    candidateSymbolsAdded,
                    sourceCandidates,
                    cancellationToken))
                {
                    AddBlastContinuation(continuations, snapshot, anchor.Identity.Id);
                    break;
                }
            }

            var implementations = (await SymbolFinder.FindImplementationsAsync(
                    anchor.Symbol,
                    snapshot.Solution,
                    cancellationToken: cancellationToken))
                .Distinct(SymbolEqualityComparer.Default)
                .OrderBy(symbol => CreateIdentity(symbol).Id, StringComparer.Ordinal)
                .ToArray();
            totalImplementations += implementations.Length;
            foreach (var implementation in implementations)
            {
                if (!await TryAddBlastSymbolItemAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    request,
                    anchor.Identity.Id,
                    implementation,
                    ImpactKind.Implementation,
                    "Symbol implements or overrides a primary code_explore anchor.",
                    items,
                    omissions,
                    candidateSymbolsAdded,
                    sourceCandidates,
                    cancellationToken))
                {
                    AddBlastContinuation(continuations, snapshot, anchor.Identity.Id);
                    break;
                }
            }

            var projects = FindDependentProjects(
                snapshot,
                anchor.Symbol,
                request.Limits.MaximumFlowDepth,
                cancellationToken);
            foreach (var project in projects)
            {
                var kind = IsTestProject(project) ? ImpactKind.Test : ImpactKind.Project;
                if (kind == ImpactKind.Test)
                {
                    if (!countedTestProjectIds.Add(project.Id))
                    {
                        continue;
                    }

                    totalTests++;
                }
                else
                {
                    if (!countedProjectIds.Add(project.Id))
                    {
                        continue;
                    }

                    totalProjects++;
                }

                var returnedIds = kind == ImpactKind.Test ? returnedTestProjectIds : returnedProjectIds;
                if (!returnedIds.Add(project.Id))
                {
                    continue;
                }

                if (items.Count >= request.Limits.MaximumBlastRadiusItems)
                {
                    AddBlastContinuation(continuations, snapshot, anchor.Identity.Id);
                    continue;
                }

                var reason = kind == ImpactKind.Test
                    ? "Test project directly or transitively depends on a project containing primary anchor evidence."
                    : "Project directly or transitively depends on a project containing primary anchor evidence.";
                items.Add(new CodeExploreBlastRadiusItem(
                    anchor.Identity.Id,
                    kind,
                    null,
                    null,
                    project.Name,
                    reason));
            }
        }

        if (items.Count >= request.Limits.MaximumBlastRadiusItems
            && (totalCallers + totalImplementations + totalProjects + totalTests) > items.Count)
        {
            omissions.Add("The compact blast-radius item limit was reached.");
        }

        omissions.Add("Blast-radius evidence is compiler/project metadata only and is not exhaustive validation scope.");
        return new CodeExploreBlastRadius(
            items,
            items.Count(item => item.Kind == ImpactKind.Caller),
            totalCallers,
            items.Count(item => item.Kind == ImpactKind.Implementation),
            totalImplementations,
            items.Count(item => item.Kind == ImpactKind.Project),
            totalProjects,
            items.Count(item => item.Kind == ImpactKind.Test),
            totalTests,
            omissions.Distinct(StringComparer.Ordinal).ToArray(),
            continuations.DistinctBy(target => $"{target.Kind}:{target.Anchor}:{target.WorkspaceGeneration}:{target.Reason}").ToArray());
    }

    private static async Task<bool> TryAddBlastSymbolItemAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreRequest request,
        string anchorSymbolId,
        ISymbol symbol,
        ImpactKind kind,
        string reason,
        List<CodeExploreBlastRadiusItem> items,
        List<string> omissions,
        HashSet<string> candidateSymbolsAdded,
        List<CodeExploreSectionCandidate> sourceCandidates,
        CancellationToken cancellationToken)
    {
        if (items.Count >= request.Limits.MaximumBlastRadiusItems)
        {
            return false;
        }

        var identity = CreateIdentity(symbol);
        var location = await FirstCodeExploreLocationAsync(
            snapshot,
            projection,
            sourceReader,
            symbol,
            cancellationToken);
        if (location is null && HasSourceEvidence(symbol))
        {
            omissions.Add("Blast-radius symbols outside the invocation path policy were omitted.");
            return true;
        }

        items.Add(new CodeExploreBlastRadiusItem(
            anchorSymbolId,
            kind,
            identity,
            location,
            location?.ProjectName,
            reason));
        await AddFlowSourceCandidateAsync(
            snapshot,
            projection,
            sourceReader,
            symbol,
            candidateSymbolsAdded,
            sourceCandidates,
            "Compact blast-radius declaration source.",
            5,
            cancellationToken);
        return true;
    }

    private static IReadOnlyList<Project> FindDependentProjects(
        AdvancedSemanticSnapshot snapshot,
        ISymbol symbol,
        int maximumDepth,
        CancellationToken cancellationToken)
    {
        var rootProjectIds = new HashSet<ProjectId>();
        foreach (var location in symbol.Locations.Where(location => location.IsInSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (location.SourceTree is null)
            {
                continue;
            }

            var document = snapshot.Solution.GetDocument(location.SourceTree);
            if (document is not null)
            {
                rootProjectIds.Add(document.Project.Id);
            }
        }

        var projects = snapshot.Solution.Projects
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToArray();
        var seen = new HashSet<ProjectId>(rootProjectIds);
        var dependents = new List<Project>();
        var pending = new Queue<(ProjectId ProjectId, int Depth)>(rootProjectIds.Select(id => (id, 0)));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (var projectId, var depth) = pending.Dequeue();
            if (depth >= maximumDepth)
            {
                continue;
            }

            foreach (var project in projects.Where(project => project.ProjectReferences.Any(reference => reference.ProjectId == projectId)))
            {
                if (!seen.Add(project.Id))
                {
                    continue;
                }

                dependents.Add(project);
                pending.Enqueue((project.Id, depth + 1));
            }
        }

        return dependents
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddBlastContinuation(
        List<CodeExploreContinuationTarget> continuations,
        AdvancedSemanticSnapshot snapshot,
        string symbolId)
    {
        continuations.Add(new CodeExploreContinuationTarget(
            CodeExploreAnchorKind.SymbolId,
            symbolId,
            null,
            null,
            null,
            false,
            null,
            null,
            snapshot.Generation,
            "Retry symbol_impact or increase maximumBlastRadiusItems for more compact impact evidence."));
    }

    private static async Task<IReadOnlyList<CodeExploreLocation>> CreateCodeExploreLocationsAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var locations = new List<CodeExploreLocation>();
        foreach (var location in symbol.Locations
            .Where(location => location.IsInSource)
            .OrderBy(location => location.SourceTree?.FilePath ?? string.Empty, PathComparer)
            .ThenBy(location => location.SourceSpan.Start))
        {
            var projected = await CreateCodeExploreLocationAsync(
                snapshot,
                projection,
                sourceReader,
                location,
                cancellationToken);
            if (projected is not null)
            {
                locations.Add(projected);
            }
        }

        return locations;
    }

    private static async Task<CodeExploreLocation?> CreateCodeExploreLocationAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        Location? location,
        CancellationToken cancellationToken)
    {
        if (location is null)
        {
            return null;
        }

        var semanticLocation = await CreateLocationAsync(snapshot, projection, location, cancellationToken);
        if (semanticLocation is null || !sourceReader.IsPathAllowed(semanticLocation.FilePath))
        {
            return null;
        }

        return ToCodeExploreLocation(semanticLocation, snapshot.RepositoryPath);
    }

    private static CodeExploreFlow? AttachCodeExploreSourceSections(
        CodeExploreFlow? flow,
        IReadOnlyList<CodeExploreFileSection> sections)
    {
        if (flow is null)
        {
            return null;
        }

        var sourceIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < sections.Count; index++)
        {
            foreach (var identity in sections[index].SemanticIdentities)
            {
                sourceIndexes.TryAdd(identity.Id, index);
            }
        }

        var nodes = flow.Nodes
            .Select(node => node with { SourceSectionIndex = FindSourceSectionIndex(sourceIndexes, node.Symbol.Id, node.SourceSectionIndex) })
            .ToArray();
        var branches = flow.DispatchBranches
            .Select(branch => branch with
            {
                Implementations = branch.Implementations
                    .Select(target => target with { SourceSectionIndex = FindSourceSectionIndex(sourceIndexes, target.Symbol.Id, target.SourceSectionIndex) })
                    .ToArray(),
            })
            .ToArray();
        return flow with
        {
            Nodes = nodes,
            DispatchBranches = branches,
        };
    }

    private static int? FindSourceSectionIndex(
        IReadOnlyDictionary<string, int> indexes,
        string symbolId,
        int? fallback)
    {
        return indexes.TryGetValue(symbolId, out var index) ? index : fallback;
    }

    private static void ValidateCodeExploreRequest(CodeExploreRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentNullException.ThrowIfNull(request.Limits);
        if (request.Query.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Code exploration queries are limited to 1,024 characters.");
        }

        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Code exploration mode is not supported.");
        }

        var limits = request.Limits;
        if (limits.MaximumAnchors is < 1 or > 16
            || limits.MaximumAlternatives is < 1 or > 25
            || limits.MaximumFiles is < 1 or > 16
            || limits.MaximumSourceCharacters is < 1 or > 100_000
            || limits.MaximumPerFileSourceCharacters is < 1 or > 65_536
            || limits.MaximumFlowPaths is < 1 or > 32
            || limits.MaximumFlowBridgeSymbols is < 0 or > 128
            || limits.MaximumFlowDepth is < 1 or > 8
            || limits.MaximumFlowNodes is < 1 or > 1000
            || limits.MaximumFlowEdges is < 1 or > 5000
            || limits.MaximumDispatchBranches is < 0 or > 200
            || limits.MaximumBlastRadiusItems is < 0 or > 200
            || limits.TimeoutMilliseconds is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Code exploration bounds are outside host limits.");
        }

        var anchorCount = request.ExactSymbolAnchors.Count + request.SymbolIds.Count + request.PathAnchors.Count;
        if (anchorCount > limits.MaximumAnchors)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The request contains more exact anchors than the configured maximum.");
        }

        foreach (var anchor in request.ExactSymbolAnchors.Concat(request.SymbolIds))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor, nameof(request));
            if (anchor.Length > 2048)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Symbol anchors are limited to 2,048 characters.");
            }
        }

        foreach (var anchor in request.PathAnchors)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Path);
            var invalidPathMode = !Enum.IsDefined(anchor.SelectionMode);
            var missingRequiredLine = RequiresLine(anchor.SelectionMode) && anchor.Line is null;
            var missingRequiredEndLine = anchor.SelectionMode == CodeExplorePathSelectionMode.ExactLineRange && anchor.EndLine is null;
            if (anchor.Path.Length > 4096
                || anchor.Line is <= 0
                || anchor.EndLine is <= 0
                || anchor.EndLine < anchor.Line
                || invalidPathMode
                || missingRequiredLine
                || missingRequiredEndLine)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Path anchors must be bounded and use valid positive one-based line ranges for their selection mode.");
            }

            var invalidExpectedDigest = anchor.ExpectedFileSha256 is { } expectedFileSha256
                && !IsSha256Hex(expectedFileSha256);
            if (anchor.ExpectedWorkspaceGeneration is < 0 || invalidExpectedDigest)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Continuation cursor fields are outside host limits.");
            }
        }
    }

    private static IReadOnlyList<CodeExploreAnchor> BuildCodeExploreAnchors(CodeExploreRequest request)
    {
        var anchors = new List<CodeExploreAnchor>();
        anchors.AddRange(request.PathAnchors.Select(anchor => new CodeExploreAnchor(
            CodeExploreAnchorKind.Path,
            anchor.Path,
            anchor.Line,
            anchor.EndLine,
            anchor.StartAtLine,
            anchor.SelectionMode,
            anchor.ExpectedFileSha256,
            anchor.ExpectedWorkspaceGeneration,
            null)));
        anchors.AddRange(request.SymbolIds.Select(symbolId => new CodeExploreAnchor(
            CodeExploreAnchorKind.SymbolId,
            symbolId,
            null,
            null,
            false,
            CodeExplorePathSelectionMode.Auto,
            null,
            null,
            null)));
        anchors.AddRange(request.ExactSymbolAnchors.Select(anchor => new CodeExploreAnchor(
            CodeExploreAnchorKind.SymbolName,
            anchor,
            null,
            null,
            false,
            CodeExplorePathSelectionMode.Auto,
            null,
            null,
            null)));
        if (anchors.Count > 0)
        {
            return anchors;
        }

        if (IsCSharpPathSpan(request.Query) || IsExactSymbolAnchor(request.Query))
        {
            return [new CodeExploreAnchor(CodeExploreAnchorKind.Query, request.Query, null, null, false, CodeExplorePathSelectionMode.Auto, null, null, null)];
        }

        return [];
    }

    private async Task<NaturalLanguageCodeExploreDiscovery> DiscoverNaturalLanguageCodeExploreAsync(
        WorkspaceId workspaceId,
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreRequest request,
        CodeExploreQueryInterpretation interpretation,
        CancellationToken cancellationToken)
    {
        var catalog = await GetOrBuildCodeExploreCatalogAsync(
            workspaceId,
            snapshot,
            projection,
            cancellationToken);
        var allowedEntries = GetAllowedCodeExploreCatalogEntries(catalog, sourceReader, cancellationToken);
        var ranked = RankNaturalLanguageCandidates(
            allowedEntries,
            interpretation,
            cancellationToken);
        ranked = await ApplyGraphConnectivityAsync(
            snapshot,
            ranked,
            cancellationToken);
        var rankedByIdentity = ranked
            .GroupBy(candidate => candidate.Entry.Identity.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var anchors = new List<CodeExploreAnchor>();
        var summaries = new List<CodeExploreCandidateSummary>();
        var selectedIdentityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbolId in interpretation.StableSymbolIds.Take(request.Limits.MaximumAnchors))
        {
            var allocationRank = anchors.Count + 1;
            anchors.Add(new CodeExploreAnchor(
                CodeExploreAnchorKind.SymbolId,
                symbolId,
                null,
                null,
                false,
                CodeExplorePathSelectionMode.Auto,
                null,
                null,
                allocationRank));
            selectedIdentityIds.Add(symbolId);
            summaries.Add(new CodeExploreCandidateSummary(
                null,
                null,
                null,
                CodeExploreCandidateTier.Pinned,
                CodeExploreSelectionReason.Pinned | CodeExploreSelectionReason.ExactIdentifier,
                summaries.Count + 1,
                true,
                "Natural-language query contained a stable semantic symbol id.",
                symbolId));
        }

        foreach (var path in interpretation.PathLikeSpans
            .Where(IsCSharpPathSpan)
            .Distinct(PathComparer)
            .Take(Math.Max(0, request.Limits.MaximumAnchors - anchors.Count)))
        {
            var allocationRank = anchors.Count + 1;
            anchors.Add(new CodeExploreAnchor(
                CodeExploreAnchorKind.Path,
                path,
                null,
                null,
                false,
                CodeExplorePathSelectionMode.WholeFile,
                null,
                null,
                allocationRank));
            summaries.Add(new CodeExploreCandidateSummary(
                null,
                null,
                path,
                CodeExploreCandidateTier.Pinned,
                CodeExploreSelectionReason.Pinned | CodeExploreSelectionReason.Path,
                summaries.Count + 1,
                true,
                "Natural-language query contained a repository-relative C# path span.",
                path));
        }

        var symbolSlots = Math.Max(0, request.Limits.MaximumAnchors - anchors.Count);
        var selected = rankedByIdentity
            .Where(candidate => !selectedIdentityIds.Contains(candidate.Entry.Identity.Id))
            .Take(symbolSlots)
            .ToArray();
        foreach (var candidate in selected)
        {
            if (!selectedIdentityIds.Add(candidate.Entry.Identity.Id))
            {
                continue;
            }

            anchors.Add(new CodeExploreAnchor(
                CodeExploreAnchorKind.SymbolId,
                candidate.Entry.Identity.Id,
                null,
                null,
                false,
                CodeExplorePathSelectionMode.Auto,
                null,
                null,
                anchors.Count + 1));
        }

        var selectedIds = selectedIdentityIds.ToHashSet(StringComparer.Ordinal);
        var pinnedSummaryCount = summaries.Count;
        var maximumCandidateSummaries = ResolveNaturalLanguageCandidateSummaryLimit(
            request.Limits.MaximumSourceCharacters,
            pinnedSummaryCount);
        summaries.AddRange(rankedByIdentity
            .Take(maximumCandidateSummaries)
            .Select((candidate, index) => CreateCandidateSummary(
                candidate,
                pinnedSummaryCount + index + 1,
                selectedIds.Contains(candidate.Entry.Identity.Id))));
        var ambiguityGroups = CreateAmbiguityGroups(rankedByIdentity, selectedIds);
        var unresolvedTerms = interpretation.Terms
            .Where(term => !ranked.Any(candidate => CandidateCoversTerm(candidate.Entry, term)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateLimitReached = rankedByIdentity.Length > maximumCandidateSummaries;
        var omissions = new List<string>(catalog.Omissions);
        if (candidateLimitReached)
        {
            omissions.Add("Natural-language candidate summaries were capped by the host result limit.");
        }

        if (ranked.Length == 0 && summaries.Count == 0)
        {
            omissions.Add("No compiler-known declarations matched the natural-language query terms.");
        }

        return new NaturalLanguageCodeExploreDiscovery(
            anchors,
            interpretation with { UnresolvedTerms = unresolvedTerms },
            new CodeExploreDiscoverySummary(
                allowedEntries.Length,
                rankedByIdentity.Length + pinnedSummaryCount,
                anchors.Count,
                catalog.IsComplete,
                candidateLimitReached || !catalog.IsComplete,
                ambiguityGroups,
                "request.maximumAnchors, request.maximumFiles, and request.maximumSourceCharacters"),
            [.. summaries],
            unresolvedTerms,
            omissions);
    }

    private async Task<CodeExploreDeclarationCatalog> GetOrBuildCodeExploreCatalogAsync(
        WorkspaceId workspaceId,
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        CancellationToken cancellationToken)
    {
        var key = CreateCodeExploreCatalogKey(workspaceId, snapshot.Generation);
        lock (_catalogGate)
        {
            if (_codeExploreCatalogs.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }

        var catalog = await BuildCodeExploreCatalogAsync(key, snapshot, projection, cancellationToken);
        lock (_catalogGate)
        {
            foreach (var staleKey in _codeExploreCatalogs.Keys
                .Where(item => item.StartsWith(workspaceId.Value.ToString("D"), StringComparison.Ordinal)
                    && !string.Equals(item, key, StringComparison.Ordinal))
                .ToArray())
            {
                _codeExploreCatalogs.Remove(staleKey);
            }

            _codeExploreCatalogs[key] = catalog;
            while (_codeExploreCatalogs.Count > MaximumCodeExploreCatalogs)
            {
                var firstKey = _codeExploreCatalogs.Keys.Order(StringComparer.Ordinal).First();
                _codeExploreCatalogs.Remove(firstKey);
            }
        }

        return catalog;
    }

    private static async Task<CodeExploreDeclarationCatalog> BuildCodeExploreCatalogAsync(
        string key,
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        CancellationToken cancellationToken)
    {
        var entries = new List<CodeExploreDeclarationCatalogEntry>();
        var omissions = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var isComplete = true;
        foreach (var project in snapshot.Solution.Projects
            .Where(project => snapshot.CompiledProjects.Contains(project.Id))
            .OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var semanticModelCache = new Dictionary<DocumentId, SemanticModel>();
            var documents = await GetCodeExploreCatalogDocumentsAsync(project, cancellationToken);
            foreach ((var document, var isSourceGenerated) in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= MaximumCodeExploreCatalogEntries)
                {
                    isComplete = false;
                    omissions.Add("The declaration catalog entry limit was reached; lower-ranked declarations may be absent.");
                    return new CodeExploreDeclarationCatalog(key, snapshot.Generation, entries, isComplete, omissions);
                }

                if (document.FilePath is not { } documentPath || !Path.IsPathRooted(documentPath))
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null)
                {
                    continue;
                }

                if (!semanticModelCache.TryGetValue(document.Id, out var model))
                {
                    model = await document.GetSemanticModelAsync(cancellationToken);
                    if (model is null)
                    {
                        omissions.Add("One loaded C# document was omitted from the declaration catalog because its semantic model was unavailable.");
                        continue;
                    }

                    semanticModelCache.Add(document.Id, model);
                }

                foreach (var declaration in EnumerateCodeExploreCatalogDeclarations(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
                    if (symbol is null)
                    {
                        continue;
                    }

                    var location = CreateDocumentLocation(document, declaration.SyntaxTree, declaration.Span, projection);
                    var entry = CreateCodeExploreCatalogEntry(snapshot, symbol, location, isSourceGenerated);
                    var entryKey = $"{entry.Identity.Id}|{entry.FilePath}|{entry.Range.StartLine}|{entry.Range.StartColumn}|{entry.Range.EndLine}|{entry.Range.EndColumn}";
                    if (seen.Add(entryKey))
                    {
                        entries.Add(entry);
                        if (entries.Count >= MaximumCodeExploreCatalogEntries)
                        {
                            isComplete = false;
                            omissions.Add("The declaration catalog entry limit was reached; lower-ranked declarations may be absent.");
                            return new CodeExploreDeclarationCatalog(key, snapshot.Generation, entries, isComplete, omissions);
                        }
                    }
                }
            }
        }

        if (snapshot.Confidence < SemanticConfidenceLevel.FullSemantic)
        {
            isComplete = false;
            omissions.Add("The declaration catalog was built from partial semantic coverage.");
        }

        return new CodeExploreDeclarationCatalog(key, snapshot.Generation, entries, isComplete, omissions);
    }

    private static async Task<IReadOnlyList<(Document Document, bool IsSourceGenerated)>> GetCodeExploreCatalogDocumentsAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        IEnumerable<Document> generatedDocuments = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
        return
        [
            .. project.Documents
                .Select(document => (Document: document, IsSourceGenerated: false))
                .Concat(generatedDocuments.Select(document => (Document: document, IsSourceGenerated: true)))
                .GroupBy(item => item.Document.Id)
                .Select(group => group.OrderByDescending(item => item.IsSourceGenerated).First())
                .OrderBy(item => item.Document.FilePath ?? item.Document.Name, PathComparer),
        ];
    }

    private static CodeExploreDeclarationCatalogEntry CreateCodeExploreCatalogEntry(
        AdvancedSemanticSnapshot snapshot,
        ISymbol symbol,
        SemanticSourceLocation location,
        bool isSourceGenerated)
    {
        var identity = CreateIdentity(symbol);
        var filePath = location.FilePath;
        var relativePath = ToRepositoryRelativePath(filePath, snapshot.RepositoryPath);
        var displayName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var fullyQualifiedName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var containingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var containingNamespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } namespaceSymbol
            ? namespaceSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            : null;
        var qualifiedNames = CreateCatalogQualifiedNames(symbol, identity, displayName, fullyQualifiedName, containingType, containingNamespace);
        var kindName = symbol is INamedTypeSymbol { IsRecord: true }
            ? "Record"
            : symbol is INamedTypeSymbol namedType ? namedType.TypeKind.ToString() : symbol.Kind.ToString();
        return new CodeExploreDeclarationCatalogEntry(
            identity,
            symbol.Name,
            symbol.MetadataName,
            displayName,
            fullyQualifiedName,
            containingType,
            containingNamespace,
            kindName,
            location.ProjectName,
            location.TargetFramework,
            filePath,
            relativePath,
            location.Range,
            location.IsGenerated || isSourceGenerated,
            location.IsLinked,
            IsTestProjectNameOrPath(location.ProjectName, relativePath),
            CreateCatalogTerms(symbol, kindName, location.ProjectName, relativePath, containingType, containingNamespace),
            qualifiedNames);
    }

    private static IEnumerable<SyntaxNode> EnumerateCodeExploreCatalogDeclarations(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return variable;
                    }

                    break;
                case EventFieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return variable;
                    }

                    break;
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax
                    or BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax
                    or IndexerDeclarationSyntax or OperatorDeclarationSyntax:
                    yield return node;
                    break;
            }
        }
    }

    private static IReadOnlyList<string> CreateCatalogQualifiedNames(
        ISymbol symbol,
        SemanticSymbolIdentity identity,
        string displayName,
        string fullyQualifiedName,
        string? containingType,
        string? containingNamespace)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeComparableName(identity.Id),
            NormalizeComparableName(symbol.Name),
            NormalizeComparableName(symbol.MetadataName),
            NormalizeComparableName(displayName),
            NormalizeComparableName(fullyQualifiedName),
        };
        if (!string.IsNullOrWhiteSpace(containingType))
        {
            names.Add(NormalizeComparableName($"{containingType}.{symbol.Name}"));
        }

        if (!string.IsNullOrWhiteSpace(containingNamespace))
        {
            names.Add(NormalizeComparableName($"{containingNamespace}.{symbol.MetadataName}"));
        }

        return names.Where(name => name.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> CreateCatalogTerms(
        ISymbol symbol,
        string kindName,
        string projectName,
        string relativePath,
        string? containingType,
        string? containingNamespace)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCodeExploreTerms(terms, symbol.Name);
        AddCodeExploreTerms(terms, symbol.MetadataName);
        AddCodeExploreTerms(terms, kindName);
        AddCodeExploreTerms(terms, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        AddCodeExploreTerms(terms, symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        AddCodeExploreTerms(terms, projectName);
        AddCodeExploreTerms(terms, relativePath);
        if (!string.IsNullOrWhiteSpace(containingType))
        {
            AddCodeExploreTerms(terms, containingType);
        }

        if (!string.IsNullOrWhiteSpace(containingNamespace))
        {
            AddCodeExploreTerms(terms, containingNamespace);
        }

        return terms.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static CodeExploreDeclarationCatalogEntry[] GetAllowedCodeExploreCatalogEntries(
        CodeExploreDeclarationCatalog catalog,
        ICodeExploreSourceReader sourceReader,
        CancellationToken cancellationToken)
    {
        var entries = new List<CodeExploreDeclarationCatalogEntry>();
        foreach (var entry in catalog.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceReader.IsPathAllowed(entry.FilePath))
            {
                entries.Add(entry);
            }
        }

        return [.. entries];
    }

    private static CodeExploreRankedCandidate[] RankNaturalLanguageCandidates(
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> entries,
        CodeExploreQueryInterpretation interpretation,
        CancellationToken cancellationToken)
    {
        var termCounts = entries
            .SelectMany(entry => entry.Terms.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(term => term, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var nameCounts = entries
            .GroupBy(entry => NormalizeComparableName(entry.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var preliminary = new List<CodeExploreRankedCandidate>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rank = RankNaturalLanguageCandidate(entry, interpretation, termCounts, nameCounts, entries.Count);
            if (rank is not null)
            {
                preliminary.Add(rank);
            }
        }

        var fileCounts = preliminary
            .GroupBy(candidate => candidate.Entry.FilePath, PathComparer)
            .ToDictionary(group => group.Key, group => group.Count(), PathComparer);
        return OrderNaturalLanguageCandidates(preliminary
            .Select(candidate => fileCounts.GetValueOrDefault(candidate.Entry.FilePath) > 1
                ? candidate with
                {
                    Reasons = candidate.Reasons | CodeExploreSelectionReason.CoLocated,
                    Score = candidate.Score + 60,
                }
                : candidate));
    }

    private static CodeExploreRankedCandidate[] OrderNaturalLanguageCandidates(
        IEnumerable<CodeExploreRankedCandidate> candidates)
    {
        return
        [
            .. candidates
                .OrderBy(candidate => candidate.Tier)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => KindSortRank(candidate.Entry.Kind))
                .ThenBy(candidate => candidate.Entry.RelativeFilePath, PathComparer)
                .ThenBy(candidate => candidate.Entry.Range.StartLine)
                .ThenBy(candidate => candidate.Entry.Identity.Id, StringComparer.Ordinal),
        ];
    }

    private static async Task<CodeExploreRankedCandidate[]> ApplyGraphConnectivityAsync(
        AdvancedSemanticSnapshot snapshot,
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        CancellationToken cancellationToken)
    {
        if (ranked.Count <= 1)
        {
            return [.. ranked];
        }

        var rankedById = ranked
            .GroupBy(candidate => candidate.Entry.Identity.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var seedIds = new HashSet<string>(StringComparer.Ordinal);
        var seeds = ranked
            .Where(candidate => candidate.Tier <= CodeExploreCandidateTier.MultiTermStructural)
            .Take(MaximumNaturalLanguageGraphSeeds)
            .ToArray();
        foreach (var seed in seeds)
        {
            seedIds.Add(seed.Entry.Identity.Id);
        }

        if (seeds.Length == 0)
        {
            return [.. ranked];
        }

        var connectedIds = new HashSet<string>(StringComparer.Ordinal);
        var edgeCount = 0;
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groups = await ResolveSymbolGroupsInSnapshotAsync(
                snapshot,
                seed.Entry.Identity.Id,
                [],
                cancellationToken);
            foreach (var symbol in groups.SelectMany(group => group.Symbols)
                .Distinct(SymbolEqualityComparer.Default)
                .OrderBy(symbol => CreateIdentity(symbol).Id, StringComparer.Ordinal))
            {
                foreach (var connected in await FindNaturalLanguageConnectedSymbolsAsync(snapshot, symbol, cancellationToken))
                {
                    if (edgeCount >= MaximumNaturalLanguageGraphEdges)
                    {
                        return BoostGraphConnectedCandidates(ranked, connectedIds, seedIds);
                    }

                    edgeCount++;
                    var connectedId = CreateIdentity(connected).Id;
                    if (!seedIds.Contains(connectedId) && rankedById.ContainsKey(connectedId))
                    {
                        connectedIds.Add(connectedId);
                    }
                }
            }
        }

        return BoostGraphConnectedCandidates(ranked, connectedIds, seedIds);
    }

    private static async Task<IReadOnlyList<ISymbol>> FindNaturalLanguageConnectedSymbolsAsync(
        AdvancedSemanticSnapshot snapshot,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var connected = new List<ISymbol>();
        var outgoing = await FindOutgoingAsync(symbol, snapshot.Solution, cancellationToken);
        connected.AddRange(outgoing.Select(edge => edge.Callee.OriginalDefinition));
        var callers = await SymbolFinder.FindCallersAsync(
            symbol,
            snapshot.Solution,
            cancellationToken);
        connected.AddRange(callers.Select(caller => caller.CallingSymbol.OriginalDefinition));
        var implementations = await SymbolFinder.FindImplementationsAsync(
            symbol,
            snapshot.Solution,
            cancellationToken: cancellationToken);
        connected.AddRange(implementations.Select(item => item.OriginalDefinition));
        if (symbol is IMethodSymbol method)
        {
            var overrides = await SymbolFinder.FindOverridesAsync(
                method,
                snapshot.Solution,
                cancellationToken: cancellationToken);
            connected.AddRange(overrides.Select(item => item.OriginalDefinition));
        }

        return
        [
            .. connected
                .Where(item => item.Locations.Any(location => location.IsInSource))
                .Distinct(SymbolEqualityComparer.Default)
                .OrderBy(item => CreateIdentity(item).Id, StringComparer.Ordinal),
        ];
    }

    private static CodeExploreRankedCandidate[] BoostGraphConnectedCandidates(
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        IReadOnlySet<string> connectedIds,
        IReadOnlySet<string> seedIds)
    {
        if (connectedIds.Count == 0)
        {
            return [.. ranked];
        }

        return OrderNaturalLanguageCandidates(ranked.Select(candidate =>
        {
            if (!connectedIds.Contains(candidate.Entry.Identity.Id)
                || seedIds.Contains(candidate.Entry.Identity.Id))
            {
                return candidate;
            }

            var reasons = (candidate.Reasons | CodeExploreSelectionReason.GraphConnected) & ~CodeExploreSelectionReason.Peripheral;
            return candidate with
            {
                Tier = MinTier(candidate.Tier, CodeExploreCandidateTier.GraphConnected),
                Reasons = reasons,
                Score = candidate.Score + 260,
            };
        }));
    }

    private static CodeExploreRankedCandidate? RankNaturalLanguageCandidate(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation,
        IReadOnlyDictionary<string, int> termCounts,
        IReadOnlyDictionary<string, int> nameCounts,
        int catalogEntryCount)
    {
        var reasons = CodeExploreSelectionReason.None;
        var tier = CodeExploreCandidateTier.Peripheral;
        var score = 0;
        foreach (var path in interpretation.PathLikeSpans.Where(IsCSharpPathSpan))
        {
            if (PathSpanMatchesEntry(path, entry))
            {
                reasons |= CodeExploreSelectionReason.Path;
                tier = MinTier(tier, CodeExploreCandidateTier.ExactQualified);
                score += 900;
            }
        }

        foreach (var qualified in interpretation.QualifiedNames)
        {
            if (entry.QualifiedNames.Any(name => string.Equals(name, qualified, StringComparison.OrdinalIgnoreCase)))
            {
                reasons |= CodeExploreSelectionReason.QualifiedName;
                tier = MinTier(tier, CodeExploreCandidateTier.ExactQualified);
                score += 850;
            }
        }

        foreach (var identifier in interpretation.ExactIdentifiers.Select(NormalizeComparableName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IdentifierMatchesEntry(identifier, entry))
            {
                continue;
            }

            reasons |= CodeExploreSelectionReason.ExactIdentifier;
            var count = nameCounts.GetValueOrDefault(identifier);
            if (count is > 0 and <= 5)
            {
                tier = MinTier(tier, CodeExploreCandidateTier.DistinctiveIdentifier);
                score += 700;
            }
            else
            {
                score += 220;
            }
        }

        var coveredTerms = interpretation.Terms
            .Where(term => CandidateCoversTerm(entry, term))
            .Select(CanonicalCodeExploreTerm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var uncommonTermCount = coveredTerms.Count(term => !IsCommonCodeExploreTerm(term, termCounts, catalogEntryCount));
        if (coveredTerms.Length >= 2 && uncommonTermCount > 0)
        {
            reasons |= CodeExploreSelectionReason.MultiTerm;
            tier = MinTier(tier, CodeExploreCandidateTier.MultiTermStructural);
            score += 360 + (coveredTerms.Length * 70) + (uncommonTermCount * 50);
        }
        else if (coveredTerms.Length == 1 && uncommonTermCount == 1)
        {
            score += 120;
        }

        if (HasContainingTypeCorroboration(entry, interpretation))
        {
            reasons |= CodeExploreSelectionReason.ContainingType;
            tier = MinTier(tier, CodeExploreCandidateTier.MultiTermStructural);
            score += 160;
        }

        if (entry.IsTest)
        {
            reasons |= CodeExploreSelectionReason.Test;
            if (HasTestFocus(interpretation))
            {
                reasons |= CodeExploreSelectionReason.UserFocus;
                score += 140;
            }
            else
            {
                score -= 120;
            }
        }

        if (entry.IsGenerated)
        {
            reasons |= CodeExploreSelectionReason.Generated;
            if (HasGeneratedFocus(interpretation))
            {
                reasons |= CodeExploreSelectionReason.UserFocus;
                score += 140;
            }
            else
            {
                score -= 120;
            }
        }

        if (HasKindFocus(entry, interpretation))
        {
            reasons |= CodeExploreSelectionReason.UserFocus;
            score += 80;
        }

        if (reasons == CodeExploreSelectionReason.None || score <= 0)
        {
            return null;
        }

        if (tier == CodeExploreCandidateTier.Peripheral)
        {
            reasons |= CodeExploreSelectionReason.Peripheral;
        }

        return new CodeExploreRankedCandidate(
            entry,
            tier,
            reasons,
            score,
            coveredTerms.Length,
            CreateAmbiguityKey(entry));
    }

    private static CodeExploreCandidateSummary CreateCandidateSummary(
        CodeExploreRankedCandidate candidate,
        int rank,
        bool selected)
    {
        var location = new CodeExploreLocation(
            candidate.Entry.ProjectName,
            candidate.Entry.TargetFramework,
            candidate.Entry.RelativeFilePath,
            candidate.Entry.Range,
            candidate.Entry.IsGenerated,
            candidate.Entry.IsLinked);
        return new CodeExploreCandidateSummary(
            candidate.Entry.Identity,
            location,
            candidate.Entry.RelativeFilePath,
            candidate.Tier,
            candidate.Reasons,
            rank,
            selected,
            CreateNaturalLanguageSelectionReason(candidate, selected),
            candidate.AmbiguityGroup);
    }

    private static CodeExploreResult CreateUnanchoredCodeExploreResult(
        AdvancedSemanticSnapshot snapshot,
        string query,
        CodeExploreQueryInterpretation interpretation,
        CodeExploreDiscoverySummary? discovery,
        IReadOnlyList<CodeExploreCandidateSummary> candidateSummaries)
    {
        var reason = "Natural-language discovery did not find a compiler-known C# declaration or confined C# path; retry with a stable symbol id, exact C# symbol, or repository-relative C# path anchor.";
        var resolution = new CodeExploreAnchorResolution(
            query,
            CodeExploreAnchorKind.Query,
            CodeExploreResolutionOutcome.NotFound,
            null,
            null,
            [],
            reason);
        return new CodeExploreResult(
            snapshot.Generation,
            snapshot.Confidence,
            [resolution],
            [],
            new CodeExploreCoverage(false, snapshot.Confidence == SemanticConfidenceLevel.FullSemantic, false, true, [reason]),
            [reason],
            [],
            QueryInterpretation: interpretation,
            Discovery: discovery,
            CandidateSummaries: candidateSummaries,
            Allocation: new CodeExploreAllocationSummary(0, 0, 0, "no source allocated", []));
    }

    private static CodeExploreQueryInterpretation InterpretCodeExploreQuery(string query)
    {
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var qualifiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stableIds = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(PathComparer);
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in ExtractCodeExploreTokens(query))
        {
            if (LooksLikeDocumentationId(token))
            {
                stableIds.Add(token);
                continue;
            }

            if (IsCSharpPathSpan(token))
            {
                paths.Add(token);
                continue;
            }

            var normalizedToken = NormalizeComparableName(token);
            if (IsQualifiedIdentifierName(normalizedToken))
            {
                qualifiedNames.Add(normalizedToken);
            }

            foreach (var part in token.Split(['.', ':', '/', '\\', '-', '+'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmedPart = TrimCodeExploreToken(part);
                if (NaturalLanguageStopWords.Contains(trimmedPart))
                {
                    ignored.Add(trimmedPart.ToLowerInvariant());
                    continue;
                }

                if (IsCodeExploreExactIdentifier(trimmedPart))
                {
                    identifiers.Add(trimmedPart);
                }

                AddCodeExploreTerms(terms, ignored, trimmedPart);
            }
        }

        return new CodeExploreQueryInterpretation(
            identifiers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            qualifiedNames.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            stableIds.Order(StringComparer.Ordinal).ToArray(),
            paths.Order(PathComparer).ToArray(),
            terms.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ignored.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            []);
    }

    private static bool ShouldUseNaturalLanguageDiscovery(CodeExploreQueryInterpretation interpretation)
    {
        return interpretation.PathLikeSpans.Count > 0
            || interpretation.StableSymbolIds.Count > 0
            || interpretation.QualifiedNames.Count > 0
            || interpretation.ExactIdentifiers.Count > 0
            || interpretation.Terms.Count > 0;
    }

    private static IEnumerable<string> ExtractCodeExploreTokens(string query)
    {
        var builder = new StringBuilder(query.Length);
        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '.' or ':' or '/' or '\\' or '-' or '`' or '+')
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0)
            {
                var token = TrimCodeExploreToken(builder.ToString());
                if (token.Length > 0)
                {
                    yield return token;
                }

                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            var token = TrimCodeExploreToken(builder.ToString());
            if (token.Length > 0)
            {
                yield return token;
            }
        }
    }

    private static string TrimCodeExploreToken(string token)
    {
        return token.Trim(' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '<', '>', '"', '\'', '`');
    }

    private static bool IsCodeExploreExactIdentifier(string value)
    {
        return value.Length > 1
            && SyntaxFacts.IsValidIdentifier(value)
            && (value.Any(char.IsUpper)
                || value.Any(char.IsDigit)
                || value.Contains('_', StringComparison.Ordinal));
    }

    private static bool IsQualifiedIdentifierName(string value)
    {
        return value.Contains('.')
            && value.Split('.', StringSplitOptions.RemoveEmptyEntries).All(part => SyntaxFacts.IsValidIdentifier(part));
    }

    private static string CreateCodeExploreCatalogKey(WorkspaceId workspaceId, long generation)
    {
        return $"{workspaceId.Value:D}:{generation}";
    }

    private static string NormalizeComparableName(string value)
    {
        var normalized = NormalizeSymbolAnchor(value)
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace('+', '.')
            .Trim();
        return normalized.ToLowerInvariant();
    }

    private static void AddCodeExploreTerms(HashSet<string> terms, string value)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCodeExploreTerms(terms, ignored, value);
    }

    private static void AddCodeExploreTerms(
        HashSet<string> terms,
        HashSet<string> ignored,
        string value)
    {
        foreach (var part in SplitCodeExploreWords(value))
        {
            foreach (var segment in SplitIdentifierSegments(part))
            {
                var term = segment.ToLowerInvariant();
                if (term.Length < 2)
                {
                    continue;
                }

                if (NaturalLanguageStopWords.Contains(term))
                {
                    ignored.Add(term);
                    continue;
                }

                AddNormalizedCodeExploreTerm(terms, term);
            }
        }
    }

    private static void AddNormalizedCodeExploreTerm(HashSet<string> terms, string term)
    {
        foreach (var variant in CreateCodeExploreTermVariants(term))
        {
            terms.Add(variant);
        }
    }

    private static IReadOnlyList<string> CreateCodeExploreTermVariants(string term)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { term };
        if (term.Length > 4 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal) && !term.EndsWith("is", StringComparison.Ordinal))
        {
            variants.Add(term[..^1]);
        }

        if (term.Length > 5 && term.EndsWith("ing", StringComparison.Ordinal))
        {
            var stem = term[..^3];
            variants.Add(stem);
            variants.Add(stem + "e");
        }

        if (term.Length > 4 && term.EndsWith("ed", StringComparison.Ordinal))
        {
            var stem = term[..^2];
            variants.Add(stem);
            variants.Add(stem + "e");
        }

        if (term.Length > 6 && term.EndsWith("ment", StringComparison.Ordinal))
        {
            variants.Add(term[..^4]);
        }

        return variants.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> SplitCodeExploreWords(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
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

    private static IEnumerable<string> SplitIdentifierSegments(string value)
    {
        foreach (var part in value.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var start = 0;
            for (var index = 1; index < part.Length; index++)
            {
                var previous = part[index - 1];
                var current = part[index];
                var next = index + 1 < part.Length ? part[index + 1] : '\0';
                var startsNewWord = (char.IsUpper(current) && (char.IsLower(previous) || (char.IsUpper(previous) && char.IsLower(next))))
                    || (char.IsDigit(current) && !char.IsDigit(previous))
                    || (!char.IsDigit(current) && char.IsDigit(previous));
                if (!startsNewWord)
                {
                    continue;
                }

                if (index > start)
                {
                    yield return part[start..index];
                }

                start = index;
            }

            if (start < part.Length)
            {
                yield return part[start..];
            }
        }
    }

    private static bool IsCSharpPathSpan(string value)
    {
        return !value.Any(char.IsWhiteSpace)
            && value.Contains(".cs", StringComparison.OrdinalIgnoreCase)
            && (value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || value.Contains('/')
                || value.Contains('\\'));
    }

    private static bool CandidateCoversTerm(CodeExploreDeclarationCatalogEntry entry, string term)
    {
        var variants = CreateCodeExploreTermVariants(term);
        return entry.Terms.Any(candidate => variants.Any(variant => string.Equals(candidate, variant, StringComparison.OrdinalIgnoreCase)));
    }

    private static string CanonicalCodeExploreTerm(string term)
    {
        return CreateCodeExploreTermVariants(term)
            .OrderBy(variant => variant.Length)
            .ThenBy(variant => variant, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static bool PathSpanMatchesEntry(string path, CodeExploreDeclarationCatalogEntry entry)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var relative = entry.RelativeFilePath.Replace('\\', '/').Trim('/');
        return relative.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith('/' + normalized, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(relative).Equals(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IdentifierMatchesEntry(string identifier, CodeExploreDeclarationCatalogEntry entry)
    {
        return string.Equals(NormalizeComparableName(entry.Name), identifier, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeComparableName(entry.MetadataName), identifier, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeComparableName(StripParameters(entry.DisplayName)), identifier, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommonCodeExploreTerm(
        string term,
        IReadOnlyDictionary<string, int> termCounts,
        int catalogEntryCount)
    {
        var count = termCounts.GetValueOrDefault(term);
        var threshold = Math.Max(25, catalogEntryCount / 12);
        return count > threshold;
    }

    private static bool HasContainingTypeCorroboration(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation)
    {
        var containerTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(entry.ContainingType))
        {
            AddCodeExploreTerms(containerTerms, entry.ContainingType);
        }

        if (!string.IsNullOrWhiteSpace(entry.ContainingNamespace))
        {
            AddCodeExploreTerms(containerTerms, entry.ContainingNamespace);
        }

        return containerTerms.Count > 0
            && interpretation.Terms.Any(containerTerms.Contains)
            && interpretation.Terms.Any(term => CandidateCoversTerm(entry, term));
    }

    private static bool HasTestFocus(CodeExploreQueryInterpretation interpretation)
    {
        return interpretation.Terms.Any(term => term is "test" or "tests" or "testing")
            || interpretation.ExactIdentifiers.Any(identifier => identifier.Contains("Test", StringComparison.Ordinal));
    }

    private static bool HasGeneratedFocus(CodeExploreQueryInterpretation interpretation)
    {
        return interpretation.Terms.Any(term => term is "generated" or "generator" or "sourcegenerated");
    }

    private static bool HasKindFocus(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation)
    {
        var kind = entry.Kind.ToLowerInvariant();
        return interpretation.Terms.Any(term => term switch
        {
            "class" or "type" => kind is "class" or "struct" or "record" or "interface" or "namedtype",
            "interface" => kind == "interface",
            "method" or "function" => kind is "method" or "constructor" or "localfunction",
            "property" => kind == "property",
            "field" => kind == "field",
            "enum" => kind == "enum",
            "delegate" => kind == "delegate",
            _ => false,
        });
    }

    private static CodeExploreCandidateTier MinTier(
        CodeExploreCandidateTier left,
        CodeExploreCandidateTier right)
    {
        return (int)left <= (int)right ? left : right;
    }

    private static int KindSortRank(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "class" or "interface" or "struct" or "record" or "namedtype" => 0,
            "constructor" => 1,
            "method" or "localfunction" => 2,
            "property" => 3,
            "field" => 4,
            "enum" or "delegate" => 5,
            _ => 6,
        };
    }

    private static IReadOnlyList<string> CreateAmbiguityGroups(
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        IReadOnlySet<string> selectedIds)
    {
        return ranked
            .GroupBy(candidate => candidate.AmbiguityGroup, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1 && group.Any(candidate => selectedIds.Contains(candidate.Entry.Identity.Id)))
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Select(group => $"{group.Key}: {group.Count()} candidates")
            .ToArray();
    }

    private static string CreateAmbiguityKey(CodeExploreDeclarationCatalogEntry entry)
    {
        var container = entry.ContainingType ?? entry.ContainingNamespace ?? entry.ProjectName;
        return string.IsNullOrWhiteSpace(container)
            ? entry.Name
            : $"{container}.{entry.Name}";
    }

    private static string CreateNaturalLanguageSelectionReason(
        CodeExploreRankedCandidate candidate,
        bool selected)
    {
        var prefix = selected ? "Selected" : "Retained";
        return $"{prefix} natural-language candidate in {candidate.Tier} tier: {FormatSelectionReasons(candidate.Reasons)}.";
    }

    private static string FormatSelectionReasons(CodeExploreSelectionReason reasons)
    {
        var names = Enum.GetValues<CodeExploreSelectionReason>()
            .Where(reason => reason != CodeExploreSelectionReason.None && (reasons & reason) == reason)
            .Select(reason => reason.ToString())
            .ToArray();
        return names.Length == 0 ? "no exposed reason" : string.Join(", ", names);
    }

    private static int ResolveNaturalLanguageCandidateSummaryLimit(
        int maximumSourceCharacters,
        int pinnedSummaryCount)
    {
        var budgetedSummaries = Math.Max(1, maximumSourceCharacters / 512);
        return Math.Max(
            0,
            Math.Min(MaximumNaturalLanguageCandidateSummaries, budgetedSummaries) - pinnedSummaryCount);
    }

    private static int EstimateReservedCodeExploreCharacters(
        CodeExploreQueryInterpretation interpretation,
        CodeExploreDiscoverySummary? discovery)
    {
        var reserved = 256
            + (interpretation.Terms.Count * 16)
            + (interpretation.PathLikeSpans.Count * 48)
            + ((discovery?.SelectedCount ?? 0) * 64);
        return Math.Min(4096, reserved);
    }

    private static bool IsUsefulCodeExploreSection(CodeExploreFileSection section)
    {
        if (section.Source.NumberedLines.Count == 0)
        {
            return false;
        }

        return section.Source.Completeness == CodeExploreSourceCompleteness.Complete
            || section.Source.NumberedLines.Sum(line => line.Length) >= MinimumUsefulSourceCharacters;
    }

    private static bool IsTestProjectNameOrPath(string projectName, string relativePath)
    {
        return projectName.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(relativePath).Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CodeExploreSymbolResolution> ResolveCodeExploreSymbolIdAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        IReadOnlyList<CodeExplorePathAnchor> pathAnchors,
        int maximumAlternatives,
        CodeExploreAnchor anchor,
        List<CodeExploreAnchorResolution> resolutions,
        List<CodeExploreSectionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var groups = await ResolveSymbolGroupsInSnapshotAsync(snapshot, anchor.Value, pathAnchors, cancellationToken);
        if (groups.Count == 0)
        {
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.NotFound,
                null,
                null,
                [],
                "The stable symbol id is not loaded in the current semantic workspace."));
            return new(false);
        }

        var locatedGroups = await LocateAllowedSymbolGroupsAsync(snapshot, projection, sourceReader, groups, cancellationToken);
        if (locatedGroups.Count == 0)
        {
            resolutions.Add(CreatePolicyOmittedSymbolResolution(anchor));
            return new(false);
        }

        if (locatedGroups.Count > 1)
        {
            var alternativesCapped = locatedGroups.Count > maximumAlternatives;
            var alternatives = locatedGroups
                .Take(maximumAlternatives)
                .Select(group => new CodeExploreAlternative(group.Group.Identity, group.Location))
                .ToArray();
            var selected = locatedGroups[0];
            var reason = alternativesCapped
                ? "The stable symbol id maps to multiple declarations with distinct source ownership; alternatives were capped and a path anchor is required to disambiguate."
                : "The stable symbol id maps to multiple declarations with distinct source ownership; retry with a path anchor to disambiguate.";
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.Ambiguous,
                selected.Group.Identity,
                selected.Location,
                alternatives,
                reason));
            return new(alternativesCapped);
        }

        var resolved = locatedGroups[0];
        resolutions.Add(new CodeExploreAnchorResolution(
            anchor.Value,
            anchor.Kind,
            CodeExploreResolutionOutcome.Resolved,
            resolved.Group.Identity,
            resolved.Location,
            [],
            "Stable symbol id resolved exactly."));
        foreach (var symbol in resolved.Group.Symbols)
        {
            await AddSymbolSourceCandidatesAsync(
                snapshot,
                projection,
                sourceReader,
                symbol,
                anchor,
                "Stable symbol id declaration source.",
                1,
                candidates,
                cancellationToken);
        }

        return new(false);
    }

    private static async Task<CodeExploreSymbolResolution> ResolveCodeExploreSymbolNameAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreRequest request,
        CodeExploreAnchor anchor,
        List<CodeExploreAnchorResolution> resolutions,
        List<CodeExploreSectionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var symbols = await FindExactCodeExploreSymbolsAsync(
            snapshot,
            anchor.Value,
            request.PathAnchors,
            cancellationToken);
        var groups = GroupSymbolsByIdentity(symbols);
        if (groups.Count == 0)
        {
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.NotFound,
                null,
                null,
                [],
                "No exact C# declaration matched the anchor in compiled projects."));
            return new(false);
        }

        var locatedGroups = await LocateAllowedSymbolGroupsAsync(snapshot, projection, sourceReader, groups, cancellationToken);
        if (locatedGroups.Count == 0)
        {
            resolutions.Add(CreatePolicyOmittedSymbolResolution(anchor));
            return new(false);
        }

        var alternativesCapped = locatedGroups.Count > request.Limits.MaximumAlternatives;
        var selected = locatedGroups[0];
        var alternatives = locatedGroups
            .Take(request.Limits.MaximumAlternatives)
            .Select(group => new CodeExploreAlternative(group.Group.Identity, group.Location))
            .ToArray();
        var allowedGroups = locatedGroups.Select(group => group.Group).ToArray();
        var hasStableIdentityCollision = HasStableIdentityCollision(allowedGroups);
        var outcome = locatedGroups.Count == 1
            ? CodeExploreResolutionOutcome.Resolved
            : CodeExploreResolutionOutcome.Ambiguous;
        var reason = locatedGroups.Count == 1
            ? "Exact symbol anchor resolved to one declaration identity."
            : hasStableIdentityCollision
                ? "Exact symbol anchor maps the same stable id to multiple declarations with distinct source ownership; retry with a path anchor to disambiguate."
                : alternativesCapped
                    ? "Exact symbol anchor is ambiguous; alternatives were capped by the request limits."
                    : "Exact symbol anchor is ambiguous; alternatives are returned deterministically.";
        resolutions.Add(new CodeExploreAnchorResolution(
            anchor.Value,
            anchor.Kind,
            outcome,
            selected.Group.Identity,
            selected.Location,
            alternatives,
            reason));
        if (!hasStableIdentityCollision || locatedGroups.Count == 1)
        {
            foreach (var group in locatedGroups.Take(request.Limits.MaximumAlternatives))
            {
                foreach (var symbol in group.Group.Symbols)
                {
                    await AddSymbolSourceCandidatesAsync(
                        snapshot,
                        projection,
                        sourceReader,
                        symbol,
                        anchor,
                        "Exact symbol declaration source.",
                        1,
                        candidates,
                        cancellationToken);
                }
            }
        }

        return new(alternativesCapped);
    }

    private static async Task<CodeExploreSymbolResolution> ResolveCodeExplorePathAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        int maximumAlternatives,
        CodeExploreAnchor anchor,
        List<CodeExploreAnchorResolution> resolutions,
        List<CodeExploreSectionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            fullPath = NormalizeScope(anchor.Value, snapshot.RepositoryPath)
                ?? throw new ArgumentException("A path anchor must be non-empty.", nameof(anchor));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException)
        {
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.Omitted,
                null,
                null,
                [],
                "The C# path anchor is invalid or outside the repository scope."));
            return new(false);
        }

        var relativePath = ToRepositoryRelativePath(fullPath, snapshot.RepositoryPath);
        if (anchor.ExpectedWorkspaceGeneration is { } expectedGeneration && expectedGeneration != snapshot.Generation)
        {
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.Omitted,
                null,
                new CodeExploreLocation(string.Empty, string.Empty, relativePath, CreateLineRange(anchor.Line ?? 1), false, false),
                [],
                $"The continuation expected workspace generation {expectedGeneration}, but the current generation is {snapshot.Generation}."));
            return new(false);
        }

        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.Unsupported,
                null,
                new CodeExploreLocation(string.Empty, string.Empty, relativePath, CreateLineRange(anchor.Line ?? 1), false, false),
                [],
                "Only C# source paths are supported by this code_explore foundation."));
            return new(false);
        }

        if (!sourceReader.IsPathAllowed(fullPath))
        {
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.Omitted,
                null,
                new CodeExploreLocation(string.Empty, string.Empty, relativePath, CreateLineRange(anchor.Line ?? 1), false, false),
                [],
                "The confined C# path is outside the invocation path policy."));
            return new(false);
        }

        var documents = FindDocumentsByPath(snapshot.Solution, fullPath);
        if (documents.Count == 0)
        {
            CodeExploreSourceText sourceText;
            try
            {
                sourceText = await sourceReader.ReadTextAsync(
                    fullPath,
                    MaximumCurrentSourceFileBytes,
                    cancellationToken);
            }
            catch (FileNotFoundException)
            {
                resolutions.Add(new CodeExploreAnchorResolution(
                    anchor.Value,
                    anchor.Kind,
                    CodeExploreResolutionOutcome.NotFound,
                    null,
                    new CodeExploreLocation(string.Empty, string.Empty, relativePath, CreateLineRange(anchor.Line ?? 1), false, false),
                    [],
                    "The confined C# path does not exist."));
                return new(false);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or InvalidOperationException
                or DecoderFallbackException)
            {
                resolutions.Add(new CodeExploreAnchorResolution(
                    anchor.Value,
                    anchor.Kind,
                    CodeExploreResolutionOutcome.Omitted,
                    null,
                    new CodeExploreLocation(string.Empty, string.Empty, relativePath, CreateLineRange(anchor.Line ?? 1), false, false),
                    [],
                    $"The confined C# path could not be read for code exploration: {exception.GetType().Name}."));
                return new(false);
            }

            var location = new CodeExploreLocation(
                string.Empty,
                string.Empty,
                relativePath,
                CreateLineRange(anchor.Line ?? 1),
                IsGeneratedPath(fullPath),
                false);
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.Resolved,
                null,
                location,
                [],
                "The confined C# path exists but is not loaded in the semantic workspace; source is returned without symbol identity."));
            candidates.Add(new CodeExploreSectionCandidate(
                null,
                fullPath,
                null,
                null,
                location,
                anchor.Kind,
                anchor.Value,
                "Pinned C# path source outside the loaded semantic workspace.",
                0,
                anchor.Line,
                anchor.EndLine,
                anchor.StartAtLine,
                EffectiveSelectionMode(anchor),
                anchor.ExpectedFileSha256,
                anchor.ExpectedWorkspaceGeneration,
                anchor.AllocationRank,
                SourceText.From(sourceText.Text, Encoding.UTF8),
                sourceText.FileSha256));
            return new(false);
        }

        var pathCandidates = new List<CodeExploreSectionCandidate>();
        foreach (var document in documents)
        {
            var candidate = await CreatePathDocumentCandidateAsync(
                snapshot,
                projection,
                document,
                anchor,
                cancellationToken);
            if (candidate is not null)
            {
                pathCandidates.Add(candidate);
            }
        }

        if (pathCandidates.Count == 0)
        {
            resolutions.Add(new CodeExploreAnchorResolution(
                anchor.Value,
                anchor.Kind,
                CodeExploreResolutionOutcome.NotFound,
                null,
                null,
                [],
                "The path line did not map to loaded C# source."));
            return new(false);
        }

        var selected = pathCandidates[0];
        var alternativesCapped = pathCandidates.Count - 1 > maximumAlternatives;
        var alternatives = pathCandidates
            .Skip(1)
            .Take(maximumAlternatives)
            .Select(candidate => new CodeExploreAlternative(
                candidate.Identity ?? new SemanticSymbolIdentity(
                    $"path:{candidate.Location?.FilePath ?? relativePath}:{candidate.Location?.ProjectName ?? string.Empty}",
                    candidate.Location?.FilePath ?? relativePath,
                    "Path"),
                candidate.Location))
            .ToArray();
        var reason = anchor.Line is null
            ? "Pinned C# path resolved in the loaded semantic workspace."
            : "Pinned C# path and line resolved in the loaded semantic workspace.";
        if (alternativesCapped)
        {
            reason += " Additional linked path candidates were omitted by the maximumAlternatives limit.";
        }

        resolutions.Add(new CodeExploreAnchorResolution(
            anchor.Value,
            anchor.Kind,
            CodeExploreResolutionOutcome.Resolved,
            selected.Identity,
            selected.Location,
            alternatives,
            reason));
        candidates.AddRange(pathCandidates.Take(maximumAlternatives + 1));
        return new(alternativesCapped);
    }

    private static async Task<IReadOnlyList<ISymbol>> FindExactCodeExploreSymbolsAsync(
        AdvancedSemanticSnapshot snapshot,
        string anchor,
        IReadOnlyList<CodeExplorePathAnchor> pathAnchors,
        CancellationToken cancellationToken)
    {
        var direct = await ResolveDocumentationSymbolsAsync(snapshot, anchor, cancellationToken);
        if (direct.Count > 0)
        {
            return FilterSymbolsByPathAnchors(snapshot, direct, pathAnchors);
        }

        var searchTerm = ExtractSymbolSearchTerm(anchor);
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return [];
        }

        var found = new List<ISymbol>();
        foreach (var project in snapshot.Solution.Projects
            .Where(project => snapshot.CompiledProjects.Contains(project.Id))
            .OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var declarations = await SymbolFinder.FindDeclarationsAsync(
                project,
                searchTerm,
                ignoreCase: false,
                SymbolFilter.TypeAndMember,
                cancellationToken);
            found.AddRange(declarations.Where(symbol => MatchesCodeExploreAnchor(symbol, anchor)));
        }

        var symbols = found
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(symbol => CreateIdentity(symbol).Id, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), StringComparer.Ordinal)
            .ToArray();
        return FilterSymbolsByPathAnchors(snapshot, symbols, pathAnchors);
    }

    private static IReadOnlyList<ISymbol> FilterSymbolsByPathAnchors(
        AdvancedSemanticSnapshot snapshot,
        IReadOnlyList<ISymbol> symbols,
        IReadOnlyList<CodeExplorePathAnchor> pathAnchors)
    {
        if (pathAnchors.Count == 0)
        {
            return symbols;
        }

        return symbols
            .Where(symbol => SymbolMatchesAnyPathAnchor(snapshot, symbol, pathAnchors))
            .ToArray();
    }

    private static bool SymbolMatchesAnyPathAnchor(
        AdvancedSemanticSnapshot snapshot,
        ISymbol symbol,
        IReadOnlyList<CodeExplorePathAnchor> pathAnchors)
    {
        foreach (var anchor in pathAnchors)
        {
            string? fullPath;
            try
            {
                fullPath = NormalizeScope(anchor.Path, snapshot.RepositoryPath);
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException)
            {
                continue;
            }

            if (fullPath is null)
            {
                continue;
            }

            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var sourcePath = reference.SyntaxTree.FilePath;
                if (string.IsNullOrWhiteSpace(sourcePath)
                    || !Path.GetFullPath(sourcePath).Equals(fullPath, PathComparison))
                {
                    continue;
                }

                if (anchor.Line is null)
                {
                    return true;
                }

                var lineSpan = reference.SyntaxTree.GetLineSpan(reference.Span);
                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;
                if (anchor.Line >= startLine && anchor.Line <= endLine)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<IReadOnlyList<CodeExploreSymbolGroup>> ResolveSymbolGroupsInSnapshotAsync(
        AdvancedSemanticSnapshot snapshot,
        string symbolId,
        IReadOnlyList<CodeExplorePathAnchor> pathAnchors,
        CancellationToken cancellationToken)
    {
        var symbols = new List<ISymbol>();
        foreach (var project in snapshot.Solution.Projects
            .Where(project => snapshot.CompiledProjects.Contains(project.Id))
            .OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            var compilation = await GetCompilationBoundedAsync(project, cancellationToken);
            var symbol = compilation is null
                ? null
                : DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, compilation);
            if (symbol is not null)
            {
                symbols.Add(symbol);
            }
        }

        var separator = symbolId.IndexOf(':');
        if (symbols.Count == 0 && separator > 0)
        {
            var anchor = symbolId[(separator + 1)..];
            symbols.AddRange((await FindExactCodeExploreSymbolsAsync(snapshot, anchor, pathAnchors, cancellationToken))
                .Where(symbol => string.Equals(CreateIdentity(symbol).Id, symbolId, StringComparison.Ordinal)));
        }

        var filteredSymbols = FilterSymbolsByPathAnchors(snapshot, symbols, pathAnchors);
        return [.. GroupSymbolsByIdentity(filteredSymbols)
            .Where(group => string.Equals(group.Identity.Id, symbolId, StringComparison.Ordinal))];
    }

    private static async Task<IReadOnlyList<ISymbol>> ResolveDocumentationSymbolsAsync(
        AdvancedSemanticSnapshot snapshot,
        string anchor,
        CancellationToken cancellationToken)
    {
        if (!LooksLikeDocumentationId(anchor))
        {
            return [];
        }

        var symbols = new List<ISymbol>();
        foreach (var project in snapshot.Solution.Projects
            .Where(project => snapshot.CompiledProjects.Contains(project.Id))
            .OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await GetCompilationBoundedAsync(project, cancellationToken);
            var symbol = compilation is null
                ? null
                : DocumentationCommentId.GetFirstSymbolForDeclarationId(anchor, compilation);
            if (symbol is not null)
            {
                symbols.Add(symbol);
            }
        }

        return symbols
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(symbol => CreateIdentity(symbol).Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static CodeExploreAnchorResolution CreatePolicyOmittedSymbolResolution(CodeExploreAnchor anchor)
    {
        return new CodeExploreAnchorResolution(
            anchor.Value,
            anchor.Kind,
            CodeExploreResolutionOutcome.Omitted,
            null,
            null,
            [],
            "Resolved declaration evidence is outside the invocation path policy and was omitted.");
    }

    private static async Task<IReadOnlyList<CodeExploreLocatedSymbolGroup>> LocateAllowedSymbolGroupsAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        IReadOnlyList<CodeExploreSymbolGroup> groups,
        CancellationToken cancellationToken)
    {
        var locatedGroups = new List<CodeExploreLocatedSymbolGroup>();
        foreach (var group in groups)
        {
            var location = await FirstCodeExploreGroupLocationAsync(
                snapshot,
                projection,
                sourceReader,
                group,
                cancellationToken);
            if (location is not null)
            {
                locatedGroups.Add(new CodeExploreLocatedSymbolGroup(group, location));
            }
        }

        return locatedGroups;
    }

    private static async Task AddSymbolSourceCandidatesAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        ISymbol symbol,
        CodeExploreAnchor anchor,
        string selectionReason,
        int priority,
        List<CodeExploreSectionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(symbol);
        foreach (var reference in symbol.DeclaringSyntaxReferences
            .OrderBy(reference => reference.SyntaxTree.FilePath, PathComparer)
            .ThenBy(reference => reference.Span.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntaxPath = reference.SyntaxTree.FilePath;
            if (string.IsNullOrWhiteSpace(syntaxPath)
                || !Path.IsPathRooted(syntaxPath)
                || !sourceReader.IsPathAllowed(syntaxPath))
            {
                continue;
            }

            var declaration = await reference.GetSyntaxAsync(cancellationToken);
            var document = await FindDocumentForSyntaxTreeAsync(
                snapshot,
                declaration.SyntaxTree,
                cancellationToken);
            if (document is null)
            {
                continue;
            }

            var location = ToCodeExploreLocation(
                CreateDocumentLocation(document, declaration.SyntaxTree, declaration.Span, projection),
                snapshot.RepositoryPath);
            candidates.Add(new CodeExploreSectionCandidate(
                document,
                document.FilePath ?? declaration.SyntaxTree.FilePath,
                declaration.Span,
                identity,
                location,
                anchor.Kind,
                anchor.Value,
                selectionReason,
                priority,
                null,
                null,
                false,
                CodeExplorePathSelectionMode.Auto,
                null,
                null,
                anchor.AllocationRank,
                null,
                null));
        }

        if (symbol.DeclaringSyntaxReferences.Length > 0)
        {
            return;
        }

        foreach (var location in symbol.Locations.Where(location => location.IsInSource))
        {
            if (location.SourceTree is null
                || string.IsNullOrWhiteSpace(location.SourceTree.FilePath)
                || !Path.IsPathRooted(location.SourceTree.FilePath)
                || !sourceReader.IsPathAllowed(location.SourceTree.FilePath))
            {
                continue;
            }

            var sourceLocation = await CreateLocationAsync(snapshot, projection, location, cancellationToken);
            if (sourceLocation is null)
            {
                continue;
            }

            var document = await FindDocumentForSyntaxTreeAsync(
                snapshot,
                location.SourceTree,
                cancellationToken);
            candidates.Add(new CodeExploreSectionCandidate(
                document,
                sourceLocation.FilePath,
                location.SourceSpan,
                identity,
                ToCodeExploreLocation(sourceLocation, snapshot.RepositoryPath),
                anchor.Kind,
                anchor.Value,
                selectionReason,
                priority,
                null,
                null,
                false,
                CodeExplorePathSelectionMode.Auto,
                null,
                null,
                anchor.AllocationRank,
                null,
                null));
        }
    }

    private static async Task<CodeExploreSectionCandidate?> CreatePathDocumentCandidateAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        Document document,
        CodeExploreAnchor anchor,
        CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
        {
            return null;
        }

        var mode = EffectiveSelectionMode(anchor);
        var span = new TextSpan(0, text.Length);
        ISymbol? symbol = null;
        var selectionReason = mode == CodeExplorePathSelectionMode.WholeFile
            ? "Pinned C# path selected the whole file."
            : "Pinned C# path source.";
        if (anchor.Line is { } line)
        {
            if (line > text.Lines.Count)
            {
                return null;
            }

            if (mode == CodeExplorePathSelectionMode.ContainingDeclaration)
            {
                var declaration = FindContainingMember(root, text, line);
                if (declaration is not null)
                {
                    span = declaration.Span;
                    if (snapshot.CompiledProjects.Contains(document.Project.Id))
                    {
                        var model = await document.GetSemanticModelAsync(cancellationToken);
                        symbol = model is null ? null : GetDeclaredSymbol(model, declaration, cancellationToken);
                    }

                    selectionReason = symbol is null
                        ? "Pinned C# path line selected a loaded declaration without compiled symbol identity."
                        : "Pinned C# path line selected the containing declaration.";
                }
                else
                {
                    span = text.Lines[line - 1].Span;
                    selectionReason = "Pinned C# path line selected a bounded source line because no containing declaration was found.";
                }
            }
            else
            {
                var selectedSpan = CreateFileOrLineSpan(text, line, anchor.EndLine, anchor.StartAtLine, mode);
                if (selectedSpan is null)
                {
                    return null;
                }

                span = selectedSpan.Value;
                selectionReason = mode switch
                {
                    CodeExplorePathSelectionMode.SingleLine => "Pinned C# path selected one exact source line.",
                    CodeExplorePathSelectionMode.TailWindow => "Continuation path cursor selected source beginning at the requested line.",
                    CodeExplorePathSelectionMode.ExactLineRange => "Continuation path cursor selected an exact source line range.",
                    _ => selectionReason,
                };
            }
        }

        var location = ToCodeExploreLocation(
            CreateDocumentLocation(document, root.SyntaxTree, span, projection),
            snapshot.RepositoryPath);
        return new CodeExploreSectionCandidate(
            document,
            document.FilePath ?? root.SyntaxTree.FilePath,
            span,
            symbol is null ? null : CreateIdentity(symbol),
            location,
            anchor.Kind,
            anchor.Value,
            selectionReason,
            0,
            anchor.Line,
            anchor.EndLine,
            anchor.StartAtLine,
            EffectiveSelectionMode(anchor),
            anchor.ExpectedFileSha256,
            anchor.ExpectedWorkspaceGeneration,
            anchor.AllocationRank,
            null,
            null);
    }

    private static async Task<ProjectedCodeExploreSection> ProjectCodeExploreSectionAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreSectionCandidate candidate,
        int sourceCharacterBudget,
        CancellationToken cancellationToken)
    {
        var continuations = new List<CodeExploreContinuationTarget>();
        var relativePath = ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath);
        var projectName = candidate.Location?.ProjectName ?? candidate.Document?.Project.Name ?? string.Empty;
        var targetFramework = candidate.Location?.TargetFramework
            ?? (candidate.Document is null ? string.Empty : projection.GetTargetFramework(candidate.Document.Project.Id));
        if (candidate.ExpectedWorkspaceGeneration is { } expectedGeneration && expectedGeneration != snapshot.Generation)
        {
            var source = new CodeExploreSourceRange(
                candidate.Location?.Range ?? CreateLineRange(candidate.PreferredLine ?? 1),
                [],
                null,
                null,
                CodeExploreSourceCompleteness.Drifted,
                [$"The continuation expected workspace generation {expectedGeneration}, but the current generation is {snapshot.Generation}; source was omitted."],
                null);
            var drifted = new CodeExploreFileSection(
                relativePath,
                projectName,
                targetFramework,
                CreateSectionIdentities(candidate),
                source,
                candidate.Location?.IsGenerated ?? IsGeneratedPath(candidate.FilePath),
                candidate.Location?.IsLinked ?? false,
                candidate.SelectionReason);
            return new(drifted, 0, []);
        }

        if (!Path.IsPathRooted(candidate.FilePath))
        {
            var source = new CodeExploreSourceRange(
                candidate.Location?.Range ?? CreateLineRange(candidate.PreferredLine ?? 1),
                [],
                null,
                null,
                CodeExploreSourceCompleteness.Omitted,
                ["The source path is not repository-rooted and cannot be policy-verified; source was omitted before reading."],
                null);
            var omitted = new CodeExploreFileSection(
                relativePath,
                projectName,
                targetFramework,
                CreateSectionIdentities(candidate),
                source,
                candidate.Location?.IsGenerated ?? IsGeneratedPath(candidate.FilePath),
                candidate.Location?.IsLinked ?? false,
                candidate.SelectionReason);
            return new(omitted, 0, []);
        }

        if (candidate.Document is not null && !sourceReader.IsPathAllowed(candidate.FilePath))
        {
            var source = new CodeExploreSourceRange(
                candidate.Location?.Range ?? CreateLineRange(candidate.PreferredLine ?? 1),
                [],
                null,
                null,
                CodeExploreSourceCompleteness.Omitted,
                ["The source path is outside the invocation path policy; source was omitted before reading current content."],
                null);
            var omitted = new CodeExploreFileSection(
                relativePath,
                projectName,
                targetFramework,
                CreateSectionIdentities(candidate),
                source,
                candidate.Location?.IsGenerated ?? IsGeneratedPath(candidate.FilePath),
                candidate.Location?.IsLinked ?? false,
                candidate.SelectionReason);
            return new(omitted, 0, []);
        }

        var text = candidate.PreloadedText
            ?? (candidate.Document is null
                ? await ReadSourceTextFromFileAsync(sourceReader, candidate.FilePath, cancellationToken)
                : await candidate.Document.GetTextAsync(cancellationToken));
        var span = candidate.Span ?? CreateFileOrLineSpan(text, candidate.PreferredLine, candidate.EndLine, candidate.StartAtLine, candidate.SelectionMode);
        if (span is null)
        {
            var source = new CodeExploreSourceRange(
                CreateLineRange(candidate.PreferredLine ?? 1),
                [],
                null,
                null,
                CodeExploreSourceCompleteness.Omitted,
                ["The requested line is outside the current file."],
                null);
            var omitted = new CodeExploreFileSection(
                relativePath,
                projectName,
                targetFramework,
                CreateSectionIdentities(candidate),
                source,
                IsGeneratedPath(candidate.FilePath),
                candidate.Location?.IsLinked ?? false,
                candidate.SelectionReason);
            return new(omitted, 0, []);
        }

        var fileIdentity = candidate.Document is null && candidate.PreloadedFileSha256 is not null
            ? (FileSha256: candidate.PreloadedFileSha256, DriftReason: (string?)null)
            : await VerifyCurrentFileIdentityAsync(
                sourceReader,
                candidate.FilePath,
                text,
                cancellationToken);
        if (fileIdentity.DriftReason is null
            && candidate.ExpectedFileSha256 is { } expectedFileSha256
            && !string.Equals(fileIdentity.FileSha256, expectedFileSha256, StringComparison.OrdinalIgnoreCase))
        {
            fileIdentity = (fileIdentity.FileSha256, "The continuation expected a different file digest; source was omitted to avoid stale continuation evidence.");
        }

        if (fileIdentity.DriftReason is not null)
        {
            var source = new CodeExploreSourceRange(
                candidate.Location?.Range ?? CreateLineRange(candidate.PreferredLine ?? 1),
                [],
                fileIdentity.FileSha256,
                null,
                CodeExploreSourceCompleteness.Drifted,
                [fileIdentity.DriftReason],
                null);
            var drifted = new CodeExploreFileSection(
                relativePath,
                projectName,
                targetFramework,
                CreateSectionIdentities(candidate),
                source,
                IsGeneratedPath(candidate.FilePath),
                candidate.Location?.IsLinked ?? false,
                candidate.SelectionReason);
            return new(drifted, 0, []);
        }

        if (sourceCharacterBudget < MinimumUsefulSourceCharacters && span.Value.Length > sourceCharacterBudget)
        {
            var startLine = text.Lines.GetLineFromPosition(Math.Min(span.Value.Start, text.Length)).LineNumber + 1;
            var endLine = GetSpanEndLine(text, span.Value);
            var source = new CodeExploreSourceRange(
                new SourceRange(startLine, 1, endLine, 1),
                [],
                fileIdentity.FileSha256,
                null,
                CodeExploreSourceCompleteness.Omitted,
                [$"L{startLine}-L{endLine} omitted because the remaining per-file budget is below the minimum useful source section size."],
                relativePath);
            var omitted = new CodeExploreFileSection(
                relativePath,
                projectName,
                targetFramework,
                CreateSectionIdentities(candidate),
                source,
                IsGeneratedPath(candidate.FilePath),
                candidate.Location?.IsLinked ?? false,
                candidate.SelectionReason);
            return new(omitted, 0, [new CodeExploreContinuationTarget(
                CodeExploreAnchorKind.Path,
                relativePath,
                relativePath,
                startLine,
                endLine,
                false,
                CodeExplorePathSelectionMode.ExactLineRange,
                fileIdentity.FileSha256,
                snapshot.Generation,
                "Retry with this path anchor after increasing source limits; the remaining budget was too small for useful source.")]);
        }

        var projected = ProjectSourceRange(
            text,
            span.Value,
            fileIdentity.FileSha256,
            sourceCharacterBudget,
            relativePath);
        if (projected.Range.ContinuationAnchor is not null)
        {
            continuations.Add(new CodeExploreContinuationTarget(
                CodeExploreAnchorKind.Path,
                relativePath,
                relativePath,
                projected.NextLine,
                GetSpanEndLine(text, span.Value),
                true,
                CodeExplorePathSelectionMode.ExactLineRange,
                projected.Range.FileSha256,
                snapshot.Generation,
                "Retry with this path anchor and exact line range to continue only the omitted selected source."));
        }

        var section = new CodeExploreFileSection(
            relativePath,
            projectName,
            targetFramework,
            CreateSectionIdentities(candidate),
            projected.Range,
            candidate.Location?.IsGenerated ?? IsGeneratedPath(candidate.FilePath),
            candidate.Location?.IsLinked ?? false,
            candidate.SelectionReason);
        return new(section, projected.SourceCharacters, continuations);
    }

    private static int GetSpanEndLine(SourceText text, TextSpan span)
    {
        var safeStart = Math.Min(span.Start, text.Length);
        var safeEnd = Math.Min(span.End, text.Length);
        var endPosition = Math.Max(safeEnd - 1, safeStart);
        return text.Lines.GetLineFromPosition(Math.Min(endPosition, text.Length)).LineNumber + 1;
    }

    private static async Task<SourceText> ReadSourceTextFromFileAsync(
        ICodeExploreSourceReader sourceReader,
        string filePath,
        CancellationToken cancellationToken)
    {
        var content = await sourceReader.ReadTextAsync(
            filePath,
            MaximumCurrentSourceFileBytes,
            cancellationToken);
        return SourceText.From(content.Text, Encoding.UTF8);
    }

    private static TextSpan? CreateFileOrLineSpan(
        SourceText text,
        int? preferredLine,
        int? endLine,
        bool startAtLine,
        CodeExplorePathSelectionMode selectionMode)
    {
        var mode = selectionMode == CodeExplorePathSelectionMode.Auto
            ? preferredLine is null
                ? CodeExplorePathSelectionMode.WholeFile
                : startAtLine
                    ? CodeExplorePathSelectionMode.TailWindow
                    : CodeExplorePathSelectionMode.SingleLine
            : selectionMode;
        if (mode == CodeExplorePathSelectionMode.WholeFile)
        {
            return new TextSpan(0, text.Length);
        }

        if (preferredLine is null || preferredLine.Value > text.Lines.Count)
        {
            return null;
        }

        var line = text.Lines[preferredLine.Value - 1];
        return mode switch
        {
            CodeExplorePathSelectionMode.SingleLine => line.Span,
            CodeExplorePathSelectionMode.TailWindow => TextSpan.FromBounds(line.Start, text.Length),
            CodeExplorePathSelectionMode.ExactLineRange when endLine is { } end && end <= text.Lines.Count && end >= preferredLine.Value =>
                TextSpan.FromBounds(line.Start, text.Lines[end - 1].EndIncludingLineBreak),
            CodeExplorePathSelectionMode.ContainingDeclaration => line.Span,
            _ => null,
        };
    }

    private static CodeExplorePathSelectionMode EffectiveSelectionMode(CodeExploreAnchor anchor)
    {
        if (anchor.Line is null)
        {
            return CodeExplorePathSelectionMode.WholeFile;
        }

        if (anchor.SelectionMode != CodeExplorePathSelectionMode.Auto)
        {
            return anchor.SelectionMode;
        }

        return anchor.StartAtLine
            ? CodeExplorePathSelectionMode.TailWindow
            : CodeExplorePathSelectionMode.ContainingDeclaration;
    }

    private static async Task<(string? FileSha256, string? DriftReason)> VerifyCurrentFileIdentityAsync(
        ICodeExploreSourceReader sourceReader,
        string filePath,
        SourceText semanticText,
        CancellationToken cancellationToken)
    {
        var semanticContent = semanticText.ToString();
        if (!Path.IsPathRooted(filePath))
        {
            return (null, "The source path is not repository-rooted and cannot be policy-verified; source was omitted before reading.");
        }

        if (!sourceReader.IsPathAllowed(filePath))
        {
            return (null, "The source path is outside the invocation path policy; source was omitted before reading current content.");
        }

        CodeExploreSourceText current;
        try
        {
            current = await sourceReader.ReadTextAsync(
                filePath,
                MaximumCurrentSourceFileBytes,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return (null, "The source file is no longer present on disk; semantic source was omitted as stale.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or InvalidOperationException
            or DecoderFallbackException)
        {
            return (null, $"The current source file could not be read for identity verification: {exception.GetType().Name}.");
        }

        return string.Equals(current.Text, semanticContent, StringComparison.Ordinal)
            ? (current.FileSha256, null)
            : (current.FileSha256, "The current file content differs from the captured semantic span; source was omitted to avoid stale evidence.");
    }

    private static (CodeExploreSourceRange Range, int SourceCharacters, int? NextLine) ProjectSourceRange(
        SourceText text,
        TextSpan span,
        string? fileSha256,
        int sourceCharacterBudget,
        string relativePath)
    {
        if (sourceCharacterBudget <= 0)
        {
            var start = text.Lines.GetLineFromPosition(Math.Min(span.Start, text.Length)).LineNumber + 1;
            var end = text.Lines.GetLineFromPosition(Math.Max(Math.Min(span.End, text.Length) - 1, 0)).LineNumber + 1;
            return (new CodeExploreSourceRange(
                new SourceRange(start, 1, end, 1),
                [],
                fileSha256,
                null,
                CodeExploreSourceCompleteness.Omitted,
                [$"L{start}-L{end} omitted because the source-character budget was exhausted."],
                null),
                0,
                null);
        }

        var safeStart = Math.Min(span.Start, text.Length);
        var safeEnd = Math.Min(span.End, text.Length);
        var startLineIndex = text.Lines.GetLineFromPosition(safeStart).LineNumber;
        var endPosition = Math.Max(safeEnd - 1, safeStart);
        var endLineIndex = text.Lines.GetLineFromPosition(Math.Min(endPosition, text.Length)).LineNumber;
        var numberedLines = new List<string>();
        var usedCharacters = 0;
        var nextLine = (int?)null;
        for (var index = startLineIndex; index <= endLineIndex; index++)
        {
            var raw = text.Lines[index].ToString();
            var numbered = $"{index + 1}: {raw}";
            var additional = numbered.Length + (numberedLines.Count == 0 ? 0 : Environment.NewLine.Length);
            if (usedCharacters + additional > sourceCharacterBudget)
            {
                nextLine = index + 1;
                break;
            }

            numberedLines.Add(numbered);
            usedCharacters += additional;
        }

        if (numberedLines.Count == 0)
        {
            var start = startLineIndex + 1;
            return (new CodeExploreSourceRange(
                new SourceRange(start, 1, endLineIndex + 1, 1),
                [],
                fileSha256,
                null,
                CodeExploreSourceCompleteness.Omitted,
                [$"L{start}-L{endLineIndex + 1} omitted because the first line exceeds the source-character budget; increase the per-file source limit for this anchor."],
                relativePath),
                0,
                start);
        }

        var returnedEndLineIndex = startLineIndex + numberedLines.Count - 1;
        var returnedSpan = TextSpan.FromBounds(
            text.Lines[startLineIndex].Start,
            text.Lines[returnedEndLineIndex].EndIncludingLineBreak);
        var rangeText = text.GetSubText(returnedSpan).ToString();
        var completeness = nextLine is null ? CodeExploreSourceCompleteness.Complete : CodeExploreSourceCompleteness.Partial;
        var omittedRanges = nextLine is null
            ? Array.Empty<string>()
            : [$"L{nextLine}-L{endLineIndex + 1} omitted by source-character limits."];
        var continuation = nextLine is null ? null : relativePath;
        return (new CodeExploreSourceRange(
            new SourceRange(
                startLineIndex + 1,
                1,
                returnedEndLineIndex + 1,
                text.Lines[returnedEndLineIndex].ToString().Length + 1),
            numberedLines,
            fileSha256,
            ComputeSha256(rangeText),
            completeness,
            omittedRanges,
            continuation),
            usedCharacters,
            nextLine);
    }

    private static IReadOnlyList<SemanticSymbolIdentity> CreateSectionIdentities(CodeExploreSectionCandidate candidate)
    {
        return candidate.Identity is null ? [] : [candidate.Identity];
    }

    private static IReadOnlyList<CodeExploreSymbolGroup> GroupSymbolsByIdentity(IEnumerable<ISymbol> symbols)
    {
        return symbols
            .GroupBy(
                symbol => $"{CreateIdentity(symbol).Id}\u001f{CreateDeclarationOwnershipKey(symbol)}",
                StringComparer.Ordinal)
            .Select(group =>
            {
                var groupedSymbols = group
                    .Distinct(SymbolEqualityComparer.Default)
                    .OrderBy(CreatePrimaryDeclarationPath, PathComparer)
                    .ThenBy(symbol => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), StringComparer.Ordinal)
                    .ToArray();
                return new CodeExploreSymbolGroup(CreateIdentity(groupedSymbols[0]), groupedSymbols);
            })
            .OrderBy(group => group.Identity.Id, StringComparer.Ordinal)
            .ThenBy(group => CreateDeclarationOwnershipKey(group.Symbols[0]), StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasStableIdentityCollision(IReadOnlyList<CodeExploreSymbolGroup> groups)
    {
        return groups
            .GroupBy(group => group.Identity.Id, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
    }

    private static string CreateDeclarationOwnershipKey(ISymbol symbol)
    {
        var declarations = symbol.DeclaringSyntaxReferences
            .Select(reference => new
            {
                Path = NormalizeOwnershipPath(reference.SyntaxTree.FilePath),
                reference.Span.Start,
                reference.Span.End,
            })
            .OrderBy(declaration => declaration.Path, PathComparer)
            .ThenBy(declaration => declaration.Start)
            .ThenBy(declaration => declaration.End)
            .Select(declaration => $"{declaration.Path}:{declaration.Start}:{declaration.End}")
            .ToArray();
        if (declarations.Length > 0)
        {
            return string.Join("|", declarations);
        }

        return symbol.ContainingAssembly?.Identity.GetDisplayName()
            ?? symbol.ContainingModule?.Name
            ?? string.Empty;
    }

    private static string CreatePrimaryDeclarationPath(ISymbol symbol)
    {
        return symbol.DeclaringSyntaxReferences
            .Select(reference => NormalizeOwnershipPath(reference.SyntaxTree.FilePath))
            .Order(PathComparer)
            .DefaultIfEmpty(string.Empty)
            .First();
    }

    private static string NormalizeOwnershipPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : path;
    }

    private static async Task<CodeExploreLocation?> FirstCodeExploreGroupLocationAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreSymbolGroup group,
        CancellationToken cancellationToken)
    {
        foreach (var symbol in group.Symbols)
        {
            var location = await FirstCodeExploreLocationAsync(
                snapshot,
                projection,
                sourceReader,
                symbol,
                cancellationToken);
            if (location is not null)
            {
                return location;
            }
        }

        return null;
    }

    private static async Task<CodeExploreLocation?> FirstCodeExploreLocationAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var location = symbol.Locations
            .Where(location => location.IsInSource
                && location.SourceTree is not null
                && !string.IsNullOrWhiteSpace(location.SourceTree.FilePath)
                && Path.IsPathRooted(location.SourceTree.FilePath)
                && sourceReader.IsPathAllowed(location.SourceTree.FilePath))
            .OrderBy(location => location.SourceTree?.FilePath ?? string.Empty, PathComparer)
            .ThenBy(location => location.SourceSpan.Start)
            .FirstOrDefault();
        var sourceLocation = location is null
            ? null
            : await CreateLocationAsync(snapshot, projection, location, cancellationToken);
        return sourceLocation is null ? null : ToCodeExploreLocation(sourceLocation, snapshot.RepositoryPath);
    }

    private static CodeExploreLocation ToCodeExploreLocation(
        SemanticSourceLocation location,
        string repositoryPath)
    {
        return new(
            location.ProjectName,
            location.TargetFramework,
            ToRepositoryRelativePath(location.FilePath, repositoryPath),
            location.Range,
            location.IsGenerated,
            location.IsLinked);
    }

    private static IReadOnlyList<Document> FindDocumentsByPath(Solution solution, string fullPath)
    {
        return solution.Projects
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .SelectMany(project => project.Documents.OrderBy(document => document.FilePath ?? document.Name, PathComparer))
            .Where(document => document.FilePath is not null
                && Path.GetFullPath(document.FilePath).Equals(fullPath, PathComparison))
            .ToArray();
    }

    private static SyntaxNode? FindContainingMember(SyntaxNode root, SourceText text, int oneBasedLine)
    {
        var line = text.Lines[oneBasedLine - 1];
        var member = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(candidate => ContainsLine(text, candidate.Span, oneBasedLine))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (member is not null || string.IsNullOrWhiteSpace(line.ToString()))
        {
            return member;
        }

        return root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(candidate => ContainsLine(text, candidate.FullSpan, oneBasedLine))
            .OrderBy(candidate => candidate.FullSpan.Length)
            .FirstOrDefault();
    }

    private static bool ContainsLine(SourceText text, TextSpan span, int oneBasedLine)
    {
        if (span.Length == 0)
        {
            return false;
        }

        var safeStart = Math.Min(span.Start, Math.Max(text.Length - 1, 0));
        var safeEnd = Math.Min(Math.Max(span.End - 1, span.Start), Math.Max(text.Length - 1, 0));
        var startLine = text.Lines.GetLineFromPosition(safeStart).LineNumber + 1;
        var endLine = text.Lines.GetLineFromPosition(safeEnd).LineNumber + 1;
        return oneBasedLine >= startLine && oneBasedLine <= endLine;
    }

    private static ISymbol? GetDeclaredSymbol(
        SemanticModel model,
        SyntaxNode declaration,
        CancellationToken cancellationToken)
    {
        if (declaration is FieldDeclarationSyntax field)
        {
            var variable = field.Declaration.Variables.FirstOrDefault();
            return variable is null ? null : model.GetDeclaredSymbol(variable, cancellationToken);
        }

        return model.GetDeclaredSymbol(declaration, cancellationToken);
    }

    private static bool MatchesCodeExploreAnchor(ISymbol symbol, string anchor)
    {
        if (anchor.Contains('('))
        {
            var signatureAnchor = NormalizeSymbolAnchorPreservingParameters(anchor);
            return GetSymbolComparableNames(symbol)
                .Select(NormalizeSymbolAnchorPreservingParameters)
                .Any(name => string.Equals(name, signatureAnchor, StringComparison.Ordinal));
        }

        var normalizedAnchor = NormalizeSymbolAnchor(anchor);
        return GetSymbolComparableNames(symbol)
            .Select(NormalizeSymbolAnchor)
            .Any(name => string.Equals(name, normalizedAnchor, StringComparison.Ordinal));
    }

    private static IEnumerable<string> GetSymbolComparableNames(ISymbol symbol)
    {
        var identity = symbol.GetDocumentationCommentId();
        if (!string.IsNullOrEmpty(identity))
        {
            yield return identity;
        }

        yield return symbol.Name;
        yield return symbol.MetadataName;
        var errorDisplay = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var fullyQualified = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        yield return errorDisplay;
        yield return StripParameters(errorDisplay);
        yield return fullyQualified;
        yield return StripParameters(fullyQualified);
        if (symbol is IMethodSymbol method)
        {
            var signature = CreateMethodSignature(method);
            yield return signature;
            if (symbol.ContainingType is not null)
            {
                var containingType = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                var fullyQualifiedContainingType = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                yield return $"{containingType}.{signature}";
                yield return $"{fullyQualifiedContainingType}.{signature}";
                yield return $"{containingType}.{method.Name}";
                yield return $"{fullyQualifiedContainingType}.{method.Name}";
            }
        }

        if (symbol.ContainingType is not null)
        {
            var containingType = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            var fullyQualifiedContainingType = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            yield return $"{containingType}.{symbol.Name}";
            yield return $"{fullyQualifiedContainingType}.{symbol.Name}";
        }

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace)
        {
            yield return $"{containingNamespace}.{symbol.MetadataName}";
        }
    }

    private static string CreateMethodSignature(IMethodSymbol method)
    {
        var parameters = string.Join(
            ",",
            method.Parameters.Select(CreateParameterSignature));
        return $"{method.Name}({parameters})";
    }

    private static string CreateParameterSignature(IParameterSymbol parameter)
    {
        var refKind = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => string.Empty,
        };
        return refKind + parameter.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    }

    private static string ExtractSymbolSearchTerm(string anchor)
    {
        if (LooksLikeDocumentationId(anchor))
        {
            return string.Empty;
        }

        var value = StripParameters(NormalizeSymbolAnchor(anchor));
        var lastDot = value.LastIndexOf('.');
        var term = lastDot < 0 ? value : value[(lastDot + 1)..];
        var genericTick = term.IndexOf('`');
        if (genericTick >= 0)
        {
            term = term[..genericTick];
        }

        return SyntaxFacts.IsValidIdentifier(term) ? term : string.Empty;
    }

    private static bool IsExactSymbolAnchor(string query)
    {
        return LooksLikeDocumentationId(query) || IsSafeName(StripParameters(query));
    }

    private static bool QueryLooksLikePath(string query)
    {
        var trimmed = query.Trim();
        return !trimmed.Any(char.IsWhiteSpace)
            && (trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains('/')
                || trimmed.Contains('\\'));
    }

    private static bool LooksLikeDocumentationId(string value)
    {
        return value.Length > 2
            && value[1] == ':'
            && value[0] is 'T' or 'M' or 'P' or 'F' or 'E' or 'N';
    }

    private static string NormalizeSymbolAnchor(string value)
    {
        return StripParameters(NormalizeSymbolAnchorPreservingParameters(value));
    }

    private static string NormalizeSymbolAnchorPreservingParameters(string value)
    {
        var normalized = value.Trim().Replace("global::", string.Empty, StringComparison.Ordinal).Replace('+', '.');
        var builder = new StringBuilder(normalized.Length);
        var pendingWhitespace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = true;
                continue;
            }

            if (character is ',' or ')' or '(')
            {
                TrimTrailingSpace(builder);
                builder.Append(character);
                pendingWhitespace = false;
                continue;
            }

            if (pendingWhitespace
                && builder.Length > 0
                && builder[^1] is not '(' and not ',' and not '.')
            {
                builder.Append(' ');
            }

            builder.Append(character);
            pendingWhitespace = false;
        }

        TrimTrailingSpace(builder);
        return builder.ToString();
    }

    private static void TrimTrailingSpace(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }
    }

    private static string StripParameters(string value)
    {
        var parameters = value.IndexOf('(');
        return parameters < 0 ? value : value[..parameters];
    }

    private static bool IsFlowCallSiteCandidate(CodeExploreSectionCandidate candidate)
    {
        return string.Equals(candidate.SelectionReason, "Compiler-proven flow call-site source.", StringComparison.Ordinal);
    }

    private static string CreateSectionKey(CodeExploreSectionCandidate candidate)
    {
        var range = candidate.Location?.Range;
        return string.Join(
            '|',
            candidate.FilePath,
            candidate.Location?.ProjectName ?? string.Empty,
            candidate.Location?.TargetFramework ?? string.Empty,
            range?.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            range?.StartColumn.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            range?.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            range?.EndColumn.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            candidate.Identity?.Id ?? string.Empty);
    }

    private static bool IsOutputBoundOmission(string omission)
    {
        return omission.Contains("source-character budget", StringComparison.OrdinalIgnoreCase)
            || omission.Contains("source-character limits", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresLine(CodeExplorePathSelectionMode mode)
    {
        return mode is CodeExplorePathSelectionMode.SingleLine
            or CodeExplorePathSelectionMode.TailWindow
            or CodeExplorePathSelectionMode.ExactLineRange;
    }

    private static bool IsSha256Hex(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static SourceRange CreateLineRange(int line)
    {
        return new SourceRange(line, 1, line, 1);
    }

    private static string ToRepositoryRelativePath(string filePath, string repositoryPath)
    {
        if (!Path.IsPathRooted(filePath))
        {
            return filePath.Replace('\\', '/');
        }

        var fullPath = Path.GetFullPath(filePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (fullPath.Equals(root, PathComparison) || fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        }

        return filePath.Replace('\\', '/');
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static CancellationTokenSource CreateTimeout(int milliseconds, CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
        return timeout;
    }

    private static async Task<Compilation?> GetCompilationBoundedAsync(Project project, CancellationToken cancellationToken)
    {
        var task = project.GetCompilationAsync(cancellationToken);
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(
                task,
                Task.Delay(NonCooperativeCompilationBackstop, CancellationToken.None));
            if (completed == task)
            {
                _ = task.Exception;
            }
            else
            {
                ObserveAbandonedCompilation(task);
            }

            throw;
        }
    }

    private static void ObserveAbandonedCompilation(Task<Compilation?> task)
    {
        _ = task.ContinueWith(
            faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        var patternVersion = request.Pattern.Version ?? 1;
        if (patternVersion != 1)
        {
            throw new NotSupportedException($"C# pattern version {patternVersion} is not supported.");
        }

        if (request.MaximumMatches is < 1 or > 1000 || request.TimeoutMilliseconds is < 1 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Pattern-search bounds are outside host limits.");
        }

        var requiredModifiers = request.Pattern.RequiredModifiers ?? [];
        var requiredAttributes = request.Pattern.RequiredAttributes ?? [];
        foreach (var value in requiredModifiers)
        {
            if (!AllowedModifiers.Contains(value))
            {
                throw new ArgumentException($"Unsupported C# modifier '{value}'.", nameof(request));
            }
        }

        foreach (var value in new[] { request.Pattern.Name, request.Pattern.ContainingType, request.Pattern.Capture }
            .Concat(requiredAttributes))
        {
            if (value is { Length: > 256 } || (value is not null && !IsSafeName(value)))
            {
                throw new ArgumentException("Pattern names must be bounded C# identifiers.", nameof(request));
            }
        }

        if (requiredModifiers.Count > 16 || requiredAttributes.Count > 16)
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
            var compilation = await GetCompilationBoundedAsync(project, cancellationToken);
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
                var target = info.Symbol;
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

    private static async Task<SemanticSourceLocation?> CreateLocationAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        Location location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!location.IsInSource || location.SourceTree is null)
        {
            return null;
        }

        var document = await FindDocumentForSyntaxTreeAsync(
            snapshot,
            location.SourceTree,
            cancellationToken);
        return document is null
            ? null
            : CreateDocumentLocation(document, location.SourceTree, location.SourceSpan, projection);
    }

    private static async Task<Document?> FindDocumentForSyntaxTreeAsync(
        AdvancedSemanticSnapshot snapshot,
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        var document = snapshot.Solution.GetDocument(syntaxTree);
        if (document is not null)
        {
            return document;
        }

        foreach (var project in snapshot.Solution.Projects
            .Where(project => snapshot.CompiledProjects.Contains(project.Id))
            .OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<Document> generatedDocuments = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
            foreach (var generatedDocument in generatedDocuments.OrderBy(item => item.FilePath ?? item.Name, PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await generatedDocument.GetSyntaxRootAsync(cancellationToken);
                if (root is null)
                {
                    continue;
                }

                if (ReferenceEquals(root.SyntaxTree, syntaxTree)
                    || string.Equals(root.SyntaxTree.FilePath, syntaxTree.FilePath, PathComparison))
                {
                    return generatedDocument;
                }
            }
        }

        return null;
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
        var requiredModifiers = pattern.RequiredModifiers ?? [];
        if (requiredModifiers.Any(required => !modifiers.Any(token => token.ValueText == required)))
        {
            return false;
        }

        var attributes = GetAttributes(node);
        var requiredAttributes = pattern.RequiredAttributes ?? [];
        return requiredAttributes.All(required => attributes.Any(actual => AttributeNamesEqual(required, actual)));
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

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
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
