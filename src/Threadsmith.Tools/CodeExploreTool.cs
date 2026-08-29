namespace Threadsmith.Tools;

using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Minimal model-facing arguments for C# code exploration.</summary>
public sealed record CodeExploreInput
{
    /// <summary>Natural-language question, symbol, file, or code term to explore.</summary>
    public required string Query { get; init; }

    /// <summary>Optional result-file hint; omission uses the host's adaptive default.</summary>
    public int MaxFiles { get; init; } = 8;
}

/// <summary>Resolves C# queries and returns bounded current source from one semantic generation.</summary>
public sealed class CodeExploreTool : Tool<CodeExploreInput, CodeExploreResult>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<CodeExploreInput, CodeExploreResult>(
        "code_explore",
        "Explore current C# code using a natural-language question, exact symbol, file, or code term. Returns relevant dependencies and grouped line-numbered source. Provide query and optionally maxFiles; the host owns all traversal, timeout, byte, and source budgets. Use this before text search or raw file reads for C# implementation questions, and inspect its result before choosing a fallback.",
        ToolCategory.SemanticSearch,
        RepositoryTrustLevel.TrustedBuild,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(60),
        1024 * 1024)
    with
    {
        RequiresWorkspace = false,
        PreferStrictArguments = true,
    };

    private static readonly string[] ImpactIntentPhrases =
    [
        " affected ",
        " blast radius ",
        " callers of ",
        " callers for ",
        " dependent project ",
        " dependent projects ",
        " dependents of ",
        " downstream of ",
        " impact ",
        " projects depend on ",
        " references to ",
        " tests depend on ",
        " usages of ",
        " uses of ",
        " what calls ",
        " what depends on ",
        " what uses ",
        " who calls ",
        " who depends on ",
        " who uses ",
    ];

    private readonly ICodeExploreService _service;
    private readonly IProcessManager? _processManager;

    /// <summary>Initializes a new instance of the <see cref="CodeExploreTool"/> class.</summary>
    public CodeExploreTool(
        ICodeExploreService service,
        IProcessManager? processManager = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _processManager = processManager;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override Task<ToolExecution<CodeExploreResult>> ExecuteAsync(
        CodeExploreInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(CreateRequest(input), context, cancellationToken);
    }

    /// <summary>Executes an internal host-authored request with explicit semantic anchors and limits.</summary>
    public async Task<ToolExecution<CodeExploreResult>> ExecuteAsync(
        CodeExploreRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        _ = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var effectiveInput = ApplyModelBudget(request, context.Invocation, out var budgetSource);
        if (context.Invocation.WorkspaceId is not { } workspaceId)
        {
            var unavailable = CreateNoWorkspaceResult(effectiveInput);
            unavailable = ApplyBudgetSource(unavailable, budgetSource);
            unavailable = BoundResultForModelBudget(unavailable, context.Invocation);
            return new(unavailable, [new ToolProvenanceSource("repository", context.Invocation.RepositoryPath)], IsTruncated(unavailable));
        }

        var sourceReader = new PolicyCodeExploreSourceReader(context, _processManager);
        var result = await _service.QueryCodeExploreAsync(
            workspaceId,
            effectiveInput,
            sourceReader,
            cancellationToken,
            context.Invocation.VisibleSourceFrontier);
        result = ApplyBudgetSource(result, budgetSource);
        result = Confine(result, context.Invocation);
        result = BoundResultForModelBudget(result, context.Invocation);
        ToolProvenanceSource[] sources = [
            .. result.FileSections.Select(section => new ToolProvenanceSource(
                "file",
                section.FilePath,
                $"L{section.Source.Range.StartLine}-L{section.Source.Range.EndLine}")),
            .. CreateAssociatedArtifactProvenanceSources(result.AssociatedArtifacts),
            new("semantic-workspace", workspaceId.Value.ToString("D")),
        ];
        return new(result, sources, IsTruncated(result));
    }

    /// <inheritdoc />
    protected override string DescribeActivity(CodeExploreInput input)
    {
        return BoundActivity(input.Query);
    }

    /// <inheritdoc />
    protected override void ValidateInput(CodeExploreInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Query);
        if (input.Query.Length > 1024)
        {
            throw new ToolArgumentValidationException("query exceeds 1,024 characters.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        CodeExploreInput input,
        ToolInvocationContext context)
    {
        return QueryLooksLikePath(input.Query)
            ? [context.RepositoryPath, input.Query]
            : [context.RepositoryPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(CodeExploreInput input)
    {
        return _processManager is null ? null : "git";
    }

    /// <inheritdoc />
    protected override string? GetExecutable(CodeExploreInput input, ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.WorkspaceId is null ? null : GetExecutable(input);
    }

    private static CodeExploreRequest CreateRequest(CodeExploreInput input)
    {
        var limits = new CodeExploreLimits();
        var maximumFiles = input.MaxFiles <= 0
            ? limits.MaximumFiles
            : Math.Clamp(input.MaxFiles, 1, 16);
        if (CodeExploreContinuationCursor.TryCreateRequest(input.Query, limits, maximumFiles, out var continuationRequest)
            && continuationRequest is not null)
        {
            return continuationRequest;
        }

        return new CodeExploreRequest
        {
            Query = input.Query,
            Mode = DeriveMode(input.Query),
            Limits = limits with { MaximumFiles = maximumFiles },
        };
    }

    private static CodeExploreMode DeriveMode(string query)
    {
        return LooksLikeImpactQuery(query)
            ? CodeExploreMode.Impact
            : CodeExploreMode.Auto;
    }

    private static bool LooksLikeImpactQuery(string query)
    {
        var normalized = NormalizeQueryForIntent(query);
        return ImpactIntentPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }

    private static string NormalizeQueryForIntent(string query)
    {
        var builder = new StringBuilder(query.Length + 2);
        builder.Append(' ');
        foreach (var character in query)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-'
                ? char.ToLowerInvariant(character)
                : ' ');
        }

        builder.Append(' ');
        var compact = string.Join(' ', builder
            .ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return " " + compact + " ";
    }

    private static void ValidateRequest(CodeExploreRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentNullException.ThrowIfNull(request.Limits);
        if (request.Query.Length > 1024)
        {
            throw new ToolArgumentValidationException("query exceeds 1,024 characters.");
        }

        if (!Enum.IsDefined(request.Mode))
        {
            throw new ToolArgumentValidationException("code exploration mode is not supported.");
        }

        if (!Enum.IsDefined(request.AssociatedArtifacts))
        {
            throw new ToolArgumentValidationException("associated artifact mode is not supported.");
        }

        ValidateLimits(request.Limits);
        var anchorCount = request.ExactSymbolAnchors.Count + request.SymbolIds.Count + request.PathAnchors.Count;
        if (anchorCount > request.Limits.MaximumAnchors)
        {
            throw new ToolArgumentValidationException("the request contains more exact anchors than maximumAnchors.");
        }

        foreach (var anchor in request.ExactSymbolAnchors.Concat(request.SymbolIds))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
            if (anchor.Length > 2048)
            {
                throw new ToolArgumentValidationException("symbol anchors exceed 2,048 characters.");
            }
        }

        foreach (var anchor in request.PathAnchors)
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

        if (request.AssociatedArtifactPathAnchors.Count > request.Limits.MaximumAssociatedArtifactCandidates)
        {
            throw new ToolArgumentValidationException("associated artifact path anchor count must not exceed maximumAssociatedArtifactCandidates.");
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
                throw new ToolArgumentValidationException("associated artifact path anchors must be bounded and use valid positive one-based line ranges.");
            }

            if (anchor.ExpectedWorkspaceGeneration is < 0)
            {
                throw new ToolArgumentValidationException("associated artifact expectedWorkspaceGeneration must be non-negative.");
            }

            if (anchor.ExpectedFileSha256 is { } expectedFileSha256 && !IsSha256Hex(expectedFileSha256))
            {
                throw new ToolArgumentValidationException("associated artifact expectedFileSha256 must be a 64-character lowercase or uppercase SHA-256 hex digest.");
            }
        }
    }

    private static CodeExploreResult CreateNoWorkspaceResult(CodeExploreRequest input)
    {
        const string reason = "No workspace is open for code_explore. Open or select a C# workspace, then retry semantic source exploration.";
        var action = new CodeExploreNextActionHint(
            CodeExploreNextActionKind.OpenWorkspace,
            "Open or select a C# workspace before retrying code_explore.");
        var availability = new CodeExploreAvailability(
            CodeExploreAvailabilityStatus.NoWorkspaceOpen,
            reason,
            true,
            null,
            SemanticConfidenceLevel.PartialCompilation,
            true,
            [action]);
        var coverage = new CodeExploreCoverage(
            false,
            false,
            false,
            true,
            [reason]);
        var scale = new CodeExploreRepositoryScale(
            CodeExploreRepositoryScaleTier.Unknown,
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            null,
            input.AssociatedArtifactPathAnchors.Count);
        var adaptiveBudget = new CodeExploreAdaptiveBudget(
            scale,
            input.Limits.MaximumFiles,
            input.Limits.MaximumSourceCharacters,
            input.Limits.MaximumPerFileSourceCharacters,
            Math.Clamp(input.Limits.MaximumSourceCharacters / 512, 1, 64),
            1,
            CodeExplorePresentationVerbosity.Compact,
            "no workspace; request source limits retained");
        var presentation = new CodeExplorePresentation(
            $"Availability: {availability.Status}. {reason}",
            [],
            [new CodeExploreNotShownTarget(CodeExploreNotShownTargetKind.General, null, null, reason)],
            [action]);
        return new CodeExploreResult(
            0,
            SemanticConfidenceLevel.None,
            [],
            [],
            coverage,
            coverage.Omissions,
            [],
            Allocation: new CodeExploreAllocationSummary(
                input.Limits.MaximumSourceCharacters,
                0,
                0,
                adaptiveBudget.BudgetSource,
                []),
            Availability: availability,
            Presentation: presentation,
            AdaptiveBudget: adaptiveBudget,
            FileRelevance: []);
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
            || limits.MaximumAssociatedArtifacts is < 0 or > 16
            || limits.MaximumAssociatedArtifactCandidates is < 0 or > 128
            || limits.MaximumAssociatedArtifactCharacters is < 0 or > 100_000
            || limits.MaximumPerAssociatedArtifactCharacters is < 0 or > 65_536
            || limits.MaximumAssociatedArtifactBytes is < 1 or > 1024 * 1024
            || limits.MaximumAssociatedArtifactNameMatches is < 0 or > 64
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
        if (budgetSource is null)
        {
            return result;
        }

        return result with
        {
            Allocation = result.Allocation is null
                ? null
                : result.Allocation with { BudgetSource = result.Allocation.BudgetSource + "; " + budgetSource },
            AdaptiveBudget = result.AdaptiveBudget is null
                ? null
                : result.AdaptiveBudget with { BudgetSource = result.AdaptiveBudget.BudgetSource + "; " + budgetSource },
        };
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
        bounded = TrimPresentation(bounded, maximumSerializedBytes);
        bounded = TrimFileRelevance(bounded, maximumSerializedBytes);
        bounded = TrimCandidateSummaries(bounded, maximumSerializedBytes);
        bounded = TrimBlastRadius(bounded, maximumSerializedBytes);
        bounded = TrimFlow(bounded, maximumSerializedBytes);
        bounded = TrimAssociatedArtifacts(bounded, maximumSerializedBytes);
        bounded = TrimBackReferences(bounded, maximumSerializedBytes);
        bounded = TrimEmissions(bounded, maximumSerializedBytes);
        bounded = TrimAnchorAlternatives(bounded, maximumSerializedBytes);
        bounded = TrimContinuationTargets(bounded, maximumSerializedBytes);
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
            BackReferences = [],
            Emissions = [],
            AssociatedArtifacts = [],
            Presentation = null,
            FileRelevance = [],
            ArtifactCoverage = bounded.ArtifactCoverage is null
                ? null
                : bounded.ArtifactCoverage with
                {
                    ReturnedCount = 0,
                    OmittedCount = Math.Max(
                        bounded.ArtifactCoverage.OmittedCount,
                        bounded.ArtifactCoverage.CandidateCount),
                    SpentCharacters = 0,
                    Complete = false,
                    FileLimitReached = true,
                    CharacterLimitReached = true,
                    Omissions = AddResultBoundOmission(bounded.ArtifactCoverage.Omissions),
                    ContinuationTargets = [],
                },
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

    private static CodeExploreResult TrimPresentation(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        var presentation = result.Presentation;
        while (presentation is not null && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            if (!string.IsNullOrWhiteSpace(presentation.ModelSummary))
            {
                presentation = presentation with { ModelSummary = string.Empty };
            }
            else if (presentation.NotShownTargets.Count > 0)
            {
                presentation = presentation with { NotShownTargets = TakeHalf(presentation.NotShownTargets) };
            }
            else if (presentation.SourceGuarantees.Count > 0)
            {
                presentation = presentation with { SourceGuarantees = TakeHalf(presentation.SourceGuarantees) };
            }
            else if (presentation.NextActions.Count > 0)
            {
                presentation = presentation with { NextActions = TakeHalf(presentation.NextActions) };
            }
            else
            {
                presentation = null;
            }

            result = result with
            {
                Presentation = presentation,
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

    private static CodeExploreResult TrimFileRelevance(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        var fileRelevance = result.FileRelevance;
        while (fileRelevance is { Count: > 0 } && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            fileRelevance = TakeHalf(fileRelevance);
            result = result with
            {
                FileRelevance = fileRelevance,
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
        while (result.ContinuationTargets.Count > 1 && GetSerializedByteCount(result) > maximumSerializedBytes)
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

    private static IEnumerable<ToolProvenanceSource> CreateAssociatedArtifactProvenanceSources(
        IReadOnlyList<CodeExploreAssociatedArtifact>? artifacts)
    {
        if (artifacts is null)
        {
            yield break;
        }

        foreach (var artifact in artifacts)
        {
            if (artifact is not { Content: { } content, FilePath: { } filePath })
            {
                continue;
            }

            yield return new ToolProvenanceSource(
                "artifact",
                filePath,
                $"L{content.Range.StartLine}-L{content.Range.EndLine}");
        }
    }

    private static CodeExploreResult TrimAssociatedArtifacts(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        var artifacts = result.AssociatedArtifacts;
        var artifactContinuations = result.ArtifactCoverage?.ContinuationTargets.ToList() ?? [];
        while (artifacts is { Count: > 0 }
            && artifacts.Any(artifact => artifact.Content is not null)
            && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            artifacts = artifacts
                .Select(artifact => TrimAssociatedArtifactContent(
                    artifact,
                    artifactContinuations,
                    result.WorkspaceGeneration))
                .ToArray();
            result = result with
            {
                AssociatedArtifacts = artifacts,
                ArtifactCoverage = UpdateArtifactCoverageAfterTrim(
                    result.ArtifactCoverage,
                    artifacts,
                    artifactContinuations,
                    fileLimitReached: false,
                    characterLimitReached: true),
                Omissions = AddResultBoundOmission(result.Omissions),
            };
        }

        while (artifacts is { Count: > 0 } && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            var retainedCount = artifacts.Count / 2;
            foreach (var artifact in artifacts.Skip(retainedCount))
            {
                AddTrimmedArtifactContentContinuation(
                    artifactContinuations,
                    artifact,
                    result.WorkspaceGeneration);
            }

            artifacts = artifacts.Take(retainedCount).ToArray();
            result = result with
            {
                AssociatedArtifacts = artifacts,
                ArtifactCoverage = UpdateArtifactCoverageAfterTrim(
                    result.ArtifactCoverage,
                    artifacts,
                    artifactContinuations,
                    fileLimitReached: true,
                    characterLimitReached: false),
                Omissions = AddResultBoundOmission(result.Omissions),
            };
        }

        return result;
    }

    private static CodeExploreAssociatedArtifact TrimAssociatedArtifactContent(
        CodeExploreAssociatedArtifact artifact,
        List<CodeExploreArtifactContinuationTarget> continuationTargets,
        long workspaceGeneration)
    {
        if (artifact.Content is null)
        {
            return artifact;
        }

        AddTrimmedArtifactContentContinuation(
            continuationTargets,
            artifact,
            workspaceGeneration);
        return artifact with
        {
            Content = null,
            Omissions = AddResultBoundOmission(artifact.Omissions),
        };
    }

    private static void AddTrimmedArtifactContentContinuation(
        List<CodeExploreArtifactContinuationTarget> continuationTargets,
        CodeExploreAssociatedArtifact artifact,
        long workspaceGeneration)
    {
        if (artifact is not { FilePath: { } filePath, Content: { ReturnedCharacters: > 0 } content })
        {
            return;
        }

        var target = new CodeExploreArtifactContinuationTarget(
            filePath,
            content.Range.StartLine,
            content.Range.EndLine,
            content.FileSha256,
            workspaceGeneration,
            "Retry with this explicit associated artifact path anchor and digest; artifact content was omitted to fit the selected model request budget.");
        if (!continuationTargets.Any(existing => IsSameArtifactContinuation(existing, target)))
        {
            continuationTargets.Add(target);
        }
    }

    private static CodeExploreArtifactCoverage? UpdateArtifactCoverageAfterTrim(
        CodeExploreArtifactCoverage? coverage,
        IReadOnlyList<CodeExploreAssociatedArtifact> artifacts,
        IReadOnlyList<CodeExploreArtifactContinuationTarget> continuationTargets,
        bool fileLimitReached,
        bool characterLimitReached)
    {
        if (coverage is null)
        {
            return null;
        }

        var incompleteReturned = artifacts.Count(IsIncompleteAssociatedArtifact);
        return coverage with
        {
            ReturnedCount = artifacts.Count,
            OmittedCount = Math.Max(
                coverage.OmittedCount,
                Math.Max(0, coverage.CandidateCount - artifacts.Count) + incompleteReturned),
            SpentCharacters = artifacts.Sum(static artifact => artifact.Content?.ReturnedCharacters ?? 0),
            Complete = false,
            FileLimitReached = coverage.FileLimitReached || fileLimitReached,
            CharacterLimitReached = coverage.CharacterLimitReached || characterLimitReached,
            Omissions = AddResultBoundOmission(coverage.Omissions),
            ContinuationTargets = continuationTargets
                .DistinctBy(static target => $"{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.ExpectedFileSha256}:{target.WorkspaceGeneration}:{target.Reason}")
                .ToArray(),
        };
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

    private static bool IsSameArtifactContinuation(
        CodeExploreArtifactContinuationTarget left,
        CodeExploreArtifactContinuationTarget right)
    {
        return string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase)
            && left.StartLine == right.StartLine
            && left.EndLine == right.EndLine
            && string.Equals(left.ExpectedFileSha256, right.ExpectedFileSha256, StringComparison.OrdinalIgnoreCase)
            && left.WorkspaceGeneration == right.WorkspaceGeneration;
    }

    private static CodeExploreResult TrimBackReferences(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        var backReferences = result.BackReferences;
        while (backReferences is { Count: > 0 } && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            backReferences = backReferences.Take(backReferences.Count / 2).ToArray();
            result = result with
            {
                BackReferences = backReferences,
                Deduplication = result.Deduplication is null
                    ? null
                    : result.Deduplication with { SuppressedRanges = backReferences.Count },
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

    private static CodeExploreResult TrimEmissions(
        CodeExploreResult result,
        int maximumSerializedBytes)
    {
        var emissions = result.Emissions;
        while (emissions is { Count: > 0 } && GetSerializedByteCount(result) > maximumSerializedBytes)
        {
            emissions = emissions.Take(emissions.Count / 2).ToArray();
            result = result with
            {
                Emissions = emissions,
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
            var retainedCount = result.FileSections.Count / 2;
            var fileSections = result.FileSections.Take(retainedCount).ToArray();
            var removedSections = result.FileSections.Skip(retainedCount).ToArray();
            var continuationTargets = AddTrimmedSectionContinuations(
                result.ContinuationTargets,
                removedSections,
                result.WorkspaceGeneration);
            result = result with
            {
                FileSections = fileSections,
                ContinuationTargets = continuationTargets,
                Flow = NullRemovedSourceSectionIndexes(result.Flow, fileSections.Length),
                Allocation = TrimAllocationToFileSections(result.Allocation, fileSections),
                Presentation = TrimPresentationToFileSections(
                    result.Presentation,
                    fileSections,
                    removedSections,
                    result.WorkspaceGeneration),
                FileRelevance = MarkFileRelevanceAfterFileSectionTrim(result.FileRelevance, removedSections),
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

    private static IReadOnlyList<CodeExploreContinuationTarget> AddTrimmedSectionContinuations(
        IReadOnlyList<CodeExploreContinuationTarget> existingTargets,
        IReadOnlyList<CodeExploreFileSection> removedSections,
        long workspaceGeneration)
    {
        if (removedSections.Count == 0)
        {
            return existingTargets;
        }

        var targets = existingTargets.ToList();
        foreach (var section in removedSections)
        {
            var target = CreateTrimmedSectionContinuation(section, workspaceGeneration);
            if (!targets.Any(existing => IsSameContinuationTarget(existing, target)))
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    private static CodeExploreContinuationTarget CreateTrimmedSectionContinuation(
        CodeExploreFileSection section,
        long workspaceGeneration)
    {
        return new CodeExploreContinuationTarget(
            CodeExploreAnchorKind.Path,
            section.FilePath,
            section.FilePath,
            section.Source.Range.StartLine,
            section.Source.Range.EndLine,
            false,
            CodeExplorePathSelectionMode.ExactLineRange,
            section.Source.FileSha256,
            workspaceGeneration,
            "Retry with this exact path range; source was removed only to fit the selected model request budget.");
    }

    private static bool IsSameContinuationTarget(
        CodeExploreContinuationTarget left,
        CodeExploreContinuationTarget right)
    {
        return left.Kind == right.Kind
            && string.Equals(left.Anchor, right.Anchor, StringComparison.Ordinal)
            && string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase)
            && left.StartLine == right.StartLine
            && left.EndLine == right.EndLine
            && left.StartAtLine == right.StartAtLine
            && left.SelectionMode == right.SelectionMode
            && string.Equals(left.ExpectedFileSha256, right.ExpectedFileSha256, StringComparison.OrdinalIgnoreCase)
            && left.WorkspaceGeneration == right.WorkspaceGeneration;
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

    private static CodeExplorePresentation? TrimPresentationToFileSections(
        CodeExplorePresentation? presentation,
        IReadOnlyList<CodeExploreFileSection> fileSections,
        IReadOnlyList<CodeExploreFileSection> removedSections,
        long workspaceGeneration)
    {
        if (presentation is null)
        {
            return null;
        }

        var retainedPaths = fileSections
            .Select(section => section.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notShownTargets = presentation.NotShownTargets.ToList();
        foreach (var section in removedSections)
        {
            var target = new CodeExploreNotShownTarget(
                CodeExploreNotShownTargetKind.Source,
                section.FilePath,
                section.Source.Range,
                "Source was removed only to fit the selected model request budget; use the exact path continuation target if needed.",
                section.FilePath,
                section.Source.FileSha256,
                workspaceGeneration);
            if (!notShownTargets.Any(existing => IsSameNotShownTarget(existing, target)))
            {
                notShownTargets.Add(target);
            }
        }

        return presentation with
        {
            SourceGuarantees = presentation.SourceGuarantees
                .Where(guarantee => guarantee.Kind == CodeExploreSourceGuaranteeKind.BackReference
                    || retainedPaths.Contains(guarantee.FilePath))
                .ToArray(),
            NotShownTargets = notShownTargets.ToArray(),
            NextActions = presentation.NextActions
                .Where(action => action.FilePath is null
                    || retainedPaths.Contains(action.FilePath)
                    || action.Kind == CodeExploreNextActionKind.FollowContinuation)
                .ToArray(),
        };
    }

    private static bool IsSameNotShownTarget(
        CodeExploreNotShownTarget left,
        CodeExploreNotShownTarget right)
    {
        return left.Kind == right.Kind
            && string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase)
            && Equals(left.Range, right.Range)
            && string.Equals(left.ContinuationAnchor, right.ContinuationAnchor, StringComparison.Ordinal)
            && string.Equals(left.ExpectedFileSha256, right.ExpectedFileSha256, StringComparison.OrdinalIgnoreCase)
            && left.WorkspaceGeneration == right.WorkspaceGeneration;
    }

    private static IReadOnlyList<CodeExploreFileRelevanceSummary>? MarkFileRelevanceAfterFileSectionTrim(
        IReadOnlyList<CodeExploreFileRelevanceSummary>? fileRelevance,
        IReadOnlyList<CodeExploreFileSection> removedSections)
    {
        if (fileRelevance is null || removedSections.Count == 0)
        {
            return fileRelevance;
        }

        var removedPaths = removedSections
            .Select(section => section.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return fileRelevance
            .Select(summary => removedPaths.Contains(summary.FilePath)
                ? summary with
                {
                    SpentCharacters = 0,
                    OutputStatus = CodeExploreFileOutputStatus.RemovedByModelBudget,
                    Reason = summary.Reason + " Final source was removed during model-budget trimming; use the exact continuation target if needed.",
                }
                : summary)
            .ToArray();
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
        var associatedArtifacts = ConfineAssociatedArtifacts(result.AssociatedArtifacts, context, out var associatedArtifactOmitted);
        var artifactCoverage = ConfineArtifactCoverage(result.ArtifactCoverage, associatedArtifacts, context, associatedArtifactOmitted);
        var backReferences = ConfineBackReferences(result.BackReferences, context, out var backReferenceOmitted);
        var emissions = ConfineEmissions(result.Emissions, context, out var emissionOmitted);
        var availability = ConfineAvailability(result.Availability, context, out var availabilityOmitted);
        var presentation = ConfinePresentation(result.Presentation, context, out var presentationOmitted);
        var fileRelevance = ConfineFileRelevance(result.FileRelevance, context, out var fileRelevanceOmitted);
        var alternativesOmitted = result.ResolvedAnchors
            .Zip(resolutions)
            .Any(item => item.First.Alternatives.Count != item.Second.Alternatives.Count);
        var csharpOmitted = sections.Length != result.FileSections.Count
            || alternativesOmitted
            || resolutions.Any(resolution => resolution.Outcome == CodeExploreResolutionOutcome.Omitted)
            || continuations.Length != result.ContinuationTargets.Count
            || flowOmitted
            || blastRadiusOmitted
            || candidateSummaryOmitted
            || allocationOmitted
            || backReferenceOmitted
            || emissionOmitted
            || availabilityOmitted
            || presentationOmitted
            || fileRelevanceOmitted;
        var omitted = csharpOmitted || associatedArtifactOmitted;
        var hadVisibleSourceBeforeConfinement = HasVisibleCodeExploreSource(
            result.FileSections,
            result.BackReferences);
        var hasVisibleSourceAfterConfinement = HasVisibleCodeExploreSource(
            sections,
            backReferences);
        availability = ReclassifyAvailabilityAfterConfinement(
            availability,
            hadVisibleSourceBeforeConfinement,
            hasVisibleSourceAfterConfinement,
            omitted);
        presentation = UpdatePresentationAfterAvailabilityConfinement(presentation, availability);
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
            AssociatedArtifacts = associatedArtifacts,
            ArtifactCoverage = artifactCoverage,
            BackReferences = backReferences,
            Emissions = emissions,
            Availability = availability,
            Presentation = presentation,
            FileRelevance = fileRelevance,
            Omissions = omissions,
            Coverage = result.Coverage with
            {
                SourceComplete = result.Coverage.SourceComplete && !csharpOmitted,
                OutputComplete = result.Coverage.OutputComplete && !csharpOmitted,
                Omissions = AddPolicyOmission(result.Coverage.Omissions, csharpOmitted),
            },
        };
    }

    private static bool HasVisibleCodeExploreSource(
        IReadOnlyList<CodeExploreFileSection> sections,
        IReadOnlyList<CodeExploreBackReference>? backReferences)
    {
        return sections.Any(section => section.Source.NumberedLines.Count > 0)
            || backReferences is { Count: > 0 };
    }

    private static CodeExploreAvailability? ReclassifyAvailabilityAfterConfinement(
        CodeExploreAvailability? availability,
        bool hadVisibleSourceBeforeConfinement,
        bool hasVisibleSourceAfterConfinement,
        bool omitted)
    {
        if (availability is null
            || availability.Status != CodeExploreAvailabilityStatus.Available
            || !omitted
            || !hadVisibleSourceBeforeConfinement
            || hasVisibleSourceAfterConfinement)
        {
            return availability;
        }

        return availability with
        {
            Status = CodeExploreAvailabilityStatus.NoSourceAfterPolicy,
            Reason = "Post-query path-policy confinement removed all visible code_explore source from this result.",
            IsRetryable = false,
            GranularFallbackMayHelp = true,
            RecommendedActions = [new CodeExploreNextActionHint(
                CodeExploreNextActionKind.AskUser,
                "Ask the user whether the workspace scope or approved roots should be adjusted before retrying.")],
        };
    }

    private static CodeExplorePresentation? UpdatePresentationAfterAvailabilityConfinement(
        CodeExplorePresentation? presentation,
        CodeExploreAvailability? availability)
    {
        if (presentation is null || availability is null || availability.Status == CodeExploreAvailabilityStatus.Available)
        {
            return presentation;
        }

        return presentation with
        {
            ModelSummary = $"Availability: {availability.Status}. {availability.Reason}",
            NextActions = availability.RecommendedActions.Count > 0
                ? availability.RecommendedActions
                : presentation.NextActions,
        };
    }

    private static CodeExploreAvailability? ConfineAvailability(
        CodeExploreAvailability? availability,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (availability is null)
        {
            return null;
        }

        var actions = availability.RecommendedActions
            .Where(action => action.FilePath is null || IsAllowed(action.FilePath, context))
            .ToArray();
        omitted = actions.Length != availability.RecommendedActions.Count;
        return availability with { RecommendedActions = actions };
    }

    private static CodeExplorePresentation? ConfinePresentation(
        CodeExplorePresentation? presentation,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (presentation is null)
        {
            return null;
        }

        var guarantees = presentation.SourceGuarantees
            .Where(guarantee => IsAllowed(guarantee.FilePath, context))
            .ToArray();
        var notShownTargets = presentation.NotShownTargets
            .Where(target => target.FilePath is null || IsAllowed(target.FilePath, context))
            .ToArray();
        var nextActions = presentation.NextActions
            .Where(action => action.FilePath is null || IsAllowed(action.FilePath, context))
            .ToArray();
        omitted = guarantees.Length != presentation.SourceGuarantees.Count
            || notShownTargets.Length != presentation.NotShownTargets.Count
            || nextActions.Length != presentation.NextActions.Count;
        var summary = omitted
            ? "Presentation was confined by invocation path policy; rely on returned structured fields and safe continuations."
            : presentation.ModelSummary;
        return presentation with
        {
            ModelSummary = summary,
            SourceGuarantees = guarantees,
            NotShownTargets = notShownTargets,
            NextActions = nextActions,
        };
    }

    private static IReadOnlyList<CodeExploreFileRelevanceSummary>? ConfineFileRelevance(
        IReadOnlyList<CodeExploreFileRelevanceSummary>? fileRelevance,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (fileRelevance is null)
        {
            return null;
        }

        var confined = fileRelevance
            .Where(summary => IsAllowed(summary.FilePath, context))
            .ToArray();
        omitted = confined.Length != fileRelevance.Count;
        return confined;
    }

    private static IReadOnlyList<CodeExploreBackReference>? ConfineBackReferences(
        IReadOnlyList<CodeExploreBackReference>? backReferences,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (backReferences is null)
        {
            return null;
        }

        var confined = backReferences
            .Where(reference => IsAllowed(reference.FilePath, context))
            .ToArray();
        omitted = confined.Length != backReferences.Count;
        return confined;
    }

    private static IReadOnlyList<CodeExploreEmissionRecord>? ConfineEmissions(
        IReadOnlyList<CodeExploreEmissionRecord>? emissions,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (emissions is null)
        {
            return null;
        }

        var confined = emissions
            .Where(emission => IsAllowed(emission.FilePath, context))
            .ToArray();
        omitted = confined.Length != emissions.Count;
        return confined;
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

    private static IReadOnlyList<CodeExploreAssociatedArtifact>? ConfineAssociatedArtifacts(
        IReadOnlyList<CodeExploreAssociatedArtifact>? artifacts,
        ToolInvocationContext context,
        out bool omitted)
    {
        omitted = false;
        if (artifacts is null)
        {
            return null;
        }

        var confined = artifacts
            .Where(artifact => IsAllowed(artifact.OriginFilePath, context)
                && (artifact.FilePath is null
                    ? !string.IsNullOrWhiteSpace(artifact.LogicalName)
                    : IsAllowed(artifact.FilePath, context)))
            .ToArray();
        omitted = confined.Length != artifacts.Count;
        return confined;
    }

    private static CodeExploreArtifactCoverage? ConfineArtifactCoverage(
        CodeExploreArtifactCoverage? coverage,
        IReadOnlyList<CodeExploreAssociatedArtifact>? artifacts,
        ToolInvocationContext context,
        bool alreadyOmitted)
    {
        if (coverage is null)
        {
            return null;
        }

        var continuations = coverage.ContinuationTargets
            .Where(target => IsAllowed(target.FilePath, context))
            .ToArray();
        var omitted = alreadyOmitted || continuations.Length != coverage.ContinuationTargets.Count;
        return coverage with
        {
            ReturnedCount = artifacts?.Count ?? 0,
            ContinuationTargets = continuations,
            Complete = coverage.Complete && !omitted,
            Omissions = AddPolicyOmission(coverage.Omissions, omitted),
        };
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
        var artifactsTruncated = result.ArtifactCoverage is { Complete: false }
            || (result.AssociatedArtifacts is { } artifacts && artifacts.Any(artifact => artifact.Omissions.Count > 0
                || artifact.Content is { Completeness: not CodeExploreSourceCompleteness.Complete }));
        return artifactsTruncated
            || !result.Coverage.SymbolResolutionComplete
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

    private sealed class PolicyCodeExploreSourceReader : ICodeExploreSourceReader, ICodeExploreArtifactReader
    {
        private const int MaximumReadableFileBytes = 1024 * 1024;
        private const int MaximumArtifactEnumerationEntries = 5000;
        private const int MaximumGitInventoryOutputCharacters = 512 * 1024;

        private static readonly HashSet<string> ArtifactExcludedDirectories = new(
            [".codegraph", ".git", ".hg", ".svn", ".vs", ".idea", "bin", "obj", "node_modules", "packages", ".nuget", "TestResults", "artifacts", "dist", "out", "coverage"],
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SensitiveArtifactNames = new(
            [".env", ".env.local", ".env.development", ".env.production", "secrets.json", "appsettings.secrets.json", "credentials.json"],
            StringComparer.OrdinalIgnoreCase);

        private readonly ToolExecutionContext _executionContext;
        private readonly ToolInvocationContext _context;
        private readonly IProcessManager? _processManager;
        private readonly Dictionary<string, CodeExploreArtifactDirectoryInventory> _artifactInventories = new(HostPathComparer);

        internal PolicyCodeExploreSourceReader(
            ToolExecutionContext context,
            IProcessManager? processManager)
        {
            ArgumentNullException.ThrowIfNull(context);
            _executionContext = context;
            _context = context.Invocation;
            _processManager = processManager;
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

        public bool IsSupportedTextArtifactPath(string path)
        {
            return ProbeArtifactPath(path).IsSupported;
        }

        public CodeExploreArtifactPathProbe ProbeArtifactPath(string path)
        {
            try
            {
                var normalized = ToolPathRules.NormalizeAndValidate(path, _context);
                if (GetArtifactPathPolicyRejectionReason(normalized) is { } policyReason)
                {
                    return RejectedArtifactPath(policyReason);
                }

                if (!TryClassifyArtifactMedia(normalized, out var mediaKind))
                {
                    return RejectedArtifactPath("media type is not supported for associated artifacts.");
                }

                var info = new FileInfo(normalized);
                if (!info.Exists)
                {
                    return RejectedArtifactPath("file does not exist.");
                }

                if (!IsExistingRegularFile(info))
                {
                    return RejectedArtifactPath("path is not a regular non-reparse file.");
                }

                return new CodeExploreArtifactPathProbe(true, mediaKind, null);
            }
            catch (Exception exception) when (exception is ToolArgumentValidationException
                or UnauthorizedAccessException
                or IOException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
            {
                return RejectedArtifactPath("path was denied by repository path policy.");
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

        public async Task<CodeExploreArtifactText> ReadArtifactTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            var normalized = ToolPathRules.NormalizeAndValidate(path, _context);
            var effectiveMaximumBytes = Math.Min(maximumBytes, MaximumReadableFileBytes);
            var mediaKind = ValidateReadableArtifactFile(normalized, effectiveMaximumBytes);
            var before = new FileInfo(normalized);
            var beforeLength = before.Length;
            var beforeLastWriteTimeUtc = before.LastWriteTimeUtc;
            await using var stream = new FileStream(
                normalized,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!stream.CanSeek)
            {
                throw new UnauthorizedAccessException("Code exploration reads only seekable regular artifact files.");
            }

            if (stream.Length > effectiveMaximumBytes)
            {
                throw new InvalidOperationException($"The associated artifact exceeds the {effectiveMaximumBytes}-byte code exploration read limit.");
            }

            var bytes = await ReadBoundedBytesAsync(stream, effectiveMaximumBytes, cancellationToken);
            var after = new FileInfo(normalized);
            if (beforeLength != after.Length || beforeLastWriteTimeUtc != after.LastWriteTimeUtc)
            {
                throw new InvalidOperationException("The associated artifact changed while it was being read; content was omitted as stale.");
            }

            var text = await DecodeSourceTextAsync(bytes, cancellationToken);
            ValidateTextualArtifactShape(text);
            return new CodeExploreArtifactText(normalized, text, ComputeSha256(bytes), mediaKind, beforeLength);
        }

        public async Task<CodeExploreArtifactFileSearchResult> FindArtifactFilesByNameAsync(
            string directoryPath,
            string fileName,
            int maximumMatches,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            if (maximumMatches <= 0 || !IsSafeExactArtifactFileName(fileName))
            {
                return new CodeExploreArtifactFileSearchResult([], 0, maximumMatches <= 0);
            }

            var directory = ToolPathRules.NormalizeAndValidate(directoryPath, _context);
            if (!Directory.Exists(directory) || !IsArtifactSearchDirectory(directory))
            {
                return new CodeExploreArtifactFileSearchResult([], 0, false);
            }

            return await FindArtifactFilesByNameFromGitInventoryAsync(
                directory,
                fileName,
                maximumMatches,
                cancellationToken);
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

        private CodeExploreArtifactMediaKind ValidateReadableArtifactFile(string normalized, int effectiveMaximumBytes)
        {
            var info = new FileInfo(normalized);
            if (!info.Exists)
            {
                throw new FileNotFoundException("The requested associated artifact does not exist.", normalized);
            }

            if (!IsExistingRegularFile(info))
            {
                throw new UnauthorizedAccessException("Code exploration reads only regular non-reparse artifact files.");
            }

            if (!IsArtifactPathInspectable(normalized) || !TryClassifyArtifactMedia(normalized, out var mediaKind))
            {
                throw new UnauthorizedAccessException("The associated artifact path or media type is not supported by code exploration policy.");
            }

            if (info.Length > effectiveMaximumBytes)
            {
                throw new InvalidOperationException($"The associated artifact exceeds the {effectiveMaximumBytes}-byte code exploration read limit.");
            }

            return mediaKind;
        }

        private static bool IsExistingRegularFile(FileSystemInfo info)
        {
            return info.Exists
                && (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
        }

        private bool IsArtifactPathInspectable(string normalized)
        {
            return GetArtifactPathPolicyRejectionReason(normalized) is null;
        }

        private string? GetArtifactPathPolicyRejectionReason(string normalized)
        {
            var relative = Path.GetRelativePath(_context.RepositoryPath, normalized).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || ToolPathRules.IsProhibited(relative, _context.ProhibitedPaths))
            {
                return "path is outside the approved repository scope or is prohibited by invocation policy.";
            }

            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return "path does not identify a repository file.";
            }

            if (parts.Any(ArtifactExcludedDirectories.Contains))
            {
                return "path is in a Git, generated, dependency, or build-output directory.";
            }

            if (parts.Any(IsSensitiveArtifactNameOrSegment))
            {
                return "path is excluded by secret or credential sensitivity policy.";
            }

            if (ToolPathRules.ContainsReservedWindowsDeviceName(relative))
            {
                return "path contains a reserved Windows device name.";
            }

            return null;
        }

        private static CodeExploreArtifactPathProbe RejectedArtifactPath(string reason)
        {
            return new CodeExploreArtifactPathProbe(false, null, reason);
        }

        private bool IsArtifactSearchDirectory(string normalizedDirectory)
        {
            var info = new DirectoryInfo(normalizedDirectory);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var relative = Path.GetRelativePath(_context.RepositoryPath, normalizedDirectory).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || ToolPathRules.IsProhibited(relative, _context.ProhibitedPaths))
            {
                return false;
            }

            return relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .All(part => !ArtifactExcludedDirectories.Contains(part)
                    && !IsSensitiveArtifactNameOrSegment(part));
        }

        private static bool IsSensitiveArtifactNameOrSegment(string value)
        {
            return SensitiveArtifactNames.Contains(value)
                || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || value.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || value.Contains("token", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafeExactArtifactFileName(string fileName)
        {
            return !string.IsNullOrWhiteSpace(fileName)
                && fileName.Length <= 255
                && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && !fileName.Contains('/')
                && !fileName.Contains('\\')
                && !SensitiveArtifactNames.Contains(fileName)
                && !IsSensitiveArtifactNameOrSegment(fileName)
                && TryClassifyArtifactMedia(fileName, out _);
        }

        private async Task<CodeExploreArtifactFileSearchResult> FindArtifactFilesByNameFromGitInventoryAsync(
            string directory,
            string fileName,
            int maximumMatches,
            CancellationToken cancellationToken)
        {
            var inventory = await GetArtifactDirectoryInventoryFromGitAsync(directory, cancellationToken);
            var sortedMatches = inventory.Files
                .Where(match => string.Equals(Path.GetFileName(match.Path), fileName, HostPathComparison))
                .OrderBy(match => match.Path, HostPathComparer)
                .ToArray();
            var limitedMatches = sortedMatches.Take(maximumMatches).ToArray();
            var truncated = inventory.Truncated || limitedMatches.Length < sortedMatches.Length;
            return new CodeExploreArtifactFileSearchResult(
                limitedMatches,
                inventory.InspectedEntries,
                truncated);
        }

        private async Task<CodeExploreArtifactDirectoryInventory> GetArtifactDirectoryInventoryFromGitAsync(
            string directory,
            CancellationToken cancellationToken)
        {
            var inventoryKey = Path.GetFullPath(directory);
            if (_artifactInventories.TryGetValue(inventoryKey, out var cachedInventory))
            {
                return cachedInventory;
            }

            var inventory = await LoadArtifactDirectoryInventoryFromGitAsync(
                directory,
                cancellationToken);
            _artifactInventories[inventoryKey] = inventory;
            return inventory;
        }

        private async Task<CodeExploreArtifactDirectoryInventory> LoadArtifactDirectoryInventoryFromGitAsync(
            string directory,
            CancellationToken cancellationToken)
        {
            if (_processManager is null)
            {
                return new CodeExploreArtifactDirectoryInventory([], 0, true);
            }

            var relativeDirectory = Path.GetRelativePath(_context.RepositoryPath, directory).Replace('\\', '/');
            if (relativeDirectory.Length == 0 || relativeDirectory == ".")
            {
                relativeDirectory = ".";
            }

            ProcessExecutionResult result;
            try
            {
                result = await _processManager.RunAsync(
                    new ProcessExecutionRequest
                    {
                        ToolInvocationId = _executionContext.ToolInvocationId,
                        RunId = _executionContext.RunId,
                        FileName = "git",
                        Arguments =
                        [
                            "--no-pager",
                            "--literal-pathspecs",
                            "-c",
                            "color.ui=false",
                            "-c",
                            "core.pager=cat",
                            "-c",
                            "core.quotepath=false",
                            "-c",
                            "core.fsmonitor=false",
                            "ls-files",
                            "--cached",
                            "--others",
                            "--exclude-standard",
                            "-z",
                            "--",
                            relativeDirectory,
                        ],
                        WorkingDirectory = _context.RepositoryPath,
                        Timeout = TimeSpan.FromSeconds(10),
                        MaximumOutputCharacters = MaximumGitInventoryOutputCharacters,
                        StandardOutputFormat = ProcessStandardOutputFormat.NullDelimitedJsonArray,
                        EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["GIT_OPTIONAL_LOCKS"] = "0",
                            ["GIT_TERMINAL_PROMPT"] = "0",
                            ["GIT_CONFIG_NOSYSTEM"] = "1",
                        },
                        Origin = ProcessRequestOrigin.Host,
                    },
                    cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return new CodeExploreArtifactDirectoryInventory([], 0, true);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
            {
                return new CodeExploreArtifactDirectoryInventory([], 0, true);
            }

            if (result.TimedOut || result.ExitCode != 0)
            {
                return new CodeExploreArtifactDirectoryInventory([], 0, true);
            }

            string[] relativePaths;
            try
            {
                relativePaths = JsonSerializer.Deserialize<string[]>(result.StandardOutput) ?? [];
            }
            catch (JsonException)
            {
                return new CodeExploreArtifactDirectoryInventory([], 0, true);
            }

            var inspected = 0;
            var files = new List<CodeExploreArtifactFileMatch>();
            foreach (var relativePath in relativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (relativePath.Length == 0)
                {
                    continue;
                }

                inspected++;
                if (inspected > MaximumArtifactEnumerationEntries)
                {
                    var cappedFiles = files
                        .OrderBy(match => match.Path, HostPathComparer)
                        .ToArray();
                    return new CodeExploreArtifactDirectoryInventory(cappedFiles, inspected, true);
                }

                if (relativePath.Any(char.IsControl))
                {
                    continue;
                }

                string normalizedPath;
                try
                {
                    normalizedPath = ToolPathRules.NormalizeAndValidate(relativePath, _context);
                }
                catch (Exception exception) when (exception is ToolArgumentValidationException
                    or UnauthorizedAccessException
                    or IOException)
                {
                    continue;
                }

                if (!IsSupportedTextArtifactPath(normalizedPath)
                    || !TryClassifyArtifactMedia(normalizedPath, out var mediaKind))
                {
                    continue;
                }

                files.Add(new CodeExploreArtifactFileMatch(normalizedPath, mediaKind));
            }

            var sortedFiles = files
                .OrderBy(match => match.Path, HostPathComparer)
                .ToArray();
            return new CodeExploreArtifactDirectoryInventory(
                sortedFiles,
                inspected,
                result.StandardOutputTruncated);
        }

        private sealed record CodeExploreArtifactDirectoryInventory(
            IReadOnlyList<CodeExploreArtifactFileMatch> Files,
            int InspectedEntries,
            bool Truncated);

        private static StringComparison HostPathComparison
            => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static StringComparer HostPathComparer
            => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static bool TryClassifyArtifactMedia(
            string path,
            out CodeExploreArtifactMediaKind mediaKind)
        {
            var fileName = Path.GetFileName(path);
            var extension = Path.GetExtension(path);
            if (fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(".globalconfig", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".config", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.Configuration;
                return true;
            }

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = fileName.Contains("schema", StringComparison.OrdinalIgnoreCase)
                    ? CodeExploreArtifactMediaKind.Schema
                    : fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                        ? CodeExploreArtifactMediaKind.Configuration
                        : CodeExploreArtifactMediaKind.Json;
                return true;
            }

            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.ProjectMetadata;
                return true;
            }

            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".resx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ruleset", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.Xml;
                return true;
            }

            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = fileName.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("template", StringComparison.OrdinalIgnoreCase)
                        ? CodeExploreArtifactMediaKind.Prompt
                        : CodeExploreArtifactMediaKind.Markdown;
                return true;
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
                mediaKind = CodeExploreArtifactMediaKind.Prompt;
                return true;
            }

            if (extension.Equals(".schema", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".graphql", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".gql", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.Schema;
                return true;
            }

            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".http", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".rest", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.Text;
                return true;
            }

            mediaKind = CodeExploreArtifactMediaKind.Text;
            return false;
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
                    throw new InvalidOperationException($"The file exceeds the {maximumBytes}-byte code exploration read limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        private static Task<string> DecodeSourceTextAsync(
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoding = DetectStrictTextEncoding(bytes, out var offset);
            var text = encoding.GetString(bytes, offset, bytes.Length - offset);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(text);
        }

        private static Encoding DetectStrictTextEncoding(byte[] bytes, out int offset)
        {
            offset = 0;
            if (bytes.Length >= 4
                && bytes[0] == 0xFF
                && bytes[1] == 0xFE
                && bytes[2] == 0x00
                && bytes[3] == 0x00)
            {
                offset = 4;
                return new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
            }

            if (bytes.Length >= 4
                && bytes[0] == 0x00
                && bytes[1] == 0x00
                && bytes[2] == 0xFE
                && bytes[3] == 0xFF)
            {
                offset = 4;
                return new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
            }

            if (bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF)
            {
                offset = 3;
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                offset = 2;
                return new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                offset = 2;
                return new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }

        private static void ValidateTextualArtifactShape(string text)
        {
            foreach (var character in text)
            {
                if (character is '\t' or '\n' or '\r' or '\f')
                {
                    continue;
                }

                if (char.IsControl(character))
                {
                    throw new InvalidOperationException("The associated artifact decoded as UTF-8 but contains binary-shaped control data.");
                }
            }
        }

        private static string ComputeSha256(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}

/// <summary>Creates and parses host-owned code_explore continuation cursors carried through the minimal query schema.</summary>
internal static class CodeExploreContinuationCursor
{
    /// <summary>Prefix that marks a pasteable code_explore continuation query cursor.</summary>
    internal const string Prefix = "code_explore:continue:";

    private const int MaximumCursorCharacters = 960;
    private const int MaximumPayloadBytes = 4096;
    private const int MaximumEmbeddedQueryCharacters = 240;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Creates a replay cursor for an omitted or partial C# source continuation.</summary>
    internal static string? CreateSource(CodeExploreContinuationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Create(new CursorPayload
        {
            Version = 1,
            Type = "source",
            Kind = target.Kind.ToString(),
            Anchor = target.Anchor,
            Path = target.FilePath,
            StartLine = target.StartLine,
            EndLine = target.EndLine,
            StartAtLine = target.StartAtLine,
            SelectionMode = target.SelectionMode?.ToString(),
            ExpectedFileSha256 = target.ExpectedFileSha256,
            WorkspaceGeneration = target.WorkspaceGeneration,
            Mode = nameof(CodeExploreMode.Auto),
        });
    }

    /// <summary>Creates a replay cursor for a compact blast-radius continuation.</summary>
    internal static string? CreateImpact(CodeExploreContinuationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Create(new CursorPayload
        {
            Version = 1,
            Type = "impact",
            Kind = target.Kind.ToString(),
            Anchor = target.Anchor,
            Path = target.FilePath,
            StartLine = target.StartLine,
            EndLine = target.EndLine,
            StartAtLine = target.StartAtLine,
            SelectionMode = target.SelectionMode?.ToString(),
            ExpectedFileSha256 = target.ExpectedFileSha256,
            WorkspaceGeneration = target.WorkspaceGeneration,
            Mode = nameof(CodeExploreMode.Impact),
        });
    }

    /// <summary>Creates a replay cursor for an omitted or partial associated-artifact continuation.</summary>
    internal static string? CreateArtifact(
        CodeExploreArtifactContinuationTarget target,
        CodeExploreAssociatedArtifact? artifact,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Create(new CursorPayload
        {
            Version = 1,
            Type = "artifact",
            Path = target.FilePath,
            StartLine = target.StartLine,
            EndLine = target.EndLine,
            ExpectedFileSha256 = target.ExpectedFileSha256,
            WorkspaceGeneration = target.WorkspaceGeneration,
            Query = BoundEmbeddedQuery(query),
            OriginSymbolId = artifact?.OriginSymbolId,
            OriginPath = artifact?.OriginFilePath,
            OriginStartLine = artifact?.OriginRange.StartLine,
            OriginEndLine = artifact?.OriginRange.EndLine,
            Mode = nameof(CodeExploreMode.Auto),
        });
    }

    /// <summary>Attempts to convert a query-carried continuation cursor back into a host-owned code_explore request.</summary>
    internal static bool TryCreateRequest(
        string query,
        CodeExploreLimits limits,
        int maximumFiles,
        out CodeExploreRequest? request)
    {
        ArgumentNullException.ThrowIfNull(limits);
        request = null;
        if (!TryReadPayload(query, out var payload) || payload.Version != 1)
        {
            return false;
        }

        var boundedLimits = limits with { MaximumFiles = maximumFiles };
        if (string.Equals(payload.Type, "artifact", StringComparison.Ordinal))
        {
            return TryCreateArtifactRequest(payload, boundedLimits, out request);
        }

        if (string.Equals(payload.Type, "impact", StringComparison.Ordinal))
        {
            return TryCreateSourceOrImpactRequest(payload, boundedLimits, CodeExploreMode.Impact, out request);
        }

        if (string.Equals(payload.Type, "source", StringComparison.Ordinal))
        {
            var mode = TryParseEnum(payload.Mode, out CodeExploreMode parsedMode)
                ? parsedMode
                : CodeExploreMode.Auto;
            return TryCreateSourceOrImpactRequest(payload, boundedLimits, mode, out request);
        }

        return false;
    }

    private static bool TryCreateArtifactRequest(
        CursorPayload payload,
        CodeExploreLimits limits,
        out CodeExploreRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(payload.Path))
        {
            return false;
        }

        var query = FirstNonWhiteSpace(
            payload.Query,
            payload.OriginSymbolId,
            payload.OriginPath,
            payload.Path);
        var pathAnchors = CreateArtifactOriginAnchors(payload);
        request = new CodeExploreRequest
        {
            Query = query,
            Mode = CodeExploreMode.Auto,
            AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
            SymbolIds = string.IsNullOrWhiteSpace(payload.OriginSymbolId)
                ? []
                : [payload.OriginSymbolId],
            PathAnchors = pathAnchors,
            AssociatedArtifactPathAnchors =
            [
                new CodeExploreArtifactPathAnchor
                {
                    Path = payload.Path,
                    Line = payload.StartLine,
                    EndLine = payload.EndLine,
                    ExpectedFileSha256 = payload.ExpectedFileSha256,
                    ExpectedWorkspaceGeneration = payload.WorkspaceGeneration,
                },
            ],
            Limits = limits,
        };
        return true;
    }

    private static IReadOnlyList<CodeExplorePathAnchor> CreateArtifactOriginAnchors(
        CursorPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.OriginSymbolId)
            || string.IsNullOrWhiteSpace(payload.OriginPath))
        {
            return [];
        }

        var selectionMode = payload.OriginStartLine is null
            ? CodeExplorePathSelectionMode.WholeFile
            : payload.OriginEndLine is null
                ? CodeExplorePathSelectionMode.ContainingDeclaration
                : CodeExplorePathSelectionMode.ExactLineRange;
        return
        [
            new CodeExplorePathAnchor
            {
                Path = payload.OriginPath,
                Line = payload.OriginStartLine,
                EndLine = payload.OriginEndLine,
                SelectionMode = selectionMode,
                ExpectedWorkspaceGeneration = payload.WorkspaceGeneration,
            },
        ];
    }

    private static bool TryCreateSourceOrImpactRequest(
        CursorPayload payload,
        CodeExploreLimits limits,
        CodeExploreMode mode,
        out CodeExploreRequest? request)
    {
        request = null;
        if (!TryParseEnum(payload.Kind, out CodeExploreAnchorKind kind))
        {
            return false;
        }

        var anchor = FirstNonWhiteSpace(payload.Anchor, payload.Path);
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return false;
        }

        if (kind == CodeExploreAnchorKind.Path || !string.IsNullOrWhiteSpace(payload.Path))
        {
            var path = FirstNonWhiteSpace(payload.Path, anchor);
            var selectionMode = TryParseEnum(payload.SelectionMode, out CodeExplorePathSelectionMode parsedSelectionMode)
                ? parsedSelectionMode
                : CodeExplorePathSelectionMode.Auto;
            request = new CodeExploreRequest
            {
                Query = path,
                Mode = mode,
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = path,
                        Line = payload.StartLine,
                        EndLine = payload.EndLine,
                        StartAtLine = payload.StartAtLine,
                        SelectionMode = selectionMode,
                        ExpectedFileSha256 = payload.ExpectedFileSha256,
                        ExpectedWorkspaceGeneration = payload.WorkspaceGeneration,
                    },
                ],
                Limits = limits,
            };
            return true;
        }

        if (kind == CodeExploreAnchorKind.SymbolId)
        {
            request = new CodeExploreRequest
            {
                Query = anchor,
                Mode = mode,
                SymbolIds = [anchor],
                Limits = limits,
            };
            return true;
        }

        request = new CodeExploreRequest
        {
            Query = anchor,
            Mode = mode,
            ExactSymbolAnchors = [anchor],
            Limits = limits,
        };
        return true;
    }

    private static string? Create(CursorPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        var cursor = Prefix + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return cursor.Length <= MaximumCursorCharacters ? cursor : null;
    }

    private static bool TryReadPayload(string query, out CursorPayload payload)
    {
        payload = new CursorPayload();
        var token = FindCursorToken(query);
        if (token is null || !TryDecodeBase64Url(token, out var bytes))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CursorPayload>(bytes, SerializerOptions);
            if (parsed is null)
            {
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string? FindCursorToken(string query)
    {
        var start = query.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var tokenStart = start + Prefix.Length;
        var tokenEnd = tokenStart;
        while (tokenEnd < query.Length && IsBase64UrlCharacter(query[tokenEnd]))
        {
            tokenEnd++;
        }

        return tokenEnd == tokenStart ? null : query[tokenStart..tokenEnd];
    }

    private static bool TryDecodeBase64Url(string token, out byte[] bytes)
    {
        bytes = [];
        var base64 = token.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => string.Empty,
        };
        if (base64.Length == 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(base64);
            return bytes.Length <= MaximumPayloadBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: false, out result) && Enum.IsDefined(result);
    }

    private static string FirstNonWhiteSpace(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string? BoundEmbeddedQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        if (query.Length <= MaximumEmbeddedQueryCharacters)
        {
            return query;
        }

        var length = MaximumEmbeddedQueryCharacters;
        if (char.IsHighSurrogate(query[length - 1])
            && length < query.Length
            && char.IsLowSurrogate(query[length]))
        {
            length--;
        }

        return query[..length];
    }

    private static bool IsBase64UrlCharacter(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
    }

    private sealed record CursorPayload
    {
        public int Version { get; init; }

        public string Type { get; init; } = string.Empty;

        public string? Kind { get; init; }

        public string? Anchor { get; init; }

        public string? Path { get; init; }

        public int? StartLine { get; init; }

        public int? EndLine { get; init; }

        public bool StartAtLine { get; init; }

        public string? SelectionMode { get; init; }

        public string? ExpectedFileSha256 { get; init; }

        public long? WorkspaceGeneration { get; init; }

        public string? Mode { get; init; }

        public string? Query { get; init; }

        public string? OriginSymbolId { get; init; }

        public string? OriginPath { get; init; }

        public int? OriginStartLine { get; init; }

        public int? OriginEndLine { get; init; }
    }
}
