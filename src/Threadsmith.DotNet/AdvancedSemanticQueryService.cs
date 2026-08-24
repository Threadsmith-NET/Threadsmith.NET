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

    private static readonly TimeSpan NonCooperativeCompilationBackstop = TimeSpan.FromSeconds(2);

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
        var anchors = BuildCodeExploreAnchors(request);
        if (anchors.Count == 0)
        {
            EnsureCurrent(engine, snapshot.Generation);
            return CreateUnanchoredCodeExploreResult(snapshot, request.Query);
        }

        var resolutions = new List<CodeExploreAnchorResolution>();
        var candidates = new List<CodeExploreSectionCandidate>();
        var omissions = new List<string>();
        var continuations = new List<CodeExploreContinuationTarget>();
        var alternativesCapped = false;
        var timeReached = false;
        SemanticSourceProjection? projection = null;
        try
        {
            projection = new SemanticSourceProjection(snapshot.Solution, timeout.Token);
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
        var remainingSourceCharacters = request.Limits.MaximumSourceCharacters;
        var outputBoundReached = false;
        if (!timeReached && projection is not null)
        {
            try
            {
                foreach (var candidate in candidates
                    .OrderBy(candidate => candidate.Priority)
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

                    if (remainingSourceCharacters <= 0)
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

                    var projected = await ProjectCodeExploreSectionAsync(
                        snapshot,
                        projection,
                        sourceReader,
                        candidate,
                        Math.Min(remainingSourceCharacters, request.Limits.MaximumPerFileSourceCharacters),
                        timeout.Token);
                    remainingSourceCharacters -= projected.SourceCharacters;
                    selectedSections.Add(projected.Section);
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
                omissions.Add("The code exploration time limit was reached during source projection.");
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
        return new CodeExploreResult(
            snapshot.Generation,
            snapshot.Confidence,
            resolutions,
            selectedSections,
            coverage,
            coverageOmissions,
            continuations.DistinctBy(target => $"{target.Kind}:{target.Anchor}:{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.StartAtLine}:{target.SelectionMode}:{target.ExpectedFileSha256}:{target.WorkspaceGeneration}:{target.Reason}").ToArray());
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
        long? ExpectedWorkspaceGeneration);

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
        SourceText? PreloadedText,
        string? PreloadedFileSha256);

    private sealed record ProjectedCodeExploreSection(
        CodeExploreFileSection Section,
        int SourceCharacters,
        IReadOnlyList<CodeExploreContinuationTarget> ContinuationTargets);

    private static void ValidateCodeExploreRequest(CodeExploreRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentNullException.ThrowIfNull(request.Limits);
        if (request.Query.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Code exploration queries are limited to 1,024 characters.");
        }

        var limits = request.Limits;
        if (limits.MaximumAnchors is < 1 or > 16
            || limits.MaximumAlternatives is < 1 or > 25
            || limits.MaximumFiles is < 1 or > 16
            || limits.MaximumSourceCharacters is < 1 or > 100_000
            || limits.MaximumPerFileSourceCharacters is < 1 or > 65_536
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
            anchor.ExpectedWorkspaceGeneration)));
        anchors.AddRange(request.SymbolIds.Select(symbolId => new CodeExploreAnchor(
            CodeExploreAnchorKind.SymbolId,
            symbolId,
            null,
            null,
            false,
            CodeExplorePathSelectionMode.Auto,
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
            null)));
        if (anchors.Count > 0)
        {
            return anchors;
        }

        if (QueryLooksLikePath(request.Query) || IsExactSymbolAnchor(request.Query))
        {
            return [new CodeExploreAnchor(CodeExploreAnchorKind.Query, request.Query, null, null, false, CodeExplorePathSelectionMode.Auto, null, null)];
        }

        return [];
    }

    private static CodeExploreResult CreateUnanchoredCodeExploreResult(
        AdvancedSemanticSnapshot snapshot,
        string query)
    {
        var reason = "The initial code_explore capability requires an exact C# symbol, stable symbol id, or confined C# path anchor; natural-language discovery is not enabled yet.";
        var resolution = new CodeExploreAnchorResolution(
            query,
            CodeExploreAnchorKind.Query,
            CodeExploreResolutionOutcome.Unsupported,
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
            []);
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
            var document = snapshot.Solution.GetDocument(declaration.SyntaxTree);
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

            var document = snapshot.Solution.GetDocument(location.SourceTree);
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
        if (anchor.SelectionMode != CodeExplorePathSelectionMode.Auto)
        {
            return anchor.SelectionMode;
        }

        if (anchor.Line is null)
        {
            return CodeExplorePathSelectionMode.WholeFile;
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
        var position = Math.Min(line.Start, Math.Max(text.Length - 1, 0));
        var token = root.FindToken(position);
        return token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
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
                yield return $"{containingType}.{signature}";
            }
        }

        if (symbol.ContainingType is not null)
        {
            var containingType = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            yield return $"{containingType}.{symbol.Name}";
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
        return query.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || query.Contains('/')
            || query.Contains('\\');
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
        return mode is CodeExplorePathSelectionMode.ContainingDeclaration
            or CodeExplorePathSelectionMode.SingleLine
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
