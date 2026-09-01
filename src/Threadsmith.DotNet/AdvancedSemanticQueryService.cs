namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
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
    private const int MaximumGeneratedDocuments = 100;
    private const int MaximumGeneratedContentCharacters = 16_384;
    private const int MaximumCurrentSourceFileBytes = 1024 * 1024;
    private const int MaximumCodeExploreCatalogEntries = 50_000;
    private const int MaximumCodeExploreCatalogs = 4;
    private const int MaximumNaturalLanguageCandidateSummaries = 64;
    private const int NaturalLanguageNameSegmentBaseScore = 160;
    private const int NaturalLanguageNameSegmentConceptScore = 40;
    private const int NaturalLanguagePrimaryNameSegmentConceptScore = 20;
    private const int NaturalLanguageMaximumNameSegmentCoverageScore = 40;
    private const int NaturalLanguageRareNameSegmentScore = 120;
    private const int MaximumNaturalLanguageGraphDepth = 3;
    private const int MaximumNaturalLanguageGraphNodes = 200;
    private const int MaximumNaturalLanguageGraphEdges = 800;
    private const int MaximumNaturalLanguageGraphConcurrency = 4;
    private const int MaximumNaturalLanguageGraphReferenceLocations = 32;
    private const int NaturalLanguageDefaultCoLocationBoost = 60;
    private const int NaturalLanguageToolIntentCoLocationBoost = 10;
    private const int NaturalLanguageCoLocationConceptBoost = 20;
    private const int NaturalLanguageDefaultGraphBoost = 260;
    private const int MaximumSourceClusterGapLines = 8;
    private const int ToolIntentMaximumSelectedPerType = 1;
    private const int ToolIntentMaximumSelectedPerFile = 3;
    private const int SurveyIntentMaximumSelectedPerType = 3;
    private const int SurveyIntentMaximumSelectedPerFile = 5;
    private const int MinimumUsefulSourceCharacters = 256;
    private const int MinimumDedupSourceCharacters = 256;
    private const int MaximumArtifactLiteralLength = 512;
    private const int MaximumExactNameArtifactLiterals = 16;
    private const int MaximumExactNameArtifactLookups = 32;
    private const int MaximumPresentationSummaryCharacters = 900;
    private const int MaximumPresentationGuarantees = 12;
    private const int MaximumPresentationNotShownTargets = 12;
    private const int MaximumPresentationNextActions = 8;
    private const int MaximumFileRelevanceSummaries = 24;
    private const int MaximumCodeExploreScaleProjects = 121;
    private const int MaximumCodeExploreScaleDocuments = 2_501;
    private const string NaturalLanguageAnchorSourceReason = "Stable symbol id declaration source.";
    private const string NaturalLanguageCompanionSourceReason = "Selected as relevant source context in an admitted natural-language file.";
    private const string AppSettingsArtifactStem = "appsettings";
    private const string GlobalNamespaceAlias = "global::";
    private const string CallHierarchyToolFamily = "call_hierarchy";
    private const string CodeExploreToolFamily = "code_explore";
    private const string CSharpPatternSearchToolFamily = "csharp_pattern_search";
    private const string FindImplementationsToolFamily = "find_implementations";
    private const string FindReferencesToolFamily = "find_references";
    private const string FindSymbolToolFamily = "find_symbol";
    private const string GeneratedCodeQueryToolFamily = "generated_code_query";
    private const string GeneratedCodeToolFamily = "generated_code";
    private const string SymbolImpactToolFamily = "symbol_impact";

    private static readonly TimeSpan NonCooperativeCompilationBackstop = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> TestPathSegments = new(
        [
            "__tests__",
            "spec",
            "specs",
            "test",
            "testing",
            "testlib",
            "tests",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] TestNameSuffixes =
    [
        "TestCase",
        "Tests",
        "Tester",
        "Test",
        "Specs",
        "Spec",
    ];

    private static readonly HashSet<string> TestNameTokens = new(
        [
            "spec",
            "specs",
            "test",
            "tests",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ArtifactFocusTerms = new(
        [
            "additionaldocument",
            "additionaldocuments",
            "additionalfile",
            "additionalfiles",
            "analyzerconfig",
            "artifact",
            "artifacts",
            "configuration",
            "configurations",
            "config",
            "prompt",
            "prompts",
            "resource",
            "resources",
            "schema",
            "schemas",
            "template",
            "templates",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ArtifactFocusQualifierTerms = new(
        ["artifact", "artifacts", "doc", "docs", "document", "documents", "documentation", "file", "files", "item", "items", "metadata"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ArtifactFocusContextTerms = new(
        ["additional", "analyzer", "markdown", "project", "text"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ArtifactFocusExtensions = new(
        [
            "config",
            "csproj",
            "editorconfig",
            "fsproj",
            "globalconfig",
            "gql",
            "graphql",
            "handlebars",
            "http",
            "json",
            "jsonc",
            "liquid",
            "markdown",
            "md",
            "mustache",
            "prompt",
            "prompty",
            "props",
            "rest",
            "resx",
            "ruleset",
            "schema",
            "scriban",
            "targets",
            "template",
            "tmpl",
            "txt",
            "vbproj",
            "xml",
            "yaml",
            "yml",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AmbiguousArtifactFocusExtensions = new(
        ["config", "json", "xml"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ConventionalJsonArtifactStems = new(
        ["global", "launchsettings", "package", "packages.lock"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ConventionalConfigArtifactStems = new(
        ["app", "nuget", "packages", "web"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> PromptContextTerms = new(
        ["prompt", "template", "message", "systemmessage", "markdown", "render", "completion"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ConfigurationContextTerms = new(
        ["configuration", "config", "options", "setting", "settings", "appsettings", "section", "getsection", "getvalue"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ProjectItemElementNames = new(
        ["AdditionalFiles", "AdditionalFile", "Content", "None", "EditorConfigFiles", "AnalyzerConfigFiles"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ProjectResourceElementNames = new(
        ["EmbeddedResource", "Resource"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NaturalLanguageStopWords = new(
        ["a", "an", "and", "are", "as", "at", "be", "been", "being", "by", "can", "could", "did", "do", "does", "for", "from", "has", "have", "how", "i", "in", "into", "is", "it", "its", "me", "of", "on", "or", "please", "show", "that", "the", "their", "this", "through", "to", "was", "were", "what", "when", "where", "which", "who", "why", "with", "without"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NaturalLanguageRetrievalStopWords = new(
        [
            "all", "also", "answer", "called", "class", "code", "describe", "done", "each", "every",
            "everywhere", "explain", "file", "files", "found", "function", "functions", "give", "just",
            "list", "made", "method", "methods", "more", "question", "some", "tell", "than", "then",
            "tool", "tools", "type", "types", "use", "used", "uses", "using", "work", "works",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ToolCapabilityIntentTerms = new(
        ["agentic", "capability", "capabilities", "compiler", "efficient", "efficiency", "explain", "semantic", "semantics", "tool", "tools", "workflow", "workflows"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ToolLifecycleIntentTerms = new(
        ["available", "availability", "bootstrap", "compose", "composition", "expose", "exposed", "register", "registered", "registration", "startup", "wire", "wired", "wiring"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ToolCompositionContextTerms = new(
        ["agentic", "capability", "capabilities", "semantic", "semantics", "tools", "workflow", "workflows"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SemanticToolIdentityTerms = new(
        [
            "call", "code", "csharp", "explore", "find", "generated", "hierarchy", "implementation",
            "implementations", "impact", "pattern", "query", "reference", "references", "search", "symbol",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> SemanticToolIdFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        [CallHierarchyToolFamily] = CallHierarchyToolFamily,
        [CodeExploreToolFamily] = CodeExploreToolFamily,
        [CSharpPatternSearchToolFamily] = CSharpPatternSearchToolFamily,
        [FindImplementationsToolFamily] = FindImplementationsToolFamily,
        [FindReferencesToolFamily] = FindReferencesToolFamily,
        [FindSymbolToolFamily] = FindSymbolToolFamily,
        [GeneratedCodeQueryToolFamily] = GeneratedCodeToolFamily,
        [SymbolImpactToolFamily] = SymbolImpactToolFamily,
    };

    private static readonly string[] ToolCapabilitySurveyFamilies =
    [
        CodeExploreToolFamily,
        FindSymbolToolFamily,
        FindReferencesToolFamily,
        FindImplementationsToolFamily,
        SymbolImpactToolFamily,
        CallHierarchyToolFamily,
    ];

    private static readonly string[] IndexedToolCapabilityFamilies =
    [
        .. ToolCapabilitySurveyFamilies,
        CSharpPatternSearchToolFamily,
        GeneratedCodeToolFamily,
    ];

    private readonly Lock _catalogGate = new();
    private readonly Dictionary<string, CodeExploreDeclarationCatalog> _codeExploreCatalogs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedCodeExploreBuild<CodeExploreDeclarationCatalog>> _codeExploreCatalogBuilds = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, long> _latestCodeExploreCatalogGenerations = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _naturalLanguageGraphNeighbors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedCodeExploreBuild<IReadOnlyList<string>>> _naturalLanguageGraphBuilds = new(StringComparer.Ordinal);
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

                if (documents.Count >= MaximumGeneratedDocuments)
                {
                    truncated = true;
                    break;
                }

                var text = await document.GetTextAsync(cancellationToken);
                var content = request.IncludeContent ? text.ToString() : null;
                var contentTruncated = content is { Length: var length } && length > MaximumGeneratedContentCharacters;
                if (contentTruncated)
                {
                    content = content?[..MaximumGeneratedContentCharacters];
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
        CancellationToken cancellationToken = default,
        ModelVisibleSourceFrontier? visibleSourceFrontier = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceReader);
        ValidateCodeExploreRequest(request);
        using var timeout = CreateTimeout(request.Limits.TimeoutMilliseconds, cancellationToken);
        timeout.Token.ThrowIfCancellationRequested();
        var engine = _registry.GetEngine(workspaceId);
        var readiness = engine.CaptureCodeExploreReadinessSnapshot();
        var queryInterpretation = InterpretCodeExploreQuery(request.Query);
        if (CreateInitialCodeExploreAvailability(readiness) is { } initialAvailability)
        {
            var unavailableScale = CreateUnknownCodeExploreRepositoryScale(
                request.AssociatedArtifactPathAnchors.Count);
            request = ApplyCodeExploreAdaptiveDefaults(request, unavailableScale, out var unavailableBudget);
            EnsureCurrent(engine, readiness.Generation);
            return CreateUnavailableCodeExploreResult(
                readiness,
                request,
                queryInterpretation,
                initialAvailability,
                unavailableBudget);
        }

        var solution = readiness.Solution
            ?? throw new InvalidOperationException("Code exploration requires a captured semantic solution.");
        var repositoryPath = readiness.RepositoryPath
            ?? throw new InvalidOperationException("Code exploration requires an opened repository path.");
        var workspacePath = readiness.WorkspacePath
            ?? throw new InvalidOperationException("Code exploration requires an opened project or solution path.");
        var snapshot = new AdvancedSemanticSnapshot(
            solution,
            readiness.CompiledProjects,
            readiness.Confidence,
            repositoryPath,
            workspacePath,
            readiness.Generation);
        CodeExploreRepositoryScale repositoryScale;
        try
        {
            repositoryScale = CreateCodeExploreRepositoryScale(
                snapshot,
                sourceReader,
                declarationCatalogEntryCount: null,
                declarationCatalogComplete: null,
                associatedArtifactCandidateCount: request.AssociatedArtifactPathAnchors.Count,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            var timedOutScale = CreateUnknownCodeExploreRepositoryScale(
                request.AssociatedArtifactPathAnchors.Count);
            request = ApplyCodeExploreAdaptiveDefaults(request, timedOutScale, out var timedOutBudget);
            var timedOutAvailability = CreateInitialTimeoutCodeExploreAvailability(readiness);
            EnsureCurrent(engine, readiness.Generation);
            return CreateUnavailableCodeExploreResult(
                readiness,
                request,
                queryInterpretation,
                timedOutAvailability,
                timedOutBudget);
        }

        request = ApplyCodeExploreAdaptiveDefaults(request, repositoryScale, out var adaptiveBudget);
        var anchors = BuildCodeExploreAnchors(request, queryInterpretation).ToList();
        var resolutions = new List<CodeExploreAnchorResolution>();
        var candidates = new List<CodeExploreSectionCandidate>();
        var omissions = new List<string>();
        var continuations = new List<CodeExploreContinuationTarget>();
        var allocationFiles = new List<CodeExploreAllocationFileSummary>();
        var candidateSummaries = Array.Empty<CodeExploreCandidateSummary>();
        IReadOnlyList<CodeExploreRankedCandidate> naturalLanguageSourceCompanions = [];
        IReadOnlyDictionary<string, CodeExploreSelectedRelevance> selectedRelevance =
            new Dictionary<string, CodeExploreSelectedRelevance>(StringComparer.Ordinal);
        CodeExploreDiscoverySummary? discovery = null;
        CodeExploreNaturalLanguageIntent? naturalLanguageIntent = null;
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
                queryInterpretation = naturalLanguage.Interpretation;
                discovery = naturalLanguage.Discovery;
                naturalLanguageIntent = naturalLanguage.Intent;
                candidateSummaries = naturalLanguage.Candidates;
                naturalLanguageSourceCompanions = naturalLanguage.SourceCompanions;
                selectedRelevance = naturalLanguage.SelectedRelevance;
                omissions.AddRange(naturalLanguage.Omissions);
            }

            if (anchors.Count == 0)
            {
                EnsureCurrent(engine, snapshot.Generation);
                return CreateUnanchoredCodeExploreResult(
                    snapshot,
                    request,
                    queryInterpretation,
                    discovery,
                    candidateSummaries,
                    adaptiveBudget);
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

            for (var companionIndex = 0; companionIndex < naturalLanguageSourceCompanions.Count; companionIndex++)
            {
                await AddNaturalLanguageSourceCompanionAsync(
                    snapshot,
                    projection,
                    sourceReader,
                    naturalLanguageSourceCompanions[companionIndex],
                    anchors.Count + companionIndex + 1,
                    candidates,
                    timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            timeReached = true;
            omissions.Add("The code exploration time limit was reached during anchor resolution.");
        }

        var selectedSections = new List<CodeExploreFileSection>();
        var selectedSourceFiles = new HashSet<string>(PathComparer);
        var selectedArtifactOrigins = new List<CodeExploreSectionCandidate>();
        CodeExploreArtifactProjection? artifactProjection = null;
        var backReferences = new List<CodeExploreBackReference>();
        var emissionRecords = new List<CodeExploreEmissionRecord>();
        var dedupReasons = new HashSet<string>(StringComparer.Ordinal);
        var dedupCandidateRanges = 0;
        var coveredRanges = 0;
        var suppressedRanges = 0;
        var reEmittedRanges = 0;
        var reclaimedCharacters = 0;
        var usedForNewSourceCharacters = 0;
        var seenSections = new HashSet<string>(StringComparer.Ordinal);
        var reservedCharacters = discovery is null
            ? 0
            : Math.Min(
                request.Limits.MaximumSourceCharacters,
                EstimateReservedCodeExploreCharacters(queryInterpretation, discovery));
        var availableSourceCharacters = Math.Max(0, request.Limits.MaximumSourceCharacters - reservedCharacters);
        var remainingSourceCharacters = availableSourceCharacters;
        var remainingSourceCharactersWithoutSuppression = availableSourceCharacters;
        var outputBoundReached = false;
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
                if (ShouldBuildCodeExploreFlow(request, flowAnchors, naturalLanguageIntent))
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

                if (ShouldBuildCodeExploreBlastRadius(request, flowAnchors, naturalLanguageIntent))
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
            var omitUnrequestedTestSource = naturalLanguageIntent is not null
                && !HasTestFocus(queryInterpretation);
            if (omitUnrequestedTestSource
                && candidates.Any(candidate => IsTestSourceCandidate(candidate)
                    && !IsExactSelectedSourceCandidate(candidate, selectedRelevance, queryInterpretation)))
            {
                omissions.Add("Unrequested test source was omitted; compact test dependencies remain available in blast-radius evidence.");
            }

            await ProjectAvailableSourceAsync(
                candidate => !omitUnrequestedTestSource
                    || !IsTestSourceCandidate(candidate)
                    || IsExactSelectedSourceCandidate(candidate, selectedRelevance, queryInterpretation),
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
                var allOrderedCandidates = candidates
                    .Where(candidate => !seenSections.Contains(CreateSectionKey(candidate)))
                    .OrderByDescending(candidate => candidate.IsFlowSpine)
                    .ThenByDescending(candidate => candidate.Importance)
                    .ThenBy(candidate => IsExactSelectedSourceCandidate(
                        candidate,
                        selectedRelevance,
                        queryInterpretation) ? 0 : 1)
                    .ThenBy(candidate => candidate.Priority)
                    .ThenBy(candidate => candidate.AllocationRank ?? int.MaxValue)
                    .ThenBy(candidate => candidate.FilePath, PathComparer)
                    .ThenBy(candidate => candidate.Location?.Range.StartLine ?? candidate.PreferredLine ?? 0)
                    .ThenBy(candidate => candidate.Identity?.Id ?? string.Empty, StringComparer.Ordinal)
                    .DistinctBy(CreateSectionKey, StringComparer.Ordinal)
                    .ToArray();
                var eligibleCandidates = allOrderedCandidates
                    .Where(predicate)
                    .ToArray();
                var broadContainerKeys = GetBroadContainerSourceCandidateKeys(
                    eligibleCandidates,
                    selectedRelevance,
                    queryInterpretation,
                    request.Limits.MaximumPerFileSourceCharacters);
                foreach (var broadContainer in eligibleCandidates
                    .Where(candidate => broadContainerKeys.Contains(CreateSectionKey(candidate))))
                {
                    _ = seenSections.Add(CreateSectionKey(broadContainer));
                    outputBoundReached = true;
                    AddOrMergeContinuation(
                        continuations,
                        CreateSkippedCandidateContinuation(
                            snapshot,
                            broadContainer,
                            "The oversized container envelope was replaced by more specific selected members from the same file."));
                    allocationFiles.Add(new CodeExploreAllocationFileSummary(
                        ToRepositoryRelativePath(broadContainer.FilePath, snapshot.RepositoryPath),
                        0,
                        0,
                        CodeExploreSourceCompleteness.Omitted,
                        false,
                        "An oversized non-exact container envelope was omitted because more specific selected members carry the file's source reservation."));
                }

                var unclutteredCandidates = eligibleCandidates
                    .Where(candidate => !broadContainerKeys.Contains(CreateSectionKey(candidate)))
                    .ToArray();
                var orderedCandidates = await ClusterNearbySourceCandidatesAsync(
                    unclutteredCandidates,
                    selectedRelevance,
                    queryInterpretation,
                    request.Limits.MaximumPerFileSourceCharacters,
                    timeout.Token);
                var sourceAllocationCandidates = CreateSourceAllocationCandidates(
                    orderedCandidates,
                    selectedRelevance,
                    queryInterpretation);
                var allocationPlan = CodeExploreSourceAllocationPlanner.Create(
                    sourceAllocationCandidates,
                    remainingSourceCharacters,
                    Math.Max(0, request.Limits.MaximumFiles - selectedSourceFiles.Count),
                    request.Limits.MaximumPerFileSourceCharacters);
                var remainingReservations = allocationPlan.Reservations.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal);
                var remainingCandidatesByAllocationKey = orderedCandidates
                    .GroupBy(CreateSourceAllocationKey, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                foreach (var candidate in orderedCandidates)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var dedupeKey = CreateSectionKey(candidate);
                    var allocationKey = CreateSourceAllocationKey(candidate);
                    if (!seenSections.Add(dedupeKey))
                    {
                        continue;
                    }

                    if (!allocationPlan.Reservations.TryGetValue(allocationKey, out var reservation))
                    {
                        outputBoundReached = true;
                        var continuation = CreateSkippedCandidateContinuation(
                            snapshot,
                            candidate,
                            "The source allocation pass retained stronger evidence above the relevance cliff.");
                        AddOrMergeContinuation(continuations, continuation);
                        allocationFiles.Add(new CodeExploreAllocationFileSummary(
                            ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath),
                            0,
                            0,
                            CodeExploreSourceCompleteness.Omitted,
                            false,
                            "Source was cliffed by relevance-aware allocation; use the continuation target if this tail evidence is needed."));
                        continue;
                    }

                    dedupCandidateRanges++;
                    var priorCovered = await TryCreateCodeExploreBackReferenceAsync(
                        workspaceId,
                        snapshot,
                        sourceReader,
                        visibleSourceFrontier,
                        candidate,
                        timeout.Token);
                    if (priorCovered.BackReference is not null)
                    {
                        selectedArtifactOrigins.Add(candidate);
                        coveredRanges++;
                        suppressedRanges++;
                        reclaimedCharacters += priorCovered.SourceCharacters;
                        _ = ConsumeSourceCharacters(
                            ref remainingSourceCharactersWithoutSuppression,
                            priorCovered.SourceCharacters);
                        backReferences.Add(priorCovered.BackReference);
                        dedupReasons.Add("An unchanged complete source range already visible in the current request was replaced with a compact back-reference.");
                        continue;
                    }

                    var disqualificationReason = priorCovered.DisqualificationReason;
                    if (disqualificationReason is not null)
                    {
                        dedupReasons.Add(disqualificationReason);
                    }

                    var relativeCandidatePath = ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath);
                    if (!selectedSourceFiles.Contains(relativeCandidatePath)
                        && selectedSourceFiles.Count >= request.Limits.MaximumFiles)
                    {
                        outputBoundReached = true;
                        AddOrMergeContinuation(
                            continuations,
                            CreateSkippedCandidateContinuation(
                                snapshot,
                                candidate,
                                "The maximum file-section count was reached."));
                        continue;
                    }

                    if (remainingSourceCharacters <= 0 && selectedSections.Count > 0)
                    {
                        outputBoundReached = true;
                        AddOrMergeContinuation(
                            continuations,
                            CreateSkippedCandidateContinuation(
                                snapshot,
                                candidate,
                                "The maximum total source-character count was reached."));
                        continue;
                    }

                    var remainingFileReservation = remainingReservations.GetValueOrDefault(allocationKey);
                    var remainingCandidatesInFile = remainingCandidatesByAllocationKey.GetValueOrDefault(allocationKey, 1);
                    var fairFileShare = remainingFileReservation / Math.Max(1, remainingCandidatesInFile);
                    var availableForCandidate = Math.Min(
                        request.Limits.MaximumPerFileSourceCharacters,
                        Math.Min(remainingFileReservation, Math.Max(0, remainingSourceCharacters)));
                    var sourceAllowance = Math.Min(
                        availableForCandidate,
                        Math.Max(MinimumUsefulSourceCharacters, fairFileShare));
                    if (sourceAllowance <= 0
                        || (sourceAllowance < MinimumUsefulSourceCharacters && selectedSections.Count > 0))
                    {
                        outputBoundReached = true;
                        var continuation = CreateSkippedCandidateContinuation(
                            snapshot,
                            candidate,
                            "The source allocation pass reserved remaining budget for stronger code_explore candidates.");
                        AddOrMergeContinuation(continuations, continuation);
                        allocationFiles.Add(new CodeExploreAllocationFileSummary(
                            ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath),
                            0,
                            0,
                            CodeExploreSourceCompleteness.Omitted,
                            false,
                            "Source was cliffed by relevance-aware allocation; use the continuation target if this tail evidence is needed."));
                        continue;
                    }

                    var projected = await ProjectCodeExploreSectionAsync(
                        snapshot,
                        activeProjection,
                        sourceReader,
                        candidate,
                        sourceAllowance,
                        timeout.Token);
                    remainingReservations[allocationKey] = Math.Max(
                        0,
                        remainingFileReservation - projected.SourceCharacters);
                    remainingCandidatesByAllocationKey[allocationKey] = Math.Max(
                        0,
                        remainingCandidatesInFile - 1);
                    remainingSourceCharacters -= projected.SourceCharacters;
                    var unreclaimedSourceCharacters = ConsumeSourceCharacters(
                        ref remainingSourceCharactersWithoutSuppression,
                        projected.SourceCharacters);
                    var reclaimedSourceCharacters = projected.SourceCharacters - unreclaimedSourceCharacters;
                    if (reclaimedSourceCharacters > 0)
                    {
                        usedForNewSourceCharacters += Math.Min(
                            reclaimedSourceCharacters,
                            Math.Max(0, reclaimedCharacters - usedForNewSourceCharacters));
                    }

                    selectedSections.Add(projected.Section);
                    _ = selectedSourceFiles.Add(projected.Section.FilePath);
                    if (CanUseProjectedSectionAsArtifactOrigin(projected))
                    {
                        selectedArtifactOrigins.Add(candidate);
                    }

                    if (disqualificationReason is not null && projected.SourceCharacters > 0)
                    {
                        reEmittedRanges++;
                    }

                    if (projected.Section.Source.NumberedLines.Count > 0
                        && projected.Section.Source.FileSha256 is not null
                        && projected.SourceCharacters > 0)
                    {
                        emissionRecords.Add(new CodeExploreEmissionRecord(
                            projected.Section.FilePath,
                            projected.Section.Source.Range,
                            projected.Section.Source.FileSha256,
                            projected.Section.Source.RangeSha256,
                            projected.SourceCharacters));
                    }

                    allocationFiles.Add(new CodeExploreAllocationFileSummary(
                        projected.Section.FilePath,
                        sourceAllowance,
                        projected.SourceCharacters,
                        projected.Section.Source.Completeness,
                        IsUsefulCodeExploreSection(projected.Section),
                        projected.Section.Source.OmittedRanges.FirstOrDefault()));
                    foreach (var continuation in projected.ContinuationTargets)
                    {
                        AddOrMergeContinuation(continuations, continuation);
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

        if (!timeReached
            && projection is not null
            && request.AssociatedArtifacts != CodeExploreAssociatedArtifactsMode.Disabled
            && sourceReader is ICodeExploreArtifactReader artifactReader
            && selectedArtifactOrigins.Count > 0)
        {
            try
            {
                artifactProjection = await BuildAssociatedArtifactsAsync(
                    snapshot,
                    artifactReader,
                    request,
                    queryInterpretation,
                    selectedArtifactOrigins,
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                artifactProjection = CreateTimedOutAssociatedArtifactProjection(
                    snapshot,
                    request,
                    selectedArtifactOrigins);
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

        if (suppressedRanges > 0)
        {
            omissions.Add("Unchanged source already visible in the current request was replaced with compact code_explore back-references.");
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
            && selectedSections.Sum(section => Math.Max(1, section.SemanticIdentities.Count))
                + backReferences.Count
                == candidates.Select(CreateSectionKey).Distinct(StringComparer.Ordinal).Count();
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
        var spentSourceCharacters = availableSourceCharacters - remainingSourceCharacters;
        var allocation = new CodeExploreAllocationSummary(
            request.Limits.MaximumSourceCharacters,
            reservedCharacters,
            spentSourceCharacters,
            CreateAllocationBudgetSource(adaptiveBudget),
            allocationFiles);
        var deduplication = visibleSourceFrontier is null || dedupCandidateRanges == 0
            ? null
            : new CodeExploreDedupSummary(
                dedupCandidateRanges,
                coveredRanges,
                suppressedRanges,
                reEmittedRanges,
                reclaimedCharacters,
                usedForNewSourceCharacters,
                dedupReasons.Order(StringComparer.Ordinal).ToArray());
        var continuationTargets = continuations
            .DistinctBy(target => $"{target.Kind}:{target.Anchor}:{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.StartAtLine}:{target.SelectionMode}:{target.ExpectedFileSha256}:{target.WorkspaceGeneration}:{target.Reason}")
            .ToArray();
        adaptiveBudget = UpdateAdaptiveBudgetScale(
            adaptiveBudget,
            discovery,
            artifactProjection?.Coverage);
        var availability = CreateCodeExploreAvailability(
            snapshot,
            coverage,
            resolutions,
            selectedSections,
            backReferences,
            continuationTargets,
            timeReached);
        var presentation = CreateCodeExplorePresentation(
            availability,
            selectedSections,
            backReferences,
            continuationTargets,
            coverageOmissions,
            artifactProjection?.Coverage,
            adaptiveBudget.PresentationVerbosity);
        var fileRelevance = CreateCodeExploreFileRelevanceSummaries(
            candidateSummaries,
            allocationFiles,
            selectedSections,
            backReferences,
            continuationTargets,
            queryInterpretation);
        return new CodeExploreResult(
            snapshot.Generation,
            snapshot.Confidence,
            resolutions,
            selectedSections,
            coverage,
            coverageOmissions,
            continuationTargets,
            flow,
            blastRadius,
            queryInterpretation,
            discovery,
            candidateSummaries,
            allocation,
            backReferences,
            deduplication,
            emissionRecords,
            artifactProjection?.Artifacts,
            artifactProjection?.Coverage,
            availability,
            presentation,
            adaptiveBudget,
            fileRelevance);
    }

    private static CodeExploreAvailability? CreateInitialCodeExploreAvailability(
        CodeExploreReadinessSnapshot snapshot)
    {
        if (snapshot.Solution is null || string.IsNullOrWhiteSpace(snapshot.RepositoryPath))
        {
            var status = snapshot.Confidence == SemanticConfidenceLevel.None
                ? CodeExploreAvailabilityStatus.SemanticWorkspaceUnavailable
                : CodeExploreAvailabilityStatus.SemanticReadinessBelowMinimum;
            var reason = snapshot.Confidence == SemanticConfidenceLevel.None
                ? "The opened workspace has not loaded compiler-aware project state yet."
                : "The semantic workspace does not currently expose compiler-aware project state.";
            return new CodeExploreAvailability(
                status,
                reason,
                true,
                snapshot.Confidence,
                SemanticConfidenceLevel.PartialCompilation,
                true,
                [new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.WaitForWorkspace,
                    "Wait for semantic workspace loading, then retry code_explore for C# source-bearing evidence.")]);
        }

        if (snapshot.Confidence < SemanticConfidenceLevel.PartialCompilation)
        {
            return new CodeExploreAvailability(
                CodeExploreAvailabilityStatus.SemanticReadinessBelowMinimum,
                "The semantic workspace is below the minimum readiness required for compiler-known C# source exploration.",
                true,
                snapshot.Confidence,
                SemanticConfidenceLevel.PartialCompilation,
                true,
                [new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.WaitForWorkspace,
                    "Wait until at least partial compilation is available, then retry code_explore.")]);
        }

        if (snapshot.CompiledProjects.Count == 0)
        {
            return new CodeExploreAvailability(
                CodeExploreAvailabilityStatus.NoCompiledProjects,
                "The opened workspace has no compiled C# projects available for source-bearing semantic exploration.",
                true,
                snapshot.Confidence,
                SemanticConfidenceLevel.PartialCompilation,
                true,
                [new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.OpenWorkspace,
                    "Open or load a C# solution/project workspace before retrying code_explore.")]);
        }

        return null;
    }

    private static CodeExploreAvailability CreateInitialTimeoutCodeExploreAvailability(
        CodeExploreReadinessSnapshot snapshot)
    {
        return new CodeExploreAvailability(
            CodeExploreAvailabilityStatus.TimedOutPartial,
            "The code exploration time limit was reached while inspecting workspace scale before source could be projected.",
            true,
            snapshot.Confidence,
            SemanticConfidenceLevel.PartialCompilation,
            true,
            [new CodeExploreNextActionHint(
                CodeExploreNextActionKind.RefineAnchor,
                "Retry code_explore with more exact symbol or path anchors, or use a narrower granular fallback for the immediate gap.")]);
    }

    private static CodeExploreResult CreateUnavailableCodeExploreResult(
        CodeExploreReadinessSnapshot snapshot,
        CodeExploreRequest request,
        CodeExploreQueryInterpretation interpretation,
        CodeExploreAvailability availability,
        CodeExploreAdaptiveBudget adaptiveBudget)
    {
        var coverage = new CodeExploreCoverage(
            false,
            false,
            false,
            true,
            [availability.Reason]);
        var allocation = new CodeExploreAllocationSummary(
            request.Limits.MaximumSourceCharacters,
            0,
            0,
            CreateAllocationBudgetSource(adaptiveBudget),
            []);
        var presentation = CreateCodeExplorePresentation(
            availability,
            [],
            [],
            [],
            coverage.Omissions,
            null,
            adaptiveBudget.PresentationVerbosity);
        return new CodeExploreResult(
            snapshot.Generation,
            snapshot.Confidence,
            [],
            [],
            coverage,
            coverage.Omissions,
            [],
            QueryInterpretation: interpretation,
            Allocation: allocation,
            Availability: availability,
            Presentation: presentation,
            AdaptiveBudget: adaptiveBudget,
            FileRelevance: []);
    }

    private static CodeExploreRepositoryScale CreateUnknownCodeExploreRepositoryScale(
        int associatedArtifactCandidateCount)
    {
        return new CodeExploreRepositoryScale(
            CodeExploreRepositoryScaleTier.Unknown,
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            null,
            associatedArtifactCandidateCount);
    }

    private static CodeExploreRepositoryScale CreateCodeExploreRepositoryScale(
        AdvancedSemanticSnapshot snapshot,
        ICodeExploreSourceReader sourceReader,
        int? declarationCatalogEntryCount,
        bool? declarationCatalogComplete,
        int associatedArtifactCandidateCount,
        CancellationToken cancellationToken)
    {
        var projectRecords = new List<CodeExploreScaleProject>();
        var inspectedProjects = 0;
        var inspectedDocuments = 0;
        foreach (var project in snapshot.Solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inspectedProjects >= MaximumCodeExploreScaleProjects)
            {
                break;
            }

            inspectedProjects++;
            var record = CreatePolicyAdmittedScaleProject(
                project,
                sourceReader,
                ref inspectedDocuments,
                cancellationToken);
            if (record.ProjectFileAllowed || record.DocumentCount > 0)
            {
                projectRecords.Add(record);
            }

            if (inspectedDocuments >= MaximumCodeExploreScaleDocuments)
            {
                break;
            }
        }

        var records = projectRecords.ToArray();
        var totalDocuments = records.Sum(record => record.DocumentCount);
        var compiledDocuments = records
            .Where(record => snapshot.CompiledProjects.Contains(record.Project.Id))
            .Sum(record => record.DocumentCount);
        var compiledProjectCount = records.Count(record => snapshot.CompiledProjects.Contains(record.Project.Id));
        var generatedDocuments = records.Sum(record => record.GeneratedDocumentCount);
        int? targetFrameworkCount = null;
        var tier = SelectCodeExploreRepositoryScaleTier(
            records.Length,
            totalDocuments,
            compiledDocuments,
            declarationCatalogEntryCount);
        return new CodeExploreRepositoryScale(
            tier,
            records.Length,
            compiledProjectCount,
            totalDocuments,
            compiledDocuments,
            generatedDocuments,
            targetFrameworkCount,
            declarationCatalogEntryCount,
            declarationCatalogComplete,
            associatedArtifactCandidateCount);
    }

    private static CodeExploreScaleProject CreatePolicyAdmittedScaleProject(
        Project project,
        ICodeExploreSourceReader sourceReader,
        ref int inspectedDocuments,
        CancellationToken cancellationToken)
    {
        var documentCount = 0;
        var generatedDocumentCount = 0;
        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inspectedDocuments >= MaximumCodeExploreScaleDocuments)
            {
                break;
            }

            inspectedDocuments++;
            if (document.FilePath is not { } path || !sourceReader.IsPathAllowed(path))
            {
                continue;
            }

            documentCount++;
            if (IsGeneratedPath(document.FilePath ?? document.Name))
            {
                generatedDocumentCount++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var projectFileAllowed = project.FilePath is { } projectPath && sourceReader.IsPathAllowed(projectPath);
        return new CodeExploreScaleProject(
            project,
            projectFileAllowed,
            documentCount,
            generatedDocumentCount);
    }

    private static CodeExploreRepositoryScaleTier SelectCodeExploreRepositoryScaleTier(
        int projectCount,
        int totalDocuments,
        int compiledDocuments,
        int? declarationCatalogEntryCount)
    {
        var effectiveDocuments = Math.Max(totalDocuments, compiledDocuments);
        var declarations = declarationCatalogEntryCount ?? 0;
        if (projectCount == 0 && effectiveDocuments == 0 && declarations == 0)
        {
            return CodeExploreRepositoryScaleTier.Unknown;
        }

        if (projectCount <= 2 && effectiveDocuments <= 25 && declarations <= 800)
        {
            return CodeExploreRepositoryScaleTier.Tiny;
        }

        if (projectCount <= 8 && effectiveDocuments <= 120 && declarations <= 4_000)
        {
            return CodeExploreRepositoryScaleTier.Small;
        }

        if (projectCount <= 30 && effectiveDocuments <= 600 && declarations <= 18_000)
        {
            return CodeExploreRepositoryScaleTier.Medium;
        }

        if (projectCount <= 120 && effectiveDocuments <= 2_500 && declarations <= 60_000)
        {
            return CodeExploreRepositoryScaleTier.Large;
        }

        return CodeExploreRepositoryScaleTier.VeryLarge;
    }

    private static CodeExploreRequest ApplyCodeExploreAdaptiveDefaults(
        CodeExploreRequest request,
        CodeExploreRepositoryScale repositoryScale,
        out CodeExploreAdaptiveBudget adaptiveBudget)
    {
        var envelope = SelectAdaptiveEnvelope(repositoryScale.Tier);
        var appliesToSourceDefaults = UsesDefaultCodeExploreSourceEnvelope(request.Limits);
        var limits = appliesToSourceDefaults
            ? request.Limits with
            {
                MaximumFiles = Math.Min(request.Limits.MaximumFiles, envelope.MaximumFiles),
                MaximumSourceCharacters = Math.Min(request.Limits.MaximumSourceCharacters, envelope.MaximumSourceCharacters),
                MaximumPerFileSourceCharacters = Math.Min(request.Limits.MaximumPerFileSourceCharacters, envelope.MaximumPerFileSourceCharacters),
            }
            : request.Limits;
        var budgetSource = appliesToSourceDefaults
            ? $"repository scale {repositoryScale.Tier} adaptive defaults applied within request/model source limits"
            : $"repository scale {repositoryScale.Tier} recorded; explicit request source limits retained";
        adaptiveBudget = new CodeExploreAdaptiveBudget(
            repositoryScale,
            limits.MaximumFiles,
            limits.MaximumSourceCharacters,
            limits.MaximumPerFileSourceCharacters,
            ResolveNaturalLanguageCandidateSummaryLimit(limits.MaximumSourceCharacters, 0, 0),
            envelope.RecommendedFollowUpCount,
            envelope.PresentationVerbosity,
            budgetSource);
        return request with { Limits = limits };
    }

    private static bool UsesDefaultCodeExploreSourceEnvelope(CodeExploreLimits limits)
    {
        var defaults = new CodeExploreLimits();
        return limits.MaximumFiles == defaults.MaximumFiles
            && limits.MaximumSourceCharacters == defaults.MaximumSourceCharacters
            && limits.MaximumPerFileSourceCharacters == defaults.MaximumPerFileSourceCharacters;
    }

    private static (
        int MaximumFiles,
        int MaximumSourceCharacters,
        int MaximumPerFileSourceCharacters,
        int RecommendedFollowUpCount,
        CodeExplorePresentationVerbosity PresentationVerbosity) SelectAdaptiveEnvelope(CodeExploreRepositoryScaleTier tier)
    {
        return tier switch
        {
            CodeExploreRepositoryScaleTier.Tiny => (4, 13_000, 3_800, 1, CodeExplorePresentationVerbosity.Compact),
            CodeExploreRepositoryScaleTier.Small => (5, 18_000, 3_800, 1, CodeExplorePresentationVerbosity.Compact),
            CodeExploreRepositoryScaleTier.Large => (8, 24_000, 6_500, 3, CodeExplorePresentationVerbosity.Guided),
            CodeExploreRepositoryScaleTier.VeryLarge => (8, 24_000, 7_000, 4, CodeExplorePresentationVerbosity.Guided),
            _ => (8, 24_000, 6_500, 2, CodeExplorePresentationVerbosity.Standard),
        };
    }

    private static CodeExploreAdaptiveBudget UpdateAdaptiveBudgetScale(
        CodeExploreAdaptiveBudget adaptiveBudget,
        CodeExploreDiscoverySummary? discovery,
        CodeExploreArtifactCoverage? artifactCoverage)
    {
        var current = adaptiveBudget.RepositoryScale;
        var updated = current with
        {
            DeclarationCatalogEntryCount = discovery?.CatalogEntryCount ?? current.DeclarationCatalogEntryCount,
            DeclarationCatalogComplete = discovery?.CatalogComplete ?? current.DeclarationCatalogComplete,
            AssociatedArtifactCandidateCount = artifactCoverage?.CandidateCount ?? current.AssociatedArtifactCandidateCount,
        };
        var budgetSource = discovery is null
            ? adaptiveBudget.BudgetSource
            : adaptiveBudget.BudgetSource + "; declaration-catalog observations attached after envelope selection";
        return adaptiveBudget with
        {
            RepositoryScale = updated,
            BudgetSource = budgetSource,
        };
    }

    private static string CreateAllocationBudgetSource(CodeExploreAdaptiveBudget adaptiveBudget)
    {
        return adaptiveBudget.BudgetSource
            + "; request.maximumSourceCharacters after reserved metadata, plus relevance-aware per-file allocation";
    }

    private static async Task<CodeExploreSectionCandidate[]> ClusterNearbySourceCandidatesAsync(
        IReadOnlyList<CodeExploreSectionCandidate> candidates,
        IReadOnlyDictionary<string, CodeExploreSelectedRelevance> selectedRelevance,
        CodeExploreQueryInterpretation queryInterpretation,
        int maximumPerFileSourceCharacters,
        CancellationToken cancellationToken)
    {
        var clustered = new List<CodeExploreSectionCandidate>();
        foreach (var fileGroup in candidates.GroupBy(CreateSourceAllocationKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eligible = fileGroup
                .Where(candidate => IsClusterableSourceCandidate(candidate)
                    && !IsExactSelectedSourceCandidate(candidate, selectedRelevance, queryInterpretation))
                .OrderBy(candidate => candidate.Span?.Start)
                .ToArray();
            clustered.AddRange(fileGroup.Where(candidate => !IsClusterableSourceCandidate(candidate)
                || IsExactSelectedSourceCandidate(candidate, selectedRelevance, queryInterpretation)));
            if (eligible.Length == 0)
            {
                continue;
            }

            var document = eligible[0].Document
                ?? throw new InvalidOperationException("Clustered source requires a loaded document.");
            var text = await document.GetTextAsync(cancellationToken);
            var current = eligible[0];
            foreach (var next in eligible.Skip(1))
            {
                if (CanClusterSourceCandidates(current, next, text, maximumPerFileSourceCharacters))
                {
                    current = MergeSourceCandidates(current, next);
                    continue;
                }

                clustered.Add(current);
                current = next;
            }

            clustered.Add(current);
        }

        return
        [
            .. clustered
                .OrderByDescending(candidate => candidate.IsFlowSpine)
                .ThenByDescending(candidate => candidate.Importance)
                .ThenBy(candidate => IsExactSelectedSourceCandidate(
                    candidate,
                    selectedRelevance,
                    queryInterpretation) ? 0 : 1)
                .ThenBy(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.AllocationRank ?? int.MaxValue)
                .ThenBy(candidate => candidate.FilePath, PathComparer)
                .ThenBy(candidate => candidate.Span?.Start ?? candidate.PreferredLine ?? 0)
                .ThenBy(candidate => candidate.Identity?.Id ?? string.Empty, StringComparer.Ordinal),
        ];
    }

    private static bool IsClusterableSourceCandidate(CodeExploreSectionCandidate candidate)
    {
        return candidate.Document is not null
            && candidate.Span is not null
            && candidate.Identity is not null
            && candidate.SelectionMode == CodeExplorePathSelectionMode.Auto
            && candidate.ExpectedFileSha256 is null
            && candidate.ExpectedWorkspaceGeneration is null;
    }

    private static bool CanClusterSourceCandidates(
        CodeExploreSectionCandidate current,
        CodeExploreSectionCandidate next,
        SourceText text,
        int maximumPerFileSourceCharacters)
    {
        if (current.Document?.Id != next.Document?.Id
            || current.Span is not { } currentSpan
            || next.Span is not { } nextSpan)
        {
            return false;
        }

        var union = TextSpan.FromBounds(
            Math.Min(currentSpan.Start, nextSpan.Start),
            Math.Max(currentSpan.End, nextSpan.End));
        if (union.Length > maximumPerFileSourceCharacters)
        {
            return false;
        }

        var currentEnd = Math.Max(currentSpan.End - 1, currentSpan.Start);
        var currentEndLine = text.Lines.GetLineFromPosition(Math.Min(currentEnd, text.Length)).LineNumber;
        var nextStartLine = text.Lines.GetLineFromPosition(Math.Min(nextSpan.Start, text.Length)).LineNumber;
        return nextStartLine - currentEndLine - 1 <= MaximumSourceClusterGapLines;
    }

    private static CodeExploreSectionCandidate MergeSourceCandidates(
        CodeExploreSectionCandidate current,
        CodeExploreSectionCandidate next)
    {
        var currentSpan = current.Span
            ?? throw new InvalidOperationException("Clustered source requires a current source span.");
        var nextSpan = next.Span
            ?? throw new InvalidOperationException("Clustered source requires a next source span.");
        var identities = CreateSectionIdentities(current)
            .Concat(CreateSectionIdentities(next))
            .DistinctBy(identity => identity.Id, StringComparer.Ordinal)
            .ToArray();
        return current with
        {
            Span = TextSpan.FromBounds(
                Math.Min(currentSpan.Start, nextSpan.Start),
                Math.Max(currentSpan.End, nextSpan.End)),
            Identity = identities[0],
            Location = MergeClusteredLocations(current.Location, next.Location),
            AdditionalIdentities = identities.Skip(1).ToArray(),
            Priority = Math.Min(current.Priority, next.Priority),
            PreferredLine = MinNullable(current.PreferredLine, next.PreferredLine),
            EndLine = MaxNullable(current.EndLine, next.EndLine),
            AllocationRank = current.AllocationRank is { } currentRank
                ? next.AllocationRank is { } nextRank
                    ? Math.Min(currentRank, nextRank)
                    : currentRank
                : next.AllocationRank,
            Importance = MaxSourceImportance(current.Importance, next.Importance),
            IsFlowSpine = current.IsFlowSpine || next.IsFlowSpine,
            SelectionReason = "Nearby selected declarations were clustered into one exact contiguous source range.",
        };
    }

    private static CodeExploreSourceImportance MaxSourceImportance(
        CodeExploreSourceImportance left,
        CodeExploreSourceImportance right)
    {
        return (CodeExploreSourceImportance)Math.Max((int)left, (int)right);
    }

    private static CodeExploreLocation? MergeClusteredLocations(
        CodeExploreLocation? current,
        CodeExploreLocation? next)
    {
        if (current is null)
        {
            return next;
        }

        if (next is null)
        {
            return current;
        }

        var startColumn = current.Range.StartLine <= next.Range.StartLine
            ? current.Range.StartColumn
            : next.Range.StartColumn;
        var endColumn = current.Range.EndLine >= next.Range.EndLine
            ? current.Range.EndColumn
            : next.Range.EndColumn;
        return current with
        {
            Range = new SourceRange(
                Math.Min(current.Range.StartLine, next.Range.StartLine),
                startColumn,
                Math.Max(current.Range.EndLine, next.Range.EndLine),
                endColumn),
        };
    }

    private static int? MinNullable(int? left, int? right)
    {
        return left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);
    }

    private static int? MaxNullable(int? left, int? right)
    {
        return left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);
    }

    private static IReadOnlySet<string> GetBroadContainerSourceCandidateKeys(
        IReadOnlyList<CodeExploreSectionCandidate> candidates,
        IReadOnlyDictionary<string, CodeExploreSelectedRelevance> selectedRelevance,
        CodeExploreQueryInterpretation queryInterpretation,
        int maximumPerFileSourceCharacters)
    {
        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate.Span is not { } containerSpan
                || candidate.Identity is not { } identity
                || !IsTypeDeclarationKind(identity.Kind)
                || IsExactSelectedSourceCandidate(candidate, selectedRelevance, queryInterpretation))
            {
                continue;
            }

            var moreSpecificCandidates = candidates.Where(other =>
                !ReferenceEquals(other, candidate)
                && PathComparer.Equals(other.FilePath, candidate.FilePath)
                && other.Span is { } memberSpan
                && containerSpan.Contains(memberSpan)
                && containerSpan != memberSpan)
                .ToArray();
            var hasExactMember = moreSpecificCandidates.Any(other =>
                IsExactSelectedSourceCandidate(other, selectedRelevance, queryInterpretation));
            var isBroadCompanion = string.Equals(
                candidate.SelectionReason,
                NaturalLanguageCompanionSourceReason,
                StringComparison.Ordinal);
            var hasPrimaryMember = moreSpecificCandidates.Any(other => string.Equals(
                other.SelectionReason,
                NaturalLanguageAnchorSourceReason,
                StringComparison.Ordinal));
            if (hasExactMember
                || (isBroadCompanion && hasPrimaryMember)
                || (containerSpan.Length > maximumPerFileSourceCharacters
                    && moreSpecificCandidates.Length > 0))
            {
                _ = suppressed.Add(CreateSectionKey(candidate));
            }
        }

        return suppressed;
    }

    private static IReadOnlyList<CodeExploreSourceAllocationCandidate> CreateSourceAllocationCandidates(
        IReadOnlyList<CodeExploreSectionCandidate> candidates,
        IReadOnlyDictionary<string, CodeExploreSelectedRelevance> selectedRelevance,
        CodeExploreQueryInterpretation queryInterpretation)
    {
        var files = candidates
            .GroupBy(CreateSourceAllocationKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var fileCandidates = group.ToArray();
                var relevance = fileCandidates
                    .Select(candidate => candidate.Identity is null
                        ? null
                        : selectedRelevance.GetValueOrDefault(candidate.Identity.Id))
                    .Where(item => item is not null)
                    .Cast<CodeExploreSelectedRelevance>()
                    .OrderByDescending(item => item.Score)
                    .ToArray();
                var rawScore = relevance.Length > 0
                    ? relevance[0].Score + Math.Min(5, relevance.Skip(1).Sum(item => item.Score) / 100.0)
                    : fileCandidates.Max(candidate => candidate.Priority switch
                    {
                        <= 0 => 50,
                        1 => 10,
                        2 => 3,
                        _ => 1,
                    });
                var allocationRank = relevance
                    .Select(item => item.Rank)
                    .Concat(fileCandidates
                        .Select(candidate => candidate.AllocationRank)
                        .Where(rank => rank.HasValue)
                        .Select(rank => rank.GetValueOrDefault()))
                    .DefaultIfEmpty(fileCandidates.Min(candidate => Math.Max(1, candidate.Priority + 1)))
                    .Min();
                var isFlowSpine = fileCandidates.Any(candidate => candidate.IsFlowSpine);
                var isPinned = fileCandidates.Any(candidate => IsExactSelectedSourceCandidate(
                        candidate,
                        selectedRelevance,
                        queryInterpretation)
                    || candidate.Importance == CodeExploreSourceImportance.Pinned
                    || candidate.AnchorKind == CodeExploreAnchorKind.Path
                    || candidate.Priority == 0
                    || candidate.SelectionMode != CodeExplorePathSelectionMode.Auto);
                var isGenerated = fileCandidates.All(candidate =>
                    candidate.Location?.IsGenerated ?? IsGeneratedPath(candidate.FilePath));
                var isTest = fileCandidates.All(candidate => IsTestProjectNameOrPath(
                    candidate.Location?.ProjectName ?? string.Empty,
                    candidate.FilePath));
                var sourceWorth = isPinned
                    ? 1.0
                    : isGenerated
                    ? CodeExploreRelevancePolicy.GeneratedScoreMultiplier
                    : isTest
                        ? CodeExploreRelevancePolicy.TestScoreMultiplier
                        : 1.0;
                return new
                {
                    StableKey = group.Key,
                    RawScore = rawScore,
                    IsPinned = isPinned,
                    IsFlowSpine = isFlowSpine,
                    SourceWorth = sourceWorth,
                    AllocationRank = allocationRank,
                };
            })
            .ToArray();
        var maximumRawScore = files
            .Select(file => file.RawScore)
            .DefaultIfEmpty(1)
            .Max();
        return files
            .Select(file => new CodeExploreSourceAllocationCandidate(
                file.StableKey,
                (50 * file.RawScore / Math.Max(1, maximumRawScore)) * (file.IsFlowSpine ? 2 : 1),
                file.SourceWorth,
                file.IsPinned,
                file.IsFlowSpine,
                file.AllocationRank))
            .ToArray();
    }

    private static bool IsExactSelectedSourceCandidate(
        CodeExploreSectionCandidate candidate,
        IReadOnlyDictionary<string, CodeExploreSelectedRelevance> selectedRelevance,
        CodeExploreQueryInterpretation queryInterpretation)
    {
        if (CreateSectionIdentities(candidate).Any(identity =>
            selectedRelevance.TryGetValue(identity.Id, out var relevance)
            && HasExactCandidateReason(relevance.Reasons)))
        {
            return true;
        }

        return candidate.AnchorKind switch
        {
            CodeExploreAnchorKind.SymbolId => queryInterpretation.StableSymbolIds.Contains(
                candidate.Anchor,
                StringComparer.Ordinal),
            CodeExploreAnchorKind.Path => queryInterpretation.PathLikeSpans.Any(path =>
                PathComparer.Equals(path, candidate.Anchor)),
            _ => false,
        };
    }

    private static string CreateSourceAllocationKey(CodeExploreSectionCandidate candidate)
    {
        var normalized = candidate.FilePath.Replace('\\', '/');
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static CodeExploreAvailability CreateCodeExploreAvailability(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreCoverage coverage,
        IReadOnlyList<CodeExploreAnchorResolution> resolutions,
        IReadOnlyList<CodeExploreFileSection> sections,
        IReadOnlyList<CodeExploreBackReference> backReferences,
        IReadOnlyList<CodeExploreContinuationTarget> continuationTargets,
        bool timeReached)
    {
        var hasVisibleSource = sections.Any(section => section.Source.NumberedLines.Count > 0)
            || backReferences.Count > 0;
        if (timeReached)
        {
            var reason = hasVisibleSource || resolutions.Count > 0 || continuationTargets.Count > 0
                ? "The code exploration time limit was reached after returning bounded safe evidence."
                : "The code exploration time limit was reached before source evidence could be assembled.";
            return new CodeExploreAvailability(
                CodeExploreAvailabilityStatus.TimedOutPartial,
                reason,
                true,
                snapshot.Confidence,
                SemanticConfidenceLevel.PartialCompilation,
                continuationTargets.Count == 0,
                CreateTimeoutAvailabilityActions(continuationTargets));
        }

        if (IsProjectScopedCodeExploreSnapshot(snapshot))
        {
            var canRefineAnchor = !hasVisibleSource
                && resolutions.Count > 0
                && resolutions.All(resolution => resolution.Outcome == CodeExploreResolutionOutcome.NotFound);
            return CreateProjectScopedCodeExploreAvailability(
                snapshot,
                hasVisibleSource,
                canRefineAnchor);
        }

        if (!hasVisibleSource
            && resolutions.Count > 0
            && resolutions.All(resolution => resolution.Outcome == CodeExploreResolutionOutcome.NotFound))
        {
            return new CodeExploreAvailability(
                CodeExploreAvailabilityStatus.NoMatchingDeclarations,
                "No compiler-known C# declaration or confined C# path matched the request.",
                true,
                snapshot.Confidence,
                SemanticConfidenceLevel.PartialCompilation,
                true,
                [new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.RefineAnchor,
                    "Retry code_explore with an exact symbol name, stable symbol id, or repository-relative C# path anchor.")]);
        }

        if (!hasVisibleSource && HasPolicySourceOmission(resolutions, sections, coverage.Omissions))
        {
            return new CodeExploreAvailability(
                CodeExploreAvailabilityStatus.NoSourceAfterPolicy,
                "Relevant C# source was found but all source text was removed by path, policy, drift, or source-read safety checks.",
                false,
                snapshot.Confidence,
                SemanticConfidenceLevel.PartialCompilation,
                true,
                [new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.AskUser,
                    "Ask the user whether the workspace scope or repository path policy should be adjusted before retrying.")]);
        }

        return new CodeExploreAvailability(
            CodeExploreAvailabilityStatus.Available,
            "Compiler-aware code exploration completed with the returned bounded evidence.",
            false,
            snapshot.Confidence,
            SemanticConfidenceLevel.PartialCompilation,
            !coverage.SourceComplete,
            []);
    }

    private static bool IsProjectScopedCodeExploreSnapshot(AdvancedSemanticSnapshot snapshot)
    {
        return Path.GetExtension(snapshot.WorkspacePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static CodeExploreAvailability CreateProjectScopedCodeExploreAvailability(
        AdvancedSemanticSnapshot snapshot,
        bool hasVisibleSource,
        bool canRefineAnchor)
    {
        var actions = new List<CodeExploreNextActionHint>();
        if (hasVisibleSource)
        {
            actions.Add(new CodeExploreNextActionHint(
                CodeExploreNextActionKind.UseReturnedSource,
                "Use the returned source only for the loaded project's advertised ranges."));
        }
        else if (canRefineAnchor)
        {
            actions.Add(new CodeExploreNextActionHint(
                CodeExploreNextActionKind.RefineAnchor,
                "Retry code_explore with an exact symbol name, stable symbol id, or repository-relative C# path anchor."));
        }

        const string granularFallback = "Keep complete returned file sections as current source evidence and do not reread those ranges. "
            + "Search once for the most distinctive missing identifier, then read only the exact missing ranges.";
        actions.Add(new CodeExploreNextActionHint(
            CodeExploreNextActionKind.UseGranularFallback,
            granularFallback));
        actions.Add(new CodeExploreNextActionHint(
            CodeExploreNextActionKind.OpenWorkspace,
            "Load the repository solution to obtain compiler-aware cross-project source and usage coverage."));
        var reason = $"The semantic workspace was loaded from one project ({Path.GetFileName(snapshot.WorkspacePath)}); "
            + "referenced projects may be metadata-only and upstream projects or tests are outside this result.";
        return new CodeExploreAvailability(
            CodeExploreAvailabilityStatus.ProjectScopedPartial,
            reason,
            true,
            snapshot.Confidence,
            SemanticConfidenceLevel.PartialCompilation,
            true,
            actions);
    }

    private static IReadOnlyList<CodeExploreNextActionHint> CreateTimeoutAvailabilityActions(
        IReadOnlyList<CodeExploreContinuationTarget> continuationTargets)
    {
        if (continuationTargets.Count > 0)
        {
            var first = continuationTargets[0];
            return [new CodeExploreNextActionHint(
                CodeExploreNextActionKind.FollowContinuation,
                "Use the returned continuation target for a narrower retry instead of repeating the broad request.",
                first.FilePath,
                CreateContinuationRange(first),
                first.Anchor)];
        }

        return [new CodeExploreNextActionHint(
            CodeExploreNextActionKind.RefineAnchor,
            "Retry with a narrower exact symbol, stable id, or C# path anchor.")];
    }

    private static bool HasPolicySourceOmission(
        IReadOnlyList<CodeExploreAnchorResolution> resolutions,
        IReadOnlyList<CodeExploreFileSection> sections,
        IReadOnlyList<string> omissions)
    {
        return resolutions.Any(resolution => resolution.Outcome == CodeExploreResolutionOutcome.Omitted)
            || sections.Any(section => section.Source.Completeness is CodeExploreSourceCompleteness.Omitted or CodeExploreSourceCompleteness.Drifted)
            || omissions.Any(omission => omission.Contains("policy", StringComparison.OrdinalIgnoreCase)
                || omission.Contains("outside", StringComparison.OrdinalIgnoreCase)
                || omission.Contains("drift", StringComparison.OrdinalIgnoreCase)
                || omission.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    private static CodeExplorePresentation CreateCodeExplorePresentation(
        CodeExploreAvailability availability,
        IReadOnlyList<CodeExploreFileSection> sections,
        IReadOnlyList<CodeExploreBackReference> backReferences,
        IReadOnlyList<CodeExploreContinuationTarget> continuationTargets,
        IReadOnlyList<string> omissions,
        CodeExploreArtifactCoverage? artifactCoverage,
        CodeExplorePresentationVerbosity verbosity)
    {
        var guarantees = CreateSourceGuarantees(sections, backReferences, verbosity);
        var notShownTargets = CreateNotShownTargets(continuationTargets, omissions, artifactCoverage, verbosity);
        var nextActions = CreatePresentationNextActions(availability, guarantees, continuationTargets, verbosity);
        var summary = CreatePresentationSummary(
            availability,
            guarantees,
            notShownTargets,
            continuationTargets,
            verbosity);
        return new CodeExplorePresentation(summary, guarantees, notShownTargets, nextActions);
    }

    private static IReadOnlyList<CodeExploreSourceGuarantee> CreateSourceGuarantees(
        IReadOnlyList<CodeExploreFileSection> sections,
        IReadOnlyList<CodeExploreBackReference> backReferences,
        CodeExplorePresentationVerbosity verbosity)
    {
        var maximumGuarantees = verbosity == CodeExplorePresentationVerbosity.Compact
            ? Math.Min(6, MaximumPresentationGuarantees)
            : MaximumPresentationGuarantees;
        var guarantees = new List<CodeExploreSourceGuarantee>();
        foreach (var section in sections.OrderBy(section => section.FilePath, PathComparer).ThenBy(section => section.Source.Range.StartLine))
        {
            if (guarantees.Count >= maximumGuarantees)
            {
                break;
            }

            var source = section.Source;
            var hasCurrentSource = source.NumberedLines.Count > 0 && source.FileSha256 is not null;
            var kind = source.Completeness switch
            {
                CodeExploreSourceCompleteness.Complete when hasCurrentSource && source.RangeSha256 is not null => CodeExploreSourceGuaranteeKind.ReadEquivalent,
                CodeExploreSourceCompleteness.Partial when hasCurrentSource => CodeExploreSourceGuaranteeKind.Partial,
                CodeExploreSourceCompleteness.Drifted => CodeExploreSourceGuaranteeKind.Drifted,
                _ => CodeExploreSourceGuaranteeKind.Omitted,
            };
            var readEquivalent = kind == CodeExploreSourceGuaranteeKind.ReadEquivalent;
            var message = kind switch
            {
                CodeExploreSourceGuaranteeKind.ReadEquivalent => $"{section.FilePath} {FormatSourceRange(source.Range)} is current line-numbered source projected through host output sanitization; digests identify the original current source bytes before sanitization, so treat it as read-equivalent for code structure but not proof of redacted literal bytes.",
                CodeExploreSourceGuaranteeKind.Partial => $"{section.FilePath} {FormatSourceRange(source.Range)} is current line-numbered partial source projected through host output sanitization; use continuation anchors for omitted lines before claiming complete-file or exact-byte coverage.",
                CodeExploreSourceGuaranteeKind.Drifted => $"{section.FilePath} {FormatSourceRange(source.Range)} was not emitted because semantic or continuation identity drifted from current source.",
                _ => $"{section.FilePath} {FormatSourceRange(source.Range)} was not emitted; use the omission reason or continuation target before falling back.",
            };
            guarantees.Add(new CodeExploreSourceGuarantee(
                kind,
                section.FilePath,
                source.Range,
                hasCurrentSource,
                false,
                hasCurrentSource,
                readEquivalent,
                source.FileSha256,
                source.RangeSha256,
                section.SemanticIdentities.Select(identity => identity.Id).Take(8).ToArray(),
                BoundPresentationText(message, 420)));
        }

        foreach (var reference in backReferences.OrderBy(reference => reference.FilePath, PathComparer).ThenBy(reference => reference.Range.StartLine))
        {
            if (guarantees.Count >= maximumGuarantees)
            {
                break;
            }

            var message = $"{reference.FilePath} {FormatSourceRange(reference.Range)} is unchanged source already visible in the current model request via holder {reference.HolderId}, tool call {reference.ToolCallId}; that visible text may already be host-sanitized, so use it for code structure instead of reading the same range again but do not infer redacted literal bytes.";
            guarantees.Add(new CodeExploreSourceGuarantee(
                CodeExploreSourceGuaranteeKind.BackReference,
                reference.FilePath,
                reference.Range,
                true,
                false,
                true,
                true,
                reference.FileSha256,
                reference.RangeSha256,
                reference.SymbolIds.Take(8).ToArray(),
                BoundPresentationText(message, 420)));
        }

        return guarantees;
    }

    private static IReadOnlyList<CodeExploreNotShownTarget> CreateNotShownTargets(
        IReadOnlyList<CodeExploreContinuationTarget> continuationTargets,
        IReadOnlyList<string> omissions,
        CodeExploreArtifactCoverage? artifactCoverage,
        CodeExplorePresentationVerbosity verbosity)
    {
        var maximumTargets = verbosity == CodeExplorePresentationVerbosity.Compact
            ? Math.Min(6, MaximumPresentationNotShownTargets)
            : MaximumPresentationNotShownTargets;
        var targets = new List<CodeExploreNotShownTarget>();
        foreach (var continuation in continuationTargets)
        {
            if (targets.Count >= maximumTargets)
            {
                break;
            }

            AddNotShownTarget(targets, new CodeExploreNotShownTarget(
                CodeExploreNotShownTargetKind.Source,
                continuation.FilePath,
                CreateContinuationRange(continuation),
                BoundPresentationText(continuation.Reason, 320),
                continuation.Anchor,
                continuation.ExpectedFileSha256,
                continuation.WorkspaceGeneration));
        }

        foreach (var continuation in artifactCoverage?.ContinuationTargets ?? [])
        {
            if (targets.Count >= maximumTargets)
            {
                break;
            }

            AddNotShownTarget(targets, new CodeExploreNotShownTarget(
                CodeExploreNotShownTargetKind.Artifact,
                continuation.FilePath,
                CreateArtifactContinuationRange(continuation),
                BoundPresentationText(continuation.Reason, 320),
                continuation.FilePath,
                continuation.ExpectedFileSha256,
                continuation.WorkspaceGeneration));
        }

        var remainingSlots = Math.Max(0, maximumTargets - targets.Count);
        foreach (var omission in omissions.Distinct(StringComparer.Ordinal).Take(remainingSlots))
        {
            AddNotShownTarget(targets, new CodeExploreNotShownTarget(
                CodeExploreNotShownTargetKind.General,
                null,
                null,
                BoundPresentationText(omission, 280)));
        }

        return targets;
    }

    private static void AddNotShownTarget(
        List<CodeExploreNotShownTarget> targets,
        CodeExploreNotShownTarget target)
    {
        var key = $"{target.Kind}:{target.FilePath}:{target.Range?.StartLine}:{target.Range?.EndLine}:{target.ContinuationAnchor}:{target.Reason}";
        if (!targets.Any(existing => string.Equals(
            $"{existing.Kind}:{existing.FilePath}:{existing.Range?.StartLine}:{existing.Range?.EndLine}:{existing.ContinuationAnchor}:{existing.Reason}",
            key,
            StringComparison.Ordinal)))
        {
            targets.Add(target);
        }
    }

    private static SourceRange? CreateContinuationRange(CodeExploreContinuationTarget continuation)
    {
        if (continuation.StartLine is not { } startLine)
        {
            return null;
        }

        return new SourceRange(startLine, 1, continuation.EndLine ?? startLine, 1);
    }

    private static SourceRange? CreateArtifactContinuationRange(CodeExploreArtifactContinuationTarget continuation)
    {
        if (continuation.StartLine is not { } startLine)
        {
            return null;
        }

        return new SourceRange(startLine, 1, continuation.EndLine ?? startLine, 1);
    }

    private static IReadOnlyList<CodeExploreNextActionHint> CreatePresentationNextActions(
        CodeExploreAvailability availability,
        IReadOnlyList<CodeExploreSourceGuarantee> guarantees,
        IReadOnlyList<CodeExploreContinuationTarget> continuationTargets,
        CodeExplorePresentationVerbosity verbosity)
    {
        var maximumActions = verbosity == CodeExplorePresentationVerbosity.Compact
            ? Math.Min(5, MaximumPresentationNextActions)
            : MaximumPresentationNextActions;
        var actions = new List<CodeExploreNextActionHint>();
        foreach (var action in availability.RecommendedActions)
        {
            AddPresentationAction(actions, action, maximumActions);
        }

        var returnedSource = guarantees.FirstOrDefault(guarantee => guarantee.Kind == CodeExploreSourceGuaranteeKind.ReadEquivalent);
        if (returnedSource is not null)
        {
            AddPresentationAction(
                actions,
                new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.UseReturnedSource,
                    "Use complete current FileSections as host-sanitized, source-identity-backed evidence for their advertised ranges; do not infer redacted literal bytes.",
                    returnedSource.FilePath,
                    returnedSource.Range),
                maximumActions);
        }

        var backReference = guarantees.FirstOrDefault(guarantee => guarantee.Kind == CodeExploreSourceGuaranteeKind.BackReference);
        if (backReference is not null)
        {
            AddPresentationAction(
                actions,
                new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.UseBackReference,
                    "Use the named current-request back-reference instead of re-reading unchanged source already visible to the model.",
                    backReference.FilePath,
                    backReference.Range),
                maximumActions);
        }

        if (continuationTargets.Count > 0)
        {
            var continuation = continuationTargets[0];
            AddPresentationAction(
                actions,
                new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.FollowContinuation,
                    "For omitted or partial source, retry with the exact returned continuation target rather than broad search.",
                    continuation.FilePath,
                    CreateContinuationRange(continuation),
                    continuation.Anchor),
                maximumActions);
        }

        if (actions.Count == 0 && availability.GranularFallbackMayHelp)
        {
            AddPresentationAction(
                actions,
                new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.UseGranularFallback,
                    "Use find_symbol, search, or read_file only for the specific gap described by availability or omissions."),
                maximumActions);
        }

        return actions;
    }

    private static void AddPresentationAction(
        List<CodeExploreNextActionHint> actions,
        CodeExploreNextActionHint action,
        int maximumActions)
    {
        if (actions.Count >= maximumActions)
        {
            return;
        }

        var key = $"{action.Kind}:{action.FilePath}:{action.Range?.StartLine}:{action.Range?.EndLine}:{action.ContinuationAnchor}";
        if (!actions.Any(existing => string.Equals(
            $"{existing.Kind}:{existing.FilePath}:{existing.Range?.StartLine}:{existing.Range?.EndLine}:{existing.ContinuationAnchor}",
            key,
            StringComparison.Ordinal)))
        {
            actions.Add(action);
        }
    }

    private static string CreatePresentationSummary(
        CodeExploreAvailability availability,
        IReadOnlyList<CodeExploreSourceGuarantee> guarantees,
        IReadOnlyList<CodeExploreNotShownTarget> notShownTargets,
        IReadOnlyList<CodeExploreContinuationTarget> continuationTargets,
        CodeExplorePresentationVerbosity verbosity)
    {
        var parts = new List<string> { $"Availability: {availability.Status}. {availability.Reason}" };
        var readEquivalentCount = guarantees.Count(guarantee => guarantee.Kind == CodeExploreSourceGuaranteeKind.ReadEquivalent);
        if (readEquivalentCount > 0)
        {
            parts.Add($"{readEquivalentCount} returned current line-numbered source range(s) are host-sanitized and source-identity backed for their advertised ranges.");
        }

        var partialCount = guarantees.Count(guarantee => guarantee.Kind == CodeExploreSourceGuaranteeKind.Partial);
        if (partialCount > 0)
        {
            parts.Add($"{partialCount} returned source range(s) are partial; use exact continuations before claiming complete coverage.");
        }

        var backReferenceCount = guarantees.Count(guarantee => guarantee.Kind == CodeExploreSourceGuaranteeKind.BackReference);
        if (backReferenceCount > 0)
        {
            parts.Add($"{backReferenceCount} unchanged range(s) are already visible by current-request back-reference.");
        }

        if (continuationTargets.Count > 0)
        {
            parts.Add($"{continuationTargets.Count} exact continuation target(s) identify the next focused code_explore calls.");
        }

        if (notShownTargets.Count > 0 && verbosity != CodeExplorePresentationVerbosity.Compact)
        {
            parts.Add($"{notShownTargets.Count} not-shown target(s) summarize omitted source, artifacts, or safety notes.");
        }

        var maximumCharacters = verbosity == CodeExplorePresentationVerbosity.Compact
            ? 520
            : MaximumPresentationSummaryCharacters;
        return BoundPresentationText(string.Join(' ', parts), maximumCharacters);
    }

    private static IReadOnlyList<CodeExploreFileRelevanceSummary> CreateCodeExploreFileRelevanceSummaries(
        IReadOnlyList<CodeExploreCandidateSummary>? candidateSummaries,
        IReadOnlyList<CodeExploreAllocationFileSummary> allocationFiles,
        IReadOnlyList<CodeExploreFileSection> sections,
        IReadOnlyList<CodeExploreBackReference> backReferences,
        IReadOnlyList<CodeExploreContinuationTarget> continuationTargets,
        CodeExploreQueryInterpretation interpretation)
    {
        var candidatesByPath = new Dictionary<string, List<CodeExploreCandidateSummary>>(PathComparer);
        foreach (var summary in candidateSummaries ?? [])
        {
            var path = GetCandidateSummaryPath(summary);
            if (path is null)
            {
                continue;
            }

            if (!candidatesByPath.TryGetValue(path, out var summaries))
            {
                summaries = [];
                candidatesByPath.Add(path, summaries);
            }

            summaries.Add(summary);
        }

        var allocationByPath = allocationFiles
            .GroupBy(file => file.FilePath, PathComparer)
            .ToDictionary(
                group => group.Key,
                group => new CodeExploreAllocationFileSummary(
                    group.Key,
                    group.Sum(file => file.AllowedCharacters),
                    group.Sum(file => file.SpentCharacters),
                    group.Max(file => file.Completeness),
                    group.Any(file => file.UsefulSection),
                    group.Select(file => file.OmissionReason).FirstOrDefault(reason => reason is not null)),
                PathComparer);
        var sectionSourceCharacters = sections
            .GroupBy(section => section.FilePath, PathComparer)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(section => section.Source.NumberedLines.Sum(line => line.Length)),
                PathComparer);
        var returnedSourcePaths = sections
            .Where(section => section.Source.NumberedLines.Count > 0)
            .Select(section => section.FilePath)
            .ToHashSet(PathComparer);
        var backReferencePaths = backReferences
            .Select(reference => reference.FilePath)
            .ToHashSet(PathComparer);
        var continuationPaths = continuationTargets
            .Select(target => target.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path ?? string.Empty)
            .ToHashSet(PathComparer);
        var paths = candidatesByPath.Keys
            .Concat(allocationByPath.Keys)
            .Concat(sectionSourceCharacters.Keys)
            .Concat(backReferencePaths)
            .Concat(continuationPaths)
            .Distinct(PathComparer)
            .ToArray();
        var results = new List<CodeExploreFileRelevanceSummary>();
        foreach (var path in paths)
        {
            candidatesByPath.TryGetValue(path, out var summaries);
            summaries ??= [];
            allocationByPath.TryGetValue(path, out var allocation);
            sectionSourceCharacters.TryGetValue(path, out var spentFromSections);
            var reasons = summaries.Aggregate(
                CodeExploreSelectionReason.None,
                (current, summary) => current | summary.Reasons);
            var band = ResolveFileRelevanceBand(summaries, reasons);
            var allocated = allocation?.AllowedCharacters ?? 0;
            var spent = allocation?.SpentCharacters ?? spentFromSections;
            var sourceCliffed = allocation is { AllowedCharacters: 0, SpentCharacters: 0, OmissionReason: { } omissionReason }
                && omissionReason.Contains("cliffed by relevance-aware allocation", StringComparison.OrdinalIgnoreCase);
            var outputStatus = ResolveFileOutputStatus(
                path,
                allocation,
                spent,
                returnedSourcePaths,
                backReferencePaths,
                continuationPaths);
            results.Add(new CodeExploreFileRelevanceSummary(
                path,
                0,
                band,
                reasons,
                summaries.Count(summary => summary.Selected),
                CountFileQueryTermCoverage(path, interpretation),
                sourceCliffed,
                allocated,
                spent,
                CreateFileRelevanceReason(band, sourceCliffed),
                outputStatus));
        }

        return results
            .OrderBy(summary => summary.Band)
            .ThenByDescending(summary => summary.SelectedSymbolCount)
            .ThenByDescending(summary => summary.AllocatedCharacters)
            .ThenByDescending(summary => summary.SpentCharacters)
            .ThenByDescending(summary => summary.QueryTermCoverage)
            .ThenBy(summary => summary.FilePath, PathComparer)
            .Take(MaximumFileRelevanceSummaries)
            .Select((summary, index) => summary with { Rank = index + 1 })
            .ToArray();
    }

    private static string? GetCandidateSummaryPath(CodeExploreCandidateSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.FilePath))
        {
            return summary.FilePath;
        }

        return string.IsNullOrWhiteSpace(summary.Location?.FilePath)
            ? null
            : summary.Location.FilePath;
    }

    private static CodeExploreFileRelevanceBand ResolveFileRelevanceBand(
        IReadOnlyList<CodeExploreCandidateSummary> summaries,
        CodeExploreSelectionReason reasons)
    {
        if ((reasons & (CodeExploreSelectionReason.Pinned | CodeExploreSelectionReason.FlowSpine)) != 0)
        {
            return CodeExploreFileRelevanceBand.Primary;
        }

        if (summaries.Any(summary => summary.Tier <= CodeExploreCandidateTier.DistinctiveIdentifier)
            || (reasons & CodeExploreSelectionReason.QualifiedName) != 0)
        {
            return CodeExploreFileRelevanceBand.Strong;
        }

        if (summaries.Any(summary => summary.Tier <= CodeExploreCandidateTier.GraphConnected)
            || (reasons & (CodeExploreSelectionReason.MultiTerm | CodeExploreSelectionReason.CoLocated | CodeExploreSelectionReason.GraphConnected)) != 0)
        {
            return CodeExploreFileRelevanceBand.Supporting;
        }

        return CodeExploreFileRelevanceBand.Peripheral;
    }

    private static int CountFileQueryTermCoverage(string filePath, CodeExploreQueryInterpretation interpretation)
    {
        var fileTerms = CreateCanonicalCodeExploreTermSet([filePath]);
        var queryTerms = CreateCanonicalCodeExploreTermSet(
            interpretation.Terms.Concat(interpretation.ExactIdentifiers));
        return queryTerms.Count(fileTerms.Contains);
    }

    private static CodeExploreFileOutputStatus ResolveFileOutputStatus(
        string filePath,
        CodeExploreAllocationFileSummary? allocation,
        int spentCharacters,
        IReadOnlySet<string> returnedSourcePaths,
        IReadOnlySet<string> backReferencePaths,
        IReadOnlySet<string> continuationPaths)
    {
        if (spentCharacters > 0 || returnedSourcePaths.Contains(filePath))
        {
            return CodeExploreFileOutputStatus.SourceReturned;
        }

        if (backReferencePaths.Contains(filePath))
        {
            return CodeExploreFileOutputStatus.BackReferenceOnly;
        }

        if (continuationPaths.Contains(filePath))
        {
            return CodeExploreFileOutputStatus.ContinuationOnly;
        }

        if (allocation is null)
        {
            return CodeExploreFileOutputStatus.Unknown;
        }

        return allocation.Completeness is CodeExploreSourceCompleteness.Omitted or CodeExploreSourceCompleteness.Drifted
            ? CodeExploreFileOutputStatus.OmittedByPolicyOrSafety
            : CodeExploreFileOutputStatus.Unknown;
    }

    private static string CreateFileRelevanceReason(
        CodeExploreFileRelevanceBand band,
        bool sourceCliffed)
    {
        var baseReason = band switch
        {
            CodeExploreFileRelevanceBand.Primary => "Primary source allocation from pinned or flow-spine evidence.",
            CodeExploreFileRelevanceBand.Strong => "Strong source allocation from exact, qualified, or distinctive declaration evidence.",
            CodeExploreFileRelevanceBand.Supporting => "Supporting source allocation from multi-term, co-located, or graph-connected evidence.",
            _ => "Peripheral evidence retained primarily as a focused follow-up target.",
        };
        return sourceCliffed
            ? baseReason + " Source was not emitted because relevance-aware allocation preserved budget for stronger files."
            : baseReason;
    }

    private static string FormatSourceRange(SourceRange range)
    {
        return $"L{range.StartLine}-L{range.EndLine}";
    }

    private static string BoundPresentationText(string value, int maximumCharacters)
    {
        return BoundText(value.ReplaceLineEndings(" ").Trim(), maximumCharacters);
    }

    private static int ConsumeSourceCharacters(ref int remainingSourceCharacters, int sourceCharacters)
    {
        if (sourceCharacters <= 0)
        {
            return 0;
        }

        var consumed = Math.Min(Math.Max(remainingSourceCharacters, 0), sourceCharacters);
        remainingSourceCharacters = Math.Max(0, remainingSourceCharacters - sourceCharacters);
        return consumed;
    }

    private static void AddOrMergeContinuation(
        List<CodeExploreContinuationTarget> continuations,
        CodeExploreContinuationTarget target)
    {
        if (target.Kind == CodeExploreAnchorKind.Path && target.FilePath is not null)
        {
            var existingIndex = continuations.FindIndex(existing =>
                existing.Kind == CodeExploreAnchorKind.Path
                && existing.FilePath is not null
                && PathComparer.Equals(existing.FilePath, target.FilePath));
            if (existingIndex >= 0)
            {
                continuations[existingIndex] = MergeSourceContinuation(
                    continuations[existingIndex],
                    target);
                return;
            }
        }

        continuations.Add(target);
    }

    private static CodeExploreContinuationTarget MergeSourceContinuation(
        CodeExploreContinuationTarget existing,
        CodeExploreContinuationTarget additional)
    {
        var selectionMode = existing.SelectionMode == CodeExplorePathSelectionMode.WholeFile
                || additional.SelectionMode == CodeExplorePathSelectionMode.WholeFile
                || existing.StartLine is null
                || additional.StartLine is null
            ? CodeExplorePathSelectionMode.WholeFile
            : existing.SelectionMode == CodeExplorePathSelectionMode.TailWindow
                || additional.SelectionMode == CodeExplorePathSelectionMode.TailWindow
                || (existing.SelectionMode == CodeExplorePathSelectionMode.Auto
                    && existing.StartAtLine)
                || (additional.SelectionMode == CodeExplorePathSelectionMode.Auto
                    && additional.StartAtLine)
                ? CodeExplorePathSelectionMode.TailWindow
                : CodeExplorePathSelectionMode.ExactLineRange;
        var startLine = selectionMode == CodeExplorePathSelectionMode.WholeFile
            ? null
            : MinNullable(existing.StartLine, additional.StartLine);
        var endLine = selectionMode == CodeExplorePathSelectionMode.ExactLineRange
            ? MaxNullable(
                existing.EndLine ?? existing.StartLine,
                additional.EndLine ?? additional.StartLine)
            : null;
        var reason = StringComparer.Ordinal.Equals(existing.Reason, additional.Reason)
            ? existing.Reason
            : "Multiple omitted source ranges in this file were aggregated into one continuation target.";
        var expectedFileSha256 = StringComparer.Ordinal.Equals(
            existing.ExpectedFileSha256,
            additional.ExpectedFileSha256)
                ? existing.ExpectedFileSha256
                : null;
        var workspaceGeneration = existing.WorkspaceGeneration == additional.WorkspaceGeneration
            ? existing.WorkspaceGeneration
            : null;
        var filePath = existing.FilePath
            ?? additional.FilePath
            ?? throw new InvalidOperationException("A source continuation requires a file path.");
        return new CodeExploreContinuationTarget(
            CodeExploreAnchorKind.Path,
            filePath,
            filePath,
            startLine,
            endLine,
            selectionMode == CodeExplorePathSelectionMode.TailWindow,
            selectionMode,
            expectedFileSha256,
            workspaceGeneration,
            reason);
    }

    private static bool CanUseProjectedSectionAsArtifactOrigin(ProjectedCodeExploreSection projected)
    {
        return projected.Section.Source.FileSha256 is not null
            && projected.Section.Source.Completeness is CodeExploreSourceCompleteness.Complete
                or CodeExploreSourceCompleteness.Partial;
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

    private static async Task<CodeExploreArtifactProjection> BuildAssociatedArtifactsAsync(
        AdvancedSemanticSnapshot snapshot,
        ICodeExploreArtifactReader artifactReader,
        CodeExploreRequest request,
        CodeExploreQueryInterpretation queryInterpretation,
        IReadOnlyList<CodeExploreSectionCandidate> selectedSourceCandidates,
        CancellationToken cancellationToken)
    {
        var origins = selectedSourceCandidates
            .DistinctBy(CreateSectionKey)
            .Select(candidate => CreateArtifactOrigin(snapshot, candidate))
            .ToArray();
        var inspectedDirectories = new HashSet<string>(PathComparer);
        var candidates = new List<CodeExploreArtifactCandidate>();
        var logicalCandidates = new List<CodeExploreLogicalArtifactCandidate>();
        var candidateKeys = new HashSet<CodeExploreArtifactCandidateKey>(CodeExploreArtifactCandidateKeyComparer.Instance);
        var logicalCandidateKeys = new HashSet<string>(StringComparer.Ordinal);
        var omissions = new List<string>();
        var candidateAttempts = 0;
        var policyExcludedCandidates = 0;
        var candidateLimitReached = false;
        var exactNameLookups = new CodeExploreExactNameLookupState(
            Math.Min(MaximumExactNameArtifactLiterals, request.Limits.MaximumAssociatedArtifactCandidates),
            Math.Min(MaximumExactNameArtifactLookups, request.Limits.MaximumAssociatedArtifactCandidates * 2));

        CodeExploreArtifactCandidateAdmission AddCandidate(CodeExploreArtifactCandidate candidate)
        {
            if (!TryContinueArtifactCandidateDiscovery())
            {
                return CodeExploreArtifactCandidateAdmission.LimitReached;
            }

            var key = CreateArtifactCandidateKey(candidate);
            if (!candidateKeys.Add(key))
            {
                return CodeExploreArtifactCandidateAdmission.Duplicate;
            }

            candidateAttempts++;
            var probe = artifactReader.ProbeArtifactPath(candidate.FilePath);
            if (!probe.IsSupported)
            {
                policyExcludedCandidates++;
                omissions.Add(CreateArtifactCandidateRejectionOmission(snapshot, candidate, probe.RejectionReason));
                return CodeExploreArtifactCandidateAdmission.Rejected;
            }

            candidates.Add(candidate);
            return CodeExploreArtifactCandidateAdmission.Accepted;
        }

        CodeExploreArtifactCandidateAdmission AddLogicalCandidate(CodeExploreLogicalArtifactCandidate candidate)
        {
            if (!TryContinueArtifactCandidateDiscovery())
            {
                return CodeExploreArtifactCandidateAdmission.LimitReached;
            }

            var key = CreateLogicalArtifactCandidateKey(candidate);
            if (!logicalCandidateKeys.Add(key))
            {
                return CodeExploreArtifactCandidateAdmission.Duplicate;
            }

            candidateAttempts++;
            logicalCandidates.Add(candidate);
            return CodeExploreArtifactCandidateAdmission.Accepted;
        }

        bool TryContinueArtifactCandidateDiscovery()
        {
            if (!candidateLimitReached
                && candidateAttempts < request.Limits.MaximumAssociatedArtifactCandidates)
            {
                return true;
            }

            candidateLimitReached = true;
            return false;
        }

        if (origins.Length == 0
            || request.Limits.MaximumAssociatedArtifacts == 0
            || request.Limits.MaximumAssociatedArtifactCandidates == 0)
        {
            var earlyCandidateLimitReached = origins.Length > 0
                && request.Limits.MaximumAssociatedArtifacts > 0
                && request.Limits.MaximumAssociatedArtifactCandidates == 0;
            if (earlyCandidateLimitReached)
            {
                omissions.Add("The associated artifact candidate limit was reached.");
            }

            return CreateAssociatedArtifactProjection(
                origins,
                inspectedDirectories,
                [],
                candidateAttempts,
                policyExcludedCandidates,
                earlyCandidateLimitReached,
                fileLimitReached: origins.Length > 0 && request.Limits.MaximumAssociatedArtifacts == 0,
                characterLimitReached: false,
                timeLimitReached: false,
                spentCharacters: 0,
                omissions,
                []);
        }

        AddExplicitArtifactPathCandidates(
            snapshot,
            request,
            origins[0],
            AddCandidate,
            TryContinueArtifactCandidateDiscovery,
            omissions,
            cancellationToken);
        foreach (var origin in origins)
        {
            if (candidateLimitReached)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await AddLiteralArtifactCandidatesAsync(
                snapshot,
                artifactReader,
                request,
                origin,
                inspectedDirectories,
                AddCandidate,
                AddLogicalCandidate,
                TryContinueArtifactCandidateDiscovery,
                exactNameLookups,
                omissions,
                cancellationToken);
        }

        if (ShouldIncludeProjectArtifacts(request, queryInterpretation))
        {
            foreach (var projectGroup in origins
                .Where(origin => origin.Project is not null)
                .GroupBy(origin => origin.Project?.Id)
                .OrderBy(group => group.First().ProjectName, StringComparer.Ordinal))
            {
                if (candidateLimitReached)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await AddProjectArtifactCandidatesAsync(
                    snapshot,
                    artifactReader,
                    request,
                    queryInterpretation,
                    projectGroup.First(),
                    inspectedDirectories,
                    AddCandidate,
                    TryContinueArtifactCandidateDiscovery,
                    omissions,
                    cancellationToken);
            }
        }

        if (candidateLimitReached)
        {
            omissions.Add("The associated artifact candidate limit was reached.");
        }

        var artifacts = new List<CodeExploreAssociatedArtifact>();
        var continuations = new List<CodeExploreArtifactContinuationTarget>();
        var remainingCharacters = request.Limits.MaximumAssociatedArtifactCharacters;
        var spentCharacters = 0;
        var fileLimitReached = false;
        var characterLimitReached = false;
        foreach (var workItem in candidates
            .Select(CodeExploreArtifactWorkItem.Create)
            .Concat(logicalCandidates.Select(CodeExploreArtifactWorkItem.Create))
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.SortKey, PathComparer)
            .ThenBy(item => item.Relationship)
            .ThenBy(item => item.OriginSymbolSortKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifacts.Count >= request.Limits.MaximumAssociatedArtifacts)
            {
                fileLimitReached = true;
                if (workItem.FileCandidate is { } fileCandidate)
                {
                    continuations.Add(CreateArtifactContinuationTarget(
                        snapshot,
                        fileCandidate,
                        null,
                        fileCandidate.EndLine,
                        fileCandidate.ExpectedFileSha256,
                        "Retry with this explicit associated artifact path anchor after increasing artifact file limits."));
                }
                else if (workItem.LogicalCandidate is { } logicalCandidate)
                {
                    omissions.Add($"Logical {logicalCandidate.Relationship} reference '{BoundText(logicalCandidate.LogicalName, 120)}' was omitted because the associated artifact output count limit was reached.");
                }

                continue;
            }

            if (workItem.LogicalCandidate is { } logical)
            {
                artifacts.Add(CreateLogicalAssociatedArtifact(logical));
                continue;
            }

            if (workItem.FileCandidate is not { } candidate)
            {
                continue;
            }

            if (remainingCharacters <= 0)
            {
                characterLimitReached = true;
                continuations.Add(CreateArtifactContinuationTarget(
                    snapshot,
                    candidate,
                    null,
                    candidate.EndLine,
                    candidate.ExpectedFileSha256,
                    "Retry with this explicit associated artifact path anchor after increasing artifact character limits."));
                continue;
            }

            var allowance = Math.Min(
                remainingCharacters,
                request.Limits.MaximumPerAssociatedArtifactCharacters);
            var projected = await ProjectAssociatedArtifactAsync(
                snapshot,
                artifactReader,
                request,
                candidate,
                allowance,
                cancellationToken);
            artifacts.Add(projected.Artifact);
            spentCharacters += projected.SourceCharacters;
            remainingCharacters = Math.Max(0, remainingCharacters - projected.SourceCharacters);
            characterLimitReached |= projected.CharacterLimitReached;
            continuations.AddRange(projected.ContinuationTargets);
        }

        if (fileLimitReached)
        {
            omissions.Add("Associated artifact file-output bounds were reached; use artifact continuation targets for focused follow-up.");
        }

        if (characterLimitReached)
        {
            omissions.Add("Associated artifact character-output bounds were reached; use artifact continuation targets for focused follow-up.");
        }

        return CreateAssociatedArtifactProjection(
            origins,
            inspectedDirectories,
            artifacts,
            candidateAttempts,
            policyExcludedCandidates,
            candidateLimitReached,
            fileLimitReached,
            characterLimitReached,
            timeLimitReached: false,
            spentCharacters,
            omissions,
            continuations);
    }

    private static CodeExploreArtifactProjection CreateTimedOutAssociatedArtifactProjection(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreRequest request,
        IReadOnlyList<CodeExploreSectionCandidate> selectedSourceCandidates)
    {
        var origins = selectedSourceCandidates
            .DistinctBy(CreateSectionKey)
            .Select(candidate => CreateArtifactOrigin(snapshot, candidate))
            .ToArray();
        var coverage = new CodeExploreArtifactCoverage(
            origins.Length,
            origins.Select(origin => origin.ProjectName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).Count(),
            0,
            0,
            0,
            0,
            0,
            false,
            false,
            request.Limits.MaximumAssociatedArtifacts == 0,
            false,
            true,
            ["The code exploration time limit was reached during associated artifact discovery."],
            []);
        return new([], coverage);
    }

    private static CodeExploreArtifactProjection CreateAssociatedArtifactProjection(
        IReadOnlyList<CodeExploreArtifactOrigin> origins,
        IReadOnlyCollection<string> inspectedDirectories,
        IReadOnlyList<CodeExploreAssociatedArtifact> artifacts,
        int candidateAttempts,
        int policyExcludedCandidates,
        bool candidateLimitReached,
        bool fileLimitReached,
        bool characterLimitReached,
        bool timeLimitReached,
        int spentCharacters,
        List<string> omissions,
        IReadOnlyList<CodeExploreArtifactContinuationTarget> continuations)
    {
        var incompleteReturned = artifacts.Count(IsIncompleteAssociatedArtifact);
        var omittedCount = Math.Max(0, candidateAttempts - artifacts.Count) + incompleteReturned;
        var complete = !candidateLimitReached
            && !fileLimitReached
            && !characterLimitReached
            && !timeLimitReached
            && policyExcludedCandidates == 0
            && incompleteReturned == 0
            && omissions.Count == 0;
        var coverage = new CodeExploreArtifactCoverage(
            origins.Count,
            origins.Select(origin => origin.ProjectName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).Count(),
            inspectedDirectories.Count,
            candidateAttempts,
            artifacts.Count,
            omittedCount,
            spentCharacters,
            complete,
            candidateLimitReached,
            fileLimitReached,
            characterLimitReached,
            timeLimitReached,
            omissions.Distinct(StringComparer.Ordinal).ToArray(),
            continuations
                .DistinctBy(target => $"{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.ExpectedFileSha256}:{target.WorkspaceGeneration}:{target.Reason}")
                .ToArray());
        return new(artifacts, coverage);
    }

    private static bool IsIncompleteAssociatedArtifact(CodeExploreAssociatedArtifact artifact)
    {
        if (artifact.FilePath is null && artifact.LogicalName is not null)
        {
            return artifact.Omissions.Count > 0;
        }

        return artifact.Content is null
            || artifact.Content.Completeness != CodeExploreSourceCompleteness.Complete
            || artifact.Omissions.Count > 0;
    }

    private static void AddExplicitArtifactPathCandidates(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreRequest request,
        CodeExploreArtifactOrigin origin,
        Func<CodeExploreArtifactCandidate, CodeExploreArtifactCandidateAdmission> addCandidate,
        Func<bool> tryContinueDiscovery,
        List<string> omissions,
        CancellationToken cancellationToken)
    {
        foreach (var anchor in request.AssociatedArtifactPathAnchors)
        {
            if (!tryContinueDiscovery())
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeRepositoryPath(snapshot, anchor.Path, snapshot.RepositoryPath, out var artifactPath))
            {
                omissions.Add("An explicit associated artifact path anchor was omitted because it was not a safe repository-confined path.");
                continue;
            }

            var admission = addCandidate(new CodeExploreArtifactCandidate(
                artifactPath,
                CodeExploreArtifactRelationshipKind.ExplicitPath,
                CodeExploreArtifactEvidenceLevel.ExplicitRequest,
                origin,
                ["Explicit associated artifact path supplied by the request and confined by host policy."],
                0,
                anchor.Line,
                anchor.EndLine,
                anchor.ExpectedFileSha256,
                anchor.ExpectedWorkspaceGeneration));
            if (admission == CodeExploreArtifactCandidateAdmission.LimitReached)
            {
                return;
            }
        }
    }

    private static async Task AddProjectArtifactCandidatesAsync(
        AdvancedSemanticSnapshot snapshot,
        ICodeExploreArtifactReader artifactReader,
        CodeExploreRequest request,
        CodeExploreQueryInterpretation queryInterpretation,
        CodeExploreArtifactOrigin origin,
        ISet<string> inspectedDirectories,
        Func<CodeExploreArtifactCandidate, CodeExploreArtifactCandidateAdmission> addCandidate,
        Func<bool> tryContinueDiscovery,
        List<string> omissions,
        CancellationToken cancellationToken)
    {
        var project = origin.Project;
        if (project is null)
        {
            return;
        }

        foreach (var document in project.AdditionalDocuments
            .OrderBy(document => document.FilePath ?? document.Name, PathComparer))
        {
            if (!tryContinueDiscovery())
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath is null || !Path.IsPathRooted(document.FilePath))
            {
                continue;
            }

            var admission = addCandidate(new CodeExploreArtifactCandidate(
                document.FilePath,
                CodeExploreArtifactRelationshipKind.AdditionalDocument,
                CodeExploreArtifactEvidenceLevel.ProjectProven,
                origin,
                [$"Roslyn loaded this additional document for selected project '{project.Name}'."],
                GetProjectArtifactRank(document.FilePath, queryInterpretation, 10)));
            if (admission == CodeExploreArtifactCandidateAdmission.LimitReached)
            {
                return;
            }
        }

        foreach (var document in project.AnalyzerConfigDocuments
            .OrderBy(document => document.FilePath ?? document.Name, PathComparer))
        {
            if (!tryContinueDiscovery())
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath is null || !Path.IsPathRooted(document.FilePath))
            {
                continue;
            }

            var admission = addCandidate(new CodeExploreArtifactCandidate(
                document.FilePath,
                CodeExploreArtifactRelationshipKind.AnalyzerConfiguration,
                CodeExploreArtifactEvidenceLevel.ProjectProven,
                origin,
                [$"Roslyn loaded this analyzer configuration document for selected project '{project.Name}'."],
                GetProjectArtifactRank(document.FilePath, queryInterpretation, 11)));
            if (admission == CodeExploreArtifactCandidateAdmission.LimitReached)
            {
                return;
            }
        }

        if (project.FilePath is null
            || !tryContinueDiscovery())
        {
            return;
        }

        var projectMetadataAdmission = addCandidate(new CodeExploreArtifactCandidate(
            project.FilePath,
            CodeExploreArtifactRelationshipKind.ProjectItem,
            CodeExploreArtifactEvidenceLevel.ProjectProven,
            origin,
            [$"Selected C# source belongs to loaded project '{project.Name}'; project metadata is associated but is not runtime authority."],
            GetProjectArtifactRank(project.FilePath, queryInterpretation, 30)));
        if (projectMetadataAdmission == CodeExploreArtifactCandidateAdmission.LimitReached)
        {
            return;
        }

        await AddProjectItemCandidatesAsync(
            snapshot,
            artifactReader,
            request,
            queryInterpretation,
            origin,
            inspectedDirectories,
            addCandidate,
            tryContinueDiscovery,
            omissions,
            cancellationToken);
    }

    private static async Task AddProjectItemCandidatesAsync(
        AdvancedSemanticSnapshot snapshot,
        ICodeExploreArtifactReader artifactReader,
        CodeExploreRequest request,
        CodeExploreQueryInterpretation queryInterpretation,
        CodeExploreArtifactOrigin origin,
        ISet<string> inspectedDirectories,
        Func<CodeExploreArtifactCandidate, CodeExploreArtifactCandidateAdmission> addCandidate,
        Func<bool> tryContinueDiscovery,
        List<string> omissions,
        CancellationToken cancellationToken)
    {
        if (!tryContinueDiscovery())
        {
            return;
        }

        var projectPath = origin.Project?.FilePath;
        var projectDirectory = origin.ProjectDirectory;
        if (projectPath is null || projectDirectory is null)
        {
            return;
        }

        CodeExploreArtifactText projectText;
        try
        {
            projectText = await artifactReader.ReadArtifactTextAsync(
                projectPath,
                request.Limits.MaximumAssociatedArtifactBytes,
                cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or FileNotFoundException
            or IOException
            or InvalidOperationException
            or DecoderFallbackException
            or XmlException)
        {
            omissions.Add($"Project metadata for associated artifact discovery could not be inspected: {exception.GetType().Name}.");
            return;
        }

        XDocument document;
        try
        {
            using var stringReader = new StringReader(projectText.Text);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            document = await XDocument.LoadAsync(xmlReader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException exception)
        {
            omissions.Add($"Project metadata for associated artifact discovery could not be parsed safely: {exception.GetType().Name}.");
            return;
        }

        AddUniqueDirectory(inspectedDirectories, projectDirectory);
        foreach (var element in document.Descendants()
            .Where(element => ProjectItemElementNames.Contains(element.Name.LocalName)
                || ProjectResourceElementNames.Contains(element.Name.LocalName))
            .OrderBy(element => element.Name.LocalName, StringComparer.Ordinal)
            .ThenBy(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? string.Empty, StringComparer.Ordinal))
        {
            if (!tryContinueDiscovery())
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var include = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update");
            if (!IsSafeProjectItemInclude(include)
                || !TryNormalizeRepositoryPath(snapshot, include, projectDirectory, out var artifactPath))
            {
                continue;
            }

            var relationship = ProjectResourceElementNames.Contains(element.Name.LocalName)
                ? CodeExploreArtifactRelationshipKind.ProjectResource
                : CodeExploreArtifactRelationshipKind.ProjectItem;
            var admission = addCandidate(new CodeExploreArtifactCandidate(
                artifactPath,
                relationship,
                CodeExploreArtifactEvidenceLevel.BoundedTextualInference,
                origin,
                [$"Project metadata text contains '{element.Name.LocalName}' Include/Update for this checked-in artifact; conditions, imports, removes, and item evaluation were not applied, so this association is textual and the artifact remains untrusted data."],
                GetProjectArtifactRank(
                    artifactPath,
                    queryInterpretation,
                    relationship == CodeExploreArtifactRelationshipKind.ProjectResource ? 31 : 32)));
            if (admission == CodeExploreArtifactCandidateAdmission.LimitReached)
            {
                return;
            }
        }
    }

    private static async Task AddLiteralArtifactCandidatesAsync(
        AdvancedSemanticSnapshot snapshot,
        ICodeExploreArtifactReader artifactReader,
        CodeExploreRequest request,
        CodeExploreArtifactOrigin origin,
        ISet<string> inspectedDirectories,
        Func<CodeExploreArtifactCandidate, CodeExploreArtifactCandidateAdmission> addCandidate,
        Func<CodeExploreLogicalArtifactCandidate, CodeExploreArtifactCandidateAdmission> addLogicalCandidate,
        Func<bool> tryContinueDiscovery,
        CodeExploreExactNameLookupState exactNameLookups,
        List<string> omissions,
        CancellationToken cancellationToken)
    {
        if (!tryContinueDiscovery())
        {
            return;
        }

        SourceText text;
        SyntaxNode? root;
        if (origin.Candidate.Document is null)
        {
            text = origin.Candidate.PreloadedText ?? SourceText.From(string.Empty, Encoding.UTF8);
            var syntaxTree = CSharpSyntaxTree.ParseText(text, path: origin.Candidate.FilePath, cancellationToken: cancellationToken);
            root = await syntaxTree.GetRootAsync(cancellationToken);
        }
        else
        {
            text = await origin.Candidate.Document.GetTextAsync(cancellationToken);
            root = await origin.Candidate.Document.GetSyntaxRootAsync(cancellationToken);
        }

        if (root is null)
        {
            return;
        }

        var span = origin.Candidate.Span
            ?? CreateFileOrLineSpan(
                text,
                origin.Candidate.PreferredLine,
                origin.Candidate.EndLine,
                origin.Candidate.StartAtLine,
                origin.Candidate.SelectionMode);
        if (span is null)
        {
            return;
        }

        foreach (var node in root.DescendantNodes(span.Value)
            .Where(node => node.Span.IntersectsWith(span.Value)))
        {
            if (!tryContinueDiscovery())
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetLiteralArtifactValue(node, out var literal))
            {
                continue;
            }

            var relationship = ClassifyLiteralRelationship(literal, node);
            var directPathAccepted = false;
            foreach (var artifactPath in EnumerateLiteralArtifactPaths(snapshot, origin, literal))
            {
                var rank = relationship switch
                {
                    CodeExploreArtifactRelationshipKind.PromptReference => 4,
                    CodeExploreArtifactRelationshipKind.ConfigurationReference => 5,
                    _ => 6,
                };
                var admission = addCandidate(new CodeExploreArtifactCandidate(
                    artifactPath,
                    relationship,
                    CodeExploreArtifactEvidenceLevel.SourceLiteral,
                    origin,
                    [$"Selected C# source literal references '{BoundText(literal, 120)}'; the artifact content is untrusted data and was not executed."],
                    rank));
                if (admission == CodeExploreArtifactCandidateAdmission.LimitReached)
                {
                    return;
                }

                directPathAccepted |= admission == CodeExploreArtifactCandidateAdmission.Accepted;
            }

            var safeExactFileName = IsSafeExactArtifactFileNameLiteral(literal);
            if (!directPathAccepted
                && !safeExactFileName
                && IsLogicalArtifactReference(relationship)
                && IsPlausibleLogicalArtifactName(literal))
            {
                var admission = addLogicalCandidate(new CodeExploreLogicalArtifactCandidate(
                    literal,
                    relationship,
                    CodeExploreArtifactEvidenceLevel.SourceLiteral,
                    origin,
                    [$"Selected C# source references logical {relationship} name '{BoundText(literal, 120)}'; no file content is implied or executed."],
                    relationship == CodeExploreArtifactRelationshipKind.PromptReference ? 7 : 8));
                if (admission == CodeExploreArtifactCandidateAdmission.LimitReached)
                {
                    return;
                }
            }

            if (directPathAccepted || !safeExactFileName)
            {
                continue;
            }

            if (request.Limits.MaximumAssociatedArtifactNameMatches <= 0
                || !tryContinueDiscovery())
            {
                continue;
            }

            if (!exactNameLookups.TryAdmitLiteral(literal, out var literalLimitOmission))
            {
                if (literalLimitOmission is not null)
                {
                    omissions.Add(literalLimitOmission);
                }

                continue;
            }

            foreach (var directory in GetLiteralBaseDirectories(origin).Distinct(PathComparer))
            {
                if (exactNameLookups.GetCachedResult(directory, literal) is not { } searchResult)
                {
                    if (!exactNameLookups.TryAdmitLookup(directory, literal, out var lookupLimitOmission))
                    {
                        if (lookupLimitOmission is not null)
                        {
                            omissions.Add(lookupLimitOmission);
                        }

                        break;
                    }

                    AddUniqueDirectory(inspectedDirectories, directory);
                    searchResult = await artifactReader.FindArtifactFilesByNameAsync(
                        directory,
                        literal,
                        request.Limits.MaximumAssociatedArtifactNameMatches,
                        cancellationToken);
                    exactNameLookups.CacheResult(directory, literal, searchResult);
                }

                if (searchResult.Truncated)
                {
                    var relativeDirectory = ToRepositoryRelativePath(directory, snapshot.RepositoryPath);
                    omissions.Add($"Bounded exact-name associated artifact lookup for '{BoundText(literal, 120)}' under '{relativeDirectory}' was truncated after inspecting {searchResult.InspectedEntries} entries.");
                }

                foreach (var match in searchResult.Matches.OrderBy(match => match.Path, PathComparer))
                {
                    var admission = addCandidate(new CodeExploreArtifactCandidate(
                        match.Path,
                        CodeExploreArtifactRelationshipKind.BoundedExactNameInference,
                        CodeExploreArtifactEvidenceLevel.BoundedTextualInference,
                        origin,
                        [$"Selected C# source literal named '{BoundText(literal, 120)}'; bounded exact-name lookup found this supported artifact under the selected project or source directory."],
                        9));
                    if (admission == CodeExploreArtifactCandidateAdmission.LimitReached)
                    {
                        return;
                    }
                }
            }
        }
    }

    private static async Task<ProjectedCodeExploreArtifact> ProjectAssociatedArtifactAsync(
        AdvancedSemanticSnapshot snapshot,
        ICodeExploreArtifactReader artifactReader,
        CodeExploreRequest request,
        CodeExploreArtifactCandidate candidate,
        int characterBudget,
        CancellationToken cancellationToken)
    {
        var relativePath = ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath);
        if (candidate.ExpectedWorkspaceGeneration is { } expectedGeneration && expectedGeneration != snapshot.Generation)
        {
            var driftContent = new CodeExploreArtifactContent(
                CreateLineRange(candidate.StartLine ?? 1),
                [],
                candidate.ExpectedFileSha256 ?? string.Empty,
                null,
                CodeExploreSourceCompleteness.Drifted,
                [$"The artifact continuation expected workspace generation {expectedGeneration}, but the current generation is {snapshot.Generation}; content was omitted."],
                null,
                0);
            return new(
                CreateAssociatedArtifact(candidate, relativePath, driftContent, driftContent.OmittedRanges),
                0,
                false,
                []);
        }

        CodeExploreArtifactText artifactText;
        try
        {
            artifactText = await artifactReader.ReadArtifactTextAsync(
                candidate.FilePath,
                request.Limits.MaximumAssociatedArtifactBytes,
                cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or FileNotFoundException
            or IOException
            or InvalidOperationException
            or DecoderFallbackException)
        {
            var omission = $"Associated artifact content could not be read safely: {exception.GetType().Name}.";
            return new(
                CreateAssociatedArtifact(candidate, relativePath, null, [omission]),
                0,
                false,
                []);
        }

        if (candidate.ExpectedFileSha256 is { } expectedFileSha256
            && !string.Equals(artifactText.FileSha256, expectedFileSha256, StringComparison.OrdinalIgnoreCase))
        {
            var digestDriftContent = new CodeExploreArtifactContent(
                CreateLineRange(candidate.StartLine ?? 1),
                [],
                artifactText.FileSha256,
                null,
                CodeExploreSourceCompleteness.Drifted,
                ["The artifact continuation expected a different file digest; content was omitted to avoid stale evidence."],
                null,
                0);
            return new(
                CreateAssociatedArtifact(candidate, relativePath, digestDriftContent, digestDriftContent.OmittedRanges),
                0,
                false,
                []);
        }

        var text = SourceText.From(artifactText.Text, Encoding.UTF8);
        var span = CreateArtifactSpan(text, candidate.StartLine, candidate.EndLine);
        if (span is null)
        {
            var omittedContent = new CodeExploreArtifactContent(
                CreateLineRange(candidate.StartLine ?? 1),
                [],
                artifactText.FileSha256,
                null,
                CodeExploreSourceCompleteness.Omitted,
                ["The requested artifact line range is outside the current file."],
                null,
                0);
            return new(
                CreateAssociatedArtifact(candidate, relativePath, omittedContent, omittedContent.OmittedRanges),
                0,
                false,
                []);
        }

        var projected = ProjectSourceRange(
            text,
            span.Value,
            artifactText.FileSha256,
            characterBudget,
            relativePath);
        var contentRange = projected.Range;
        var content = new CodeExploreArtifactContent(
            contentRange.Range,
            contentRange.NumberedLines,
            contentRange.FileSha256 ?? artifactText.FileSha256,
            contentRange.RangeSha256,
            contentRange.Completeness,
            contentRange.OmittedRanges,
            contentRange.ContinuationAnchor,
            projected.SourceCharacters);
        var omittedEndLine = GetEndLineForSpan(text, span.Value);
        IReadOnlyList<CodeExploreArtifactContinuationTarget> continuationTargets = content.ContinuationAnchor is null
            ? []
            : [CreateArtifactContinuationTarget(
                snapshot,
                candidate,
                projected.NextLine,
                omittedEndLine,
                artifactText.FileSha256,
                "Retry with this explicit associated artifact path anchor and digest to continue omitted artifact content.")];
        return new(
            CreateAssociatedArtifact(candidate, relativePath, content, content.OmittedRanges),
            projected.SourceCharacters,
            content.Completeness != CodeExploreSourceCompleteness.Complete,
            continuationTargets);
    }

    private static CodeExploreAssociatedArtifact CreateAssociatedArtifact(
        CodeExploreArtifactCandidate candidate,
        string relativePath,
        CodeExploreArtifactContent? content,
        IReadOnlyList<string> omissions)
    {
        var mediaKind = ClassifyReturnedArtifactMedia(candidate.FilePath);
        return new CodeExploreAssociatedArtifact(
            relativePath,
            mediaKind,
            candidate.Origin.ProjectName,
            candidate.Origin.OriginSymbolId,
            candidate.Origin.RelativeOriginFilePath,
            candidate.Origin.OriginRange,
            candidate.Relationship,
            candidate.Evidence,
            candidate.SelectionReasons,
            content,
            omissions);
    }

    private static CodeExploreAssociatedArtifact CreateLogicalAssociatedArtifact(
        CodeExploreLogicalArtifactCandidate candidate)
    {
        var mediaKind = candidate.Relationship == CodeExploreArtifactRelationshipKind.PromptReference
            ? CodeExploreArtifactMediaKind.Prompt
            : CodeExploreArtifactMediaKind.Configuration;
        return new CodeExploreAssociatedArtifact(
            null,
            mediaKind,
            candidate.Origin.ProjectName,
            candidate.Origin.OriginSymbolId,
            candidate.Origin.RelativeOriginFilePath,
            candidate.Origin.OriginRange,
            candidate.Relationship,
            candidate.Evidence,
            candidate.SelectionReasons,
            null,
            [],
            candidate.LogicalName);
    }

    private static CodeExploreArtifactContinuationTarget CreateArtifactContinuationTarget(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreArtifactCandidate candidate,
        int? nextLine,
        int? endLine,
        string? expectedFileSha256,
        string reason)
    {
        var relativePath = ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath);
        return new CodeExploreArtifactContinuationTarget(
            relativePath,
            nextLine ?? candidate.StartLine,
            endLine,
            expectedFileSha256,
            candidate.ExpectedWorkspaceGeneration ?? snapshot.Generation,
            reason)
        {
            OriginSymbolId = candidate.Origin.OriginSymbolId,
            OriginFilePath = candidate.Origin.RelativeOriginFilePath,
            OriginRange = candidate.Origin.OriginRange,
        };
    }

    private static CodeExploreArtifactOrigin CreateArtifactOrigin(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreSectionCandidate candidate)
    {
        var project = candidate.Document?.Project;
        var projectName = candidate.Location?.ProjectName ?? project?.Name ?? string.Empty;
        var originRange = candidate.Location?.Range ?? CreateLineRange(candidate.PreferredLine ?? 1);
        var originDirectory = Path.GetDirectoryName(candidate.FilePath) ?? snapshot.RepositoryPath;
        var projectDirectory = project?.FilePath is null ? null : Path.GetDirectoryName(project.FilePath);
        return new CodeExploreArtifactOrigin(
            candidate,
            project,
            projectName,
            projectDirectory,
            originDirectory,
            ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath),
            originRange,
            candidate.Identity?.Id);
    }

    private static bool TryGetLiteralArtifactValue(
        SyntaxNode node,
        out string value)
    {
        value = string.Empty;
        if (node is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression)
            && literal.Token.Value is string literalValue
            && IsSafeArtifactLiteral(literalValue))
        {
            value = literalValue;
            return true;
        }

        if (node is InterpolatedStringExpressionSyntax interpolated
            && !interpolated.Contents.OfType<InterpolationSyntax>().Any())
        {
            var builder = new StringBuilder();
            foreach (var text in interpolated.Contents.OfType<InterpolatedStringTextSyntax>())
            {
                builder.Append(text.TextToken.ValueText);
            }

            var interpolatedValue = builder.ToString();
            if (IsSafeArtifactLiteral(interpolatedValue))
            {
                value = interpolatedValue;
                return true;
            }
        }

        return false;
    }

    private static CodeExploreArtifactRelationshipKind ClassifyLiteralRelationship(
        string literal,
        SyntaxNode node)
    {
        var context = string.Join(
            ' ',
            node.AncestorsAndSelf()
                .Take(8)
                .Select(CreateArtifactLiteralContextToken)
                .Where(token => !string.IsNullOrWhiteSpace(token)));
        var fileName = Path.GetFileName(literal);
        if (ContainsAnyContextTerm(context, PromptContextTerms)
            || fileName.Contains("prompt", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("template", StringComparison.OrdinalIgnoreCase))
        {
            return CodeExploreArtifactRelationshipKind.PromptReference;
        }

        if (ContainsAnyContextTerm(context, ConfigurationContextTerms)
            || fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            || literal.Contains(':', StringComparison.Ordinal))
        {
            return CodeExploreArtifactRelationshipKind.ConfigurationReference;
        }

        return CodeExploreArtifactRelationshipKind.SourceLiteralPath;
    }

    private static string CreateArtifactLiteralContextToken(SyntaxNode node)
    {
        return node switch
        {
            InvocationExpressionSyntax invocation => invocation.Expression.ToString(),
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            AttributeSyntax attribute => attribute.Name.ToString(),
            ArgumentSyntax argument when argument.NameColon is not null => argument.NameColon.Name.Identifier.ValueText,
            _ => string.Empty,
        };
    }

    private static bool ContainsAnyContextTerm(string context, IEnumerable<string> terms)
    {
        return terms.Any(term => context.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLogicalArtifactReference(CodeExploreArtifactRelationshipKind relationship)
    {
        return relationship is CodeExploreArtifactRelationshipKind.PromptReference
            or CodeExploreArtifactRelationshipKind.ConfigurationReference;
    }

    private static bool IsPlausibleLogicalArtifactName(string literal)
    {
        return literal.Length is > 0 and <= 128
            && literal.Any(char.IsLetter)
            && literal.All(character => char.IsLetterOrDigit(character)
                || character is '.' or '-' or '_' or ':');
    }

    private static IEnumerable<string> EnumerateLiteralArtifactPaths(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreArtifactOrigin origin,
        string literal)
    {
        if (!LooksLikeArtifactPathLiteral(literal))
        {
            yield break;
        }

        foreach (var directory in GetLiteralPathBaseDirectories(snapshot, origin, literal).Distinct(PathComparer))
        {
            if (TryNormalizeRepositoryPath(snapshot, literal, directory, out var artifactPath))
            {
                yield return artifactPath;
            }
        }
    }

    private static IEnumerable<string> GetLiteralPathBaseDirectories(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreArtifactOrigin origin,
        string literal)
    {
        yield return origin.OriginDirectory;
        if (origin.ProjectDirectory is not null)
        {
            yield return origin.ProjectDirectory;
        }

        if (literal.Contains('/', StringComparison.Ordinal) || literal.Contains('\\', StringComparison.Ordinal))
        {
            yield return snapshot.RepositoryPath;
        }
    }

    private static IEnumerable<string> GetLiteralBaseDirectories(CodeExploreArtifactOrigin origin)
    {
        yield return origin.OriginDirectory;
        if (origin.ProjectDirectory is not null)
        {
            yield return origin.ProjectDirectory;
        }
    }

    private static bool TryNormalizeRepositoryPath(
        AdvancedSemanticSnapshot snapshot,
        string? path,
        string baseDirectory,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (path is null || !IsSafeArtifactPathText(path))
        {
            return false;
        }

        try
        {
            var normalizedInput = path.Replace('/', Path.DirectorySeparatorChar);
            var rooted = Path.IsPathRooted(normalizedInput)
                ? Path.GetFullPath(normalizedInput)
                : Path.GetFullPath(normalizedInput, baseDirectory);
            fullPath = NormalizeScope(rooted, snapshot.RepositoryPath) ?? string.Empty;
            return fullPath.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool LooksLikeArtifactPathLiteral(string literal)
    {
        if (!IsSafeArtifactPathText(literal)
            || literal.Contains('$', StringComparison.Ordinal)
            || literal.Contains('*', StringComparison.Ordinal)
            || literal.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        return literal.Contains('/', StringComparison.Ordinal)
            || literal.Contains('\\', StringComparison.Ordinal)
            || IsSafeExactArtifactFileNameLiteral(literal);
    }

    private static bool IsSafeArtifactPathText(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.Length <= MaximumArtifactLiteralLength
            && !path.Any(char.IsControl)
            && !path.Contains("..", StringComparison.Ordinal)
            && !path.Contains("<", StringComparison.Ordinal)
            && !path.Contains(">", StringComparison.Ordinal)
            && !path.Contains("|", StringComparison.Ordinal)
            && !path.Contains('"', StringComparison.Ordinal)
            && !Uri.TryCreate(path, UriKind.Absolute, out _);
    }

    private static bool IsSafeArtifactLiteral(string literal)
    {
        return !string.IsNullOrWhiteSpace(literal)
            && literal.Length <= MaximumArtifactLiteralLength
            && !literal.Any(char.IsControl);
    }

    private static bool IsSafeExactArtifactFileNameLiteral(string literal)
    {
        return IsSafeArtifactLiteral(literal)
            && !literal.Contains('/', StringComparison.Ordinal)
            && !literal.Contains('\\', StringComparison.Ordinal)
            && !literal.Contains(':', StringComparison.Ordinal)
            && literal.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && Path.GetExtension(literal).Length > 0
            && !literal.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeProjectItemInclude(string? include)
    {
        return include is not null
            && IsSafeArtifactPathText(include)
            && !include.Contains('$', StringComparison.Ordinal)
            && !include.Contains('*', StringComparison.Ordinal)
            && !include.Contains('?', StringComparison.Ordinal);
    }

    private static bool ShouldIncludeProjectArtifacts(
        CodeExploreRequest request,
        CodeExploreQueryInterpretation queryInterpretation)
    {
        return request.AssociatedArtifacts == CodeExploreAssociatedArtifactsMode.Enabled
            || QueryContainsExplicitArtifactFileName(request.Query, queryInterpretation)
            || queryInterpretation.PathLikeSpans.Any(IsExplicitArtifactPathSpan)
            || QueryContainsStandaloneArtifactFocus(request.Query)
            || HasQualifiedArtifactFocus(queryInterpretation);
    }

    private static bool QueryContainsStandaloneArtifactFocus(string query)
    {
        return ExtractCodeExploreTokens(query).Any(token =>
            !token.Contains('.', StringComparison.Ordinal)
            && !token.Contains('/', StringComparison.Ordinal)
            && !token.Contains('\\', StringComparison.Ordinal)
            && ArtifactFocusTerms.Contains(token.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)));
    }

    private static bool QueryContainsExplicitArtifactFileName(
        string query,
        CodeExploreQueryInterpretation queryInterpretation)
    {
        var hasArtifactQualifier = queryInterpretation.Terms
            .Concat(queryInterpretation.ExactIdentifiers)
            .Any(ArtifactFocusQualifierTerms.Contains);
        return ExtractCodeExploreTokens(query)
            .Any(token => IsExplicitArtifactFileNameToken(token, hasArtifactQualifier));
    }

    private static bool IsExplicitArtifactFileNameToken(string token, bool hasArtifactQualifier)
    {
        var fileName = Path.GetFileName(token);
        if (fileName.Equals("editorconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("globalconfig", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(fileName).TrimStart('.');
        if (!fileName.Contains('.', StringComparison.Ordinal)
            || !ArtifactFocusExtensions.Contains(extension))
        {
            return false;
        }

        var nameSegments = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var underscoreIsFileShape = token.Contains('_', StringComparison.Ordinal)
            && (nameSegments.Length <= 2 || !IsQualifiedIdentifierName(fileName));
        var hasFileShape = token.Contains('/', StringComparison.Ordinal)
            || token.Contains('\\', StringComparison.Ordinal)
            || token.Contains('-', StringComparison.Ordinal)
            || underscoreIsFileShape;
        return hasFileShape
            || hasArtifactQualifier
            || IsConventionalArtifactFileName(fileName, extension)
            || (nameSegments.Length == 2
                && !AmbiguousArtifactFocusExtensions.Contains(extension));
    }

    private static bool IsConventionalArtifactFileName(string fileName, string extension)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (extension.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return stem.Equals(AppSettingsArtifactStem, StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith($"{AppSettingsArtifactStem}.", StringComparison.OrdinalIgnoreCase)
                || ConventionalJsonArtifactStems.Contains(stem);
        }

        return extension.Equals("config", StringComparison.OrdinalIgnoreCase)
            && ConventionalConfigArtifactStems.Contains(stem);
    }

    private static bool IsExplicitArtifactPathSpan(string pathSpan)
    {
        var fileName = Path.GetFileName(pathSpan);
        var extension = Path.GetExtension(fileName).TrimStart('.');
        return ArtifactFocusExtensions.Contains(extension)
            || ArtifactFocusExtensions.Contains(fileName.TrimStart('.'));
    }

    private static bool HasQualifiedArtifactFocus(CodeExploreQueryInterpretation queryInterpretation)
    {
        var terms = queryInterpretation.Terms
            .Concat(queryInterpretation.ExactIdentifiers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!terms.Any(ArtifactFocusQualifierTerms.Contains))
        {
            return false;
        }

        return terms.Any(term => ArtifactFocusContextTerms.Contains(term)
            || ArtifactFocusExtensions.Contains(term.TrimStart('.')));
    }

    private static int GetProjectArtifactRank(
        string path,
        CodeExploreQueryInterpretation queryInterpretation,
        int defaultRank)
    {
        return QueryExplicitlyNamesArtifact(path, queryInterpretation) ? 1 : defaultRank;
    }

    private static bool QueryExplicitlyNamesArtifact(
        string path,
        CodeExploreQueryInterpretation queryInterpretation)
    {
        var fileName = Path.GetFileName(path);
        if (queryInterpretation.PathLikeSpans.Any(span =>
            string.Equals(Path.GetFileName(span), fileName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var queryTerms = queryInterpretation.Terms
            .Concat(queryInterpretation.ExactIdentifiers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileNameTerms = fileName
            .TrimStart('.')
            .Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return fileNameTerms.Length > 0 && fileNameTerms.All(queryTerms.Contains);
    }

    private static void AddUniqueDirectory(ISet<string> directories, string directory)
    {
        if (Path.IsPathRooted(directory))
        {
            directories.Add(Path.GetFullPath(directory));
        }
    }

    private static int GetEndLineForSpan(SourceText text, TextSpan span)
    {
        var safeStart = Math.Min(span.Start, text.Length);
        var safeEnd = Math.Min(span.End, text.Length);
        var endPosition = Math.Max(safeEnd - 1, safeStart);
        return text.Lines.GetLineFromPosition(Math.Min(endPosition, text.Length)).LineNumber + 1;
    }

    private static TextSpan? CreateArtifactSpan(SourceText text, int? startLine, int? endLine)
    {
        if (startLine is null)
        {
            return new TextSpan(0, text.Length);
        }

        if (startLine.Value <= 0 || startLine.Value > text.Lines.Count)
        {
            return null;
        }

        var line = text.Lines[startLine.Value - 1];
        if (endLine is null)
        {
            return line.Span;
        }

        if (endLine.Value < startLine.Value || endLine.Value > text.Lines.Count)
        {
            return null;
        }

        return TextSpan.FromBounds(line.Start, text.Lines[endLine.Value - 1].EndIncludingLineBreak);
    }

    private static CodeExploreArtifactMediaKind ClassifyReturnedArtifactMedia(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase))
        {
            return fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                ? CodeExploreArtifactMediaKind.Configuration
                : fileName.Contains("schema", StringComparison.OrdinalIgnoreCase)
                    ? CodeExploreArtifactMediaKind.Schema
                    : CodeExploreArtifactMediaKind.Json;
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
        {
            return CodeExploreArtifactMediaKind.ProjectMetadata;
        }

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".resx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ruleset", StringComparison.OrdinalIgnoreCase))
        {
            return CodeExploreArtifactMediaKind.Xml;
        }

        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return fileName.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("template", StringComparison.OrdinalIgnoreCase)
                    ? CodeExploreArtifactMediaKind.Prompt
                    : CodeExploreArtifactMediaKind.Markdown;
        }

        if (extension.Equals(".prompt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".prompty", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tmpl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".template", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".liquid", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".scriban", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mustache", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".handlebars", StringComparison.OrdinalIgnoreCase))
        {
            return CodeExploreArtifactMediaKind.Prompt;
        }

        if (fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".globalconfig", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".config", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return CodeExploreArtifactMediaKind.Configuration;
        }

        if (extension.Equals(".schema", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".graphql", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gql", StringComparison.OrdinalIgnoreCase))
        {
            return CodeExploreArtifactMediaKind.Schema;
        }

        return CodeExploreArtifactMediaKind.Text;
    }

    private static CodeExploreArtifactCandidateKey CreateArtifactCandidateKey(CodeExploreArtifactCandidate candidate)
    {
        return new CodeExploreArtifactCandidateKey(
            Path.GetFullPath(candidate.FilePath),
            candidate.Relationship,
            candidate.StartLine,
            candidate.EndLine,
            candidate.ExpectedFileSha256,
            candidate.ExpectedWorkspaceGeneration);
    }

    private static string CreateLogicalArtifactCandidateKey(CodeExploreLogicalArtifactCandidate candidate)
    {
        return string.Join(
            '|',
            "logical",
            candidate.Relationship,
            candidate.LogicalName,
            candidate.Origin.RelativeOriginFilePath,
            candidate.Origin.OriginRange.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidate.Origin.OriginRange.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string CreateArtifactCandidateRejectionOmission(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreArtifactCandidate candidate,
        string? rejectionReason)
    {
        var reason = rejectionReason ?? "path failed artifact policy.";
        var pathDisplay = TryCreateRejectedArtifactPathDisplay(snapshot, candidate.FilePath, reason);
        var candidateIdentity = pathDisplay is null
            ? $"Associated artifact candidate ({candidate.Relationship})"
            : $"Associated artifact candidate '{BoundText(pathDisplay, 160)}' ({candidate.Relationship})";
        return $"{candidateIdentity} was omitted: {reason}";
    }

    private static string? TryCreateRejectedArtifactPathDisplay(
        AdvancedSemanticSnapshot snapshot,
        string candidatePath,
        string rejectionReason)
    {
        if (rejectionReason.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || rejectionReason.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || rejectionReason.Contains("sensitivity", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.RepositoryPath));
            var fullPath = Path.GetFullPath(Path.IsPathRooted(candidatePath)
                ? candidatePath
                : Path.Combine(root, candidatePath));
            if (!fullPath.Equals(root, PathComparison)
                && !fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            {
                return null;
            }

            return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    private sealed record CodeExploreArtifactProjection(
        IReadOnlyList<CodeExploreAssociatedArtifact> Artifacts,
        CodeExploreArtifactCoverage Coverage);

    private sealed class CodeExploreExactNameLookupState
    {
        private readonly int _maximumLiterals;
        private readonly int _maximumLookups;
        private readonly HashSet<string> _literals = new(PathComparer);
        private readonly HashSet<string> _lookups = new(PathComparer);
        private readonly Dictionary<string, CodeExploreArtifactFileSearchResult> _results = new(PathComparer);
        private bool _literalLimitReported;
        private bool _lookupLimitReported;

        internal CodeExploreExactNameLookupState(int maximumLiterals, int maximumLookups)
        {
            _maximumLiterals = Math.Max(0, maximumLiterals);
            _maximumLookups = Math.Max(0, maximumLookups);
        }

        internal bool TryAdmitLiteral(string fileName, out string? omission)
        {
            omission = null;
            if (_literals.Contains(fileName))
            {
                return true;
            }

            if (_literals.Count >= _maximumLiterals)
            {
                if (!_literalLimitReported)
                {
                    _literalLimitReported = true;
                    omission = $"Bounded exact-name associated artifact lookup skipped additional file-name literals after reaching the {_maximumLiterals}-literal query bound.";
                }

                return false;
            }

            _literals.Add(fileName);
            return true;
        }

        internal bool TryAdmitLookup(
            string directory,
            string fileName,
            out string? omission)
        {
            omission = null;
            var key = CreateLookupKey(directory, fileName);
            if (_lookups.Contains(key))
            {
                return true;
            }

            if (_lookups.Count >= _maximumLookups)
            {
                if (!_lookupLimitReported)
                {
                    _lookupLimitReported = true;
                    omission = $"Bounded exact-name associated artifact lookup skipped additional directory scans after reaching the {_maximumLookups}-lookup query bound.";
                }

                return false;
            }

            _lookups.Add(key);
            return true;
        }

        internal CodeExploreArtifactFileSearchResult? GetCachedResult(
            string directory,
            string fileName)
        {
            return _results.GetValueOrDefault(CreateLookupKey(directory, fileName));
        }

        internal void CacheResult(
            string directory,
            string fileName,
            CodeExploreArtifactFileSearchResult result)
        {
            _results[CreateLookupKey(directory, fileName)] = result;
        }

        private static string CreateLookupKey(string directory, string fileName)
        {
            return string.Join('|', Path.GetFullPath(directory), fileName);
        }
    }

    private sealed record ProjectedCodeExploreArtifact(
        CodeExploreAssociatedArtifact Artifact,
        int SourceCharacters,
        bool CharacterLimitReached,
        IReadOnlyList<CodeExploreArtifactContinuationTarget> ContinuationTargets);

    private sealed record CodeExploreArtifactOrigin(
        CodeExploreSectionCandidate Candidate,
        Project? Project,
        string ProjectName,
        string? ProjectDirectory,
        string OriginDirectory,
        string RelativeOriginFilePath,
        SourceRange OriginRange,
        string? OriginSymbolId);

    private sealed record CodeExploreArtifactCandidate(
        string FilePath,
        CodeExploreArtifactRelationshipKind Relationship,
        CodeExploreArtifactEvidenceLevel Evidence,
        CodeExploreArtifactOrigin Origin,
        IReadOnlyList<string> SelectionReasons,
        int Rank,
        int? StartLine = null,
        int? EndLine = null,
        string? ExpectedFileSha256 = null,
        long? ExpectedWorkspaceGeneration = null);

    private readonly record struct CodeExploreArtifactCandidateKey(
        string FilePath,
        CodeExploreArtifactRelationshipKind Relationship,
        int? StartLine,
        int? EndLine,
        string? ExpectedFileSha256,
        long? ExpectedWorkspaceGeneration);

    private sealed class CodeExploreArtifactCandidateKeyComparer : IEqualityComparer<CodeExploreArtifactCandidateKey>
    {
        internal static CodeExploreArtifactCandidateKeyComparer Instance { get; } = new();

        public bool Equals(CodeExploreArtifactCandidateKey left, CodeExploreArtifactCandidateKey right)
        {
            return PathComparer.Equals(left.FilePath, right.FilePath)
                && left.Relationship == right.Relationship
                && left.StartLine == right.StartLine
                && left.EndLine == right.EndLine
                && StringComparer.OrdinalIgnoreCase.Equals(left.ExpectedFileSha256, right.ExpectedFileSha256)
                && left.ExpectedWorkspaceGeneration == right.ExpectedWorkspaceGeneration;
        }

        public int GetHashCode(CodeExploreArtifactCandidateKey candidate)
        {
            var hash = default(HashCode);
            hash.Add(candidate.FilePath, PathComparer);
            hash.Add(candidate.Relationship);
            hash.Add(candidate.StartLine);
            hash.Add(candidate.EndLine);
            hash.Add(candidate.ExpectedFileSha256, StringComparer.OrdinalIgnoreCase);
            hash.Add(candidate.ExpectedWorkspaceGeneration);
            return hash.ToHashCode();
        }
    }

    private sealed record CodeExploreLogicalArtifactCandidate(
        string LogicalName,
        CodeExploreArtifactRelationshipKind Relationship,
        CodeExploreArtifactEvidenceLevel Evidence,
        CodeExploreArtifactOrigin Origin,
        IReadOnlyList<string> SelectionReasons,
        int Rank);

    private sealed record CodeExploreArtifactWorkItem(
        CodeExploreArtifactCandidate? FileCandidate,
        CodeExploreLogicalArtifactCandidate? LogicalCandidate,
        int Rank,
        string SortKey,
        CodeExploreArtifactRelationshipKind Relationship,
        string OriginSymbolSortKey)
    {
        internal static CodeExploreArtifactWorkItem Create(CodeExploreArtifactCandidate candidate)
        {
            return new CodeExploreArtifactWorkItem(
                candidate,
                null,
                candidate.Rank,
                candidate.FilePath,
                candidate.Relationship,
                candidate.Origin.OriginSymbolId ?? string.Empty);
        }

        internal static CodeExploreArtifactWorkItem Create(CodeExploreLogicalArtifactCandidate candidate)
        {
            return new CodeExploreArtifactWorkItem(
                null,
                candidate,
                candidate.Rank,
                candidate.LogicalName,
                candidate.Relationship,
                candidate.Origin.OriginSymbolId ?? string.Empty);
        }
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
        string? PreloadedFileSha256,
        CodeExploreSourceImportance Importance = CodeExploreSourceImportance.Supporting,
        bool IsFlowSpine = false)
    {
        /// <summary>Gets additional declaration identities represented by a clustered source span.</summary>
        public IReadOnlyList<SemanticSymbolIdentity> AdditionalIdentities { get; init; } = [];
    }

    private enum CodeExploreSourceImportance
    {
        Supporting,
        FlowSpine,
        Named,
        Pinned,
    }

    /// <summary>Owns synchronized cancellation for one shared catalog or graph build.</summary>
    private interface ISharedCodeExploreBuild
    {
        /// <summary>Cancels unfinished work without racing cancellation-source disposal.</summary>
        void Cancel();
    }

    private sealed class SharedCodeExploreBuild<T>(
        Guid workspaceId,
        long generation,
        CancellationTokenSource cancellation,
        Task<T> task) : ISharedCodeExploreBuild
    {
        private readonly Lock _cancellationGate = new();
        private bool _cancelInProgress;
        private bool _cancellationDisposed;
        private bool _disposeCancellationRequested;

        public Guid WorkspaceId { get; } = workspaceId;

        public long Generation { get; } = generation;

        public Task<T> Task { get; } = task;

        public int WaiterCount { get; set; }

        public void Cancel()
        {
            lock (_cancellationGate)
            {
                if (_cancellationDisposed || _cancelInProgress)
                {
                    return;
                }

                _cancelInProgress = true;
                try
                {
                    cancellation.Cancel();
                }
                finally
                {
                    _cancelInProgress = false;
                    if (_disposeCancellationRequested)
                    {
                        _cancellationDisposed = true;
                        cancellation.Dispose();
                    }
                }
            }
        }

        public void DisposeCancellation()
        {
            lock (_cancellationGate)
            {
                if (_cancellationDisposed)
                {
                    return;
                }

                if (_cancelInProgress)
                {
                    _disposeCancellationRequested = true;
                    return;
                }

                _cancellationDisposed = true;
                cancellation.Dispose();
            }
        }
    }

    private sealed record CodeExploreToolCapabilityDescriptor(
        string ToolTypeName,
        string Family,
        IReadOnlySet<string> RelatedContractTypeNames);

    private sealed record CodeExploreRankedCandidate(
        CodeExploreDeclarationCatalogEntry Entry,
        CodeExploreCandidateTier Tier,
        CodeExploreSelectionReason Reasons,
        int Score,
        int CoveredTermCount,
        string AmbiguityGroup,
        double GraphMass = 0,
        bool IsGraphSeed = false,
        bool IsFocusedNameMatch = false);

    private sealed record CodeExploreTermCoverage(
        string QueryTerm,
        string CanonicalTerm,
        double Strength);

    private sealed class DescendingGraphMassComparer(double maximumGraphMass) : IComparer<double>
    {
        private readonly double _tieThreshold = Math.Max(0, maximumGraphMass)
            * CodeExploreRelevancePolicy.GraphMassTieRatio;

        public int Compare(double left, double right)
        {
            return Math.Abs(left - right) <= _tieThreshold
                ? 0
                : right.CompareTo(left);
        }
    }

    private sealed record NaturalLanguageIntentAdjustment(
        int Score,
        CodeExploreSelectionReason Reasons,
        CodeExploreCandidateTier Tier);

    private sealed record CodeExploreScaleProject(
        Project Project,
        bool ProjectFileAllowed,
        int DocumentCount,
        int GeneratedDocumentCount);

    private sealed record NaturalLanguageCodeExploreDiscovery(
        IReadOnlyList<CodeExploreAnchor> Anchors,
        CodeExploreQueryInterpretation Interpretation,
        CodeExploreNaturalLanguageIntent Intent,
        CodeExploreDiscoverySummary Discovery,
        CodeExploreCandidateSummary[] Candidates,
        IReadOnlyList<CodeExploreRankedCandidate> SourceCompanions,
        IReadOnlyDictionary<string, CodeExploreSelectedRelevance> SelectedRelevance,
        IReadOnlyList<string> UnresolvedTerms,
        IReadOnlyList<string> Omissions);

    private sealed record CodeExploreSelectedRelevance(
        int Rank,
        int Score,
        double GraphMass,
        CodeExploreSelectionReason Reasons);

    private sealed record LocatedSemanticDocument(Document Document, bool IsSourceGenerated);

    private sealed record ProjectedCodeExploreSection(
        CodeExploreFileSection Section,
        int SourceCharacters,
        IReadOnlyList<CodeExploreContinuationTarget> ContinuationTargets);

    private enum CodeExploreArtifactCandidateAdmission
    {
        Accepted,
        Rejected,
        Duplicate,
        LimitReached,
    }

    private enum CodeExploreNaturalLanguageIntent
    {
        Exact,
        Impact,
        Flow,
        ToolCapabilityExplanation,
        Survey,
    }

    private sealed record CodeExplorePriorCoverage(
        CodeExploreBackReference? BackReference,
        int SourceCharacters,
        string? DisqualificationReason);

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
        IReadOnlyList<CodeExploreFlowAnchor> anchors,
        CodeExploreNaturalLanguageIntent? naturalLanguageIntent)
    {
        if (anchors.Count < 2)
        {
            return false;
        }

        return request.Mode is CodeExploreMode.Survey or CodeExploreMode.Flow or CodeExploreMode.Impact
            || (request.Mode == CodeExploreMode.Auto
                && naturalLanguageIntent is not CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
                    and not CodeExploreNaturalLanguageIntent.Survey);
    }

    private static bool ShouldBuildCodeExploreBlastRadius(
        CodeExploreRequest request,
        IReadOnlyList<CodeExploreFlowAnchor> anchors,
        CodeExploreNaturalLanguageIntent? naturalLanguageIntent)
    {
        return anchors.Count > 0
            && (request.Mode is CodeExploreMode.Survey or CodeExploreMode.Flow or CodeExploreMode.Impact
                || (request.Mode == CodeExploreMode.Auto
                    && anchors.Count >= 2
                    && naturalLanguageIntent is not CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
                        and not CodeExploreNaturalLanguageIntent.Survey));
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
            CodeExploreSourceImportance.FlowSpine,
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
            null,
            CodeExploreSourceImportance.FlowSpine,
            true));
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

        if (!Enum.IsDefined(request.AssociatedArtifacts))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Associated artifact mode is not supported.");
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
            || limits.MaximumAssociatedArtifacts is < 0 or > 16
            || limits.MaximumAssociatedArtifactCandidates is < 0 or > 128
            || limits.MaximumAssociatedArtifactCharacters is < 0 or > 100_000
            || limits.MaximumPerAssociatedArtifactCharacters is < 0 or > 65_536
            || limits.MaximumAssociatedArtifactBytes is < 1 or > 1024 * 1024
            || limits.MaximumAssociatedArtifactNameMatches is < 0 or > 64
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

        if (request.AssociatedArtifactPathAnchors.Count > request.Limits.MaximumAssociatedArtifactCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Associated artifact path anchor count must not exceed maximumAssociatedArtifactCandidates.");
        }

        foreach (var anchor in request.AssociatedArtifactPathAnchors)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Path);
            if (anchor.Path.Length > 4096
                || anchor.Line is <= 0
                || anchor.EndLine is <= 0
                || anchor.EndLine < anchor.Line
                || (anchor.EndLine is not null && anchor.Line is null))
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Associated artifact path anchors must use bounded positive one-based line ranges.");
            }

            var invalidExpectedDigest = anchor.ExpectedFileSha256 is { } expectedFileSha256
                && !IsSha256Hex(expectedFileSha256);
            if (anchor.ExpectedWorkspaceGeneration is < 0 || invalidExpectedDigest)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Associated artifact continuation cursor fields are outside host limits.");
            }
        }
    }

    private static IReadOnlyList<CodeExploreAnchor> BuildCodeExploreAnchors(
        CodeExploreRequest request,
        CodeExploreQueryInterpretation queryInterpretation)
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

        if (IsCSharpPathSpan(request.Query)
            || (IsExactSymbolAnchor(request.Query)
                && !MentionsKnownSemanticToolId(queryInterpretation)))
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
        if (allowedEntries.Length != catalog.Entries.Count)
        {
            allowedEntries = [.. LinkCodeExploreToolCapabilities(allowedEntries, cancellationToken)];
        }

        var candidateIndex = allowedEntries.Length == catalog.Entries.Count
            ? catalog.Index
            : CreateCodeExploreCandidateIndex(allowedEntries, cancellationToken);
        var intentInterpretation = RemoveRepositoryNameContextTerm(snapshot, interpretation);
        var intent = ClassifyNaturalLanguageIntent(request, intentInterpretation);
        var rankingInterpretation = ApplyNaturalLanguageRetrievalVocabularyPolicy(intentInterpretation);
        var relationshipIntent = intent switch
        {
            CodeExploreNaturalLanguageIntent.Flow => CodeExploreRelationshipIntent.Flow,
            CodeExploreNaturalLanguageIntent.Impact => CodeExploreRelationshipIntent.Impact,
            _ => (CodeExploreRelationshipIntent?)null,
        };
        var relationshipAnalysis = CodeExploreQueryIntentPolicy.Analyze(request.Query, relationshipIntent);
        var nameSegmentEvidence = NaturalLanguageNameSegmentMatcher.Create(
            candidateIndex.NameSegments,
            allowedEntries
                .Select(entry => entry.Identity.Id)
                .ToHashSet(StringComparer.Ordinal),
            request.Query,
            relationshipAnalysis.ConsumedTerms,
            cancellationToken);
        if (ShouldUseProjectScopeFallback(snapshot, allowedEntries, rankingInterpretation, nameSegmentEvidence, intent))
        {
            var scopeOmission = $"The requested subject was not found in the loaded project's source scope ({Path.GetFileName(snapshot.WorkspacePath)}).";
            return new NaturalLanguageCodeExploreDiscovery(
                [],
                rankingInterpretation with { UnresolvedTerms = rankingInterpretation.Terms },
                intent,
                new CodeExploreDiscoverySummary(
                    allowedEntries.Length,
                    0,
                    0,
                    catalog.IsComplete,
                    !catalog.IsComplete,
                    [],
                    "loaded project source scope"),
                [],
                [],
                new Dictionary<string, CodeExploreSelectedRelevance>(StringComparer.Ordinal),
                rankingInterpretation.Terms,
                [.. catalog.Omissions, scopeOmission]);
        }

        var retrievalToolFamilies = GetNaturalLanguageRetrievalToolFamilies(
                intentInterpretation,
                intent)
            .Concat(GetMentionedCatalogToolFamilies(
                allowedEntries,
                rankingInterpretation,
                request.Query))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var retrievalEntries = GetIndexedCodeExploreCandidates(
            candidateIndex,
            allowedEntries,
            rankingInterpretation,
            nameSegmentEvidence,
            retrievalToolFamilies,
            cancellationToken);
        var ranked = RankNaturalLanguageCandidates(
            retrievalEntries,
            candidateIndex,
            rankingInterpretation,
            nameSegmentEvidence,
            retrievalToolFamilies,
            intent,
            cancellationToken);
        ranked = ApplyNaturalLanguageConceptReranking(ranked, rankingInterpretation);
        ranked = await ApplyGraphConnectivityAsync(
            workspaceId,
            snapshot,
            ranked,
            allowedEntries,
            rankingInterpretation,
            intent,
            cancellationToken);
        if (!HasTestFocus(rankingInterpretation)
            && ranked.Count(candidate => !candidate.Entry.IsTest)
                >= CodeExploreRelevancePolicy.MinimumProductionCandidatesForTestExclusion)
        {
            ranked =
            [
                .. ranked.Where(candidate => !candidate.Entry.IsTest
                    || HasExactCandidateReason(candidate.Reasons)),
            ];
        }

        ranked = ApplyNaturalLanguageFileRelevanceGate(
            ranked,
            rankingInterpretation,
            retrievalToolFamilies);
        ranked = ApplyNaturalLanguageRelativeFloor(ranked, retrievalToolFamilies);
        var rankedByIdentity = ranked
            .GroupBy(candidate => candidate.Entry.Identity.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var anchors = new List<CodeExploreAnchor>();
        var summaries = new List<CodeExploreCandidateSummary>();
        var selectedIdentityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbolId in rankingInterpretation.StableSymbolIds.Take(request.Limits.MaximumAnchors))
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

        foreach (var path in rankingInterpretation.PathLikeSpans
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

        var symbolSlots = ResolveNaturalLanguageAnchorLimit(
            Math.Max(0, request.Limits.MaximumAnchors - anchors.Count),
            request.Limits.MaximumFiles,
            rankedByIdentity,
            intent);
        var omissions = new List<string>(catalog.Omissions);
        var selected = SelectNaturalLanguageCandidates(
            rankedByIdentity,
            selectedIdentityIds,
            symbolSlots,
            request.Limits.MaximumFiles,
            intentInterpretation,
            intent,
            retrievalToolFamilies,
            omissions);
        var sourceCompanions = SelectNaturalLanguageSourceCompanions(
            rankedByIdentity,
            selected,
            request.Limits);
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
            pinnedSummaryCount,
            selected.Length);
        var rankedCandidatesWithPositions = rankedByIdentity
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Rank = pinnedSummaryCount + index + 1,
                IsSelected = selectedIds.Contains(candidate.Entry.Identity.Id),
            })
            .ToArray();
        summaries.AddRange(rankedCandidatesWithPositions
            .Where(item => item.IsSelected)
            .Concat(rankedCandidatesWithPositions.Where(item => !item.IsSelected))
            .Take(maximumCandidateSummaries)
            .Select(item => CreateCandidateSummary(
                item.Candidate,
                item.Rank,
                item.IsSelected,
                intent)));
        var ambiguityGroups = CreateAmbiguityGroups(rankedByIdentity, selectedIds);
        var unresolvedTerms = rankingInterpretation.Terms
            .Where(term => !ranked.Any(candidate => CandidateCoversTerm(candidate.Entry, term)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateLimitReached = rankedByIdentity.Length > maximumCandidateSummaries;
        if (candidateLimitReached)
        {
            omissions.Add("Natural-language candidate summaries were capped by the host result limit.");
        }

        if (intent is CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation or CodeExploreNaturalLanguageIntent.Survey
            && rankedByIdentity.Any(candidate => candidate.Reasons.HasFlag(CodeExploreSelectionReason.GraphConnected)))
        {
            omissions.Add("Natural-language graph connectivity was treated as low-weight corroboration for survey/tool intent; direct query-term and tool-contract evidence kept priority.");
        }

        if (intent == CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
            && allowedEntries.Any(IsPrivateImplementationHelper))
        {
            omissions.Add("Private/internal helper candidates were down-ranked for tool/capability explanation intent unless directly identified by the query.");
        }

        if (ranked.Length == 0 && summaries.Count == 0)
        {
            omissions.Add("No compiler-known declarations matched the natural-language query terms.");
        }

        return new NaturalLanguageCodeExploreDiscovery(
            anchors,
            rankingInterpretation with { UnresolvedTerms = unresolvedTerms },
            intent,
            new CodeExploreDiscoverySummary(
                allowedEntries.Length,
                rankedByIdentity.Length + pinnedSummaryCount,
                anchors.Count,
                catalog.IsComplete,
                candidateLimitReached || !catalog.IsComplete,
                ambiguityGroups,
                "request.maximumAnchors, request.maximumFiles, and request.maximumSourceCharacters"),
            [.. summaries],
            sourceCompanions,
            selected
                .Concat(sourceCompanions)
                .Select((candidate, index) => new
                {
                    candidate.Entry.Identity.Id,
                    Relevance = new CodeExploreSelectedRelevance(
                        index + 1,
                        candidate.Score,
                        candidate.GraphMass,
                        candidate.Reasons),
                })
                .ToDictionary(item => item.Id, item => item.Relevance, StringComparer.Ordinal),
            unresolvedTerms,
            omissions);
    }

    private static int ResolveNaturalLanguageAnchorLimit(
        int availableSlots,
        int maximumFiles,
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        CodeExploreNaturalLanguageIntent intent)
    {
        if (intent == CodeExploreNaturalLanguageIntent.Exact)
        {
            return availableSlots;
        }

        var exactCandidateCount = ranked.Count(candidate => HasExactCandidateReason(candidate.Reasons));
        var configuredLimit = Math.Min(
            availableSlots,
            Math.Max(Math.Min(maximumFiles, ranked.Count), exactCandidateCount));
        if (intent == CodeExploreNaturalLanguageIntent.Flow)
        {
            return Math.Min(
                configuredLimit,
                Math.Max(
                    Math.Min(CodeExploreRelevancePolicy.MaximumGraphEntryPoints, ranked.Count),
                    exactCandidateCount));
        }

        if (intent != CodeExploreNaturalLanguageIntent.Impact)
        {
            return configuredLimit;
        }

        return Math.Min(
            configuredLimit,
            Math.Max(CodeExploreRelevancePolicy.MaximumGraphEntryPoints, exactCandidateCount));
    }

    private async Task<CodeExploreDeclarationCatalog> GetOrBuildCodeExploreCatalogAsync(
        WorkspaceId workspaceId,
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        CancellationToken cancellationToken)
    {
        var key = CreateCodeExploreCatalogKey(workspaceId, snapshot.Generation);
        CodeExploreDeclarationCatalog? cachedCatalog = null;
        SharedCodeExploreBuild<CodeExploreDeclarationCatalog>? build = null;
        List<ISharedCodeExploreBuild> supersededBuilds = [];
        lock (_catalogGate)
        {
            ThrowIfSupersededCodeExploreGeneration(workspaceId.Value, snapshot.Generation);
            if (!_latestCodeExploreCatalogGenerations.TryGetValue(workspaceId.Value, out var latestGeneration)
                || snapshot.Generation > latestGeneration)
            {
                _latestCodeExploreCatalogGenerations[workspaceId.Value] = snapshot.Generation;
                supersededBuilds.AddRange(RemoveSupersededCodeExploreBuilds(
                    workspaceId.Value,
                    snapshot.Generation));
            }

            if (_codeExploreCatalogs.TryGetValue(key, out var existing))
            {
                cachedCatalog = existing;
            }
            else
            {
                if (!_codeExploreCatalogBuilds.TryGetValue(key, out build))
                {
                    var buildCancellation = new CancellationTokenSource();
                    var buildTask = Task.Run(
                        () => BuildCodeExploreCatalogAsync(
                            key,
                            snapshot,
                            projection,
                            buildCancellation.Token),
                        buildCancellation.Token);
                    build = new SharedCodeExploreBuild<CodeExploreDeclarationCatalog>(
                        workspaceId.Value,
                        snapshot.Generation,
                        buildCancellation,
                        buildTask);
                    _codeExploreCatalogBuilds.Add(key, build);
                    _ = CompleteCodeExploreCatalogBuildAsync(key, build);
                }

                build.WaiterCount++;
            }
        }

        CancelSharedCodeExploreBuilds(supersededBuilds);
        if (cachedCatalog is not null)
        {
            return cachedCatalog;
        }

        try
        {
            return await build!.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            ReleaseCodeExploreCatalogBuildWaiter(key, build!);
        }
    }

    private async Task CompleteCodeExploreCatalogBuildAsync(
        string key,
        SharedCodeExploreBuild<CodeExploreDeclarationCatalog> build)
    {
        CodeExploreDeclarationCatalog catalog;
        try
        {
#pragma warning disable VSTHRD003 // This service created and separately observes the shared single-flight task.
            catalog = await build.Task;
#pragma warning restore VSTHRD003
        }
        catch
        {
            lock (_catalogGate)
            {
                RemoveMatchingBuild(_codeExploreCatalogBuilds, key, build);
            }

            build.DisposeCancellation();
            return;
        }

        lock (_catalogGate)
        {
            RemoveMatchingBuild(_codeExploreCatalogBuilds, key, build);
            if (_latestCodeExploreCatalogGenerations.TryGetValue(build.WorkspaceId, out var latestGeneration)
                && latestGeneration == build.Generation)
            {
                foreach (var staleKey in _codeExploreCatalogs.Keys
                    .Where(item => item.StartsWith(build.WorkspaceId.ToString("D"), StringComparison.Ordinal)
                        && !string.Equals(item, key, StringComparison.Ordinal))
                    .ToArray())
                {
                    _codeExploreCatalogs.Remove(staleKey);
                    RemoveNaturalLanguageGraphCache(staleKey);
                }

                _codeExploreCatalogs[key] = catalog;
                while (_codeExploreCatalogs.Count > MaximumCodeExploreCatalogs)
                {
                    var firstKey = _codeExploreCatalogs.Keys.Order(StringComparer.Ordinal).First();
                    _codeExploreCatalogs.Remove(firstKey);
                    RemoveNaturalLanguageGraphCache(firstKey);
                }
            }
        }

        build.DisposeCancellation();
    }

    private IReadOnlyList<ISharedCodeExploreBuild> RemoveSupersededCodeExploreBuilds(
        Guid workspaceId,
        long generation)
    {
        var cancellations = new List<ISharedCodeExploreBuild>();
        foreach (var item in _codeExploreCatalogBuilds
            .Where(item => item.Value.WorkspaceId == workspaceId
                && item.Value.Generation < generation)
            .ToArray())
        {
            _codeExploreCatalogBuilds.Remove(item.Key);
            cancellations.Add(item.Value);
        }

        foreach (var item in _naturalLanguageGraphBuilds
            .Where(item => item.Value.WorkspaceId == workspaceId
                && item.Value.Generation < generation)
            .ToArray())
        {
            _naturalLanguageGraphBuilds.Remove(item.Key);
            cancellations.Add(item.Value);
        }

        return cancellations;
    }

    private void ReleaseCodeExploreCatalogBuildWaiter(
        string key,
        SharedCodeExploreBuild<CodeExploreDeclarationCatalog> build)
    {
        var cancel = false;
        lock (_catalogGate)
        {
            if (_codeExploreCatalogBuilds.TryGetValue(key, out var current)
                && ReferenceEquals(current, build))
            {
                build.WaiterCount--;
                if (build.WaiterCount == 0 && !build.Task.IsCompleted)
                {
                    _codeExploreCatalogBuilds.Remove(key);
                    cancel = true;
                }
            }
        }

        if (cancel)
        {
            build.Cancel();
        }
    }

    private static void CancelSharedCodeExploreBuilds(
        IEnumerable<ISharedCodeExploreBuild> builds)
    {
        foreach (var build in builds.Distinct())
        {
            build.Cancel();
        }
    }

    private void ThrowIfSupersededCodeExploreGeneration(Guid workspaceId, long generation)
    {
        if (_latestCodeExploreCatalogGenerations.TryGetValue(workspaceId, out var latestGeneration)
            && generation < latestGeneration)
        {
            throw new InvalidOperationException(
                "The semantic workspace changed while code exploration was running; stale catalog work was discarded.");
        }
    }

    private static void RemoveMatchingBuild<T>(
        Dictionary<string, SharedCodeExploreBuild<T>> builds,
        string key,
        SharedCodeExploreBuild<T> build)
    {
        if (builds.TryGetValue(key, out var current) && ReferenceEquals(current, build))
        {
            builds.Remove(key);
        }
    }

    private void RemoveNaturalLanguageGraphCache(string catalogKey)
    {
        foreach (var graphKey in _naturalLanguageGraphNeighbors.Keys
            .Where(item => item.StartsWith(catalogKey + ':', StringComparison.Ordinal))
            .ToArray())
        {
            _naturalLanguageGraphNeighbors.Remove(graphKey);
        }
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
                    return CreateCodeExploreDeclarationCatalog(
                        key,
                        snapshot.Generation,
                        entries,
                        isComplete,
                        omissions,
                        cancellationToken);
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

                var isGeneratedDocument = projection.IsGenerated(root.SyntaxTree, isSourceGenerated);

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

                    var location = CreateDocumentLocation(
                        document,
                        declaration.SyntaxTree,
                        declaration.Span,
                        projection,
                        isGeneratedDocument);
                    var entry = CreateCodeExploreCatalogEntry(
                        snapshot,
                        symbol,
                        declaration,
                        model,
                        location,
                        isGeneratedDocument);
                    var entryKey = $"{entry.Identity.Id}|{entry.FilePath}|{entry.Range.StartLine}|{entry.Range.StartColumn}|{entry.Range.EndLine}|{entry.Range.EndColumn}";
                    if (seen.Add(entryKey))
                    {
                        entries.Add(entry);
                        if (entries.Count >= MaximumCodeExploreCatalogEntries)
                        {
                            isComplete = false;
                            omissions.Add("The declaration catalog entry limit was reached; lower-ranked declarations may be absent.");
                            return CreateCodeExploreDeclarationCatalog(
                                key,
                                snapshot.Generation,
                                entries,
                                isComplete,
                                omissions,
                                cancellationToken);
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

        return CreateCodeExploreDeclarationCatalog(
            key,
            snapshot.Generation,
            entries,
            isComplete,
            omissions,
            cancellationToken);
    }

    private static CodeExploreDeclarationCatalog CreateCodeExploreDeclarationCatalog(
        string key,
        long workspaceGeneration,
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> entries,
        bool isComplete,
        IReadOnlyList<string> omissions,
        CancellationToken cancellationToken)
    {
        var classifiedEntries = LinkCodeExploreToolCapabilities(entries, cancellationToken);
        var index = CreateCodeExploreCandidateIndex(classifiedEntries, cancellationToken);
        return new CodeExploreDeclarationCatalog(
            key,
            workspaceGeneration,
            classifiedEntries,
            index,
            isComplete,
            omissions);
    }

    private static CodeExploreCandidateIndex CreateCodeExploreCandidateIndex(
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> entries,
        CancellationToken cancellationToken)
    {
        var exactNames = CreateCodeExplorePostings(
            entries,
            entry =>
            [
                NormalizeComparableName(entry.Name),
                NormalizeComparableName(entry.MetadataName),
                NormalizeComparableName(StripParameters(entry.DisplayName)),
            ],
            StringComparer.OrdinalIgnoreCase,
            cancellationToken);
        var nameTerms = CreateCodeExplorePostings(
            entries,
            entry => entry.NameTerms,
            StringComparer.OrdinalIgnoreCase,
            cancellationToken);
        var terms = CreateCodeExplorePostings(
            entries,
            entry => entry.Terms,
            StringComparer.OrdinalIgnoreCase,
            cancellationToken);
        var index = new CodeExploreCandidateIndex(
            CreateCodeExplorePostings(
                entries,
                entry => [entry.Identity.Id],
                StringComparer.Ordinal,
                cancellationToken),
            exactNames,
            nameTerms,
            CreateCodeExplorePostings(
                entries,
                entry => entry.QualifiedNames,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken),
            terms,
            CreateCodeExplorePostings(
                entries,
                entry =>
                [
                    NormalizeCodeExploreIndexPath(entry.RelativeFilePath),
                    NormalizeCodeExploreIndexPath(Path.GetFileName(entry.RelativeFilePath)),
                ],
                StringComparer.OrdinalIgnoreCase,
                cancellationToken),
            CreateCodeExplorePostings(
                entries,
                GetCodeExploreToolFamilies,
                StringComparer.Ordinal,
                cancellationToken),
            CreateSortedVocabulary(exactNames.Keys, cancellationToken),
            CreateSortedVocabulary(nameTerms.Keys, cancellationToken),
            CreateSortedVocabulary(terms.Keys, cancellationToken),
            CreatePostingIdentityCounts(terms, cancellationToken),
            CreateNameIdentityCounts(entries, cancellationToken),
            NaturalLanguageNameSegmentMatcher.CreateIndex(
                entries
                    .Select(entry => new NaturalLanguageNameSegmentCandidate(entry.Identity.Id, entry.Name))
                    .ToArray(),
                cancellationToken));
        return index;
    }

    private static IReadOnlyList<CodeExploreDeclarationCatalogEntry> LinkCodeExploreToolCapabilities(
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> entries,
        CancellationToken cancellationToken)
    {
        var baseEntries = entries
            .Select(entry => entry with
            {
                ToolCapability = RemoveLinkedCodeExploreToolCapability(entry.ToolCapability),
            })
            .ToArray();
        var descriptors = baseEntries
            .Where(entry => entry.ToolCapability.Role is CodeExploreToolCapabilityRole.ToolType
                or CodeExploreToolCapabilityRole.Definition)
            .Where(entry => entry.ToolCapability.ToolTypeName is not null)
            .GroupBy(entry => entry.ToolCapability.ToolTypeName!, StringComparer.Ordinal)
            .Select(group =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var declaredToolId = group
                    .Select(entry => entry.ToolCapability.DeclaredToolId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var familyTerms = declaredToolId is not null
                    ? CodeExploreToolCapabilityClassifier.CreateFamilyTerms(declaredToolId)
                    : group.Select(entry => entry.ToolCapability.FamilyTerms)
                        .FirstOrDefault(terms => terms.Count > 0) ?? [];
                return new CodeExploreToolCapabilityDescriptor(
                    group.Key,
                    CodeExploreToolCapabilityClassifier.CreateFamilyKey(familyTerms),
                    group.SelectMany(entry => entry.ToolCapability.RelatedContractTypeNames)
                        .Select(NormalizeComparableName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase));
            })
            .OrderBy(descriptor => descriptor.ToolTypeName, StringComparer.Ordinal)
            .ToArray();

        var linked = new List<CodeExploreDeclarationCatalogEntry>(baseEntries.Length);
        foreach (var entry in baseEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comparableTypeNames = new[]
            {
                NormalizeComparableName(entry.Name),
                NormalizeComparableName(entry.DisplayName),
                NormalizeComparableName(entry.FullyQualifiedName),
            };
            var directDescriptor = entry.ToolCapability.ToolTypeName is { } toolTypeName
                ? descriptors.FirstOrDefault(descriptor => descriptor.ToolTypeName.Equals(
                    toolTypeName,
                    StringComparison.Ordinal))
                : null;
            var contractDescriptors = IsTypeDeclarationKind(entry.Kind)
                ? descriptors.Where(descriptor => comparableTypeNames.Any(descriptor.RelatedContractTypeNames.Contains))
                : [];
            var referencedDescriptors = descriptors.Where(descriptor =>
                entry.ReferencedTypeNames.Any(reference =>
                    descriptor.ToolTypeName.Equals(reference, StringComparison.Ordinal)
                    || descriptor.ToolTypeName.EndsWith('.' + reference, StringComparison.Ordinal)));
            var matchedDescriptors = directDescriptor is null
                ? contractDescriptors.Concat(referencedDescriptors)
                : contractDescriptors.Concat(referencedDescriptors).Prepend(directDescriptor);
            var families = matchedDescriptors
                .Select(descriptor => descriptor.Family)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var role = entry.ToolCapability.Role == CodeExploreToolCapabilityRole.None
                && contractDescriptors.Any()
                ? CodeExploreToolCapabilityRole.DataContract
                : entry.ToolCapability.Role;
            linked.Add(entry with
            {
                ToolCapability = entry.ToolCapability with
                {
                    Role = role,
                    Families = families,
                },
            });
        }

        return linked;
    }

    private static CodeExploreToolCapabilityEvidence RemoveLinkedCodeExploreToolCapability(
        CodeExploreToolCapabilityEvidence evidence)
    {
        var role = evidence.Role == CodeExploreToolCapabilityRole.DataContract
            ? CodeExploreToolCapabilityRole.None
            : evidence.Role;
        return evidence with
        {
            Role = role,
            Families = [],
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> CreateCodeExplorePostings(
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> entries,
        Func<CodeExploreDeclarationCatalogEntry, IEnumerable<string>> keySelector,
        StringComparer comparer,
        CancellationToken cancellationToken)
    {
        var postings = new Dictionary<string, List<CodeExploreDeclarationCatalogEntry>>(comparer);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var key in keySelector(entry)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(comparer))
            {
                if (!postings.TryGetValue(key, out var values))
                {
                    values = [];
                    postings.Add(key, values);
                }

                values.Add(entry);
            }
        }

        var frozen = new Dictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>>(comparer);
        foreach (var posting in postings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frozen.Add(posting.Key, posting.Value);
        }

        return frozen;
    }

    private static IReadOnlyList<string> CreateSortedVocabulary(
        IEnumerable<string> keys,
        CancellationToken cancellationToken)
    {
        var vocabulary = new List<string>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vocabulary.Add(key);
        }

        vocabulary.Sort(StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        return vocabulary;
    }

    private static IReadOnlyDictionary<string, int> CreatePostingIdentityCounts(
        IReadOnlyDictionary<string, IReadOnlyList<CodeExploreDeclarationCatalogEntry>> postings,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var posting in postings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in posting.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = identities.Add(entry.Identity.Id);
            }

            counts.Add(posting.Key, identities.Count);
        }

        return counts;
    }

    private static IReadOnlyDictionary<string, int> CreateNameIdentityCounts(
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> entries,
        CancellationToken cancellationToken)
    {
        var identitiesByName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeComparableName(entry.Name);
            if (!identitiesByName.TryGetValue(name, out var identities))
            {
                identities = new HashSet<string>(StringComparer.Ordinal);
                identitiesByName.Add(name, identities);
            }

            _ = identities.Add(entry.Identity.Id);
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in identitiesByName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counts.Add(entry.Key, entry.Value.Count);
        }

        return counts;
    }

    private static string NormalizeCodeExploreIndexPath(string value)
    {
        return value.Replace('\\', '/').Trim('/').ToLowerInvariant();
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
        SyntaxNode declaration,
        SemanticModel semanticModel,
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
        var nameTerms = CreateCatalogNameTerms(symbol);
        var signatureTerms = CreateCatalogSignatureTerms(
            symbol,
            kindName,
            containingType,
            containingNamespace);
        var referencedTypeNames = CreateCatalogReferencedTypeNames(declaration);
        return new CodeExploreDeclarationCatalogEntry(
            identity,
            symbol.Name,
            symbol.MetadataName,
            displayName,
            fullyQualifiedName,
            containingType,
            containingNamespace,
            kindName,
            symbol.DeclaredAccessibility,
            location.ProjectName,
            location.TargetFramework,
            filePath,
            relativePath,
            location.Range,
            location.IsGenerated || isSourceGenerated,
            location.IsLinked,
            IsTestProjectNameOrPath(location.ProjectName, relativePath),
            nameTerms,
            signatureTerms,
            nameTerms
                .Concat(signatureTerms)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CreateCatalogContextTerms(location.ProjectName, relativePath),
            referencedTypeNames,
            qualifiedNames)
        {
            ToolCapability = CodeExploreToolCapabilityClassifier.Classify(
                symbol,
                declaration,
                semanticModel),
        };
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

    private static IReadOnlyList<string> CreateCatalogNameTerms(ISymbol symbol)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCodeExploreTerms(terms, symbol.Name);
        AddCodeExploreTerms(terms, symbol.MetadataName);
        return terms.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> CreateCatalogSignatureTerms(
        ISymbol symbol,
        string kindName,
        string? containingType,
        string? containingNamespace)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCodeExploreTerms(terms, kindName);
        AddCodeExploreTerms(terms, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        AddCodeExploreTerms(terms, symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
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

    private static IReadOnlyList<string> CreateCatalogContextTerms(
        string projectName,
        string relativePath)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCodeExploreTerms(terms, projectName);
        AddCodeExploreTerms(terms, relativePath);
        return terms.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> CreateCatalogReferencedTypeNames(
        SyntaxNode declaration)
    {
        IEnumerable<SyntaxNode> referenceRoots = declaration switch
        {
            BaseMethodDeclarationSyntax method => method.Body is not null
                ? [method.Body]
                : method.ExpressionBody is not null ? [method.ExpressionBody] : [],
            PropertyDeclarationSyntax property => property.Initializer is not null
                ? [property.Initializer]
                : property.ExpressionBody is not null ? [property.ExpressionBody] : [],
            VariableDeclaratorSyntax variable when variable.Initializer is not null => [variable.Initializer],
            _ => [],
        };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in referenceRoots
            .SelectMany(root => root.DescendantNodesAndSelf())
            .OfType<ObjectCreationExpressionSyntax>()
            .Select(creation => creation.Type))
        {
            var name = type.DescendantNodesAndSelf()
                .OfType<SimpleNameSyntax>()
                .LastOrDefault()?.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            names.Add(name);
        }

        return names.Order(StringComparer.Ordinal).ToArray();
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

    private static CodeExploreQueryInterpretation RemoveRepositoryNameContextTerm(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreQueryInterpretation interpretation)
    {
        var repositoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(snapshot.RepositoryPath));
        var repositoryToken = NormalizeComparableName(repositoryName);
        if (repositoryToken.Length < 5
            || interpretation.Terms.Count <= 1
            || !interpretation.Terms.Contains(repositoryToken, StringComparer.OrdinalIgnoreCase))
        {
            return interpretation;
        }

        return interpretation with
        {
            Terms = interpretation.Terms
                .Where(term => !string.Equals(term, repositoryToken, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            IgnoredTerms = interpretation.IgnoredTerms
                .Append(repositoryToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    private static CodeExploreQueryInterpretation ApplyNaturalLanguageRetrievalVocabularyPolicy(
        CodeExploreQueryInterpretation interpretation)
    {
        var ignoredRetrievalTerms = interpretation.Terms
            .Where(NaturalLanguageRetrievalStopWords.Contains)
            .ToArray();
        if (ignoredRetrievalTerms.Length == 0)
        {
            return interpretation;
        }

        return interpretation with
        {
            Terms = interpretation.Terms
                .Where(term => !NaturalLanguageRetrievalStopWords.Contains(term))
                .ToArray(),
            IgnoredTerms = interpretation.IgnoredTerms
                .Concat(ignoredRetrievalTerms)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    private static CodeExploreRetrievedCandidate[] GetIndexedCodeExploreCandidates(
        CodeExploreCandidateIndex index,
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> allowedEntries,
        CodeExploreQueryInterpretation interpretation,
        NaturalLanguageNameSegmentEvidence nameSegmentEvidence,
        IReadOnlyList<string> toolCapabilityFamilies,
        CancellationToken cancellationToken)
    {
        var compositionEntries = new List<CodeExploreDeclarationCatalogEntry>();
        if (toolCapabilityFamilies.Count > 0 && HasToolCompositionContextIntent(interpretation))
        {
            foreach (var entry in allowedEntries.Where(entry =>
                IsRequestedToolCompositionEntry(
                    entry,
                    interpretation,
                    toolCapabilityFamilies)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                compositionEntries.Add(entry);
            }
        }

        var paths = new List<CodeExplorePathRetrievalRequest>();
        foreach (var path in interpretation.PathLikeSpans.Where(IsCSharpPathSpan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchedEntries = new List<CodeExploreDeclarationCatalogEntry>();
            foreach (var entry in allowedEntries.Where(entry => PathSpanMatchesEntry(path, entry)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                matchedEntries.Add(entry);
            }

            paths.Add(new CodeExplorePathRetrievalRequest(
                NormalizeCodeExploreIndexPath(path),
                matchedEntries));
        }

        return CodeExploreCandidateRetriever.Retrieve(
            index,
            allowedEntries,
            new CodeExploreCandidateRetrievalRequest(
                interpretation.StableSymbolIds,
                interpretation.ExactIdentifiers
                    .Select(NormalizeComparableName)
                    .ToArray(),
                toolCapabilityFamilies,
                compositionEntries,
                interpretation.QualifiedNames,
                interpretation.Terms,
                nameSegmentEvidence.MatchesByIdentity.Keys.ToArray(),
                paths,
                interpretation.ExactIdentifiers
                    .Concat(interpretation.QualifiedNames)
                    .Select(NormalizeComparableName)
                    .ToArray()),
            cancellationToken);
    }

    private static IEnumerable<string> GetCodeExploreToolFamilies(
        CodeExploreDeclarationCatalogEntry entry)
    {
        foreach (var family in entry.ToolCapability.Families)
        {
            yield return family;
        }
    }

    private static CodeExploreRankedCandidate[] RankNaturalLanguageCandidates(
        IReadOnlyList<CodeExploreRetrievedCandidate> candidateEntries,
        CodeExploreCandidateIndex index,
        CodeExploreQueryInterpretation interpretation,
        NaturalLanguageNameSegmentEvidence nameSegmentEvidence,
        IReadOnlyList<string> requestedToolFamilies,
        CodeExploreNaturalLanguageIntent intent,
        CancellationToken cancellationToken)
    {
        var usesSemanticToolProfile = UsesSemanticToolCapabilityProfile(interpretation, intent);
        var qualifiedMemberTerms = GetQualifiedMemberTerms(interpretation);
        var preliminary = new List<CodeExploreRankedCandidate>();
        foreach (var retrieved in candidateEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rank = RankNaturalLanguageCandidate(
                retrieved,
                interpretation,
                nameSegmentEvidence,
                intent,
                usesSemanticToolProfile,
                requestedToolFamilies,
                qualifiedMemberTerms,
                index.TermCounts,
                index.NameCounts,
                index.NameSegments.SegmentsByIdentity.Count);
            if (rank is not null)
            {
                preliminary.Add(rank);
            }
        }

        var productionCandidateCount = preliminary.Count(candidate => !candidate.Entry.IsTest);
        if (!HasTestFocus(interpretation)
            && productionCandidateCount >= CodeExploreRelevancePolicy.MinimumProductionCandidatesForTestExclusion)
        {
            preliminary = [.. preliminary
                .Where(candidate => !candidate.Entry.IsTest
                    || HasExactCandidateReason(candidate.Reasons))];
        }

        var fileConceptCounts = preliminary
            .GroupBy(candidate => candidate.Entry.FilePath, PathComparer)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(candidate => GetCodeExploreTermCoverage(
                        candidate.Entry.NameTerms,
                        interpretation.Terms))
                    .Select(coverage => coverage.CanonicalTerm)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                PathComparer);
        var maximumCoLocationBoost = intent == CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
            ? NaturalLanguageToolIntentCoLocationBoost
            : NaturalLanguageDefaultCoLocationBoost;
        return OrderNaturalLanguageCandidates(preliminary
            .Select(candidate => fileConceptCounts.GetValueOrDefault(candidate.Entry.FilePath) is var conceptCount
                && conceptCount > 1
                ? candidate with
                {
                    Reasons = candidate.Reasons | CodeExploreSelectionReason.CoLocated,
                    Score = candidate.Score + Math.Min(
                        maximumCoLocationBoost,
                        (conceptCount - 1) * NaturalLanguageCoLocationConceptBoost),
                }
                : candidate));
    }

    private static CodeExploreRankedCandidate[] OrderNaturalLanguageCandidates(
        IEnumerable<CodeExploreRankedCandidate> candidates)
    {
        return
        [
            .. candidates
                .OrderByDescending(candidate => candidate.Reasons.HasFlag(CodeExploreSelectionReason.Pinned))
                .ThenByDescending(candidate => HasExactCandidateReason(candidate.Reasons))
                .ThenByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.GraphMass)
                .ThenBy(candidate => candidate.Tier)
                .ThenBy(candidate => KindSortRank(candidate.Entry.Kind))
                .ThenBy(candidate => candidate.Entry.RelativeFilePath, PathComparer)
                .ThenBy(candidate => candidate.Entry.Range.StartLine)
                .ThenBy(candidate => candidate.Entry.Identity.Id, StringComparer.Ordinal),
        ];
    }

    private static CodeExploreRankedCandidate[] ApplyNaturalLanguageConceptReranking(
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        CodeExploreQueryInterpretation interpretation)
    {
        var conceptGroups = interpretation.Terms
            .GroupBy(CanonicalCodeExploreTerm, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .ToArray();
        if (conceptGroups.Length < 2)
        {
            return [.. ranked];
        }

        return OrderNaturalLanguageCandidates(ranked.Select(candidate =>
        {
            var conceptStrengths = conceptGroups
                .Select(group => group.Max(concept => Math.Max(
                    GetCatalogTermMatchStrength(candidate.Entry.NameTerms, concept),
                    GetDirectoryTermMatchStrength(candidate.Entry.RelativeFilePath, concept))))
                .Where(strength => strength > 0)
                .ToArray();
            var strongConceptCount = conceptStrengths.Count(CodeExploreRelevancePolicy.IsStrongConceptMatch);
            if (strongConceptCount >= 2)
            {
                return candidate with
                {
                    Tier = MinTier(candidate.Tier, CodeExploreCandidateTier.MultiTermStructural),
                    Reasons = candidate.Reasons | CodeExploreSelectionReason.MultiTerm,
                    Score = CodeExploreRelevancePolicy.ApplyMultiTermNameCorroboration(
                        candidate.Score,
                        conceptStrengths.Sum()),
                };
            }

            var hasDistinctiveExactMatch = interpretation.ExactIdentifiers
                .Select(NormalizeComparableName)
                .Any(identifier => IdentifierMatchesEntry(identifier, candidate.Entry));
            if (hasDistinctiveExactMatch)
            {
                return candidate;
            }

            var hasCommonExactMatch = interpretation.Terms.Any(term =>
                string.Equals(
                    NormalizeComparableName(candidate.Entry.Name),
                    NormalizeComparableName(term),
                    StringComparison.OrdinalIgnoreCase));
            return candidate with
            {
                Score = CodeExploreRelevancePolicy.ApplySingleConceptDamping(
                    candidate.Score,
                    hasCommonExactMatch),
            };
        }));
    }

    private static double GetDirectoryTermMatchStrength(string relativeFilePath, string term)
    {
        var directory = Path.GetDirectoryName(relativeFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return 0;
        }

        var directoryTerms = directory
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeComparableName)
            .Where(segment => segment.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return GetCatalogTermMatchStrength(directoryTerms, term);
    }

    private static CodeExploreRankedCandidate[] ApplyNaturalLanguageRelativeFloor(
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        IReadOnlyList<string> requestedToolFamilies)
    {
        if (ranked.Count <= CodeExploreRelevancePolicy.MinimumBackfillCandidateCount)
        {
            return [.. ranked];
        }

        var strongest = ranked.Max(candidate => candidate.Score);
        var relativeFloor = CodeExploreRelevancePolicy.CalculateRelativeScoreFloor(strongest);
        return
        [
            .. ranked
                .Select((candidate, index) => new { Candidate = candidate, Index = index })
                .Where(item => CodeExploreRelevancePolicy.ShouldRetainCandidate(
                    item.Index,
                    item.Candidate.Score,
                    relativeFloor,
                    IsProtectedNaturalLanguageCandidate(item.Candidate, requestedToolFamilies)))
                .Select(item => item.Candidate),
        ];
    }

    private static bool IsProtectedNaturalLanguageCandidate(
        CodeExploreRankedCandidate candidate,
        IReadOnlyList<string> requestedToolFamilies)
    {
        return HasExactCandidateReason(candidate.Reasons)
            || ((IsSemanticToolDefinitionEntry(candidate.Entry)
                    || IsToolCompositionSurface(candidate.Entry))
                && requestedToolFamilies.Any(family =>
                    CandidateMatchesToolCapabilityFamily(candidate.Entry, family)));
    }

    private static CodeExploreRankedCandidate[] ApplyNaturalLanguageFileRelevanceGate(
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        CodeExploreQueryInterpretation interpretation,
        IReadOnlyList<string> retrievalToolFamilies)
    {
        if (ranked.Count == 0)
        {
            return [];
        }

        var qualifiedMemberTerms = GetQualifiedMemberTerms(interpretation);
        var fileGroups = ranked
            .GroupBy(candidate => candidate.Entry.FilePath, PathComparer)
            .Select(group => new
            {
                Path = group.Key,
                Candidates = group.ToArray(),
                Best = group.First(),
                GraphMass = group.Sum(candidate => candidate.GraphMass),
                TermHits = CountNaturalLanguageFileTermHits(
                    group,
                    interpretation,
                    qualifiedMemberTerms),
                IsEntry = group.Any(candidate => candidate.IsGraphSeed),
                NamedPriority = group.Max(candidate => GetNaturalLanguageNamedPriority(
                    candidate,
                    interpretation,
                    retrievalToolFamilies)),
            })
            .ToArray();
        var maximumFileGraphMass = fileGroups
            .Select(group => group.GraphMass)
            .DefaultIfEmpty(0)
            .Max();
        var hasNamedFocus = fileGroups.Any(group => group.NamedPriority >= 3);
        var centralFiles = fileGroups
            .Where(group => group.GraphMass > 0 && group.TermHits >= 1)
            .OrderByDescending(group => group.GraphMass)
            .ThenByDescending(group => group.TermHits)
            .ThenBy(group => group.Path, PathComparer)
            .Take(CodeExploreRelevancePolicy.MaximumCentralGraphFiles)
            .Select(group => group.Path)
            .ToHashSet(PathComparer);
        var gatedFiles = maximumFileGraphMass <= 0
            ? fileGroups
            :
            [
                .. fileGroups.Where(group => group.NamedPriority > 0
                    || group.IsEntry
                    || centralFiles.Contains(group.Path)
                    || (!hasNamedFocus && group.TermHits >= 2)
                    || group.GraphMass
                        >= maximumFileGraphMass * CodeExploreRelevancePolicy.MinimumGraphMassRatio),
            ];
        var admittedFiles = gatedFiles.Length > 0 ? gatedFiles : [fileGroups[0]];
        return
        [
            .. admittedFiles
                .OrderByDescending(group => group.NamedPriority)
                .ThenByDescending(group => group.TermHits >= 2
                    && (group.IsEntry || centralFiles.Contains(group.Path)))
                .ThenBy(
                    group => group.GraphMass,
                    new DescendingGraphMassComparer(maximumFileGraphMass))
                .ThenByDescending(group => group.TermHits)
                .ThenBy(group => group.Best.Entry.IsTest)
                .ThenBy(group => group.Best.Entry.IsGenerated)
                .ThenByDescending(group => group.Best.Score)
                .ThenBy(group => group.Path, PathComparer)
                .SelectMany(group => group.Candidates),
        ];
    }

    private static int CountNaturalLanguageFileTermHits(
        IEnumerable<CodeExploreRankedCandidate> candidates,
        CodeExploreQueryInterpretation interpretation,
        IReadOnlySet<string> qualifiedMemberTerms)
    {
        var materialized = candidates.ToArray();
        var matchesQualifiedFocus = interpretation.QualifiedNames.Any(qualified =>
            materialized.Any(candidate => QualifiedNameMatchesEntry(qualified, candidate.Entry)));
        var candidateTerms = CreateCanonicalCodeExploreTermSet(
            materialized
                .Select(candidate => candidate.Entry.Name)
                .Prepend(materialized[0].Entry.RelativeFilePath));
        var queryTerms = interpretation.Terms
            .Where(term => matchesQualifiedFocus || !qualifiedMemberTerms.Contains(term))
            .ToArray();
        return CreateCanonicalCodeExploreTermSet(queryTerms).Count(candidateTerms.Contains);
    }

    private static HashSet<string> GetQualifiedMemberTerms(
        CodeExploreQueryInterpretation interpretation)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identifier in interpretation.ExactIdentifiers)
        {
            var normalizedIdentifier = NormalizeComparableName(identifier);
            if (!interpretation.QualifiedNames.Any(qualified => qualified.EndsWith(
                "." + normalizedIdentifier,
                StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            AddCodeExploreTerms(terms, identifier);
        }

        return terms;
    }

    private async Task<CodeExploreRankedCandidate[]> ApplyGraphConnectivityAsync(
        WorkspaceId workspaceId,
        AdvancedSemanticSnapshot snapshot,
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> allowedEntries,
        CodeExploreQueryInterpretation interpretation,
        CodeExploreNaturalLanguageIntent intent,
        CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
        {
            return [.. ranked];
        }

        var allowedById = allowedEntries
            .GroupBy(entry => entry.Identity.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var eligibleSeeds = ranked
            .Where(candidate => candidate.Tier <= CodeExploreCandidateTier.MultiTermStructural)
            .Select((candidate, rank) => new { Candidate = candidate, Rank = rank })
            .GroupBy(item => item.Candidate.Entry.Identity.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var seeds = eligibleSeeds
            .Where(item => intent != CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
                || !IsPrivateImplementationHelper(item.Candidate.Entry)
                || HasExactCandidateReason(item.Candidate.Reasons))
            .OrderByDescending(item => IsNaturalLanguageGraphEntryCandidate(item.Candidate))
            .ThenBy(item => item.Candidate.IsFocusedNameMatch
                ? KindSortRank(item.Candidate.Entry.Kind)
                : 0)
            .ThenBy(item => item.Rank)
            .Take(CodeExploreRelevancePolicy.MaximumGraphEntryPoints)
            .Select(item => item.Candidate)
            .ToArray();
        if (seeds.Length == 0)
        {
            return [.. ranked];
        }

        var seedIds = seeds
            .Select(seed => seed.Entry.Identity.Id)
            .ToArray();
        var seedIdSet = seedIds.ToHashSet(StringComparer.Ordinal);
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var globallyDiscovered = seedIds.ToHashSet(StringComparer.Ordinal);
        var perRootNodeLimit = (int)Math.Ceiling(
            MaximumNaturalLanguageGraphNodes / (double)seedIds.Length);
        var baseRootEdgeLimit = MaximumNaturalLanguageGraphEdges / seedIds.Length;
        var extraRootEdges = MaximumNaturalLanguageGraphEdges % seedIds.Length;
        var rootEdgeLimits = seedIds
            .Select((identity, index) => new
            {
                Identity = identity,
                Limit = baseRootEdgeLimit + (index < extraRootEdges ? 1 : 0),
            })
            .ToDictionary(item => item.Identity, item => item.Limit, StringComparer.Ordinal);
        var rootEdgeCounts = seedIds.ToDictionary(identity => identity, _ => 0, StringComparer.Ordinal);
        var frontiers = seedIds.ToDictionary(
            identity => identity,
            identity => new Queue<(string Identity, int Depth)>([(identity, 0)]),
            StringComparer.Ordinal);
        var discoveredByRoot = seedIds.ToDictionary(
            identity => identity,
            identity => new HashSet<string>(StringComparer.Ordinal) { identity },
            StringComparer.Ordinal);
        foreach (var seedId in seedIds)
        {
            adjacency.Add(seedId, new HashSet<string>(StringComparer.Ordinal));
        }

        var remainingExpansionBudget = Math.Max(
            seedIds.Length,
            MaximumNaturalLanguageGraphNodes / MaximumNaturalLanguageGraphConcurrency);
        while (remainingExpansionBudget > 0
            && frontiers.Values.Any(frontier => frontier.Count > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frontierItems = seedIds
                .Where(rootId => frontiers[rootId].Count > 0)
                .Select(rootId => new
                {
                    RootId = rootId,
                    Current = frontiers[rootId].Dequeue(),
                })
                .Where(item => item.Current.Depth < MaximumNaturalLanguageGraphDepth)
                .Take(remainingExpansionBudget)
                .ToArray();
            remainingExpansionBudget -= frontierItems.Length;
            foreach (var chunk in frontierItems.Chunk(MaximumNaturalLanguageGraphConcurrency))
            {
                var expanded = await Task.WhenAll(chunk.Select(async item => new
                {
                    item.RootId,
                    item.Current,
                    ConnectedIds = await GetNaturalLanguageConnectedSymbolIdsAsync(
                        workspaceId,
                        snapshot,
                        item.Current.Identity,
                        allowedById,
                        cancellationToken),
                }));
                foreach (var item in expanded)
                {
                    var rootDiscovered = discoveredByRoot[item.RootId];
                    if (rootEdgeCounts[item.RootId] >= rootEdgeLimits[item.RootId])
                    {
                        continue;
                    }

                    foreach (var connectedId in item.ConnectedIds)
                    {
                        if (!rootDiscovered.Contains(connectedId)
                            && rootDiscovered.Count >= perRootNodeLimit)
                        {
                            continue;
                        }

                        if (!globallyDiscovered.Contains(connectedId)
                            && globallyDiscovered.Count >= MaximumNaturalLanguageGraphNodes)
                        {
                            continue;
                        }

                        if (AddNaturalLanguageGraphEdge(adjacency, item.Current.Identity, connectedId))
                        {
                            rootEdgeCounts[item.RootId]++;
                        }

                        _ = globallyDiscovered.Add(connectedId);
                        if (rootDiscovered.Add(connectedId))
                        {
                            frontiers[item.RootId].Enqueue((connectedId, item.Current.Depth + 1));
                        }

                        if (rootEdgeCounts[item.RootId] >= rootEdgeLimits[item.RootId])
                        {
                            break;
                        }
                    }
                }
            }
        }

        var graphMass = CalculateNaturalLanguageGraphMass(adjacency, seeds);
        var usageConnectedIds = adjacency
            .Where(item => item.Value.Count > 0)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        return BoostGraphConnectedCandidates(
            ranked,
            graphMass,
            seedIdSet,
            usageConnectedIds,
            allowedById,
            interpretation);
    }

    private async Task<IReadOnlyList<string>> GetNaturalLanguageConnectedSymbolIdsAsync(
        WorkspaceId workspaceId,
        AdvancedSemanticSnapshot snapshot,
        string identity,
        IReadOnlyDictionary<string, CodeExploreDeclarationCatalogEntry[]> allowedById,
        CancellationToken cancellationToken)
    {
        var catalogKey = CreateCodeExploreCatalogKey(workspaceId, snapshot.Generation);
        var cacheKey = $"{catalogKey}:{identity}";
        IReadOnlyList<string>? cachedResult = null;
        SharedCodeExploreBuild<IReadOnlyList<string>>? build = null;
        lock (_catalogGate)
        {
            ThrowIfSupersededCodeExploreGeneration(workspaceId.Value, snapshot.Generation);
            if (_naturalLanguageGraphNeighbors.TryGetValue(cacheKey, out var cached))
            {
                cachedResult = cached;
            }
            else
            {
                if (!_naturalLanguageGraphBuilds.TryGetValue(cacheKey, out build))
                {
                    var buildCancellation = new CancellationTokenSource();
                    var buildTask = Task.Run(
                        () => BuildNaturalLanguageConnectedSymbolIdsAsync(
                            snapshot,
                            identity,
                            buildCancellation.Token),
                        buildCancellation.Token);
                    build = new SharedCodeExploreBuild<IReadOnlyList<string>>(
                        workspaceId.Value,
                        snapshot.Generation,
                        buildCancellation,
                        buildTask);
                    _naturalLanguageGraphBuilds.Add(cacheKey, build);
                    _ = CompleteNaturalLanguageGraphBuildAsync(catalogKey, cacheKey, build);
                }

                build.WaiterCount++;
            }
        }

        if (cachedResult is not null)
        {
            return cachedResult.Where(allowedById.ContainsKey).ToArray();
        }

        IReadOnlyList<string> rawResult;
        try
        {
            rawResult = await build!.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            ReleaseNaturalLanguageGraphBuildWaiter(cacheKey, build!);
        }

        return rawResult.Where(allowedById.ContainsKey).ToArray();
    }

    private async Task CompleteNaturalLanguageGraphBuildAsync(
        string catalogKey,
        string cacheKey,
        SharedCodeExploreBuild<IReadOnlyList<string>> build)
    {
        IReadOnlyList<string> rawResult;
        try
        {
#pragma warning disable VSTHRD003 // This service created and separately observes the shared single-flight task.
            rawResult = await build.Task;
#pragma warning restore VSTHRD003
        }
        catch
        {
            lock (_catalogGate)
            {
                RemoveMatchingBuild(_naturalLanguageGraphBuilds, cacheKey, build);
            }

            build.DisposeCancellation();
            return;
        }

        lock (_catalogGate)
        {
            RemoveMatchingBuild(_naturalLanguageGraphBuilds, cacheKey, build);
            if (_latestCodeExploreCatalogGenerations.TryGetValue(build.WorkspaceId, out var latestGeneration)
                && latestGeneration == build.Generation
                && _codeExploreCatalogs.ContainsKey(catalogKey))
            {
                _naturalLanguageGraphNeighbors[cacheKey] = rawResult;
            }
        }

        build.DisposeCancellation();
    }

    private void ReleaseNaturalLanguageGraphBuildWaiter(
        string key,
        SharedCodeExploreBuild<IReadOnlyList<string>> build)
    {
        var cancel = false;
        lock (_catalogGate)
        {
            if (_naturalLanguageGraphBuilds.TryGetValue(key, out var current)
                && ReferenceEquals(current, build))
            {
                build.WaiterCount--;
                if (build.WaiterCount == 0 && !build.Task.IsCompleted)
                {
                    _naturalLanguageGraphBuilds.Remove(key);
                    cancel = true;
                }
            }
        }

        if (cancel)
        {
            build.Cancel();
        }
    }

    private static async Task<IReadOnlyList<string>> BuildNaturalLanguageConnectedSymbolIdsAsync(
        AdvancedSemanticSnapshot snapshot,
        string identity,
        CancellationToken cancellationToken)
    {
        var groups = await ResolveSymbolGroupsInSnapshotAsync(
            snapshot,
            identity,
            [],
            cancellationToken);
        var connectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in groups.SelectMany(group => group.Symbols)
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(symbol => CreateIdentity(symbol).Id, StringComparer.Ordinal))
        {
            foreach (var connected in await FindNaturalLanguageConnectedSymbolsAsync(
                snapshot,
                symbol,
                cancellationToken))
            {
                var connectedId = CreateIdentity(connected).Id;
                if (!string.Equals(identity, connectedId, StringComparison.Ordinal))
                {
                    _ = connectedIds.Add(connectedId);
                }
            }
        }

        var result = connectedIds.Order(StringComparer.Ordinal).ToArray();
        return result;
    }

    private static bool AddNaturalLanguageGraphEdge(
        Dictionary<string, HashSet<string>> adjacency,
        string left,
        string right)
    {
        if (!adjacency.TryGetValue(left, out var leftNeighbors))
        {
            leftNeighbors = new HashSet<string>(StringComparer.Ordinal);
            adjacency.Add(left, leftNeighbors);
        }

        if (!adjacency.TryGetValue(right, out var rightNeighbors))
        {
            rightNeighbors = new HashSet<string>(StringComparer.Ordinal);
            adjacency.Add(right, rightNeighbors);
        }

        var added = leftNeighbors.Add(right);
        _ = rightNeighbors.Add(left);
        return added;
    }

    private static IReadOnlyDictionary<string, double> CalculateNaturalLanguageGraphMass(
        IReadOnlyDictionary<string, HashSet<string>> adjacency,
        IReadOnlyList<CodeExploreRankedCandidate> seeds)
    {
        if (adjacency.Count == 0 || seeds.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var restartWeight = 1.0 / seeds.Count;
        var restart = seeds.ToDictionary(
            seed => seed.Entry.Identity.Id,
            _ => restartWeight,
            StringComparer.Ordinal);
        var mass = adjacency.Keys.ToDictionary(
            id => id,
            id => restart.GetValueOrDefault(id),
            StringComparer.Ordinal);
        for (var iteration = 0; iteration < CodeExploreRelevancePolicy.GraphWalkIterations; iteration++)
        {
            var next = adjacency.Keys.ToDictionary(
                id => id,
                id => CodeExploreRelevancePolicy.GraphRestartProbability * restart.GetValueOrDefault(id),
                StringComparer.Ordinal);
            foreach ((var id, var currentMass) in mass)
            {
                if (!adjacency.TryGetValue(id, out var neighbors) || neighbors.Count == 0)
                {
                    next[id] = next.GetValueOrDefault(id)
                        + ((1 - CodeExploreRelevancePolicy.GraphRestartProbability) * currentMass);

                    continue;
                }

                var contribution = (1 - CodeExploreRelevancePolicy.GraphRestartProbability)
                    * currentMass
                    / neighbors.Count;
                foreach (var neighbor in neighbors)
                {
                    next[neighbor] = next.GetValueOrDefault(neighbor) + contribution;
                }
            }

            mass = next;
        }

        return mass;
    }

    private static async Task<IReadOnlyList<ISymbol>> FindNaturalLanguageConnectedSymbolsAsync(
        AdvancedSemanticSnapshot snapshot,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var connected = new List<ISymbol>();
        if (symbol is IMethodSymbol method)
        {
            var outgoing = await FindOutgoingAsync(method, snapshot.Solution, cancellationToken);
            connected.AddRange(outgoing.Select(edge => edge.Callee.OriginalDefinition));
            var callers = await SymbolFinder.FindCallersAsync(
                method,
                snapshot.Solution,
                cancellationToken);
            connected.AddRange(callers.Select(caller => caller.CallingSymbol.OriginalDefinition));
            var implementations = await SymbolFinder.FindImplementationsAsync(
                method,
                snapshot.Solution,
                cancellationToken: cancellationToken);
            connected.AddRange(implementations.Select(item => item.OriginalDefinition));
            var overrides = await SymbolFinder.FindOverridesAsync(
                method,
                snapshot.Solution,
                cancellationToken: cancellationToken);
            connected.AddRange(overrides.Select(item => item.OriginalDefinition));
        }
        else
        {
            if (symbol is INamedTypeSymbol)
            {
                var implementations = await SymbolFinder.FindImplementationsAsync(
                    symbol,
                    snapshot.Solution,
                    cancellationToken: cancellationToken);
                connected.AddRange(implementations.Select(item => item.OriginalDefinition));
            }

            var references = await SymbolFinder.FindReferencesAsync(
                symbol,
                snapshot.Solution,
                cancellationToken);
            foreach (var reference in references
                .SelectMany(item => item.Locations)
                .OrderBy(item => item.Document.FilePath, PathComparer)
                .ThenBy(item => item.Location.SourceSpan.Start)
                .Take(MaximumNaturalLanguageGraphReferenceLocations))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var semanticModel = await reference.Document.GetSemanticModelAsync(cancellationToken);
                var referencingSymbol = semanticModel?.GetEnclosingSymbol(
                    reference.Location.SourceSpan.Start,
                    cancellationToken);
                if (referencingSymbol is not null)
                {
                    connected.Add(referencingSymbol.OriginalDefinition);
                }
            }
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
        IReadOnlyDictionary<string, double> graphMass,
        IReadOnlySet<string> seedIds,
        IReadOnlySet<string> usageConnectedIds,
        IReadOnlyDictionary<string, CodeExploreDeclarationCatalogEntry[]> allowedById,
        CodeExploreQueryInterpretation interpretation)
    {
        if (graphMass.Count == 0)
        {
            return
            [
                .. ranked.Select(candidate => candidate with
                {
                    Score = IsWeakLowSignalCandidate(
                        candidate.Entry,
                        candidate.CoveredTermCount,
                        candidate.Reasons)
                        ? CodeExploreRelevancePolicy.ApplyIsolatedWeakKindPenalty(candidate.Score)
                        : candidate.Score,
                    IsGraphSeed = seedIds.Contains(candidate.Entry.Identity.Id),
                }),
            ];
        }

        var hasTestFocus = HasTestFocus(interpretation);
        var hasGeneratedFocus = HasGeneratedFocus(interpretation);
        var maximumConnectedMass = graphMass
            .Where(item => !seedIds.Contains(item.Key)
                && allowedById.ContainsKey(item.Key))
            .SelectMany(item => allowedById[item.Key].Select(entry =>
                CalculateDistributedNaturalLanguageGraphMass(
                    item.Value,
                    entry,
                    allowedById[item.Key],
                    hasTestFocus,
                    hasGeneratedFocus)))
            .DefaultIfEmpty(0)
            .Max();
        var expanded = ranked.Select(candidate =>
        {
            var identity = candidate.Entry.Identity.Id;
            var hasUsageEdge = usageConnectedIds.Contains(identity);
            var candidateGraphMass = CalculateDistributedNaturalLanguageGraphMass(
                graphMass.GetValueOrDefault(candidate.Entry.Identity.Id),
                candidate.Entry,
                allowedById.GetValueOrDefault(identity) ?? [candidate.Entry],
                hasTestFocus,
                hasGeneratedFocus);
            if (candidateGraphMass <= 0
                || seedIds.Contains(candidate.Entry.Identity.Id))
            {
                return candidate with
                {
                    Score = !hasUsageEdge && IsWeakLowSignalCandidate(
                        candidate.Entry,
                        candidate.CoveredTermCount,
                        candidate.Reasons)
                        ? CodeExploreRelevancePolicy.ApplyIsolatedWeakKindPenalty(candidate.Score)
                        : candidate.Score,
                    GraphMass = candidateGraphMass,
                    IsGraphSeed = seedIds.Contains(candidate.Entry.Identity.Id),
                };
            }

            var reasons = (candidate.Reasons | CodeExploreSelectionReason.GraphConnected) & ~CodeExploreSelectionReason.Peripheral;
            return candidate with
            {
                Tier = MinTier(candidate.Tier, CodeExploreCandidateTier.GraphConnected),
                Reasons = reasons,
                Score = candidate.Score + CodeExploreRelevancePolicy.CalculateGraphBoost(
                    candidateGraphMass,
                    maximumConnectedMass,
                    NaturalLanguageDefaultGraphBoost),
                GraphMass = candidateGraphMass,
            };
        }).ToList();
        var rankedIds = ranked
            .Select(candidate => candidate.Entry.Identity.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach ((var identity, var rawMass) in graphMass
            .Where(item => !seedIds.Contains(item.Key) && !rankedIds.Contains(item.Key))
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            if (maximumConnectedMass <= 0
                || !allowedById.TryGetValue(identity, out var entries))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var mass = CalculateDistributedNaturalLanguageGraphMass(
                    rawMass,
                    entry,
                    entries,
                    hasTestFocus,
                    hasGeneratedFocus);
                var graphScore = CodeExploreRelevancePolicy.CalculateGraphBoost(
                    mass,
                    maximumConnectedMass,
                    NaturalLanguageDefaultGraphBoost);
                expanded.Add(new CodeExploreRankedCandidate(
                    entry,
                    CodeExploreCandidateTier.GraphConnected,
                    CodeExploreSelectionReason.GraphConnected,
                    graphScore,
                    0,
                    CreateAmbiguityKey(entry),
                    mass));
            }
        }

        return OrderNaturalLanguageCandidates(expanded);
    }

    private static double CalculateDistributedNaturalLanguageGraphMass(
        double identityMass,
        CodeExploreDeclarationCatalogEntry entry,
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> identityEntries,
        bool hasTestFocus,
        bool hasGeneratedFocus)
    {
        if (identityMass <= 0)
        {
            return 0;
        }

        var entryWeight = CodeExploreRelevancePolicy.ApplyGraphClassificationMultiplier(
            1,
            entry.IsTest,
            entry.IsGenerated,
            hasTestFocus,
            hasGeneratedFocus);
        var totalWeight = identityEntries.Sum(candidate =>
            CodeExploreRelevancePolicy.ApplyGraphClassificationMultiplier(
                1,
                candidate.IsTest,
                candidate.IsGenerated,
                hasTestFocus,
                hasGeneratedFocus));
        return totalWeight <= 0 ? 0 : identityMass * entryWeight / totalWeight;
    }

    private static CodeExploreRankedCandidate[] SelectNaturalLanguageCandidates(
        IReadOnlyList<CodeExploreRankedCandidate> rankedByIdentity,
        IReadOnlySet<string> alreadySelectedIds,
        int maximumCandidates,
        int maximumFiles,
        CodeExploreQueryInterpretation interpretation,
        CodeExploreNaturalLanguageIntent intent,
        IReadOnlyList<string> requestedToolFamilies,
        List<string> omissions)
    {
        if (maximumCandidates <= 0)
        {
            return [];
        }

        if (intent is CodeExploreNaturalLanguageIntent.Flow
            or CodeExploreNaturalLanguageIntent.Impact)
        {
            return SelectRelationshipNaturalLanguageCandidates(
                rankedByIdentity,
                alreadySelectedIds,
                maximumCandidates,
                interpretation,
                intent,
                requestedToolFamilies);
        }

        if (!RequiresNaturalLanguageDiversity(intent))
        {
            return
            [
                .. rankedByIdentity
                    .Where(candidate => !alreadySelectedIds.Contains(candidate.Entry.Identity.Id))
                    .Take(maximumCandidates),
            ];
        }

        var selected = new List<CodeExploreRankedCandidate>();
        var selectedIds = alreadySelectedIds.ToHashSet(StringComparer.Ordinal);
        var fileCounts = new Dictionary<string, int>(PathComparer);
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cappedCandidates = 0;
        var usesToolCapabilityProfile = UsesSemanticToolCapabilityProfile(interpretation, intent);
        var representedFiles = new HashSet<string>(PathComparer);

        foreach (var candidate in rankedByIdentity.Where(candidate => HasExactCandidateReason(candidate.Reasons)))
        {
            if (selected.Count >= maximumCandidates)
            {
                break;
            }

            if (selectedIds.Contains(candidate.Entry.Identity.Id))
            {
                continue;
            }

            TrackNaturalLanguageCandidate(candidate, fileCounts, typeCounts);
            AddNaturalLanguageCandidate(selected, selectedIds, candidate);
            _ = representedFiles.Add(candidate.Entry.FilePath);
        }

        ReserveToolCapabilityContextCandidates(
            rankedByIdentity,
            interpretation,
            requestedToolFamilies,
            maximumCandidates,
            selected,
            selectedIds,
            representedFiles,
            fileCounts,
            typeCounts);

        foreach (var candidate in rankedByIdentity.Where(IsNamedNaturalLanguageCandidate))
        {
            if (selected.Count >= maximumCandidates)
            {
                break;
            }

            if (selectedIds.Contains(candidate.Entry.Identity.Id))
            {
                continue;
            }

            if (usesToolCapabilityProfile
                && !HasExactCandidateReason(candidate.Reasons)
                && (IsPrivateImplementationHelper(candidate.Entry)
                    || IsSemanticToolFamilySpecificEntry(candidate.Entry)
                    || requestedToolFamilies.Any(family =>
                        CandidateMatchesToolCapabilityFamily(candidate.Entry, family))))
            {
                continue;
            }

            if (!HasExactCandidateReason(candidate.Reasons)
                && !TryAdmitNaturalLanguageCandidate(candidate, intent, fileCounts, typeCounts))
            {
                cappedCandidates++;
                continue;
            }

            if (HasExactCandidateReason(candidate.Reasons))
            {
                TrackNaturalLanguageCandidate(candidate, fileCounts, typeCounts);
            }

            AddNaturalLanguageCandidate(selected, selectedIds, candidate);
            _ = representedFiles.Add(candidate.Entry.FilePath);
        }

        foreach (var family in requestedToolFamilies)
        {
            if (selected.Count >= maximumCandidates)
            {
                break;
            }

            var candidate = SelectToolCapabilityFamilyCandidate(
                rankedByIdentity,
                selectedIds,
                family,
                fileCounts,
                typeCounts);
            if (candidate is not null)
            {
                AddNaturalLanguageCandidate(selected, selectedIds, candidate);
            }

            if (selected.Count >= maximumCandidates)
            {
                break;
            }
        }

        if (intent == CodeExploreNaturalLanguageIntent.Survey
            && selected.Count < maximumCandidates)
        {
            var maximumRepresentedFiles = Math.Min(maximumFiles, maximumCandidates);
            foreach (var candidate in rankedByIdentity)
            {
                if (selected.Count >= maximumCandidates
                    || representedFiles.Count >= maximumRepresentedFiles)
                {
                    break;
                }

                if (selectedIds.Contains(candidate.Entry.Identity.Id)
                    || representedFiles.Contains(candidate.Entry.FilePath)
                    || !TryAdmitNaturalLanguageCandidate(candidate, intent, fileCounts, typeCounts))
                {
                    continue;
                }

                AddNaturalLanguageCandidate(selected, selectedIds, candidate);
                _ = representedFiles.Add(candidate.Entry.FilePath);
            }
        }

        foreach (var candidate in rankedByIdentity)
        {
            if (selected.Count >= maximumCandidates)
            {
                break;
            }

            if (selectedIds.Contains(candidate.Entry.Identity.Id))
            {
                continue;
            }

            if (usesToolCapabilityProfile
                && !HasExactCandidateReason(candidate.Reasons)
                && ShouldSkipToolCapabilityBackfill(candidate, requestedToolFamilies))
            {
                continue;
            }

            if (!TryAdmitNaturalLanguageCandidate(candidate, intent, fileCounts, typeCounts))
            {
                cappedCandidates++;
                continue;
            }

            AddNaturalLanguageCandidate(selected, selectedIds, candidate);
            _ = representedFiles.Add(candidate.Entry.FilePath);
        }

        if (selected.Count == 0)
        {
            selected.AddRange(rankedByIdentity
                .Where(candidate => !alreadySelectedIds.Contains(candidate.Entry.Identity.Id))
                .Take(maximumCandidates));
        }

        if (cappedCandidates > 0)
        {
            omissions.Add($"Natural-language diversity capped {cappedCandidates} same-file or same-type candidate(s) so one implementation cluster could not consume all selected anchors.");
        }

        return [.. selected];
    }

    private static void ReserveToolCapabilityContextCandidates(
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        CodeExploreQueryInterpretation interpretation,
        IReadOnlyList<string> requestedToolFamilies,
        int maximumCandidates,
        List<CodeExploreRankedCandidate> selected,
        HashSet<string> selectedIds,
        HashSet<string> representedFiles,
        Dictionary<string, int> fileCounts,
        Dictionary<string, int> typeCounts)
    {
        if (requestedToolFamilies.Count == 0)
        {
            return;
        }

        var hasLifecycleIntent = HasToolLifecycleIntent(interpretation);
        var candidates = new[]
        {
            hasLifecycleIntent
                ? ranked.FirstOrDefault(candidate =>
                    !selectedIds.Contains(candidate.Entry.Identity.Id)
                    && (IsSemanticToolTypeEntry(candidate.Entry)
                        || IsSemanticToolDefinitionEntry(candidate.Entry))
                    && requestedToolFamilies.Any(family =>
                        CandidateMatchesToolCapabilityFamily(candidate.Entry, family)))
                : null,
            ranked.FirstOrDefault(candidate =>
                !selectedIds.Contains(candidate.Entry.Identity.Id)
                && !representedFiles.Contains(candidate.Entry.FilePath)
                && IsRequestedToolCompositionEntry(
                    candidate.Entry,
                    interpretation,
                    requestedToolFamilies)),
        };
        foreach (var candidate in candidates.OfType<CodeExploreRankedCandidate>())
        {
            if (selected.Count >= maximumCandidates || !selectedIds.Add(candidate.Entry.Identity.Id))
            {
                continue;
            }

            TrackNaturalLanguageCandidate(candidate, fileCounts, typeCounts);
            selected.Add(candidate);
            _ = representedFiles.Add(candidate.Entry.FilePath);
        }
    }

    private static CodeExploreRankedCandidate[] SelectRelationshipNaturalLanguageCandidates(
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        IReadOnlySet<string> alreadySelectedIds,
        int maximumCandidates,
        CodeExploreQueryInterpretation interpretation,
        CodeExploreNaturalLanguageIntent intent,
        IReadOnlyList<string> requestedToolFamilies)
    {
        var selected = new List<CodeExploreRankedCandidate>();
        var selectedIds = alreadySelectedIds.ToHashSet(StringComparer.Ordinal);
        var representedFiles = new HashSet<string>(PathComparer);
        var fileCounts = new Dictionary<string, int>(PathComparer);
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var qualifiedMemberTerms = GetQualifiedMemberTerms(interpretation);
        if (interpretation.QualifiedNames.Count > 0)
        {
            AddCandidates(
                ranked.Where(IsNamedNaturalLanguageCandidate),
                requireNewFile: false,
                maximumSelected: maximumCandidates);
            AddCandidates(
                ranked.Where(candidate => HasCompilerRelationshipEvidence(candidate.Reasons)),
                requireNewFile: true,
                maximumSelected: maximumCandidates);
            if (selected.Count == 0 && ranked.Count > 0)
            {
                AddCandidate(ranked[0]);
            }

            return [.. selected];
        }

        var fileGroups = ranked
            .GroupBy(candidate => candidate.Entry.FilePath, PathComparer)
            .Select(group => new
            {
                Path = group.Key,
                Candidates = group.ToArray(),
                Best = group.First(),
                TermHits = CountNaturalLanguageFileTermHits(
                    group,
                    interpretation,
                    qualifiedMemberTerms),
                GraphMass = group.Sum(candidate => candidate.GraphMass),
                IsEntry = group.Any(candidate => candidate.IsGraphSeed),
                IsNamed = group.Any(IsNamedNaturalLanguageCandidate),
            })
            .ToArray();
        var maximumFileGraphMass = fileGroups.Select(group => group.GraphMass).DefaultIfEmpty(0).Max();
        var centralFiles = fileGroups
            .Where(group => group.GraphMass > 0 && group.TermHits >= 1)
            .OrderByDescending(group => group.GraphMass)
            .ThenByDescending(group => group.TermHits)
            .ThenBy(group => group.Path, PathComparer)
            .Take(CodeExploreRelevancePolicy.MaximumCentralGraphFiles)
            .Select(group => group.Path)
            .ToHashSet(PathComparer);
        var gatedFiles = maximumFileGraphMass <= 0
            ? fileGroups
            :
            [
                .. fileGroups
                .Where(group => group.IsNamed
                    || group.IsEntry
                    || centralFiles.Contains(group.Path)
                    || group.TermHits >= 2
                    || group.GraphMass
                        >= maximumFileGraphMass * CodeExploreRelevancePolicy.MinimumGraphMassRatio),
            ];
        var admittedFiles = gatedFiles.Length >= CodeExploreRelevancePolicy.MinimumProductionCandidatesForTestExclusion
            ? gatedFiles
            : fileGroups;
        var orderedFiles = admittedFiles
            .OrderByDescending(group => group.IsNamed)
            .ThenByDescending(group => group.TermHits >= 2
                && (group.IsEntry || centralFiles.Contains(group.Path)))
            .ThenBy(
                group => group.GraphMass,
                new DescendingGraphMassComparer(maximumFileGraphMass))
            .ThenByDescending(group => group.TermHits)
            .ThenBy(group => group.Best.Entry.IsTest)
            .ThenBy(group => group.Best.Entry.IsGenerated)
            .ThenByDescending(group => group.Best.Score)
            .ThenByDescending(group => group.Candidates.Length)
            .ThenBy(group => group.Path, PathComparer)
            .ToArray();

        var fileDiversityReservation = Math.Min(
            Math.Max(0, maximumCandidates - 1),
            CodeExploreRelevancePolicy.MinimumBackfillCandidateCount);
        foreach (var family in requestedToolFamilies)
        {
            if (selected.Count >= maximumCandidates)
            {
                break;
            }

            var familyCandidate = ranked.FirstOrDefault(candidate =>
                !selectedIds.Contains(candidate.Entry.Identity.Id)
                && IsSemanticToolDefinitionEntry(candidate.Entry)
                && CandidateMatchesToolCapabilityFamily(candidate.Entry, family));
            if (familyCandidate is not null)
            {
                TrackNaturalLanguageCandidate(familyCandidate, fileCounts, typeCounts);
                AddCandidate(familyCandidate);
            }
        }

        AddCandidates(
            ranked.Where(IsNamedNaturalLanguageCandidate),
            requireNewFile: false,
            maximumSelected: maximumCandidates - fileDiversityReservation);
        AddCandidates(
            orderedFiles.Select(group => group.Best),
            requireNewFile: true,
            maximumSelected: maximumCandidates);
        if (selected.Count < CodeExploreRelevancePolicy.MinimumBackfillCandidateCount)
        {
            AddCandidates(
                ranked,
                requireNewFile: true,
                maximumSelected: Math.Min(
                    maximumCandidates,
                    CodeExploreRelevancePolicy.MinimumBackfillCandidateCount));
        }

        return [.. selected];

        void AddCandidate(CodeExploreRankedCandidate candidate)
        {
            _ = selectedIds.Add(candidate.Entry.Identity.Id);
            selected.Add(candidate);
            _ = representedFiles.Add(candidate.Entry.FilePath);
        }

        void AddCandidates(
            IEnumerable<CodeExploreRankedCandidate> candidates,
            bool requireNewFile,
            int maximumSelected)
        {
            foreach (var candidate in candidates)
            {
                if (selected.Count >= maximumSelected)
                {
                    break;
                }

                if (selectedIds.Contains(candidate.Entry.Identity.Id)
                    || (requireNewFile && representedFiles.Contains(candidate.Entry.FilePath)))
                {
                    continue;
                }

                if (!TryAdmitNaturalLanguageCandidate(
                    candidate,
                    intent,
                    fileCounts,
                    typeCounts))
                {
                    continue;
                }

                AddCandidate(candidate);
            }
        }
    }

    private static bool IsNamedNaturalLanguageCandidate(CodeExploreRankedCandidate candidate)
    {
        return HasExactCandidateReason(candidate.Reasons)
            || candidate.IsFocusedNameMatch;
    }

    private static bool HasCompilerRelationshipEvidence(CodeExploreSelectionReason reasons)
    {
        const CodeExploreSelectionReason relationshipReasons = CodeExploreSelectionReason.GraphConnected
            | CodeExploreSelectionReason.FlowSpine
            | CodeExploreSelectionReason.Implementation
            | CodeExploreSelectionReason.Caller;
        return (reasons & relationshipReasons) != 0;
    }

    private static bool IsNaturalLanguageGraphEntryCandidate(CodeExploreRankedCandidate candidate)
    {
        return IsNamedNaturalLanguageCandidate(candidate) || candidate.IsFocusedNameMatch;
    }

    private static int GetNaturalLanguageNamedPriority(
        CodeExploreRankedCandidate candidate,
        CodeExploreQueryInterpretation interpretation,
        IReadOnlyList<string> retrievalToolFamilies)
    {
        if (HasExactCandidateReason(candidate.Reasons))
        {
            return 4;
        }

        if (candidate.IsFocusedNameMatch)
        {
            return 3;
        }

        if (IsSemanticToolDefinitionEntry(candidate.Entry)
            && retrievalToolFamilies.Any(family =>
                CandidateMatchesToolCapabilityFamily(candidate.Entry, family)))
        {
            return 2;
        }

        return retrievalToolFamilies.Count > 0
            && IsRequestedToolCompositionEntry(
                candidate.Entry,
                interpretation,
                retrievalToolFamilies)
                ? 1
                : 0;
    }

    private static CodeExploreRankedCandidate[] SelectNaturalLanguageSourceCompanions(
        IReadOnlyList<CodeExploreRankedCandidate> ranked,
        IReadOnlyList<CodeExploreRankedCandidate> selected,
        CodeExploreLimits limits)
    {
        if (selected.Count == 0)
        {
            return [];
        }

        var selectedIds = selected
            .Select(candidate => candidate.Entry.Identity.Id)
            .ToHashSet(StringComparer.Ordinal);
        var selectedFiles = selected
            .Select(candidate => candidate.Entry.FilePath)
            .Distinct(PathComparer)
            .ToArray();
        var perFileCapacity = limits.MaximumPerFileSourceCharacters
            / CodeExploreSourceAllocationPlanner.MinimumAdmittedSourceCharacters;
        var totalCapacity = limits.MaximumSourceCharacters
            / CodeExploreSourceAllocationPlanner.MinimumAdmittedSourceCharacters;
        var maximumTotal = Math.Min(
            Math.Max(0, limits.MaximumFlowNodes - selected.Count),
            Math.Max(0, totalCapacity - selected.Count));
        if (perFileCapacity <= 0 || maximumTotal <= 0)
        {
            return [];
        }

        var remainingByFile = selectedFiles.ToDictionary(
            path => path,
            path => Math.Max(
                0,
                perFileCapacity - selected.Count(candidate => PathComparer.Equals(candidate.Entry.FilePath, path))),
            PathComparer);
        var candidatesByFile = selectedFiles.ToDictionary(
            path => path,
            path => ranked
                .Where(candidate => PathComparer.Equals(candidate.Entry.FilePath, path)
                    && !selectedIds.Contains(candidate.Entry.Identity.Id)
                    && IsUsefulNaturalLanguageSourceCompanion(candidate))
                .Take(remainingByFile[path])
                .ToArray(),
            PathComparer);
        var companions = new List<CodeExploreRankedCandidate>();
        var maximumPerFile = remainingByFile.Values.DefaultIfEmpty(0).Max();
        for (var index = 0; index < maximumPerFile && companions.Count < maximumTotal; index++)
        {
            foreach (var path in selectedFiles)
            {
                var fileCandidates = candidatesByFile[path];
                if (index < fileCandidates.Length)
                {
                    companions.Add(fileCandidates[index]);
                }

                if (companions.Count >= maximumTotal)
                {
                    break;
                }
            }
        }

        return [.. companions];
    }

    private static bool IsUsefulNaturalLanguageSourceCompanion(CodeExploreRankedCandidate candidate)
    {
        return candidate.CoveredTermCount >= 2
            || HasExactCandidateReason(candidate.Reasons)
            || candidate.Reasons.HasFlag(CodeExploreSelectionReason.NameSegment)
            || (candidate.GraphMass > 0
                && candidate.Reasons.HasFlag(CodeExploreSelectionReason.GraphConnected));
    }

    private static bool ShouldSkipToolCapabilityBackfill(
        CodeExploreRankedCandidate candidate,
        IReadOnlyList<string> requestedFamilies)
    {
        if (IsPrivateImplementationHelper(candidate.Entry)
            && candidate.CoveredTermCount <= 1
            && !HasExactCandidateReason(candidate.Reasons))
        {
            return true;
        }

        var isUnrequestedToolSurface = IsSemanticToolFamilySpecificEntry(candidate.Entry)
            && !requestedFamilies.Any(family =>
                CandidateMatchesToolCapabilityFamily(candidate.Entry, family));
        var hasInsufficientDirectEvidence = candidate.CoveredTermCount < 2
            && (IsToolCapabilityContractCandidate(candidate.Entry)
                || IsSemanticToolContainingType(candidate.Entry));
        return isUnrequestedToolSurface || hasInsufficientDirectEvidence;
    }

    private static bool IsSemanticToolFamilySpecificEntry(CodeExploreDeclarationCatalogEntry entry)
    {
        return IsSemanticToolTypeEntry(entry)
            || IsSemanticToolDefinitionEntry(entry)
            || IsSemanticToolDataContractEntry(entry)
            || IsSemanticToolContainingType(entry)
            || IsSemanticToolDataContractContainingType(entry)
            || SemanticToolNameContainsKnownFamily(entry.Name)
            || SemanticToolNameContainsKnownFamily(entry.DisplayName)
            || (entry.ContainingType is { } containingType
                && SemanticToolNameContainsKnownFamily(containingType));
    }

    private static CodeExploreRankedCandidate? SelectToolCapabilityFamilyCandidate(
        IReadOnlyList<CodeExploreRankedCandidate> rankedCandidates,
        IReadOnlySet<string> selectedIds,
        string family,
        Dictionary<string, int> fileCounts,
        Dictionary<string, int> typeCounts)
    {
        if (rankedCandidates.Any(candidate =>
            selectedIds.Contains(candidate.Entry.Identity.Id)
            && IsSemanticToolDefinitionEntry(candidate.Entry)
            && CandidateMatchesToolCapabilityFamily(candidate.Entry, family)))
        {
            return null;
        }

        var familyCandidates = rankedCandidates
            .Where(candidate => !selectedIds.Contains(candidate.Entry.Identity.Id)
                && CandidateMatchesToolCapabilityFamily(candidate.Entry, family))
            .OrderBy(candidate => IsSemanticToolDefinitionEntry(candidate.Entry) ? 0 : 1)
            .ToArray();
        var definitionCandidate = familyCandidates.FirstOrDefault(candidate =>
            IsSemanticToolDefinitionEntry(candidate.Entry));
        if (definitionCandidate is not null)
        {
            TrackNaturalLanguageCandidate(definitionCandidate, fileCounts, typeCounts);
            return definitionCandidate;
        }

        return familyCandidates.FirstOrDefault(candidate => TryAdmitNaturalLanguageCandidate(
            candidate,
            CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation,
            fileCounts,
            typeCounts));
    }

    private static IReadOnlyList<string> GetToolCapabilitySelectionFamilies(
        CodeExploreQueryInterpretation interpretation,
        CodeExploreNaturalLanguageIntent intent)
    {
        if (intent != CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation)
        {
            return [];
        }

        var families = new List<string>(GetMentionedToolCapabilityFamilies(interpretation));
        if (ShouldExpandSemanticToolSurvey(interpretation))
        {
            foreach (var family in ToolCapabilitySurveyFamilies)
            {
                AddMentionedToolCapabilityFamily(families, family, isMentioned: true);
            }
        }

        return families;
    }

    private static IReadOnlyList<string> GetNaturalLanguageRetrievalToolFamilies(
        CodeExploreQueryInterpretation interpretation,
        CodeExploreNaturalLanguageIntent intent)
    {
        if (intent != CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation)
        {
            return [];
        }

        var hasSemanticContext = interpretation.Terms.Any(term => term is "semantic" or "semantics" or "compiler")
            || MentionsKnownSemanticToolId(interpretation)
            || interpretation.ExactIdentifiers.Any(SemanticToolNameContainsKnownFamily)
            || HasAnyTermPair(interpretation, "code", "explore")
            || HasAnyTermPair(interpretation, "symbol", "lookup")
            || HasAnyTermPair(interpretation, "symbol", "declaration")
            || HasAnyTermPair(interpretation, "reference", "usage")
            || HasAnyTermPair(interpretation, "interface", "implementation")
            || HasAnyTermPair(interpretation, "implementation", "override")
            || HasAnyTermPair(interpretation, "blast", "radius")
            || HasAnyTermPair(interpretation, "call", "hierarchy");
        if (!hasSemanticContext)
        {
            return [];
        }

        var families = new List<string>(GetMentionedToolCapabilityFamilies(interpretation));
        if (ShouldExpandSemanticToolSurvey(interpretation))
        {
            foreach (var family in ToolCapabilitySurveyFamilies)
            {
                AddMentionedToolCapabilityFamily(families, family, isMentioned: true);
            }
        }

        return families;
    }

    private static IReadOnlyList<string> GetMentionedCatalogToolFamilies(
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> entries,
        CodeExploreQueryInterpretation interpretation,
        string query)
    {
        var exactIdentifiers = interpretation.ExactIdentifiers
            .Select(NormalizeComparableName)
            .ToArray();
        var qualifiedOwners = interpretation.QualifiedNames
            .Select(qualified => qualified.LastIndexOf('.') is var separator && separator > 0
                ? qualified[..separator]
                : qualified)
            .ToArray();
        return entries
            .Where(entry => entry.ToolCapability.Families.Count > 0)
            .Where(entry => ToolCapabilityIsExplicitlyMentioned(
                entry,
                interpretation,
                exactIdentifiers,
                qualifiedOwners,
                query))
            .SelectMany(entry => entry.ToolCapability.Families)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ToolCapabilityIsExplicitlyMentioned(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation,
        IReadOnlyList<string> exactIdentifiers,
        IReadOnlyList<string> qualifiedOwners,
        string query)
    {
        if (entry.ToolCapability.DeclaredToolId is { } declaredToolId
            && QueryContainsIdentifier(query, declaredToolId))
        {
            return true;
        }

        if (exactIdentifiers.Any(identifier =>
            !IsQualifiedNameComponent(identifier, interpretation.QualifiedNames)
            && (IdentifierMatchesEntry(identifier, entry)
                || ToolCapabilityIdentityMatches(entry, identifier))))
        {
            return true;
        }

        return qualifiedOwners.Any(owner =>
            ToolCapabilityIdentityMatches(entry, owner));
    }

    private static bool ToolCapabilityIdentityMatches(
        CodeExploreDeclarationCatalogEntry entry,
        string identity)
    {
        var normalizedIdentity = NormalizeComparableName(identity);
        return (entry.ToolCapability.ToolTypeName is { } toolTypeName
                && QualifiedToolIdentityMatches(toolTypeName, normalizedIdentity))
            || entry.ToolCapability.RelatedContractTypeNames.Any(contractTypeName =>
                QualifiedToolIdentityMatches(contractTypeName, normalizedIdentity));
    }

    private static bool QualifiedToolIdentityMatches(string candidate, string identity)
    {
        var normalizedCandidate = NormalizeComparableName(candidate);
        return normalizedCandidate.Equals(identity, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.EndsWith('.' + identity, StringComparison.OrdinalIgnoreCase);
    }

    private static bool QueryContainsIdentifier(string query, string identifier)
    {
        for (var start = 0; start <= query.Length - identifier.Length; start++)
        {
            if (!query.AsSpan(start, identifier.Length).Equals(
                identifier.AsSpan(),
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasLeadingBoundary = start == 0 || !IsIdentifierCharacter(query[start - 1]);
            var end = start + identifier.Length;
            var hasTrailingBoundary = end == query.Length || !IsIdentifierCharacter(query[end]);
            if (hasLeadingBoundary && hasTrailingBoundary)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static bool UsesSemanticToolCapabilityProfile(
        CodeExploreQueryInterpretation interpretation,
        CodeExploreNaturalLanguageIntent intent)
    {
        return intent == CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
            && (interpretation.Terms.Any(term => term is "semantic" or "semantics")
                || HasAnyTermPair(interpretation, "code", "explore")
                || CountMentionedToolCapabilityFamilies(interpretation) >= 2
                || MentionsKnownSemanticToolId(interpretation)
                || interpretation.ExactIdentifiers.Any(identifier =>
                    IsToolTypeName(identifier)
                    || SemanticToolNameContainsKnownFamily(identifier)));
    }

    private static bool RequiresNaturalLanguageDiversity(CodeExploreNaturalLanguageIntent intent)
    {
        return intent is CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
            or CodeExploreNaturalLanguageIntent.Survey;
    }

    private static bool TryAdmitNaturalLanguageCandidate(
        CodeExploreRankedCandidate candidate,
        CodeExploreNaturalLanguageIntent intent,
        Dictionary<string, int> fileCounts,
        Dictionary<string, int> typeCounts)
    {
        var maximumPerType = intent == CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
            ? ToolIntentMaximumSelectedPerType
            : SurveyIntentMaximumSelectedPerType;
        var maximumPerFile = intent == CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation
            ? ToolIntentMaximumSelectedPerFile
            : SurveyIntentMaximumSelectedPerFile;
        var typeKey = GetNaturalLanguageTypeClusterKey(candidate.Entry);
        if (typeCounts.GetValueOrDefault(typeKey) >= maximumPerType
            || fileCounts.GetValueOrDefault(candidate.Entry.FilePath) >= maximumPerFile)
        {
            return false;
        }

        TrackNaturalLanguageCandidate(candidate, fileCounts, typeCounts);
        return true;
    }

    private static void TrackNaturalLanguageCandidate(
        CodeExploreRankedCandidate candidate,
        Dictionary<string, int> fileCounts,
        Dictionary<string, int> typeCounts)
    {
        var typeKey = GetNaturalLanguageTypeClusterKey(candidate.Entry);
        typeCounts[typeKey] = typeCounts.GetValueOrDefault(typeKey) + 1;
        fileCounts[candidate.Entry.FilePath] = fileCounts.GetValueOrDefault(candidate.Entry.FilePath) + 1;
    }

    private static void AddNaturalLanguageCandidate(
        List<CodeExploreRankedCandidate> selected,
        HashSet<string> selectedIds,
        CodeExploreRankedCandidate candidate)
    {
        selected.Add(candidate);
        selectedIds.Add(candidate.Entry.Identity.Id);
    }

    private static string GetNaturalLanguageTypeClusterKey(CodeExploreDeclarationCatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ContainingType))
        {
            return NormalizeComparableName(entry.ContainingType);
        }

        return IsTypeDeclarationKind(entry.Kind)
            ? NormalizeComparableName(entry.FullyQualifiedName)
            : entry.FilePath;
    }

    private static NaturalLanguageIntentAdjustment CalculateIntentRelevanceAdjustment(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation,
        CodeExploreNaturalLanguageIntent intent,
        bool usesSemanticToolProfile,
        int coveredTermCount,
        CodeExploreSelectionReason existingReasons)
    {
        var score = 0;
        var reasons = CodeExploreSelectionReason.None;
        var tier = CodeExploreCandidateTier.Peripheral;
        if (intent == CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation)
        {
            if (!usesSemanticToolProfile
                && (IsSemanticToolTypeEntry(entry) || IsSemanticToolContainingType(entry))
                && coveredTermCount <= 1
                && !HasExactCandidateReason(existingReasons))
            {
                score -= 320;
            }

            if (usesSemanticToolProfile && IsSemanticToolTypeEntry(entry))
            {
                score += 680;
                reasons |= CodeExploreSelectionReason.UserFocus | CodeExploreSelectionReason.MultiTerm;
                tier = MinTier(tier, CodeExploreCandidateTier.DistinctiveIdentifier);
            }

            if (usesSemanticToolProfile && IsSemanticToolDefinitionEntry(entry))
            {
                score += 780;
                reasons |= CodeExploreSelectionReason.UserFocus | CodeExploreSelectionReason.MultiTerm;
                tier = MinTier(tier, CodeExploreCandidateTier.DistinctiveIdentifier);
            }

            if (IsToolCompositionEntry(entry, interpretation))
            {
                score += 520;
                reasons |= CodeExploreSelectionReason.UserFocus | CodeExploreSelectionReason.MultiTerm;
                tier = MinTier(tier, CodeExploreCandidateTier.MultiTermStructural);
            }

            if (usesSemanticToolProfile && IsSemanticServiceContractEntry(entry))
            {
                score += 380;
                reasons |= CodeExploreSelectionReason.UserFocus;
                tier = MinTier(tier, CodeExploreCandidateTier.MultiTermStructural);
            }

            if (usesSemanticToolProfile && IsSemanticToolDataContractEntry(entry))
            {
                score += 180;
                reasons |= CodeExploreSelectionReason.UserFocus;
                tier = MinTier(tier, CodeExploreCandidateTier.MultiTermStructural);
            }

            if (IsToolConstructorEntry(entry))
            {
                score -= 170;
            }

            if (IsPrivateImplementationHelper(entry)
                && coveredTermCount <= 1
                && !HasExactCandidateReason(existingReasons))
            {
                score -= 520;
            }
        }
        else if (intent == CodeExploreNaturalLanguageIntent.Survey)
        {
            if (IsPrivateImplementationHelper(entry)
                && coveredTermCount <= 1
                && !HasExactCandidateReason(existingReasons))
            {
                score -= 180;
            }

            if (IsToolConstructorEntry(entry) && coveredTermCount <= 1)
            {
                score -= 70;
            }
        }

        return new NaturalLanguageIntentAdjustment(score, reasons, tier);
    }

    private static bool HasExactCandidateReason(CodeExploreSelectionReason reasons)
    {
        return (reasons & (CodeExploreSelectionReason.Pinned
            | CodeExploreSelectionReason.Path
            | CodeExploreSelectionReason.QualifiedName
            | CodeExploreSelectionReason.ExactIdentifier)) != 0;
    }

    private static bool IsSemanticToolTypeEntry(CodeExploreDeclarationCatalogEntry entry)
    {
        return entry.ToolCapability.Role == CodeExploreToolCapabilityRole.ToolType;
    }

    private static bool IsToolCapabilityContractCandidate(CodeExploreDeclarationCatalogEntry entry)
    {
        return IsSemanticToolTypeEntry(entry)
            || IsSemanticToolDefinitionEntry(entry)
            || IsSemanticServiceContractEntry(entry)
            || IsSemanticToolDataContractEntry(entry);
    }

    private static bool IsSemanticToolDefinitionEntry(CodeExploreDeclarationCatalogEntry entry)
    {
        return entry.ToolCapability.Role == CodeExploreToolCapabilityRole.Definition;
    }

    private static bool IsSemanticServiceContractEntry(CodeExploreDeclarationCatalogEntry entry)
    {
        return entry.Kind.Equals("Interface", StringComparison.OrdinalIgnoreCase)
            && IsToolServiceContractName(entry.Name);
    }

    private static bool IsSemanticToolDataContractEntry(CodeExploreDeclarationCatalogEntry entry)
    {
        return entry.ToolCapability.Role == CodeExploreToolCapabilityRole.DataContract;
    }

    private static bool IsSemanticToolDataContractContainingType(CodeExploreDeclarationCatalogEntry entry)
    {
        return entry.ContainingType is { } containingType
            && IsSemanticToolDataContractName(containingType);
    }

    private static bool IsSemanticToolDataContractName(string name)
    {
        return (name.EndsWith("Input", StringComparison.Ordinal)
            || name.EndsWith("Output", StringComparison.Ordinal)
            || name.EndsWith("Result", StringComparison.Ordinal)
            || name.EndsWith("Request", StringComparison.Ordinal))
            && SemanticToolNameContainsKnownFamily(name);
    }

    private static bool IsToolCompositionEntry(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation)
    {
        var hasDirectCompositionEvidence = IsToolCompositionEntry(entry)
            && (interpretation.Terms.Any(term => term is "semantic" or "semantics")
                || interpretation.Terms.Any(term => !ToolCapabilityIntentTerms.Contains(term)
                    && CatalogTermsCoverTerm(entry.Terms, term)));
        if (hasDirectCompositionEvidence)
        {
            return true;
        }

        if (!IsToolCompositionSurface(entry)
            || !HasToolLifecycleIntent(interpretation))
        {
            return false;
        }

        var matchedSubjectConcepts = interpretation.Terms
            .Where(term => !ToolCapabilityIntentTerms.Contains(term)
                && !ToolLifecycleIntentTerms.Contains(term)
                && CatalogTermsCoverTerm(entry.Terms, term))
            .Select(CanonicalCodeExploreTerm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return matchedSubjectConcepts >= 2;
    }

    private static bool HasToolLifecycleIntent(CodeExploreQueryInterpretation interpretation)
    {
        return interpretation.Terms.Any(ToolLifecycleIntentTerms.Contains);
    }

    private static bool HasToolCompositionContextIntent(CodeExploreQueryInterpretation interpretation)
    {
        return HasToolLifecycleIntent(interpretation)
            || interpretation.Terms.Any(ToolCompositionContextTerms.Contains);
    }

    private static bool IsRequestedToolCompositionEntry(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation,
        IReadOnlyList<string> requestedToolFamilies)
    {
        return IsToolCompositionSurface(entry)
            && HasToolCompositionContextIntent(interpretation)
            && requestedToolFamilies.Any(family =>
                entry.ToolCapability.Families.Contains(family, StringComparer.Ordinal));
    }

    private static bool IsToolCompositionEntry(CodeExploreDeclarationCatalogEntry entry)
    {
        if (entry.Kind.Equals("Field", StringComparison.OrdinalIgnoreCase)
            || entry.Kind.Equals("EnumMember", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var declaration = NormalizeComparableName(string.Join(
            ' ',
            entry.Name,
            entry.DisplayName,
            entry.ContainingType ?? string.Empty));
        var hasRegistrationName = declaration.Contains("register", StringComparison.Ordinal)
            || declaration.Contains("capabilities", StringComparison.Ordinal)
            || declaration.Contains("capability", StringComparison.Ordinal)
            || declaration.Contains("createtools", StringComparison.Ordinal)
            || declaration.Contains("semantictool", StringComparison.Ordinal);
        return IsToolCompositionSurface(entry) && hasRegistrationName;
    }

    private static bool IsToolCompositionSurface(CodeExploreDeclarationCatalogEntry entry)
    {
        var compositionSurface = NormalizeComparableName(string.Join(
            ' ',
            entry.RelativeFilePath,
            entry.ContainingType ?? string.Empty));
        return compositionSurface.Contains("composition", StringComparison.Ordinal)
            || compositionSurface.Contains("registration", StringComparison.Ordinal)
            || compositionSurface.Contains("bootstrap", StringComparison.Ordinal)
            || compositionSurface.Contains("startup", StringComparison.Ordinal)
            || compositionSurface.Contains("host", StringComparison.Ordinal);
    }

    private static bool IsToolConstructorEntry(CodeExploreDeclarationCatalogEntry entry)
    {
        return entry.Kind.Equals("Constructor", StringComparison.OrdinalIgnoreCase)
            && IsSemanticToolContainingType(entry);
    }

    private static bool IsPrivateImplementationHelper(CodeExploreDeclarationCatalogEntry entry)
    {
        if (entry.DeclaredAccessibility is Accessibility.Public
            or Accessibility.Protected
            or Accessibility.ProtectedOrInternal)
        {
            return false;
        }

        return entry.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase)
            || entry.Kind.Equals("Constructor", StringComparison.OrdinalIgnoreCase)
            || entry.Kind.Equals("LocalFunction", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSemanticToolContainingType(CodeExploreDeclarationCatalogEntry entry)
    {
        return entry.ToolCapability.ToolTypeName is not null;
    }

    private static bool IsTypeDeclarationKind(string kind)
    {
        return kind.Equals("Class", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Interface", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Struct", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Record", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("NamedType", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Delegate", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Enum", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SemanticToolNameContainsKnownFamily(string name)
    {
        return IndexedToolCapabilityFamilies.Any(family => ToolFamilyMatchesName(name, family));
    }

    private static bool CandidateMatchesToolCapabilityFamily(
        CodeExploreDeclarationCatalogEntry entry,
        string family)
    {
        return entry.ToolCapability.Families.Contains(family, StringComparer.Ordinal);
    }

    private static bool HasRequestedToolContractRoleTerm(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation,
        string family)
    {
        var familyConcepts = GetToolFamilyIdentityTerms(family)
            .Select(CanonicalCodeExploreTerm)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetCodeExploreTermCoverage(entry.NameTerms, interpretation.Terms)
            .Any(coverage => !familyConcepts.Contains(coverage.CanonicalTerm));
    }

    private static bool ToolFamilyMatchesName(string name, string family)
    {
        var terminalName = name
            .Split(['.', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? name;
        var comparableName = NormalizeComparableName(terminalName);
        var comparableFamily = NormalizeComparableName(string.Join('_', GetToolFamilyIdentityTerms(family)));
        return comparableFamily.Length > 0
            && (comparableName.Equals(comparableFamily, StringComparison.Ordinal)
                || comparableName.Equals(comparableFamily + "tool", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> GetToolFamilyIdentityTerms(string family)
    {
        return family
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term is not ("query" or "search"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsToolTypeName(string name)
    {
        var simpleName = name.Split(['.', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];
        var genericArity = simpleName.IndexOf('`', StringComparison.Ordinal);
        if (genericArity >= 0)
        {
            simpleName = simpleName[..genericArity];
        }

        return simpleName.EndsWith("Tool", StringComparison.Ordinal);
    }

    private static bool IsToolServiceContractName(string name)
    {
        var comparable = NormalizeComparableName(name);
        var hasServiceShape = comparable.EndsWith("service", StringComparison.Ordinal)
            || comparable.EndsWith("resolver", StringComparison.Ordinal)
            || comparable.EndsWith("registry", StringComparison.Ordinal)
            || comparable.EndsWith("engine", StringComparison.Ordinal);
        return hasServiceShape
            && (comparable.Contains("semantic", StringComparison.Ordinal)
                || comparable.Contains("codeexplore", StringComparison.Ordinal)
                || comparable.Contains("tool", StringComparison.Ordinal)
                || comparable.Contains("capability", StringComparison.Ordinal));
    }

    private static CodeExploreRankedCandidate? RankNaturalLanguageCandidate(
        CodeExploreRetrievedCandidate retrieved,
        CodeExploreQueryInterpretation interpretation,
        NaturalLanguageNameSegmentEvidence nameSegmentEvidence,
        CodeExploreNaturalLanguageIntent intent,
        bool usesSemanticToolProfile,
        IReadOnlyList<string> requestedToolFamilies,
        IReadOnlySet<string> qualifiedMemberTerms,
        IReadOnlyDictionary<string, int> termCounts,
        IReadOnlyDictionary<string, int> nameCounts,
        int catalogEntryCount)
    {
        var entry = retrieved.Entry;
        var reasons = retrieved.Reasons;
        var tier = retrieved.Tier;
        var score = retrieved.Score;
        foreach (var path in interpretation.PathLikeSpans.Where(IsCSharpPathSpan))
        {
            if (PathSpanMatchesEntry(path, entry))
            {
                reasons |= CodeExploreSelectionReason.Path;
                tier = MinTier(tier, CodeExploreCandidateTier.ExactQualified);
                score = Math.Max(score, 900);
            }
        }

        foreach (var qualified in interpretation.QualifiedNames)
        {
            if (QualifiedNameMatchesEntry(qualified, entry))
            {
                reasons |= CodeExploreSelectionReason.QualifiedName;
                tier = MinTier(tier, CodeExploreCandidateTier.ExactQualified);
                score = Math.Max(score, 850);
            }
        }

        foreach (var identifier in interpretation.ExactIdentifiers.Select(NormalizeComparableName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsQualifiedNameComponent(identifier, interpretation.QualifiedNames)
                && !interpretation.QualifiedNames.Any(qualified => QualifiedNameMatchesEntry(qualified, entry)))
            {
                continue;
            }

            if (IdentifierMatchesEntry(identifier, entry))
            {
                reasons |= CodeExploreSelectionReason.ExactIdentifier;
                var count = nameCounts.GetValueOrDefault(identifier);
                if (count is > 0 and <= 5)
                {
                    tier = MinTier(tier, CodeExploreCandidateTier.DistinctiveIdentifier);
                    score = Math.Max(score, 700);
                }
                else
                {
                    score = Math.Max(score, 220);
                }
            }
            else if (CalculateDistinctiveIdentifierPrefixScore(identifier, entry.Name) is var prefixScore
                && prefixScore > 0)
            {
                reasons |= CodeExploreSelectionReason.UserFocus;
                var prefixTier = prefixScore >= 500
                    ? CodeExploreCandidateTier.DistinctiveIdentifier
                    : CodeExploreCandidateTier.Peripheral;
                tier = MinTier(tier, prefixTier);
                score = Math.Max(score, prefixScore);
            }
        }

        nameSegmentEvidence.MatchesByIdentity.TryGetValue(entry.Identity.Id, out var nameSegmentMatch);
        var matchesQualifiedFocus = interpretation.QualifiedNames.Any(qualified =>
            QualifiedNameMatchesEntry(qualified, entry));
        var hasScopedMemberCollision = !matchesQualifiedFocus
            && nameSegmentMatch?.MatchedConcepts.Any(qualifiedMemberTerms.Contains) == true;
        var isCompoundNameMatch = nameSegmentMatch is not null
            && nameSegmentMatch.MatchedConcepts.Count >= 2;
        var coversDeclarationName = nameSegmentMatch?.MatchedConcepts.Count
            == nameSegmentMatch?.DeclarationSegmentCount;
        var coversQuerySubject = nameSegmentMatch is not null
            && (nameSegmentMatch.MatchedConcepts.Count == nameSegmentEvidence.ConceptCount
                || (nameSegmentEvidence.PrimaryConceptCount >= 2
                    && nameSegmentMatch.MatchedPrimaryConceptCount
                        == nameSegmentEvidence.PrimaryConceptCount));
        var isFocusedNameMatch = isCompoundNameMatch
            && !hasScopedMemberCollision
            && coversDeclarationName
            && coversQuerySubject;
        if (nameSegmentMatch?.IsStrong == true)
        {
            reasons |= CodeExploreSelectionReason.NameSegment | CodeExploreSelectionReason.MultiTerm;
            var nameSegmentTier = isFocusedNameMatch
                ? CodeExploreCandidateTier.DistinctiveIdentifier
                : CodeExploreCandidateTier.MultiTermStructural;
            tier = MinTier(tier, nameSegmentTier);
            var queryCoverageBaseScore = NaturalLanguageNameSegmentBaseScore
                * nameSegmentMatch.MatchedConcepts.Count
                / Math.Max(1, nameSegmentEvidence.ConceptCount);
            var matchedConceptScore = nameSegmentMatch.MatchedConcepts.Count
                * NaturalLanguageMaximumNameSegmentCoverageScore;
            var declarationCoverage = Math.Min(
                NaturalLanguageMaximumNameSegmentCoverageScore,
                matchedConceptScore / Math.Max(1, nameSegmentMatch.DeclarationSegmentCount));
            var nameSegmentScore = queryCoverageBaseScore
                + (nameSegmentMatch.MatchedConcepts.Count * NaturalLanguageNameSegmentConceptScore)
                + (nameSegmentMatch.MatchedPrimaryConceptCount * NaturalLanguagePrimaryNameSegmentConceptScore)
                + declarationCoverage;
            score = Math.Max(score, nameSegmentScore);
        }
        else if (nameSegmentMatch?.IsRareSingleTerm == true)
        {
            reasons |= CodeExploreSelectionReason.NameSegment;
            score = Math.Max(score, NaturalLanguageRareNameSegmentScore);
        }

        var matchesRequestedTool = (IsSemanticToolTypeEntry(entry)
                || IsSemanticToolDefinitionEntry(entry))
            && requestedToolFamilies.Any(family =>
                CandidateMatchesToolCapabilityFamily(entry, family));
        if (matchesRequestedTool)
        {
            reasons |= CodeExploreSelectionReason.UserFocus | CodeExploreSelectionReason.MultiTerm;
            tier = MinTier(tier, CodeExploreCandidateTier.DistinctiveIdentifier);
            var toolFamilyScore = 650;
            if (IsSemanticToolDefinitionEntry(entry))
            {
                toolFamilyScore += 350;
            }

            score = Math.Max(score, toolFamilyScore);
        }

        var matchesRequestedToolContractRole = IsSemanticToolDataContractEntry(entry)
            && requestedToolFamilies.Any(family =>
                CandidateMatchesToolCapabilityFamily(entry, family)
                && HasRequestedToolContractRoleTerm(entry, interpretation, family));
        if (matchesRequestedToolContractRole)
        {
            reasons |= CodeExploreSelectionReason.UserFocus | CodeExploreSelectionReason.MultiTerm;
            tier = MinTier(tier, CodeExploreCandidateTier.DistinctiveIdentifier);
            score = Math.Max(score, 600);
        }

        var coveredTerms = GetCodeExploreTermCoverage(entry.Terms, interpretation.Terms);
        var coveredNameTerms = GetCodeExploreTermCoverage(entry.NameTerms, interpretation.Terms);
        var coveredNameConcepts = coveredNameTerms
            .Select(coverage => coverage.CanonicalTerm)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredSignatureTerms = GetCodeExploreTermCoverage(entry.SignatureTerms, interpretation.Terms)
            .Where(coverage => !coveredNameConcepts.Contains(coverage.CanonicalTerm))
            .ToArray();
        var uncommonTermCount = coveredTerms.Count(coverage =>
            !IsCommonCodeExploreTerm(coverage.QueryTerm, termCounts, catalogEntryCount));
        var textChannelScore = (int)Math.Round(
            (coveredNameTerms.Sum(coverage => coverage.Strength) * 60)
            + (coveredSignatureTerms.Sum(coverage => coverage.Strength) * 10),
            MidpointRounding.AwayFromZero);
        if (coveredTerms.Length >= 2 && uncommonTermCount > 0)
        {
            reasons |= CodeExploreSelectionReason.MultiTerm;
            var averageCoverageStrength = coveredTerms.Average(coverage => coverage.Strength);
            textChannelScore += (int)Math.Round(
                (360 + (coveredTerms.Length * 70) + (uncommonTermCount * 50))
                * averageCoverageStrength,
                MidpointRounding.AwayFromZero);
        }
        else if (coveredTerms.Length == 1 && uncommonTermCount == 1)
        {
            textChannelScore += (int)Math.Round(
                120 * coveredTerms[0].Strength,
                MidpointRounding.AwayFromZero);
        }

        score = Math.Max(score, textChannelScore);
        if (coveredTerms.Length > 0)
        {
            var contextTermStrength = GetCodeExploreTermCoverage(entry.ContextTerms, interpretation.Terms)
                .Sum(coverage => coverage.Strength);
            score += Math.Min(
                45,
                (int)Math.Round(contextTermStrength * 15, MidpointRounding.AwayFromZero));
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
        }

        if (entry.IsGenerated)
        {
            reasons |= CodeExploreSelectionReason.Generated;
            if (HasGeneratedFocus(interpretation))
            {
                reasons |= CodeExploreSelectionReason.UserFocus;
                score += 140;
            }
        }

        if (HasKindFocus(entry, interpretation))
        {
            reasons |= CodeExploreSelectionReason.UserFocus;
            score += 80;
        }

        score += CalculateKindRelevanceAdjustment(entry, interpretation, coveredTerms.Length, reasons);
        var intentAdjustment = CalculateIntentRelevanceAdjustment(
            entry,
            interpretation,
            intent,
            usesSemanticToolProfile,
            coveredTerms.Length,
            reasons);
        score += intentAdjustment.Score;
        reasons |= intentAdjustment.Reasons;
        tier = MinTier(tier, intentAdjustment.Tier);
        score = CodeExploreRelevancePolicy.ApplyClassificationMultiplier(
            score,
            entry.IsTest,
            entry.IsGenerated,
            HasTestFocus(interpretation),
            HasGeneratedFocus(interpretation));

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
            CreateAmbiguityKey(entry),
            IsFocusedNameMatch: isFocusedNameMatch || matchesRequestedToolContractRole);
    }

    private static CodeExploreCandidateSummary CreateCandidateSummary(
        CodeExploreRankedCandidate candidate,
        int rank,
        bool selected,
        CodeExploreNaturalLanguageIntent intent)
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
            CreateNaturalLanguageSelectionReason(candidate, selected, intent),
            candidate.AmbiguityGroup);
    }

    private static CodeExploreResult CreateUnanchoredCodeExploreResult(
        AdvancedSemanticSnapshot snapshot,
        CodeExploreRequest request,
        CodeExploreQueryInterpretation interpretation,
        CodeExploreDiscoverySummary? discovery,
        IReadOnlyList<CodeExploreCandidateSummary> candidateSummaries,
        CodeExploreAdaptiveBudget adaptiveBudget)
    {
        var projectScoped = IsProjectScopedCodeExploreSnapshot(snapshot);
        var reason = projectScoped
            ? "Natural-language discovery did not find the requested declaration in the loaded project's source scope."
            : "Natural-language discovery did not find a compiler-known C# declaration or confined C# path; retry with a stable symbol id, exact C# symbol, or repository-relative C# path anchor.";
        var resolution = new CodeExploreAnchorResolution(
            request.Query,
            CodeExploreAnchorKind.Query,
            CodeExploreResolutionOutcome.NotFound,
            null,
            null,
            [],
            reason);
        var coverage = new CodeExploreCoverage(
            false,
            !projectScoped && snapshot.Confidence == SemanticConfidenceLevel.FullSemantic,
            false,
            true,
            [reason]);
        var availability = projectScoped
            ? CreateProjectScopedCodeExploreAvailability(
                snapshot,
                hasVisibleSource: false,
                canRefineAnchor: true)
            : new CodeExploreAvailability(
                CodeExploreAvailabilityStatus.NoMatchingDeclarations,
                "No compiler-known C# declaration or confined C# path matched the request.",
                true,
                snapshot.Confidence,
                SemanticConfidenceLevel.PartialCompilation,
                true,
                [new CodeExploreNextActionHint(
                    CodeExploreNextActionKind.RefineAnchor,
                    "Retry code_explore with an exact symbol name, stable symbol id, or repository-relative C# path anchor.")]);
        var allocation = new CodeExploreAllocationSummary(
            request.Limits.MaximumSourceCharacters,
            0,
            0,
            CreateAllocationBudgetSource(adaptiveBudget),
            []);
        var presentation = CreateCodeExplorePresentation(
            availability,
            [],
            [],
            [],
            coverage.Omissions,
            null,
            adaptiveBudget.PresentationVerbosity);
        return new CodeExploreResult(
            snapshot.Generation,
            snapshot.Confidence,
            [resolution],
            [],
            coverage,
            [reason],
            [],
            QueryInterpretation: interpretation,
            Discovery: discovery,
            CandidateSummaries: candidateSummaries,
            Allocation: allocation,
            Availability: availability,
            Presentation: presentation,
            AdaptiveBudget: adaptiveBudget,
            FileRelevance: []);
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

            if (NormalizeCodeExplorePathSpanToken(token) is { } normalizedPath)
            {
                paths.Add(normalizedPath);
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

    private static bool ShouldUseProjectScopeFallback(
        AdvancedSemanticSnapshot snapshot,
        IReadOnlyList<CodeExploreDeclarationCatalogEntry> entries,
        CodeExploreQueryInterpretation interpretation,
        NaturalLanguageNameSegmentEvidence nameSegmentEvidence,
        CodeExploreNaturalLanguageIntent intent)
    {
        if (!IsProjectScopedCodeExploreSnapshot(snapshot)
            || intent is not (CodeExploreNaturalLanguageIntent.Impact or CodeExploreNaturalLanguageIntent.Flow))
        {
            return false;
        }

        var exactIdentifierMatch = interpretation.ExactIdentifiers
            .Select(NormalizeComparableName)
            .Any(identifier => entries.Any(entry => IdentifierMatchesEntry(identifier, entry)));
        if (exactIdentifierMatch)
        {
            return false;
        }

        if (nameSegmentEvidence.PrimaryConceptCount > 0)
        {
            return !nameSegmentEvidence.HasPrimaryEvidence;
        }

        return nameSegmentEvidence.CanEvaluateStrongQueryEvidence
            && !nameSegmentEvidence.HasStrongCoOccurrence;
    }

    private static CodeExploreNaturalLanguageIntent ClassifyNaturalLanguageIntent(
        CodeExploreRequest request,
        CodeExploreQueryInterpretation interpretation)
    {
        if (request.Mode == CodeExploreMode.Impact)
        {
            return CodeExploreNaturalLanguageIntent.Impact;
        }

        if (request.Mode == CodeExploreMode.Flow)
        {
            return CodeExploreNaturalLanguageIntent.Flow;
        }

        var relationshipQuery = RemoveKnownSemanticToolIds(request.Query);
        var relationshipAnalysis = CodeExploreQueryIntentPolicy.Analyze(relationshipQuery);
        if (relationshipAnalysis.Intent == CodeExploreRelationshipIntent.Flow)
        {
            return CodeExploreNaturalLanguageIntent.Flow;
        }

        if (relationshipAnalysis.Intent == CodeExploreRelationshipIntent.Impact)
        {
            return CodeExploreNaturalLanguageIntent.Impact;
        }

        if (HasToolCapabilityExplanationFocus(interpretation))
        {
            return CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation;
        }

        if (interpretation.PathLikeSpans.Count > 0
            || interpretation.StableSymbolIds.Count > 0
            || interpretation.QualifiedNames.Count > 0)
        {
            return CodeExploreNaturalLanguageIntent.Exact;
        }

        return CodeExploreNaturalLanguageIntent.Survey;
    }

    private static string RemoveKnownSemanticToolIds(string query)
    {
        var result = query;
        foreach (var toolId in SemanticToolIdFamilies.Keys)
        {
            result = result.Replace(toolId, " ", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static bool HasToolCapabilityExplanationFocus(CodeExploreQueryInterpretation interpretation)
    {
        var termSet = interpretation.Terms.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exactSet = interpretation.ExactIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capabilityTermCount = termSet.Count(ToolCapabilityIntentTerms.Contains);
        var familyCount = CountMentionedToolCapabilityFamilies(interpretation);
        var hasKnownToolId = MentionsKnownSemanticToolId(interpretation);
        var hasExplanationVerb = termSet.Contains("explain")
            || termSet.Contains("help")
            || termSet.Contains("improve")
            || termSet.Contains("efficient")
            || termSet.Contains("efficiency")
            || termSet.Contains("workflow")
            || termSet.Contains("workflows")
            || termSet.Contains("capability")
            || termSet.Contains("capabilities")
            || termSet.Contains("agentic");
        var hasExplicitToolCapabilityContext = termSet.Contains("tool")
            || termSet.Contains("tools")
            || termSet.Contains("capability")
            || termSet.Contains("capabilities")
            || termSet.Contains("agentic")
            || termSet.Contains("workflow")
            || termSet.Contains("workflows")
            || exactSet.Any(identifier => identifier.Contains("Tool", StringComparison.Ordinal));
        var isDirectKnownToolLookup = hasKnownToolId
            && exactSet.Count > 0
            && termSet.All(SemanticToolIdentityTerms.Contains);
        return isDirectKnownToolLookup
            || (hasKnownToolId && familyCount >= 2)
            || (hasKnownToolId && hasExplanationVerb)
            || (hasExplicitToolCapabilityContext && familyCount >= 2)
            || (hasExplicitToolCapabilityContext && hasExplanationVerb)
            || (hasExplicitToolCapabilityContext && capabilityTermCount >= 2);
    }

    private static int CountMentionedToolCapabilityFamilies(CodeExploreQueryInterpretation interpretation)
    {
        return GetMentionedToolCapabilityFamilies(interpretation).Count;
    }

    private static IReadOnlyList<string> GetMentionedToolCapabilityFamilies(
        CodeExploreQueryInterpretation interpretation)
    {
        var families = new List<string>();
        var semanticToolIdFamilies = GetMentionedSemanticToolIdFamilies(interpretation);
        foreach (var family in semanticToolIdFamilies)
        {
            AddMentionedToolCapabilityFamily(families, family, isMentioned: true);
        }

        if (semanticToolIdFamilies.Count > 0)
        {
            AddStronglyMentionedToolCapabilityFamilies(families, interpretation);
            var hasExplicitCompilerToolFocus = interpretation.Terms.Any(term =>
                term is "compiler" or "diagnostic" or "diagnostics");
            AddMentionedToolCapabilityFamily(families, CSharpPatternSearchToolFamily, hasExplicitCompilerToolFocus);
            AddMentionedToolCapabilityFamily(families, GeneratedCodeToolFamily, hasExplicitCompilerToolFocus);
            return families;
        }

        var hasCodeExploreFamily = HasAnyTermPair(interpretation, "code", "explore")
            || interpretation.ExactIdentifiers.Any(identifier => identifier.Contains("code_explore", StringComparison.OrdinalIgnoreCase)
                || identifier.Contains("CodeExplore", StringComparison.Ordinal));
        AddMentionedToolCapabilityFamily(families, CodeExploreToolFamily, hasCodeExploreFamily);
        AddMentionedToolCapabilityFamily(families, FindSymbolToolFamily, interpretation.Terms.Any(term => term is "symbol" or "symbols" or "lookup" or "declaration" or "declarations"));
        AddMentionedToolCapabilityFamily(families, FindReferencesToolFamily, interpretation.Terms.Any(term => term is "reference" or "references" or "usage" or "usages"));
        AddMentionedToolCapabilityFamily(families, FindImplementationsToolFamily, interpretation.Terms.Any(term => term is "implementation" or "implementations" or "override" or "overrides" or "derived"));
        AddMentionedToolCapabilityFamily(families, SymbolImpactToolFamily, interpretation.Terms.Any(term => term is "impact" or "blast" or "radius" or "affected" or "dependent" or "dependents"));
        AddMentionedToolCapabilityFamily(families, CallHierarchyToolFamily, interpretation.Terms.Any(term => term is "call" or "calls" or "caller" or "callers" or "hierarchy" or "flow"));
        var hasCompilerToolFocus = interpretation.Terms.Any(term => term is "compiler" or "diagnostic" or "diagnostics");
        AddMentionedToolCapabilityFamily(families, CSharpPatternSearchToolFamily, hasCompilerToolFocus);
        AddMentionedToolCapabilityFamily(families, GeneratedCodeToolFamily, hasCompilerToolFocus);
        return families;
    }

    private static void AddStronglyMentionedToolCapabilityFamilies(
        List<string> families,
        CodeExploreQueryInterpretation interpretation)
    {
        var hasSymbolFamily = HasAnyTermPair(interpretation, "symbol", "lookup")
            || HasAnyTermPair(interpretation, "symbol", "declaration");
        var hasImplementationFamily = HasAnyTermPair(interpretation, "interface", "implementation")
            || HasAnyTermPair(interpretation, "implementation", "override")
            || HasAnyTermPair(interpretation, "derived", "override");
        var hasImpactFamily = HasAnyTermPair(interpretation, "impact", "analysis")
            || HasAnyTermPair(interpretation, "blast", "radius");
        AddMentionedToolCapabilityFamily(
            families,
            CodeExploreToolFamily,
            HasAnyTermPair(interpretation, "code", "explore"));
        AddMentionedToolCapabilityFamily(
            families,
            FindSymbolToolFamily,
            hasSymbolFamily);
        AddMentionedToolCapabilityFamily(
            families,
            FindReferencesToolFamily,
            HasAnyTermPair(interpretation, "reference", "usage"));
        AddMentionedToolCapabilityFamily(
            families,
            FindImplementationsToolFamily,
            hasImplementationFamily);
        AddMentionedToolCapabilityFamily(
            families,
            SymbolImpactToolFamily,
            hasImpactFamily);
        AddMentionedToolCapabilityFamily(
            families,
            CallHierarchyToolFamily,
            HasAnyTermPair(interpretation, "call", "hierarchy"));
    }

    private static bool ShouldExpandSemanticToolSurvey(CodeExploreQueryInterpretation interpretation)
    {
        var hasSemanticFocus = interpretation.Terms.Any(term => term is "semantic" or "semantics");
        return hasSemanticFocus
            && !HasDistinctiveExactSymbolFocus(interpretation)
            && (!MentionsKnownSemanticToolId(interpretation)
                || interpretation.Terms.Contains("tools", StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasDistinctiveExactSymbolFocus(CodeExploreQueryInterpretation interpretation)
    {
        return interpretation.ExactIdentifiers.Any(identifier =>
            !ToolCapabilityIntentTerms.Contains(identifier)
            && !SemanticToolIdFamilies.ContainsKey(identifier)
            && !SemanticToolNameContainsKnownFamily(identifier));
    }

    private static bool MentionsKnownSemanticToolId(CodeExploreQueryInterpretation interpretation)
    {
        return GetMentionedSemanticToolIdFamilies(interpretation).Count > 0;
    }

    private static IReadOnlyList<string> GetMentionedSemanticToolIdFamilies(
        CodeExploreQueryInterpretation interpretation)
    {
        return interpretation.ExactIdentifiers
            .Concat(interpretation.Terms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(token => SemanticToolIdFamilies.GetValueOrDefault(token))
            .Where(static family => family is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddMentionedToolCapabilityFamily(
        List<string> families,
        string family,
        bool isMentioned)
    {
        if (isMentioned && !families.Contains(family, StringComparer.Ordinal))
        {
            families.Add(family);
        }
    }

    private static bool HasAnyTermPair(
        CodeExploreQueryInterpretation interpretation,
        string first,
        string second)
    {
        return HasCodeExploreTermConcept(interpretation, first)
            && HasCodeExploreTermConcept(interpretation, second);
    }

    private static bool HasCodeExploreTermConcept(
        CodeExploreQueryInterpretation interpretation,
        string expected)
    {
        var expectedVariants = CreateCodeExploreTermVariants(expected);
        return interpretation.Terms.Any(term => CreateCodeExploreTermVariants(term)
            .Any(variant => expectedVariants.Contains(variant, StringComparer.OrdinalIgnoreCase)));
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
            && (value[1..].Any(char.IsUpper)
                || value.Any(char.IsDigit)
                || value.Contains('_', StringComparison.Ordinal));
    }

    private static bool IsQualifiedIdentifierName(string value)
    {
        var normalized = value.StartsWith(GlobalNamespaceAlias, StringComparison.Ordinal)
            ? value[GlobalNamespaceAlias.Length..]
            : value;
        return normalized.Contains('.')
            && normalized.Split('.', StringSplitOptions.RemoveEmptyEntries).All(part => SyntaxFacts.IsValidIdentifier(part));
    }

    private static string CreateCodeExploreCatalogKey(WorkspaceId workspaceId, long generation)
    {
        return $"{workspaceId.Value:D}:{generation}";
    }

    private static string NormalizeComparableName(string value)
    {
        var normalized = NormalizeSymbolAnchor(value)
            .Replace(GlobalNamespaceAlias, string.Empty, StringComparison.Ordinal)
            .Replace('+', '.')
            .Trim();
        return normalized.ToLowerInvariant();
    }

    private static void AddCodeExploreTerms(HashSet<string> terms, string value)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCodeExploreTerms(terms, ignored, value);
    }

    private static HashSet<string> CreateCanonicalCodeExploreTermSet(IEnumerable<string> values)
    {
        var literalTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            AddCodeExploreTerms(literalTerms, value);
        }

        return literalTerms
            .Select(CanonicalCodeExploreTerm)
            .Where(term => term.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                if (term.Length < CodeExploreTermNormalizer.MinimumTermLength)
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
        terms.Add(term);
    }

    private static IReadOnlyList<string> CreateCodeExploreTermVariants(string term)
    {
        return CodeExploreTermNormalizer.CreateVariants(term);
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

            if (part.Length > CodeExploreTermNormalizer.MinimumTermLength
                && part.EndsWith('s')
                && part[..^1].All(char.IsUpper))
            {
                yield return part;
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

    private static string? NormalizeCodeExplorePathSpanToken(string value)
    {
        var token = TrimCodeExploreToken(value);
        var labelSeparator = token.IndexOf(':');
        if (labelSeparator > 0)
        {
            var label = token[..labelSeparator];
            if (label.Equals("path", StringComparison.OrdinalIgnoreCase)
                || label.Equals("file", StringComparison.OrdinalIgnoreCase)
                || label.Equals("source", StringComparison.OrdinalIgnoreCase))
            {
                token = token[(labelSeparator + 1)..];
            }
        }

        var extensionIndex = token.LastIndexOf(".cs", StringComparison.OrdinalIgnoreCase);
        if (extensionIndex >= 0)
        {
            var suffixStart = extensionIndex + ".cs".Length;
            if (suffixStart < token.Length)
            {
                var suffix = token[suffixStart..];
                if (suffix.StartsWith(":", StringComparison.Ordinal)
                    && suffix[1..].All(char.IsDigit))
                {
                    token = token[..suffixStart];
                }
                else if (suffix.StartsWith("#L", StringComparison.OrdinalIgnoreCase)
                    && suffix[2..].All(char.IsDigit))
                {
                    token = token[..suffixStart];
                }
            }
        }

        return IsCSharpPathSpan(token) ? token : null;
    }

    private static bool CandidateCoversTerm(CodeExploreDeclarationCatalogEntry entry, string term)
    {
        return CatalogTermsCoverTerm(entry.Terms, term);
    }

    private static CodeExploreTermCoverage[] GetCodeExploreTermCoverage(
        IReadOnlyList<string> catalogTerms,
        IReadOnlyList<string> queryTerms)
    {
        return
        [
            .. queryTerms
                .Select(term => new CodeExploreTermCoverage(
                    term,
                    CanonicalCodeExploreTerm(term),
                    GetCatalogTermMatchStrength(catalogTerms, term)))
                .Where(coverage => coverage.Strength > 0)
                .GroupBy(coverage => coverage.CanonicalTerm, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(coverage => coverage.Strength)
                    .ThenBy(coverage => coverage.QueryTerm, StringComparer.OrdinalIgnoreCase)
                    .First()),
        ];
    }

    private static bool CatalogTermsCoverTerm(IReadOnlyList<string> catalogTerms, string term)
    {
        return GetCatalogTermMatchStrength(catalogTerms, term) > 0;
    }

    private static double GetCatalogTermMatchStrength(
        IReadOnlyList<string> catalogTerms,
        string term)
    {
        return CodeExploreTermNormalizer.CreateWeightedVariants(term)
            .Select(variant => GetCatalogVariantMatchStrength(catalogTerms, variant))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static double GetCatalogVariantMatchStrength(
        IReadOnlyList<string> catalogTerms,
        CodeExploreTermVariant variant)
    {
        var baseStrength = variant.Kind switch
        {
            CodeExploreTermVariantKind.Literal => 1.0,
            CodeExploreTermVariantKind.Stem => CodeExploreRelevancePolicy.StemMatchMultiplier,
            CodeExploreTermVariantKind.Alias => CodeExploreRelevancePolicy.AliasMatchMultiplier,
            _ => 0,
        };
        if (baseStrength == 0)
        {
            return 0;
        }

        if (catalogTerms.Contains(variant.Value, StringComparer.OrdinalIgnoreCase))
        {
            return baseStrength;
        }

        return variant.Value.Length >= CodeExploreCandidateRetriever.MinimumPrefixTermLength
            && catalogTerms.Any(catalogTerm => catalogTerm.StartsWith(
                variant.Value,
                StringComparison.OrdinalIgnoreCase))
                    ? baseStrength * CodeExploreRelevancePolicy.PrefixCoverageMultiplier
                    : 0;
    }

    private static string CanonicalCodeExploreTerm(string term)
    {
        return CodeExploreTermNormalizer.TryCreateCanonicalTerm(term, out var canonicalTerm)
            ? canonicalTerm
            : term;
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

    private static int CalculateDistinctiveIdentifierPrefixScore(string identifier, string declarationName)
    {
        var identifierKey = CreateIdentifierSearchKey(identifier);
        var declarationKey = CreateIdentifierSearchKey(declarationName);
        if (identifierKey.Length < CodeExploreCandidateRetriever.MinimumFuzzyNameLength
            || declarationKey.Length <= identifierKey.Length
            || !declarationKey.StartsWith(identifierKey, StringComparison.Ordinal))
        {
            return 0;
        }

        return identifier.Contains('_', StringComparison.Ordinal)
            && declarationKey.Equals(identifierKey + "tool", StringComparison.Ordinal)
            ? 800
            : 150;
    }

    private static bool QualifiedNameMatchesEntry(
        string qualifiedName,
        CodeExploreDeclarationCatalogEntry entry)
    {
        return entry.QualifiedNames.Any(name =>
            string.Equals(name, qualifiedName, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith('.' + qualifiedName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsQualifiedNameComponent(
        string identifier,
        IReadOnlyList<string> qualifiedNames)
    {
        return qualifiedNames.Any(qualifiedName => qualifiedName
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(component => string.Equals(component, identifier, StringComparison.OrdinalIgnoreCase)));
    }

    private static string CreateIdentifierSearchKey(string value)
    {
        return string.Concat(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));
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

        var directConcepts = interpretation.Terms
            .Where(term => CatalogTermsCoverTerm(entry.NameTerms, term))
            .Select(CanonicalCodeExploreTerm)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containerTermArray = containerTerms.ToArray();
        var containerConcepts = interpretation.Terms
            .Where(term => CatalogTermsCoverTerm(containerTermArray, term))
            .Select(CanonicalCodeExploreTerm)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return directConcepts.Count > 0
            && containerConcepts.Except(directConcepts, StringComparer.OrdinalIgnoreCase).Any();
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

    private static int CalculateKindRelevanceAdjustment(
        CodeExploreDeclarationCatalogEntry entry,
        CodeExploreQueryInterpretation interpretation,
        int coveredTermCount,
        CodeExploreSelectionReason reasons)
    {
        var kind = entry.Kind.ToLowerInvariant();
        var behaviorFocused = HasBehaviorFocus(interpretation);
        var explicitKindFocus = HasKindFocus(entry, interpretation);
        var baseAdjustment = kind switch
        {
            "method" or "constructor" or "localfunction" => behaviorFocused ? 150 : 110,
            "class" or "record" or "struct" or "interface" or "namedtype" or "delegate" => 90,
            "property" or "event" => explicitKindFocus || coveredTermCount > 1 ? 35 : 5,
            "enum" => explicitKindFocus ? 60 : 20,
            "field" => explicitKindFocus || (reasons & CodeExploreSelectionReason.QualifiedName) != 0 ? 20 : -45,
            "enummember" => explicitKindFocus ? 10 : -70,
            _ => 0,
        };
        if ((reasons & (CodeExploreSelectionReason.QualifiedName | CodeExploreSelectionReason.Pinned)) != 0)
        {
            baseAdjustment += 60;
        }

        return baseAdjustment;
    }

    private static bool IsWeakLowSignalCandidate(
        CodeExploreDeclarationCatalogEntry entry,
        int coveredTermCount,
        CodeExploreSelectionReason reasons)
    {
        if ((reasons & (CodeExploreSelectionReason.Pinned
            | CodeExploreSelectionReason.ExactIdentifier
            | CodeExploreSelectionReason.QualifiedName
            | CodeExploreSelectionReason.MultiTerm
            | CodeExploreSelectionReason.CoLocated
            | CodeExploreSelectionReason.GraphConnected)) != 0)
        {
            return false;
        }

        var kind = entry.Kind.ToLowerInvariant();
        return coveredTermCount <= 1
            && (kind is "field" or "property" or "event" or "enummember");
    }

    private static bool HasBehaviorFocus(CodeExploreQueryInterpretation interpretation)
    {
        return interpretation.Terms.Any(term => term is "flow" or "call" or "caller" or "callee" or "dispatch" or "execute" or "execution" or "behavior" or "behaviour" or "implementation" or "handle" or "process");
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
        bool selected,
        CodeExploreNaturalLanguageIntent intent)
    {
        var prefix = selected ? "Selected" : "Retained";
        if (HasExactCandidateReason(candidate.Reasons))
        {
            return $"{prefix} because the query explicitly identifies this declaration.";
        }

        if (intent == CodeExploreNaturalLanguageIntent.ToolCapabilityExplanation)
        {
            if (IsSemanticToolDefinitionEntry(candidate.Entry))
            {
                return $"{prefix} as a model-facing semantic tool definition.";
            }

            if (IsSemanticToolTypeEntry(candidate.Entry))
            {
                return $"{prefix} as a public semantic tool implementation.";
            }

            if (IsToolCompositionEntry(candidate.Entry))
            {
                return $"{prefix} to show where semantic tools are composed for agent use.";
            }

            if (IsSemanticServiceContractEntry(candidate.Entry))
            {
                return $"{prefix} as a public semantic exploration service contract.";
            }
        }

        return $"{prefix} because it {FormatSelectionReasons(candidate.Reasons)}.";
    }

    private static string FormatSelectionReasons(CodeExploreSelectionReason reasons)
    {
        var descriptions = new List<string>();
        if (reasons.HasFlag(CodeExploreSelectionReason.NameSegment))
        {
            descriptions.Add(reasons.HasFlag(CodeExploreSelectionReason.MultiTerm)
                ? "matches multiple query concepts in its declaration name"
                : "matches a catalog-verified declaration-name segment");
        }
        else if (reasons.HasFlag(CodeExploreSelectionReason.MultiTerm))
        {
            descriptions.Add("matches multiple query terms");
        }

        if (reasons.HasFlag(CodeExploreSelectionReason.ContainingType))
        {
            descriptions.Add("is corroborated by its containing type");
        }

        if (reasons.HasFlag(CodeExploreSelectionReason.UserFocus))
        {
            descriptions.Add("matches the requested focus");
        }

        if (reasons.HasFlag(CodeExploreSelectionReason.GraphConnected))
        {
            descriptions.Add("is connected by compiler-known relationships");
        }

        return descriptions.Count == 0
            ? "matches the natural-language question"
            : string.Join(" and ", descriptions);
    }

    private static int ResolveNaturalLanguageCandidateSummaryLimit(
        int maximumSourceCharacters,
        int pinnedSummaryCount,
        int selectedSummaryCount)
    {
        var budgetedSummaries = Math.Max(1, maximumSourceCharacters / 512);
        var budgetedNonPinnedSummaries = Math.Max(
            0,
            Math.Min(MaximumNaturalLanguageCandidateSummaries, budgetedSummaries) - pinnedSummaryCount);
        return Math.Max(selectedSummaryCount, budgetedNonPinnedSummaries);
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
        var normalizedPath = relativePath.Replace('\\', '/');
        var pathSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Take(Math.Max(0, pathSegments.Length - 1)).Any(IsTestPathSegment))
        {
            return true;
        }

        var fileStem = Path.GetFileNameWithoutExtension(normalizedPath);
        return IsTestName(projectName) || IsTestFileStem(fileStem);
    }

    private static bool IsTestPathSegment(string segment)
    {
        return TestPathSegments.Contains(segment) || HasPascalCaseTestSuffix(segment);
    }

    private static bool IsTestFileStem(string fileStem)
    {
        if (fileStem.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
            || fileStem.StartsWith("test.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasDelimitedTestToken(fileStem) || HasPascalCaseTestSuffix(fileStem);
    }

    private static bool IsTestName(string name)
    {
        return name
                .Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Any(TestNameTokens.Contains)
            || HasPascalCaseTestSuffix(name);
    }

    private static bool HasDelimitedTestToken(string name)
    {
        return TestNameTokens.Any(token =>
            name.EndsWith('.' + token, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith('-' + token, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith('_' + token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasPascalCaseTestSuffix(string name)
    {
        return TestNameSuffixes.Any(suffix =>
            name.Length > suffix.Length
            && name.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static bool IsTestSourceCandidate(CodeExploreSectionCandidate candidate)
    {
        return IsTestProjectNameOrPath(
            candidate.Location?.ProjectName ?? string.Empty,
            candidate.FilePath);
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
        var sourceImportance = ResolveAnchorSourceImportance(anchor);
        foreach (var symbol in resolved.Group.Symbols)
        {
            await AddSymbolSourceCandidatesAsync(
                snapshot,
                projection,
                sourceReader,
                symbol,
                anchor,
                NaturalLanguageAnchorSourceReason,
                sourceImportance,
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
        var sourceImportance = ResolveAnchorSourceImportance(anchor);
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
                        sourceImportance,
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
                sourceText.FileSha256,
                CodeExploreSourceImportance.Pinned));
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
        CodeExploreSourceImportance importance,
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
            var locatedDocument = await FindDocumentForSyntaxTreeAsync(
                snapshot,
                declaration.SyntaxTree,
                cancellationToken);
            if (locatedDocument is null)
            {
                continue;
            }

            var document = locatedDocument.Document;
            var location = ToCodeExploreLocation(
                CreateDocumentLocation(
                    document,
                    declaration.SyntaxTree,
                    declaration.Span,
                    projection,
                    locatedDocument.IsSourceGenerated),
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
                null,
                importance,
                importance == CodeExploreSourceImportance.FlowSpine));
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

            var locatedDocument = await FindDocumentForSyntaxTreeAsync(
                snapshot,
                location.SourceTree,
                cancellationToken);
            candidates.Add(new CodeExploreSectionCandidate(
                locatedDocument?.Document,
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
                null,
                importance,
                importance == CodeExploreSourceImportance.FlowSpine));
        }
    }

    private static CodeExploreSourceImportance ResolveAnchorSourceImportance(CodeExploreAnchor anchor)
    {
        return anchor.AllocationRank.HasValue
            ? CodeExploreSourceImportance.Named
            : CodeExploreSourceImportance.Pinned;
    }

    private static async Task AddNaturalLanguageSourceCompanionAsync(
        AdvancedSemanticSnapshot snapshot,
        SemanticSourceProjection projection,
        ICodeExploreSourceReader sourceReader,
        CodeExploreRankedCandidate companion,
        int allocationRank,
        List<CodeExploreSectionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var groups = await ResolveSymbolGroupsInSnapshotAsync(
            snapshot,
            companion.Entry.Identity.Id,
            [],
            cancellationToken);
        var anchor = new CodeExploreAnchor(
            CodeExploreAnchorKind.SymbolId,
            companion.Entry.Identity.Id,
            null,
            null,
            false,
            CodeExplorePathSelectionMode.Auto,
            null,
            null,
            allocationRank);
        foreach (var symbol in groups
            .SelectMany(group => group.Symbols)
            .Where(HasSourceEvidence)
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(symbol => CreateIdentity(symbol).Id, StringComparer.Ordinal))
        {
            await AddSymbolSourceCandidatesAsync(
                snapshot,
                projection,
                sourceReader,
                symbol,
                anchor,
                NaturalLanguageCompanionSourceReason,
                CodeExploreSourceImportance.Supporting,
                1,
                candidates,
                cancellationToken);
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
            null,
            CodeExploreSourceImportance.Pinned);
    }

    private static async Task<CodeExplorePriorCoverage> TryCreateCodeExploreBackReferenceAsync(
        WorkspaceId workspaceId,
        AdvancedSemanticSnapshot snapshot,
        ICodeExploreSourceReader sourceReader,
        ModelVisibleSourceFrontier? visibleSourceFrontier,
        CodeExploreSectionCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (visibleSourceFrontier is null || visibleSourceFrontier.Entries.Count == 0)
        {
            return new(null, 0, null);
        }

        var relativePath = ToRepositoryRelativePath(candidate.FilePath, snapshot.RepositoryPath);
        if (!IsSameRepositoryPath(visibleSourceFrontier.RepositoryPath, snapshot.RepositoryPath)
            || (visibleSourceFrontier.WorkspaceId is not null && visibleSourceFrontier.WorkspaceId != workspaceId))
        {
            return new(null, 0, "Visible source coverage belonged to a different repository or workspace; source was not suppressed.");
        }

        if (candidate.ExpectedWorkspaceGeneration is { } expectedGeneration && expectedGeneration != snapshot.Generation)
        {
            return new(null, 0, "The candidate expected a different workspace generation; source was not suppressed and ordinary continuation checks handled the candidate.");
        }

        if (!Path.IsPathRooted(candidate.FilePath) || !sourceReader.IsPathAllowed(candidate.FilePath))
        {
            return new(null, 0, null);
        }

        var text = candidate.PreloadedText
            ?? (candidate.Document is null
                ? await ReadSourceTextFromFileAsync(sourceReader, candidate.FilePath, cancellationToken)
                : await candidate.Document.GetTextAsync(cancellationToken));
        var span = candidate.Span ?? CreateFileOrLineSpan(text, candidate.PreferredLine, candidate.EndLine, candidate.StartAtLine, candidate.SelectionMode);
        if (span is null || span.Value.Length < MinimumDedupSourceCharacters)
        {
            return new(null, 0, "The candidate range was too small or uncertain for code_explore source back-reference suppression.");
        }

        var fileIdentity = candidate.Document is null && candidate.PreloadedFileSha256 is not null
            ? (FileSha256: candidate.PreloadedFileSha256, DriftReason: (string?)null)
            : await VerifyCurrentFileIdentityAsync(
                sourceReader,
                candidate.FilePath,
                text,
                cancellationToken);
        if (fileIdentity.DriftReason is not null || fileIdentity.FileSha256 is null)
        {
            return new(null, 0, "Current file identity could not be proven; source was not suppressed and ordinary drift checks handled the candidate.");
        }

        var safeStart = Math.Min(span.Value.Start, text.Length);
        var safeEnd = Math.Min(span.Value.End, text.Length);
        var startLineIndex = text.Lines.GetLineFromPosition(safeStart).LineNumber;
        var endPosition = Math.Max(safeEnd - 1, safeStart);
        var endLineIndex = text.Lines.GetLineFromPosition(Math.Min(endPosition, text.Length)).LineNumber;
        var range = new SourceRange(
            startLineIndex + 1,
            1,
            endLineIndex + 1,
            text.Lines[endLineIndex].ToString().Length + 1);
        var matching = visibleSourceFrontier.Entries
            .Where(entry => string.Equals(entry.FilePath, relativePath, StringComparison.OrdinalIgnoreCase)
                && entry.WorkspaceGeneration == snapshot.Generation
                && string.Equals(entry.FileSha256, fileIdentity.FileSha256, StringComparison.OrdinalIgnoreCase)
                && ContainsRange(entry.Range, range))
            .OrderBy(entry => entry.HolderId, StringComparer.Ordinal)
            .ThenBy(entry => entry.ToolCallId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (matching is null)
        {
            return new(null, 0, "No exact unchanged visible source coverage matched the candidate range; source was not suppressed.");
        }

        var backReference = new CodeExploreBackReference(
            matching.HolderId,
            matching.ToolCallId,
            relativePath,
            range,
            fileIdentity.FileSha256,
            ComputeLineRangeSha256(text, startLineIndex, endLineIndex),
            CreateSectionIdentities(candidate).Select(identity => identity.Id).ToArray(),
            $"Unchanged source for {relativePath} L{range.StartLine}-L{range.EndLine} is already visible in the current request from tool result {matching.ToolCallId}; use that exact prior source instead of treating this as a whole-file reference.");
        var candidateSourceCharacters = CountProjectedNumberedCharacters(text, startLineIndex, endLineIndex);
        if (GetSerializedByteCount(backReference) >= candidateSourceCharacters)
        {
            return new(null, 0, "The compact back-reference would not save context compared with re-emitting source.");
        }

        return new(backReference, candidateSourceCharacters, null);
    }

    private static bool IsSameRepositoryPath(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsRange(SourceRange covering, SourceRange requested)
    {
        return covering.StartLine <= requested.StartLine
            && covering.EndLine >= requested.EndLine;
    }

    private static int GetSerializedByteCount(CodeExploreBackReference backReference)
    {
        return JsonSerializer.SerializeToUtf8Bytes(backReference).Length;
    }

    private static string ComputeLineRangeSha256(
        SourceText text,
        int startLineIndex,
        int endLineIndex)
    {
        var returnedSpan = TextSpan.FromBounds(
            text.Lines[startLineIndex].Start,
            text.Lines[endLineIndex].EndIncludingLineBreak);
        return ComputeSha256(text.GetSubText(returnedSpan).ToString());
    }

    private static int CountProjectedNumberedCharacters(
        SourceText text,
        int startLineIndex,
        int endLineIndex)
    {
        var characters = 0;
        for (var index = startLineIndex; index <= endLineIndex; index++)
        {
            var numbered = $"{index + 1}: {text.Lines[index]}";
            characters += numbered.Length;
            if (index > startLineIndex)
            {
                characters += Environment.NewLine.Length;
            }
        }

        return characters;
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
        return candidate.Identity is null
            ? candidate.AdditionalIdentities
            : candidate.AdditionalIdentities
                .Prepend(candidate.Identity)
                .DistinctBy(identity => identity.Id, StringComparer.Ordinal)
                .ToArray();
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
        return LooksLikeDocumentationId(query)
            || CSharpPatternConstraints.IsValidDottedIdentifierName(StripParameters(query));
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
        var normalized = value.Trim().Replace(GlobalNamespaceAlias, string.Empty, StringComparison.Ordinal).Replace('+', '.');
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
        var start = candidate.Span?.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? range?.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? candidate.PreferredLine?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        var end = candidate.Span?.End.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? range?.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? candidate.EndLine?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        return string.Join(
            '|',
            CreateSourceAllocationKey(candidate),
            candidate.Location?.ProjectName ?? string.Empty,
            candidate.Location?.TargetFramework ?? string.Empty,
            start,
            end);
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
            if (!CSharpPatternConstraints.AllowedModifiers.Contains(value))
            {
                throw new ArgumentException($"Unsupported C# modifier '{value}'.", nameof(request));
            }
        }

        foreach (var value in new[] { request.Pattern.Name, request.Pattern.ContainingType, request.Pattern.Capture }
            .Concat(requiredAttributes))
        {
            if (value is { Length: > CSharpPatternConstraints.MaximumNameCharacters }
                || (value is not null && !CSharpPatternConstraints.IsValidDottedIdentifierName(value)))
            {
                throw new ArgumentException("Pattern names must be bounded C# identifiers.", nameof(request));
            }
        }

        if (requiredModifiers.Count > CSharpPatternConstraints.MaximumPredicateValues
            || requiredAttributes.Count > CSharpPatternConstraints.MaximumPredicateValues)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Pattern predicate counts exceed host limits.");
        }
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

        var locatedDocument = await FindDocumentForSyntaxTreeAsync(
            snapshot,
            location.SourceTree,
            cancellationToken);
        return locatedDocument is null
            ? null
            : CreateDocumentLocation(
                locatedDocument.Document,
                location.SourceTree,
                location.SourceSpan,
                projection,
                locatedDocument.IsSourceGenerated);
    }

    private static async Task<LocatedSemanticDocument?> FindDocumentForSyntaxTreeAsync(
        AdvancedSemanticSnapshot snapshot,
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        var document = snapshot.Solution.GetDocument(syntaxTree);
        if (document is not null)
        {
            return new LocatedSemanticDocument(document, false);
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
                    return new LocatedSemanticDocument(generatedDocument, true);
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
        SemanticSourceProjection projection,
        bool? isSourceGenerated = null)
    {
        var lineSpan = syntaxTree.GetLineSpan(span);
        var filePath = document.FilePath ?? document.Name;
        var range = new SourceRange(
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1);
        var isGenerated = projection.IsGenerated(syntaxTree, isSourceGenerated == true);
        return new(
            document.Project.Name,
            projection.GetTargetFramework(document.Project.Id),
            filePath,
            range,
            isGenerated,
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
        return CodeExploreGeneratedSourceClassifier.IsGeneratedPath(path);
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
    private readonly ConcurrentDictionary<SyntaxTree, bool> _generatedDocuments = new();
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

    /// <summary>Classifies one document once for this immutable query projection.</summary>
    internal bool IsGenerated(SyntaxTree syntaxTree, bool isSourceGenerated)
    {
        if (isSourceGenerated)
        {
            return true;
        }

        return _generatedDocuments.GetOrAdd(
            syntaxTree,
            tree => CodeExploreGeneratedSourceClassifier.IsGeneratedPath(tree.FilePath)
                || CodeExploreGeneratedSourceClassifier.HasGeneratedHeader(tree.GetRoot()));
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

/// <summary>Compiler-readiness state captured before deciding whether code_explore can return source.</summary>
internal sealed record CodeExploreReadinessSnapshot(
    Solution? Solution,
    IReadOnlySet<ProjectId> CompiledProjects,
    SemanticConfidenceLevel Confidence,
    string? RepositoryPath,
    string? WorkspacePath,
    long Generation);

/// <summary>Immutable compiler-aware state captured for one advanced query.</summary>
internal sealed record AdvancedSemanticSnapshot(
    Solution Solution,
    IReadOnlySet<ProjectId> CompiledProjects,
    SemanticConfidenceLevel Confidence,
    string RepositoryPath,
    string WorkspacePath,
    long Generation);
