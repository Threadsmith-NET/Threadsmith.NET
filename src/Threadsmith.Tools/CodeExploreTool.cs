namespace Threadsmith.Tools;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Resolves exact C# anchors and returns bounded current source from one semantic generation.</summary>
public sealed class CodeExploreTool : Tool<CodeExploreRequest, CodeExploreResult>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<CodeExploreRequest, CodeExploreResult>(
        "code_explore",
        "Primary source-bearing C# exploration tool for natural-language C# architecture or behavior questions, exact symbols, stable symbol ids, and repository-relative C# path anchors. Use before find_symbol, search, or read_file when the question needs current declaration source, structural survey, flow, or compact impact evidence. Natural-language query terms drive bounded deterministic Roslyn declaration discovery/ranking when exact anchors are not known; they are inert only in that they are never executed and never provider-reranked. For path anchors, include line when selecting a containing declaration; a containing-declaration path anchor without line is treated as bounded whole-file source and will not identify a flow symbol. Do not answer implementation questions from this tool description; when source is available, inspect the repository implementation and cite returned files/declarations. Use returned continuation anchors for focused follow-up.",
        ToolCategory.SemanticSearch,
        RepositoryTrustLevel.TrustedBuild,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(60),
        1024 * 1024);

    private readonly ICodeExploreService _service;

    /// <summary>Initializes a new instance of the <see cref="CodeExploreTool"/> class.</summary>
    public CodeExploreTool(ICodeExploreService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<CodeExploreResult>> ExecuteAsync(
        CodeExploreRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var workspaceId = context.Invocation.WorkspaceId
            ?? throw new InvalidOperationException("Code exploration requires an opened workspace.");
        var sourceReader = new PolicyCodeExploreSourceReader(context.Invocation);
        var effectiveInput = ApplyModelBudget(input, context.Invocation, out var budgetSource);
        var result = await _service.QueryCodeExploreAsync(workspaceId, effectiveInput, sourceReader, cancellationToken);
        result = ApplyBudgetSource(result, budgetSource);
        result = Confine(result, context.Invocation);
        result = BoundResultForModelBudget(result, context.Invocation);
        ToolProvenanceSource[] sources = [
            .. result.FileSections.Select(section => new ToolProvenanceSource(
                "file",
                section.FilePath,
                $"L{section.Source.Range.StartLine}-L{section.Source.Range.EndLine}")),
            new("semantic-workspace", workspaceId.Value.ToString("D")),
        ];
        return new(result, sources, IsTruncated(result));
    }

    /// <inheritdoc />
    protected override string DescribeActivity(CodeExploreRequest input)
    {
        var anchorCount = input.ExactSymbolAnchors.Count + input.SymbolIds.Count + input.PathAnchors.Count;
        var mode = input.Mode == CodeExploreMode.Auto ? "auto" : input.Mode.ToString().ToLowerInvariant();
        return anchorCount == 0
            ? $"{BoundActivity(input.Query)} ({mode})"
            : $"{BoundActivity(input.Query)} ({mode}, {anchorCount} anchors)";
    }

    /// <inheritdoc />
    protected override void ValidateInput(CodeExploreRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Query);
        ArgumentNullException.ThrowIfNull(input.Limits);
        if (input.Query.Length > 1024)
        {
            throw new ToolArgumentValidationException("query exceeds 1,024 characters.");
        }

        if (!Enum.IsDefined(input.Mode))
        {
            throw new ToolArgumentValidationException("code exploration mode is not supported.");
        }

        ValidateLimits(input.Limits);
        var anchorCount = input.ExactSymbolAnchors.Count + input.SymbolIds.Count + input.PathAnchors.Count;
        if (anchorCount > input.Limits.MaximumAnchors)
        {
            throw new ToolArgumentValidationException("the request contains more exact anchors than maximumAnchors.");
        }

        foreach (var anchor in input.ExactSymbolAnchors.Concat(input.SymbolIds))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
            if (anchor.Length > 2048)
            {
                throw new ToolArgumentValidationException("symbol anchors exceed 2,048 characters.");
            }
        }

        foreach (var anchor in input.PathAnchors)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Path);
            var invalidSelectionMode = !Enum.IsDefined(anchor.SelectionMode);
            var missingRequiredLine = RequiresLine(anchor.SelectionMode) && anchor.Line is null;
            var missingRequiredEndLine = anchor.SelectionMode == CodeExplorePathSelectionMode.ExactLineRange && anchor.EndLine is null;
            if (anchor.Path.Length > 4096
                || anchor.Line is <= 0
                || anchor.EndLine is <= 0
                || anchor.EndLine < anchor.Line
                || invalidSelectionMode
                || missingRequiredLine
                || missingRequiredEndLine)
            {
                throw new ToolArgumentValidationException("path anchors must be bounded and use valid positive one-based line ranges for their selection mode.");
            }

            if (anchor.ExpectedWorkspaceGeneration is < 0)
            {
                throw new ToolArgumentValidationException("expectedWorkspaceGeneration must be non-negative.");
            }

            if (anchor.ExpectedFileSha256 is { } expectedFileSha256 && !IsSha256Hex(expectedFileSha256))
            {
                throw new ToolArgumentValidationException("expectedFileSha256 must be a 64-character lowercase or uppercase SHA-256 hex digest.");
            }
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        CodeExploreRequest input,
        ToolInvocationContext context)
    {
        var paths = new List<string> { context.RepositoryPath };
        paths.AddRange(input.PathAnchors.Select(anchor => anchor.Path));
        if (QueryLooksLikePath(input.Query))
        {
            paths.Add(input.Query);
        }

        return paths;
    }

    private static void ValidateLimits(CodeExploreLimits limits)
    {
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
            throw new ToolArgumentValidationException("code exploration bounds are outside host limits.");
        }
    }

    private static CodeExploreRequest ApplyModelBudget(
        CodeExploreRequest input,
        ToolInvocationContext context,
        out string? budgetSource)
    {
        budgetSource = null;
        if (context.ModelEffectiveInputBudgetTokens is not { } effectiveInputTokens || effectiveInputTokens <= 0)
        {
            return input;
        }

        var modelSourceCharacters = (int)Math.Clamp(
            (long)effectiveInputTokens * 3,
            1,
            100_000);
        var maximumSourceCharacters = Math.Min(
            input.Limits.MaximumSourceCharacters,
            modelSourceCharacters);
        var maximumPerFileSourceCharacters = Math.Min(
            input.Limits.MaximumPerFileSourceCharacters,
            maximumSourceCharacters);
        if (maximumSourceCharacters == input.Limits.MaximumSourceCharacters
            && maximumPerFileSourceCharacters == input.Limits.MaximumPerFileSourceCharacters)
        {
            return input;
        }

        budgetSource = "request source limits clamped by the selected model effective input budget "
            + $"({effectiveInputTokens} tokens after output reserve "
            + $"{context.ModelRequestOutputReserveTokens ?? 0} tokens; 3 source characters per token ceiling).";
        return input with
        {
            Limits = input.Limits with
            {
                MaximumSourceCharacters = maximumSourceCharacters,
                MaximumPerFileSourceCharacters = maximumPerFileSourceCharacters,
            },
        };
    }

    private static CodeExploreResult ApplyBudgetSource(
        CodeExploreResult result,
        string? budgetSource)
    {
        return budgetSource is null || result.Allocation is null
            ? result
            : result with { Allocation = result.Allocation with { BudgetSource = budgetSource } };
    }

    private static CodeExploreResult BoundResultForModelBudget(
        CodeExploreResult result,
        ToolInvocationContext context)
    {
        if (context.ModelEffectiveInputBudgetTokens is not { } effectiveInputTokens || effectiveInputTokens <= 0)
        {
            return result;
        }

        var maximumSerializedBytes = (int)Math.Clamp((long)effectiveInputTokens * 3, 1024, 1024 * 1024);
        if (GetSerializedByteCount(result) <= maximumSerializedBytes)
        {
            return result;
        }

        var bounded = result;
        bounded = TrimCandidateSummaries(bounded, maximumSerializedBytes);
        bounded = TrimBlastRadius(bounded, maximumSerializedBytes);
        bounded = TrimFlow(bounded, maximumSerializedBytes);
        bounded = TrimContinuationTargets(bounded, maximumSerializedBytes);
        bounded = TrimAnchorAlternatives(bounded, maximumSerializedBytes);
        bounded = TrimFileSections(bounded, maximumSerializedBytes);
        if (GetSerializedByteCount(bounded) <= maximumSerializedBytes)
        {
            return bounded;
        }

        var minimal = bounded with
        {
            FileSections = [],
            ContinuationTargets = [],
            Flow = null,
            BlastRadius = null,
            CandidateSummaries = [],
            Allocation = bounded.Allocation is null
                ? null
                : bounded.Allocation with
                {
                    SpentSourceCharacters = 0,
                    Files = [],
                },
            ResolvedAnchors = bounded.ResolvedAnchors
                .Select(anchor => anchor with { Alternatives = [] })
                .ToArray(),
            Omissions = AddResultBoundOmission(bounded.Omissions),
            Coverage = bounded.Coverage with
            {
                SourceComplete = false,
                SymbolResolutionComplete = false,
                OutputComplete = false,
                Omissions = AddResultBoundOmission(bounded.Coverage.Omissions),
            },
        };
        return minimal;
    }

    private static CodeExploreResult TrimCandidateSummaries(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        var summaries = result.CandidateSummaries;
        while (summaries is { Count: > 0 } && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            summaries = summaries.Take(summaries.Count / 2).ToArray();
            result = result with
            {
                CandidateSummaries = summaries,
                Discovery = result.Discovery is null
                    ? null
                    : result.Discovery with { CandidateLimitReached = true },
                Omissions = AddResultBoundOmission(result.Omissions),
                Coverage = result.Coverage with
                {
                    OutputComplete = false,
                    Omissions = AddResultBoundOmission(result.Coverage.Omissions),
                },
            };
        }

        return result;
    }

    private static CodeExploreResult TrimBlastRadius(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        var blastRadius = result.BlastRadius;
        while (blastRadius is { Items.Count: > 0 } && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            var items = blastRadius.Items.Take(blastRadius.Items.Count / 2).ToArray();
            blastRadius = blastRadius with
            {
                Items = items,
                Omissions = AddResultBoundOmission(blastRadius.Omissions),
            };
            result = result with
            {
                BlastRadius = blastRadius,
                Omissions = AddResultBoundOmission(result.Omissions),
                Coverage = result.Coverage with
                {
                    OutputComplete = false,
                    Omissions = AddResultBoundOmission(result.Coverage.Omissions),
                },
            };
        }

        return result;
    }

    private static CodeExploreResult TrimFlow(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        var flow = result.Flow;
        while (flow is not null && HasFlowMetadata(flow) && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            flow = flow with
            {
                Paths = TakeHalf(flow.Paths),
                Nodes = TakeHalf(flow.Nodes),
                Edges = TakeHalf(flow.Edges),
                DispatchBranches = TakeHalf(flow.DispatchBranches),
                Boundaries = TakeHalf(flow.Boundaries),
                Traversal = flow.Traversal with
                {
                    IsComplete = false,
                    Omissions = AddResultBoundOmission(flow.Traversal.Omissions),
                },
            };
            result = result with
            {
                Flow = flow,
                Omissions = AddResultBoundOmission(result.Omissions),
                Coverage = result.Coverage with
                {
                    OutputComplete = false,
                    Omissions = AddResultBoundOmission(result.Coverage.Omissions),
                },
            };
        }

        return result;
    }

    private static CodeExploreResult TrimContinuationTargets(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        while (result.ContinuationTargets.Count > 0 && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            result = result with
            {
                ContinuationTargets = result.ContinuationTargets.Take(result.ContinuationTargets.Count / 2).ToArray(),
                Omissions = AddResultBoundOmission(result.Omissions),
                Coverage = result.Coverage with
                {
                    OutputComplete = false,
                    Omissions = AddResultBoundOmission(result.Coverage.Omissions),
                },
            };
        }

        return result;
    }

    private static CodeExploreResult TrimAnchorAlternatives(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        if (GetSerializedByteCount(result) <= maximumSerializedBytes
            || !result.ResolvedAnchors.Any(anchor => anchor.Alternatives.Count > 0))
        {
            return result;
        }

        return result with
        {
            ResolvedAnchors = result.ResolvedAnchors
                .Select(anchor => anchor with { Alternatives = [] })
                .ToArray(),
            Omissions = AddResultBoundOmission(result.Omissions),
            Coverage = result.Coverage with
            {
                SymbolResolutionComplete = false,
                OutputComplete = false,
                Omissions = AddResultBoundOmission(result.Coverage.Omissions),
            },
        };
    }

    private static CodeExploreResult TrimFileSections(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        while (result.FileSections.Count > 0 && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            var fileSections = result.FileSections.Take(result.FileSections.Count / 2).ToArray();
            result = result with
            {
                FileSections = fileSections,
                Flow = NullRemovedSourceSectionIndexes(result.Flow, fileSections.Length),
                Allocation = TrimAllocationToFileSections(result.Allocation, fileSections),
                Omissions = AddResultBoundOmission(result.Omissions),
                Coverage = result.Coverage with
                {
                    SourceComplete = false,
                    OutputComplete = false,
                    Omissions = AddResultBoundOmission(result.Coverage.Omissions),
                },
            };
        }

        return result;
    }

    private static CodeExploreFlow? NullRemovedSourceSectionIndexes(
        CodeExploreFlow? flow,
        int retainedSectionCount)
    {
        if (flow is null)
        {
            return null;
        }

        return flow with
        {
            Nodes = flow.Nodes.Select(node => node with
            {
                SourceSectionIndex = RetainSourceSectionIndex(node.SourceSectionIndex, retainedSectionCount),
            }).ToArray(),
            DispatchBranches = flow.DispatchBranches.Select(branch => branch with
            {
                Implementations = branch.Implementations.Select(target => target with
                {
                    SourceSectionIndex = RetainSourceSectionIndex(target.SourceSectionIndex, retainedSectionCount),
                }).ToArray(),
            }).ToArray(),
        };
    }

    private static int? RetainSourceSectionIndex(int? sourceSectionIndex, int retainedSectionCount)
    {
        return sourceSectionIndex is >= 0 && sourceSectionIndex < retainedSectionCount
            ? sourceSectionIndex
            : null;
    }

    private static CodeExploreAllocationSummary? TrimAllocationToFileSections(
        CodeExploreAllocationSummary? allocation,
        IReadOnlyList<CodeExploreFileSection> fileSections)
    {
        if (allocation is null)
        {
            return null;
        }

        var retainedPaths = fileSections
            .Select(section => section.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = allocation.Files
            .Where(file => retainedPaths.Contains(file.FilePath))
            .ToArray();
        return allocation with
        {
            SpentSourceCharacters = files.Sum(file => file.SpentCharacters),
            Files = files,
        };
    }

    private static T[] TakeHalf<T>(IReadOnlyList<T> items)
    {
        return [.. items.Take(items.Count / 2)];
    }

    private static bool HasFlowMetadata(CodeExploreFlow flow)
    {
        return flow.Paths.Count > 0
            || flow.Nodes.Count > 0
            || flow.Edges.Count > 0
            || flow.DispatchBranches.Count > 0
            || flow.Boundaries.Count > 0;
    }

    private static int GetSerializedByteCount(CodeExploreResult result)
    {
        return JsonSerializer.SerializeToUtf8Bytes(result).Length;
    }

    private static IReadOnlyList<string> AddResultBoundOmission(IReadOnlyList<string> omissions)
    {
        const string omission = "Code exploration metadata was bounded to fit the selected model request budget.";
        return omissions.Contains(omission, StringComparer.Ordinal)
            ? omissions
            : [.. omissions, omission];
    }

    private static CodeExploreResult Confine(CodeExploreResult result, ToolInvocationContext context)
    {
        var sectionIndexMap = new Dictionary<int, int>();
        var confinedSections = new List<CodeExploreFileSection>();
        for (var index = 0; index < result.FileSections.Count; index++)
        {
            var section = result.FileSections[index];
            if (!IsAllowed(section.FilePath, context))
            {
                continue;
            }

            sectionIndexMap[index] = confinedSections.Count;
            confinedSections.Add(section);
        }

        CodeExploreFileSection[] sections = [.. confinedSections];
        CodeExploreAnchorResolution[] resolutions = [.. result.ResolvedAnchors.Select(resolution => Confine(resolution, context))];
        CodeExploreContinuationTarget[] continuations = [.. result.ContinuationTargets.Where(target => target.FilePath is null || IsAllowed(target.FilePath, context))];
        var flow = Confine(result.Flow, context, sectionIndexMap, out var flowOmitted);
        var blastRadius = Confine(result.BlastRadius, context, out var blastRadiusOmitted);
        var candidateSummaries = Confine(result.CandidateSummaries, context, out var candidateSummaryOmitted);
        var allocation = Confine(result.Allocation, context, out var allocationOmitted);
        var alternativesOmitted = result.ResolvedAnchors
            .Zip(resolutions)
            .Any(item => item.First.Alternatives.Count != item.Second.Alternatives.Count);
        var omitted = sections.Length != result.FileSections.Count
            || alternativesOmitted
            || resolutions.Any(resolution => resolution.Outcome == CodeExploreResolutionOutcome.Omitted)
            || continuations.Length != result.ContinuationTargets.Count
            || flowOmitted
            || blastRadiusOmitted
            || candidateSummaryOmitted
            || allocationOmitted;
        var omissions = AddPolicyOmission(result.Omissions, omitted);
        return result with
        {
            ResolvedAnchors = resolutions,
            FileSections = sections,
            ContinuationTargets = continuations,
            Flow = flow,
            BlastRadius = blastRadius,
            CandidateSummaries = candidateSummaries,
            Allocation = allocation,
            Omissions = omissions,
            Coverage = result.Coverage with
            {
                SourceComplete = result.Coverage.SourceComplete && !omitted,
                OutputComplete = result.Coverage.OutputComplete && !omitted,
                Omissions = AddPolicyOmission(result.Coverage.Omissions, omitted),
            },
        };
    }

    private static CodeExploreAnchorResolution Confine(
        CodeExploreAnchorResolution resolution,
        ToolInvocationContext context)
    {
        var selectedAllowed = resolution.SelectedLocation is null
            ? resolution.SelectedSymbol is null
            : IsAllowed(resolution.SelectedLocation.FilePath, context);
        CodeExploreAlternative[] alternatives = [.. resolution.Alternatives.Where(alternative =>
            alternative.Location is not null && IsAllowed(alternative.Location.FilePath, context))];
        return selectedAllowed
            ? resolution with { Alternatives = alternatives }
            : resolution with
            {
                Outcome = CodeExploreResolutionOutcome.Omitted,
                SelectedLocation = null,
                SelectedSymbol = null,
                Alternatives = alternatives,
                Reason = "Resolved evidence outside the invocation path policy was omitted.",
            };
    }

    private static CodeExploreFlow? Confine(
        CodeExploreFlow? flow,
        ToolInvocationContext context,
        IReadOnlyDictionary<int, int> sectionIndexMap,
        out bool omitted)
    {
        omitted = false;
        if (flow is null)
        {
            return null;
        }

        var confinedNodeCandidates = new List<CodeExploreFlowNode>();
        foreach (var node in flow.Nodes)
        {
            var confinedNode = Confine(node, context, sectionIndexMap, ref omitted);
            if (confinedNode.Locations.Count == 0 && !confinedNode.IsNamedAnchor)
            {
                omitted = true;
                continue;
            }

            confinedNodeCandidates.Add(confinedNode);
        }

        var keptNodeIds = confinedNodeCandidates
            .Select(node => node.Symbol.Id)
            .ToHashSet(StringComparer.Ordinal);
        var edgeOrdinalMap = new Dictionary<int, int>();
        var edges = new List<CodeExploreFlowEdge>();
        foreach (var edge in flow.Edges)
        {
            if ((edge.CallSite is not null && !IsAllowed(edge.CallSite.FilePath, context))
                || !keptNodeIds.Contains(edge.CallerSymbolId)
                || !keptNodeIds.Contains(edge.CalleeSymbolId))
            {
                omitted = true;
                continue;
            }

            edgeOrdinalMap[edge.Ordinal] = edges.Count;
            edges.Add(edge with { Ordinal = edges.Count });
        }

        var pathList = new List<CodeExploreFlowPath>();
        foreach (var path in flow.Paths)
        {
            pathList.Add(Confine(path, edgeOrdinalMap, keptNodeIds, ref omitted));
        }

        var paths = pathList.ToArray();
        var branchList = new List<CodeExploreDispatchBranch>();
        foreach (var branch in flow.DispatchBranches)
        {
            branchList.Add(Confine(branch, context, sectionIndexMap, ref omitted));
        }

        var branches = branchList.ToArray();
        var referencedNodeIds = paths
            .SelectMany(path => path.NodeIds)
            .Concat(edges.Select(edge => edge.CallerSymbolId))
            .Concat(edges.Select(edge => edge.CalleeSymbolId))
            .Concat(branches.SelectMany(branch => branch.Implementations.Select(target => target.Symbol.Id)))
            .ToHashSet(StringComparer.Ordinal);
        var nodes = confinedNodeCandidates
            .Where(node => node.IsNamedAnchor || referencedNodeIds.Contains(node.Symbol.Id))
            .ToArray();
        var finalNodeIds = nodes
            .Select(node => node.Symbol.Id)
            .ToHashSet(StringComparer.Ordinal);
        var boundaryList = new List<CodeExploreFlowBoundary>();
        foreach (var boundary in flow.Boundaries)
        {
            if (!finalNodeIds.Contains(boundary.SymbolId))
            {
                omitted = true;
                continue;
            }

            var callSiteAllowed = boundary.CallSite is null || IsAllowed(boundary.CallSite.FilePath, context);
            omitted |= !callSiteAllowed;
            boundaryList.Add(boundary with { CallSite = callSiteAllowed ? boundary.CallSite : null });
        }

        var boundaries = boundaryList.ToArray();
        return flow with
        {
            Paths = paths,
            Nodes = nodes,
            Edges = edges,
            DispatchBranches = branches,
            Boundaries = boundaries,
            Traversal = flow.Traversal with
            {
                IsComplete = flow.Traversal.IsComplete && !omitted,
                Omissions = AddPolicyOmission(flow.Traversal.Omissions, omitted),
            },
        };
    }

    private static CodeExploreFlowPath Confine(
        CodeExploreFlowPath path,
        IReadOnlyDictionary<int, int> edgeOrdinalMap,
        IReadOnlySet<string> keptNodeIds,
        ref bool omitted)
    {
        var nodeIds = path.NodeIds
            .Where(keptNodeIds.Contains)
            .ToArray();
        var ordinals = path.EdgeOrdinals
            .Where(edgeOrdinalMap.ContainsKey)
            .Select(ordinal => edgeOrdinalMap[ordinal])
            .ToArray();
        if (ordinals.Length != path.EdgeOrdinals.Count || nodeIds.Length != path.NodeIds.Count)
        {
            omitted = true;
        }

        return path with
        {
            NodeIds = nodeIds,
            EdgeOrdinals = ordinals,
            IsComplete = path.IsComplete
                && ordinals.Length == path.EdgeOrdinals.Count
                && nodeIds.Length == path.NodeIds.Count,
        };
    }

    private static CodeExploreFlowNode Confine(
        CodeExploreFlowNode node,
        ToolInvocationContext context,
        IReadOnlyDictionary<int, int> sectionIndexMap,
        ref bool omitted)
    {
        var locations = node.Locations
            .Where(location => IsAllowed(location.FilePath, context))
            .ToArray();
        omitted |= locations.Length != node.Locations.Count;
        return node with
        {
            Locations = locations,
            SourceSectionIndex = RemapSectionIndex(node.SourceSectionIndex, sectionIndexMap),
        };
    }

    private static CodeExploreDispatchBranch Confine(
        CodeExploreDispatchBranch branch,
        ToolInvocationContext context,
        IReadOnlyDictionary<int, int> sectionIndexMap,
        ref bool omitted)
    {
        var callSiteAllowed = branch.CallSite is null || IsAllowed(branch.CallSite.FilePath, context);
        var implementations = branch.Implementations
            .Where(target => target.Location is not null && IsAllowed(target.Location.FilePath, context))
            .Select(target => target with { SourceSectionIndex = RemapSectionIndex(target.SourceSectionIndex, sectionIndexMap) })
            .ToArray();
        omitted |= !callSiteAllowed || implementations.Length != branch.Implementations.Count;
        return branch with
        {
            CallSite = callSiteAllowed ? branch.CallSite : null,
            Implementations = implementations,
            ReturnedCount = implementations.Length,
        };
    }

    private static CodeExploreBlastRadius? Confine(
        CodeExploreBlastRadius? blastRadius,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (blastRadius is null)
        {
            return null;
        }

        var items = blastRadius.Items
            .Where(item => item.Symbol is null
                ? item.Location is null || IsAllowed(item.Location.FilePath, context)
                : item.Location is not null && IsAllowed(item.Location.FilePath, context))
            .ToArray();
        var continuations = blastRadius.ContinuationTargets
            .Where(target => target.FilePath is null || IsAllowed(target.FilePath, context))
            .ToArray();
        omitted = items.Length != blastRadius.Items.Count
            || continuations.Length != blastRadius.ContinuationTargets.Count;
        return blastRadius with
        {
            Items = items,
            ContinuationTargets = continuations,
            Omissions = AddPolicyOmission(blastRadius.Omissions, omitted),
        };
    }

    private static IReadOnlyList<CodeExploreCandidateSummary>? Confine(
        IReadOnlyList<CodeExploreCandidateSummary>? candidateSummaries,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (candidateSummaries is null)
        {
            return null;
        }

        var summaries = candidateSummaries
            .Where(summary => IsCandidateSummaryAllowed(summary, context))
            .ToArray();
        omitted = summaries.Length != candidateSummaries.Count;
        return summaries;
    }

    private static CodeExploreAllocationSummary? Confine(
        CodeExploreAllocationSummary? allocation,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (allocation is null)
        {
            return null;
        }

        var files = allocation.Files
            .Where(file => IsAllowed(file.FilePath, context))
            .ToArray();
        omitted = files.Length != allocation.Files.Count;
        return allocation with { Files = files };
    }

    private static bool IsCandidateSummaryAllowed(
        CodeExploreCandidateSummary summary,
        ToolInvocationContext context)
    {
        if (summary.Location is not null)
        {
            return IsAllowed(summary.Location.FilePath, context)
                && (summary.FilePath is null || IsAllowed(summary.FilePath, context));
        }

        if (summary.Symbol is not null)
        {
            return summary.FilePath is not null && IsAllowed(summary.FilePath, context);
        }

        return summary.FilePath is null || IsAllowed(summary.FilePath, context);
    }

    private static int? RemapSectionIndex(
        int? sourceSectionIndex,
        IReadOnlyDictionary<int, int> sectionIndexMap)
    {
        if (sourceSectionIndex is null)
        {
            return null;
        }

        return sectionIndexMap.TryGetValue(sourceSectionIndex.Value, out var remapped)
            ? remapped
            : null;
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

    private static bool IsTruncated(CodeExploreResult result)
    {
        var blastRadiusTruncated = result.BlastRadius is { } blastRadius
            && (blastRadius.ReturnedCallers < blastRadius.TotalCallers
                || blastRadius.ReturnedImplementations < blastRadius.TotalImplementations
                || blastRadius.ReturnedProjects < blastRadius.TotalProjects
                || blastRadius.ReturnedTests < blastRadius.TotalTests);
        var flowBranchTruncated = result.Flow is { } flow
            && flow.DispatchBranches.Any(branch => branch.ReturnedCount < branch.TotalCount || branch.Omissions.Count > 0);
        return !result.Coverage.SymbolResolutionComplete
            || !result.Coverage.CompiledProjectCoverageComplete
            || !result.Coverage.SourceComplete
            || !result.Coverage.OutputComplete
            || result.Discovery is { CatalogComplete: false } or { CandidateLimitReached: true }
            || result.Flow is { Traversal.IsComplete: false }
            || flowBranchTruncated
            || blastRadiusTruncated;
    }

    private static bool QueryLooksLikePath(string query)
    {
        var trimmed = query.Trim();
        return !trimmed.Any(char.IsWhiteSpace)
            && (trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains('/')
                || trimmed.Contains('\\'));
    }

    private static bool IsSha256Hex(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static bool RequiresLine(CodeExplorePathSelectionMode mode)
    {
        return mode is CodeExplorePathSelectionMode.SingleLine
            or CodeExplorePathSelectionMode.TailWindow
            or CodeExplorePathSelectionMode.ExactLineRange;
    }

    private static string BoundActivity(string value)
    {
        return value.Length <= 120 ? value : value[..120];
    }

    private sealed class PolicyCodeExploreSourceReader : ICodeExploreSourceReader
    {
        private const int MaximumReadableFileBytes = 1024 * 1024;

        private readonly ToolInvocationContext _context;

        internal PolicyCodeExploreSourceReader(ToolInvocationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _context = context;
        }

        public bool IsPathAllowed(string path)
        {
            try
            {
                _ = ToolPathRules.NormalizeAndValidate(path, _context);
                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
            {
                return false;
            }
        }

        public async Task<CodeExploreSourceText> ReadTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            var normalized = ToolPathRules.NormalizeAndValidate(path, _context);
            var effectiveMaximumBytes = Math.Min(maximumBytes, MaximumReadableFileBytes);
            ValidateReadableSourceFile(normalized, effectiveMaximumBytes);
            await using var stream = new FileStream(
                normalized,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!stream.CanSeek)
            {
                throw new UnauthorizedAccessException("Code exploration reads only seekable regular files.");
            }

            if (stream.Length > effectiveMaximumBytes)
            {
                throw new InvalidOperationException($"The source file exceeds the {effectiveMaximumBytes}-byte code exploration read limit.");
            }

            var bytes = await ReadBoundedBytesAsync(stream, effectiveMaximumBytes, cancellationToken);
            var text = await DecodeSourceTextAsync(bytes, cancellationToken);
            return new CodeExploreSourceText(normalized, text, ComputeSha256(bytes));
        }

        private static void ValidateReadableSourceFile(string normalized, int effectiveMaximumBytes)
        {
            var info = new FileInfo(normalized);
            if (!info.Exists)
            {
                throw new FileNotFoundException("The requested source file does not exist.", normalized);
            }

            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new UnauthorizedAccessException("Code exploration reads only regular non-reparse files.");
            }

            if (info.Length > effectiveMaximumBytes)
            {
                throw new InvalidOperationException($"The source file exceeds the {effectiveMaximumBytes}-byte code exploration read limit.");
            }
        }

        private static async Task<byte[]> ReadBoundedBytesAsync(
            Stream stream,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            await using var output = new MemoryStream(Math.Min(maximumBytes, buffer.Length));
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidOperationException($"The source file exceeds the {maximumBytes}-byte code exploration read limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        private static async Task<string> DecodeSourceTextAsync(
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            await using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
