namespace Threadsmith.Tools;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Decorates code_explore with configurable model-visible output formatting.</summary>
public sealed class CodeExploreOutputFormattingTool : ITool, IPostSanitizationToolOutputBoundary
{
    private readonly ITool _inner;
    private readonly CodeExploreOutputOptions _options;
    private readonly IPromptLoader _prompts;
    private readonly CodeExploreMarkdownRenderer _renderer;

    /// <summary>Initializes a new instance of the <see cref="CodeExploreOutputFormattingTool" /> class.</summary>
    public CodeExploreOutputFormattingTool(
        ITool inner,
        CodeExploreOutputOptions options,
        IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        _inner = inner;
        _options = options;
        _prompts = prompts;
        _renderer = new CodeExploreMarkdownRenderer(prompts);
    }

    /// <inheritdoc />
    public ToolDefinition Definition => _inner.Definition;

    /// <inheritdoc />
    public object DeserializeInput(string argumentsJson)
    {
        return _inner.DeserializeInput(argumentsJson);
    }

    /// <inheritdoc />
    public string? GetActivityDetail(object input)
    {
        return _inner.GetActivityDetail(input);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context)
    {
        return _inner.GetResourcePaths(input, context);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(object input)
    {
        return _inner.GetSecretReferences(input);
    }

    /// <inheritdoc />
    public string? GetExecutable(object input)
    {
        return _inner.GetExecutable(input);
    }

    /// <inheritdoc />
    public string? GetExecutable(object input, ToolInvocationContext context)
    {
        return _inner.GetExecutable(input, context);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(object input)
    {
        return _inner.GetNetworkHosts(input);
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolResourceClaim> GetSchedulingClaims(
        object input,
        ToolInvocationContext context)
    {
        return _inner.GetSchedulingClaims(input, context);
    }

    /// <inheritdoc />
    public async Task<ToolExecutionEnvelope> ExecuteAsync(
        object input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var outputFormat = _options.GetOutputFormat(context.SessionId);
        var execution = await _inner.ExecuteAsync(input, context, cancellationToken);
        if (outputFormat != CodeExploreOutputFormat.Markdown)
        {
            return execution;
        }

        var query = input is CodeExploreInput codeExploreInput
            ? codeExploreInput.Query
            : null;
        if (execution.Value is not CodeExploreResult result)
        {
            return execution;
        }

        var markdown = _renderer.Render(
            result,
            query,
            CodeExploreModelBudget.GetMaximumResultBytes(context.Invocation),
            out var markdownTruncated);
        return execution with
        {
            IsTruncated = execution.IsTruncated || markdownTruncated,
            ModelResultContent = markdown,
        };
    }

    /// <inheritdoc />
    PostSanitizationToolOutput IPostSanitizationToolOutputBoundary.BoundSanitizedOutput(
        string resultJson,
        string? modelResultContent,
        ToolInvocationContext context)
    {
        if (CodeExploreModelBudget.GetMaximumResultBytes(context) is not { } maximumBytes)
        {
            return new PostSanitizationToolOutput(resultJson, modelResultContent, false);
        }

        var resultWasTruncated = Encoding.UTF8.GetByteCount(resultJson) > maximumBytes;
        var markdownWasTruncated = modelResultContent is not null
            && Encoding.UTF8.GetByteCount(modelResultContent) > maximumBytes;
        if (resultWasTruncated || markdownWasTruncated)
        {
            var sanitizedResult = JsonSerializer.Deserialize<CodeExploreResult>(resultJson)
                ?? throw new InvalidOperationException("The sanitized code exploration result could not be deserialized.");
            if (resultWasTruncated)
            {
                resultJson = JsonSerializer.Serialize(
                    CodeExploreTool.BoundResultToMaximumBytes(sanitizedResult, maximumBytes, _prompts));
            }

            if (markdownWasTruncated && modelResultContent is not null)
            {
                modelResultContent = _renderer.RenderAfterSanitization(
                    sanitizedResult,
                    modelResultContent,
                    maximumBytes);
            }
        }

        return new PostSanitizationToolOutput(
            resultJson,
            modelResultContent,
            resultWasTruncated || markdownWasTruncated);
    }
}

/// <summary>Renders a compact source-first model projection from authoritative code-explore DTOs.</summary>
internal sealed class CodeExploreMarkdownRenderer
{
    private const int MaximumImpactItems = 10;
    private const int MaximumImpactItemsPerKind = 2;
    private const int MaximumFlowEdges = 24;
    private const int MaximumFlowBoundaries = 12;
    private const int MaximumAssociatedArtifacts = 4;
    private const int MaximumArtifactOmissions = 4;
    private const int MaximumBackReferences = 16;
    private const int MaximumOptionalContinuations = 6;
    private const int MaximumOptionalContinuationsPerKind = 2;
    private const int MaximumContinuationCursors = 3;
    private const int MaximumNextActions = 8;
    private const int MaximumOmissions = 12;
    private const int MaximumSelectedEvidenceItems = 8;
    private const int MaximumSemanticIdentities = 8;
    private const int MaximumRenderedMarkdownBytes = 25_000;
    private const double AdaptiveRenderedMarkdownMultiplier = 1.5;
    private readonly string _explorationPrefix;
    private readonly string _explorationSuffix;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="CodeExploreMarkdownRenderer"/> class.</summary>
    internal CodeExploreMarkdownRenderer(IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
        (_explorationPrefix, _explorationSuffix) = GetExplorationFrame(prompts);
    }

    /// <summary>Renders one concise source-first exploration result.</summary>
    internal string Render(
        CodeExploreResult result,
        string? query = null,
        int? maximumUtf8Bytes = null)
    {
        return Render(result, query, maximumUtf8Bytes, out _);
    }

    /// <summary>Re-renders sanitized output while preserving its sanitized exploration query.</summary>
    internal string RenderAfterSanitization(
        CodeExploreResult result,
        string sanitizedMarkdown,
        int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sanitizedMarkdown);
        return Render(
            result,
            ExtractExplorationQuery(sanitizedMarkdown),
            maximumUtf8Bytes);
    }

    /// <summary>Renders one concise exploration result and reports model-budget truncation.</summary>
    internal string Render(
        CodeExploreResult result,
        string? query,
        int? maximumUtf8Bytes,
        out bool wasTruncated)
    {
        ArgumentNullException.ThrowIfNull(result);
        var maximumBytes = ResolveRenderedMarkdownMaximumBytes(result, maximumUtf8Bytes);
        var projected = result;
        var markdown = RenderUnbounded(projected, query);
        wasTruncated = Encoding.UTF8.GetByteCount(markdown) > maximumBytes;
        if (!wasTruncated)
        {
            return markdown;
        }

        var contentMaximumBytes = GetModelBudgetContentMaximumBytes(maximumBytes);
        if (contentMaximumBytes <= 0)
        {
            return AppendModelBudgetOmission(string.Empty, maximumBytes);
        }

        if (projected.AssociatedArtifacts is { Count: > 0 })
        {
            projected = RemoveArtifactsWithContinuations(projected);
            markdown = RenderUnbounded(projected, query);
        }

        if (Encoding.UTF8.GetByteCount(markdown) > contentMaximumBytes)
        {
            projected = CompactSecondaryEvidenceForMarkdown(projected);
            markdown = RenderUnbounded(projected, query);
        }

        if (Encoding.UTF8.GetByteCount(markdown) > contentMaximumBytes
            && projected.FileSections.Count > 0)
        {
            projected = FitSourceSections(projected, query, contentMaximumBytes);
            markdown = RenderWithCompactContinuationFooter(projected, query);
        }

        var boundedContent = Encoding.UTF8.GetByteCount(markdown) > contentMaximumBytes
            ? BoundMarkdownPreservingContinuations(projected, query, contentMaximumBytes)
            : markdown;
        return AppendModelBudgetOmission(boundedContent, maximumBytes);
    }

    /// <summary>Bounds Markdown by complete UTF-8 lines while preserving fence balance.</summary>
    internal string BoundMarkdownToUtf8Bytes(string markdown, int maximumBytes)
    {
        return BoundMarkdownToUtf8BytesCore(markdown, maximumBytes);
    }

    private CodeExploreResult FitSourceSections(
        CodeExploreResult result,
        string? query,
        int maximumBytes)
    {
        var sections = result.FileSections.ToArray();
        for (var retainedCount = sections.Length; retainedCount >= 1; retainedCount--)
        {
            var candidate = retainedCount == sections.Length
                ? result
                : ProjectSourceSectionPrefix(result, sections, retainedCount);
            if (FitsWithCompactContinuationFooter(candidate, query, maximumBytes))
            {
                return candidate;
            }
        }

        return ProjectSourceSectionPrefix(result, sections, 0);
    }

    private bool FitsWithCompactContinuationFooter(
        CodeExploreResult result,
        string? query,
        int maximumBytes)
    {
        return Encoding.UTF8.GetByteCount(RenderWithCompactContinuationFooter(result, query)) <= maximumBytes;
    }

    private CodeExploreResult ProjectSourceSectionPrefix(
        CodeExploreResult result,
        IReadOnlyList<CodeExploreFileSection> sections,
        int retainedCount)
    {
        var retained = sections.Take(retainedCount).ToArray();
        var removedSectionContinuations = sections.Skip(retainedCount).Select(section => new CodeExploreContinuationTarget(
                CodeExploreAnchorKind.Path,
                section.FilePath,
                section.FilePath,
                section.Source.Range.StartLine,
                section.Source.Range.EndLine,
                false,
                CodeExplorePathSelectionMode.ExactLineRange,
                section.Source.FileSha256,
                result.WorkspaceGeneration,
                RenderPrompt(PromptFileNames.ToolCodeExploreMarkdownSourceOmissionReason)));
        var continuations = removedSectionContinuations
            .Concat(result.ContinuationTargets)
            .DistinctBy(ContinuationIdentity, StringComparer.Ordinal)
            .ToArray();
        return result with
        {
            FileSections = retained,
            ContinuationTargets = continuations,
            Coverage = result.Coverage with
            {
                SourceComplete = result.Coverage.SourceComplete && retained.Length == sections.Count,
                OutputComplete = result.Coverage.OutputComplete && retained.Length == sections.Count,
            },
        };
    }

    private static CodeExploreResult CompactSecondaryEvidenceForMarkdown(CodeExploreResult result)
    {
        return result with
        {
            BlastRadius = result.BlastRadius is null
                ? null
                : result.BlastRadius with { Items = [] },
            Flow = result.Flow is null
                ? null
                : result.Flow with
                {
                    Paths = [],
                    Nodes = [],
                    Edges = [],
                    DispatchBranches = [],
                    Boundaries = [],
                },
            BackReferences = [],
        };
    }

    private CodeExploreResult RemoveArtifactsWithContinuations(CodeExploreResult result)
    {
        var artifacts = result.AssociatedArtifacts ?? [];
        var addedTargets = CreateArtifactContinuationTargets(
            artifacts,
            result.WorkspaceGeneration);
        var existingCoverage = result.ArtifactCoverage;
        var targets = (existingCoverage?.ContinuationTargets ?? [])
            .Concat(addedTargets)
            .DistinctBy(ArtifactContinuationIdentity, StringComparer.Ordinal)
            .ToArray();
        var coverage = existingCoverage is null
            ? new CodeExploreArtifactCoverage(
                0,
                0,
                0,
                artifacts.Count,
                0,
                artifacts.Count,
                0,
                false,
                false,
                artifacts.Count > 0,
                false,
                false,
                [RenderPrompt(PromptFileNames.ToolCodeExploreMarkdownAssociatedArtifactOmission)],
                targets)
            : existingCoverage with
            {
                ReturnedCount = 0,
                OmittedCount = existingCoverage.OmittedCount + artifacts.Count,
                SpentCharacters = 0,
                Complete = false,
                FileLimitReached = existingCoverage.FileLimitReached || artifacts.Count > 0,
                ContinuationTargets = targets,
            };
        return result with
        {
            AssociatedArtifacts = [],
            ArtifactCoverage = coverage,
        };
    }

    private IEnumerable<CodeExploreArtifactContinuationTarget> CreateArtifactContinuationTargets(
        IReadOnlyList<CodeExploreAssociatedArtifact> artifacts,
        long workspaceGeneration)
    {
        foreach (var artifact in artifacts)
        {
            if (artifact.FilePath is not { } filePath)
            {
                continue;
            }

            yield return new CodeExploreArtifactContinuationTarget(
                filePath,
                artifact.Content?.Range.StartLine,
                artifact.Content?.Range.EndLine,
                artifact.Content?.FileSha256,
                workspaceGeneration,
                RenderPrompt(PromptFileNames.ToolCodeExploreMarkdownArtifactContinuationReason))
            {
                OriginSymbolId = artifact.OriginSymbolId,
                OriginFilePath = artifact.OriginFilePath,
                OriginRange = artifact.OriginRange,
            };
        }
    }

    private string BoundMarkdownPreservingContinuations(
        CodeExploreResult result,
        string? query,
        int maximumBytes)
    {
        var continuationMarkdown = CreateBoundedContinuationMarkdown(result, query, maximumBytes);
        if (continuationMarkdown.Length == 0)
        {
            return BoundMarkdownContentToUtf8Bytes(RenderUnbounded(result, query), maximumBytes);
        }

        var continuationBytes = Encoding.UTF8.GetByteCount(continuationMarkdown);
        var separator = Environment.NewLine + Environment.NewLine;
        var separatorBytes = Encoding.UTF8.GetByteCount(separator);
        var bodyBudget = maximumBytes - continuationBytes - separatorBytes;
        if (bodyBudget <= 0)
        {
            return continuationMarkdown;
        }

        var resultWithoutContinuations = RemoveContinuationTargets(result);
        var body = BoundMarkdownContentToUtf8Bytes(
            RenderUnbounded(resultWithoutContinuations, query),
            bodyBudget);
        return CombineMarkdownBodyAndContinuations(body, continuationMarkdown, separator);
    }

    private string RenderWithCompactContinuationFooter(
        CodeExploreResult result,
        string? query)
    {
        var continuationMarkdown = CreateBoundedContinuationMarkdown(result, query, int.MaxValue);
        if (continuationMarkdown.Length == 0)
        {
            return RenderUnbounded(result, query);
        }

        var body = RenderUnbounded(RemoveContinuationTargets(result), query);
        var separator = Environment.NewLine + Environment.NewLine;
        return CombineMarkdownBodyAndContinuations(body, continuationMarkdown, separator);
    }

    private static string CombineMarkdownBodyAndContinuations(
        string body,
        string continuationMarkdown,
        string separator)
    {
        return string.IsNullOrWhiteSpace(body)
            ? continuationMarkdown
            : body.TrimEnd() + separator + continuationMarkdown;
    }

    private static CodeExploreResult RemoveContinuationTargets(CodeExploreResult result)
    {
        return result with
        {
            ContinuationTargets = [],
            OmittedSourceContinuationCount = 0,
            BlastRadius = result.BlastRadius is null
                ? null
                : result.BlastRadius with { ContinuationTargets = [] },
            ArtifactCoverage = result.ArtifactCoverage is null
                ? null
                : result.ArtifactCoverage with { ContinuationTargets = [] },
        };
    }

    private string CreateBoundedContinuationMarkdown(
        CodeExploreResult result,
        string? query,
        int maximumBytes)
    {
        var continuations = CreateMarkdownContinuations(result, query);
        if (continuations.Count == 0 && result.OmittedSourceContinuationCount == 0)
        {
            return string.Empty;
        }

        var selected = SelectVisibleContinuations(continuations);
        for (var retainedCount = selected.Count; retainedCount >= 1; retainedCount--)
        {
            var builder = new StringBuilder();
            AppendContinuationSection(
                builder,
                continuations,
                selected.Take(retainedCount).ToArray(),
                result.OmittedSourceContinuationCount,
                includeCursorDetails: true,
                compact: true);
            var markdown = builder.ToString();
            if (Encoding.UTF8.GetByteCount(markdown) <= maximumBytes)
            {
                return markdown;
            }
        }

        for (var retainedCount = selected.Count; retainedCount >= 0; retainedCount--)
        {
            var builder = new StringBuilder();
            AppendContinuationSection(
                builder,
                continuations,
                selected.Take(retainedCount).ToArray(),
                result.OmittedSourceContinuationCount,
                includeCursorDetails: false,
                compact: true);
            var markdown = builder.ToString();
            if (Encoding.UTF8.GetByteCount(markdown) <= maximumBytes)
            {
                return markdown;
            }
        }

        return string.Empty;
    }

    private static string ContinuationIdentity(CodeExploreContinuationTarget target)
    {
        return $"{target.Kind}:{target.Anchor}:{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.ExpectedFileSha256}";
    }

    private static string ArtifactContinuationIdentity(CodeExploreArtifactContinuationTarget target)
    {
        return $"{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.ExpectedFileSha256}";
    }

    private string BoundMarkdownToUtf8BytesCore(string markdown, int maximumBytes)
    {
        return BoundMarkdownToUtf8BytesCore(markdown, maximumBytes, GetModelBudgetOmission());
    }

    private static string BoundMarkdownContentToUtf8Bytes(string markdown, int maximumBytes)
    {
        return BoundMarkdownToUtf8BytesCore(markdown, maximumBytes, modelBudgetOmission: null);
    }

    private static string BoundMarkdownToUtf8BytesCore(
        string markdown,
        int maximumBytes,
        string? modelBudgetOmission)
    {
        if (Encoding.UTF8.GetByteCount(markdown) <= maximumBytes)
        {
            return markdown;
        }

        var newline = Environment.NewLine;
        var omission = modelBudgetOmission is null
            ? string.Empty
            : modelBudgetOmission + newline;
        if (modelBudgetOmission is not null
            && Encoding.UTF8.GetByteCount(omission) > maximumBytes)
        {
            return BoundTextToUtf8Bytes(modelBudgetOmission, maximumBytes);
        }

        var builder = new StringBuilder();
        var usedBytes = 0;
        string? openFence = null;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var segment = line + newline;
            var nextOpenFence = GetOpenFenceAfterLine(line, openFence);
            var reservedSuffix = omission;
            if (nextOpenFence is not null)
            {
                reservedSuffix = nextOpenFence + newline + reservedSuffix;
            }

            var segmentBytes = Encoding.UTF8.GetByteCount(segment);
            if (usedBytes + segmentBytes + Encoding.UTF8.GetByteCount(reservedSuffix) > maximumBytes)
            {
                break;
            }

            builder.Append(segment);
            usedBytes += segmentBytes;
            openFence = nextOpenFence;
        }

        if (openFence is not null)
        {
            builder.Append(openFence);
            builder.Append(newline);
        }

        builder.Append(omission);
        return builder.ToString();
    }

    private string RenderUnbounded(CodeExploreResult result, string? query)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, result, query);
        AppendAvailability(builder, result);
        AppendSelectedEvidence(builder, result);
        AppendSourceCode(builder, result.FileSections);
        AppendBlastRadius(builder, result.BlastRadius);
        AppendFlow(builder, result.Flow);
        AppendAssociatedArtifacts(builder, result.AssociatedArtifacts);
        AppendBackReferences(builder, result.BackReferences);
        AppendContinuations(builder, result, query);
        AppendNextActions(builder, result.Presentation?.NextActions ?? result.Availability?.RecommendedActions);
        AppendOmissions(builder, result);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static int ResolveRenderedMarkdownMaximumBytes(
        CodeExploreResult result,
        int? modelMaximumBytes)
    {
        var nominalSourceCharacters = result.AdaptiveBudget?.EffectiveMaximumSourceCharacters
            ?? MaximumRenderedMarkdownBytes;
        var adaptiveMaximum = Math.Min(
            MaximumRenderedMarkdownBytes,
            Math.Max(1, (int)Math.Ceiling(nominalSourceCharacters * AdaptiveRenderedMarkdownMultiplier)));
        return modelMaximumBytes is > 0
            ? Math.Min(adaptiveMaximum, modelMaximumBytes.Value)
            : adaptiveMaximum;
    }

    private string AppendModelBudgetOmission(string markdown, int maximumBytes)
    {
        var newline = Environment.NewLine;
        var omission = GetModelBudgetOmission();
        var omissionLine = omission + newline;
        if (Encoding.UTF8.GetByteCount(omissionLine) > maximumBytes)
        {
            return BoundTextToUtf8Bytes(omission, maximumBytes);
        }

        var trimmedMarkdown = markdown.TrimEnd();
        if (trimmedMarkdown.Length == 0)
        {
            return omissionLine;
        }

        var separator = newline + newline;
        var contentMaximumBytes = maximumBytes
            - Encoding.UTF8.GetByteCount(separator)
            - Encoding.UTF8.GetByteCount(omissionLine);
        var boundedMarkdown = Encoding.UTF8.GetByteCount(trimmedMarkdown) <= contentMaximumBytes
            ? trimmedMarkdown
            : BoundMarkdownContentToUtf8Bytes(trimmedMarkdown, contentMaximumBytes).TrimEnd();
        return boundedMarkdown.Length == 0
            ? omissionLine
            : boundedMarkdown + separator + omissionLine;
    }

    private int GetModelBudgetContentMaximumBytes(int maximumBytes)
    {
        var newline = Environment.NewLine;
        return maximumBytes
            - Encoding.UTF8.GetByteCount(newline + newline)
            - Encoding.UTF8.GetByteCount(GetModelBudgetOmission() + newline);
    }

    private static string? GetOpenFenceAfterLine(string line, string? openFence)
    {
        var fenceLength = 0;
        while (fenceLength < line.Length && line[fenceLength] == '`')
        {
            fenceLength++;
        }

        if (fenceLength < 3)
        {
            return openFence;
        }

        var fence = line[..fenceLength];
        if (openFence is null)
        {
            return fence;
        }

        return string.Equals(line, openFence, StringComparison.Ordinal)
            ? null
            : openFence;
    }

    private static string BoundTextToUtf8Bytes(string text, int maximumBytes)
    {
        var builder = new StringBuilder();
        var usedBytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (usedBytes + runeBytes > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            usedBytes += runeBytes;
        }

        return builder.ToString();
    }

    private void AppendHeader(StringBuilder builder, CodeExploreResult result, string? query)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resolution in result.ResolvedAnchors)
        {
            if (resolution.SelectedSymbol is { } symbol)
            {
                _ = symbols.Add(symbol.Id);
            }
        }

        foreach (var section in result.FileSections)
        {
            foreach (var symbol in section.SemanticIdentities)
            {
                _ = symbols.Add(symbol.Id);
            }
        }

        var fileCount = result.FileSections
            .Select(section => section.FilePath)
            .Distinct(PathComparer)
            .Count();
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreResultHeader,
            Tokens(
                ("DisplayQuery", FormatCodeSpan(string.IsNullOrWhiteSpace(query)
                    ? "C# code"
                    : BoundInline(query, 240))),
                ("SymbolCount", symbols.Count.ToString(CultureInfo.InvariantCulture)),
                ("SymbolPluralSuffix", PluralSuffix(symbols.Count)),
                ("FileCount", fileCount.ToString(CultureInfo.InvariantCulture)),
                ("FilePluralSuffix", PluralSuffix(fileCount))));
    }

    private void AppendAvailability(StringBuilder builder, CodeExploreResult result)
    {
        if (result.Availability is not { } availability
            || availability.Status == CodeExploreAvailabilityStatus.Available)
        {
            return;
        }

        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreAvailabilitySection,
            Tokens(
                ("Status", availability.Status.ToString()),
                ("Reason", BoundInline(availability.Reason, 320))));
    }

    private void AppendBlastRadius(StringBuilder builder, CodeExploreBlastRadius? blastRadius)
    {
        if (blastRadius is null)
        {
            return;
        }

        var impactItems = new StringBuilder();
        var visibleItems = SelectRepresentativeImpactItems(blastRadius.Items);
        foreach (var item in visibleItems)
        {
            var identity = item.Symbol?.DisplayName
                ?? item.ProjectName
                ?? item.AnchorSymbolId;
            var location = item.Location is null
                ? string.Empty
                : $" — {FormatCodeSpan(item.Location.FilePath)}:{FormatRange(item.Location.Range)}";
            impactItems.AppendLine(
                $"- **{item.Kind}:** {FormatCodeSpan(identity)}{location} — {BoundInline(item.Reason, 280)}");
        }

        AppendHiddenCount(
            impactItems,
            blastRadius.Items.Count,
            visibleItems.Count,
            PromptFileNames.ToolCodeExploreHiddenImpactItems);
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreBlastRadiusSection,
            Tokens(
                ("ReturnedCallers", blastRadius.ReturnedCallers.ToString(CultureInfo.InvariantCulture)),
                ("TotalCallers", blastRadius.TotalCallers.ToString(CultureInfo.InvariantCulture)),
                ("CallerPluralSuffix", PluralSuffix(blastRadius.TotalCallers)),
                ("ReturnedImplementations", blastRadius.ReturnedImplementations.ToString(CultureInfo.InvariantCulture)),
                ("TotalImplementations", blastRadius.TotalImplementations.ToString(CultureInfo.InvariantCulture)),
                ("ImplementationPluralSuffix", PluralSuffix(blastRadius.TotalImplementations)),
                ("ReturnedProjects", blastRadius.ReturnedProjects.ToString(CultureInfo.InvariantCulture)),
                ("TotalProjects", blastRadius.TotalProjects.ToString(CultureInfo.InvariantCulture)),
                ("ProjectPluralSuffix", PluralSuffix(blastRadius.TotalProjects)),
                ("ReturnedTests", blastRadius.ReturnedTests.ToString(CultureInfo.InvariantCulture)),
                ("TotalTests", blastRadius.TotalTests.ToString(CultureInfo.InvariantCulture)),
                ("TestPluralSuffix", PluralSuffix(blastRadius.TotalTests)),
                ("ImpactItems", impactItems.ToString().TrimEnd())));
    }

    private static IReadOnlyList<CodeExploreBlastRadiusItem> SelectRepresentativeImpactItems(
        IReadOnlyList<CodeExploreBlastRadiusItem> items)
    {
        return items
            .Select((item, index) => new { Item = item, Index = index })
            .GroupBy(entry => entry.Item.Kind)
            .OrderBy(group => GetImpactKindPriority(group.Key))
            .SelectMany(group => group
                .OrderBy(entry => entry.Index)
                .Take(MaximumImpactItemsPerKind))
            .Take(MaximumImpactItems)
            .Select(entry => entry.Item)
            .ToArray();
    }

    private static int GetImpactKindPriority(ImpactKind kind)
    {
        return kind switch
        {
            ImpactKind.Caller => 0,
            ImpactKind.Implementation => 1,
            ImpactKind.Reference => 2,
            ImpactKind.Diagnostic => 3,
            ImpactKind.GeneratedDocument => 4,
            ImpactKind.Project => 5,
            ImpactKind.Test => 6,
            _ => 7,
        };
    }

    private void AppendSourceCode(
        StringBuilder builder,
        IReadOnlyList<CodeExploreFileSection> sections)
    {
        if (sections.Count == 0)
        {
            return;
        }

        var items = new StringBuilder();
        foreach (var section in sections)
        {
            var symbols = section.SemanticIdentities
                .Take(MaximumSemanticIdentities)
                .Select(symbol => FormatCodeSpan(symbol.DisplayName))
                .ToArray();
            var symbolSummary = symbols.Length == 0
                ? string.Empty
                : " — " + string.Join(", ", symbols);
            items.AppendLine($"**{FormatCodeSpan(section.FilePath)}**{symbolSummary}");

            if (section.Source.NumberedLines.Count > 0)
            {
                var fence = CreateFence(section.Source.NumberedLines);
                items.AppendLine(fence + "csharp");
                foreach (var line in section.Source.NumberedLines)
                {
                    items.AppendLine(line);
                }

                items.AppendLine(fence);
            }
            else
            {
                items.AppendLine(RenderPrompt(PromptFileNames.ToolCodeExploreSourceEmpty));
            }

            if (section.Source.Completeness != CodeExploreSourceCompleteness.Complete)
            {
                items.AppendLine(RenderPrompt(
                    PromptFileNames.ToolCodeExploreSourcePartial,
                    Tokens(
                        ("Completeness", section.Source.Completeness.ToString()),
                        ("SourceRange", FormatRange(section.Source.Range)))));
            }

            if (section.IsGenerated || section.IsLinked)
            {
                var classifications = new List<string>();
                if (section.IsGenerated)
                {
                    classifications.Add("generated");
                }

                if (section.IsLinked)
                {
                    classifications.Add("linked");
                }

                items.AppendLine(RenderPrompt(
                    PromptFileNames.ToolCodeExploreSourceClassification,
                    Tokens(("Classifications", string.Join(", ", classifications)))));
            }

            items.AppendLine();
        }

        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreSourceSection,
            Tokens(("Items", items.ToString().TrimEnd())));
    }

    private void AppendSelectedEvidence(StringBuilder builder, CodeExploreResult result)
    {
        var items = CreateSelectedEvidenceItems(result);
        if (items.Count == 0)
        {
            return;
        }

        var renderedItems = new StringBuilder();
        foreach (var item in items.Take(MaximumSelectedEvidenceItems))
        {
            var location = string.IsNullOrWhiteSpace(item.FilePath)
                ? string.Empty
                : $" — {FormatCodeSpan(item.FilePath)}{FormatNullableRange(item.Range)}";
            renderedItems.AppendLine(
                $"- {FormatCodeSpan(item.Label)}{location} — {BoundInline(item.Reason, 280)}");
        }

        AppendHiddenCount(
            renderedItems,
            items.Count,
            MaximumSelectedEvidenceItems,
            PromptFileNames.ToolCodeExploreHiddenSelectedEvidenceItems);
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreFileRelevanceSection,
            Tokens(("Items", renderedItems.ToString().TrimEnd())));
    }

    private static IReadOnlyList<MarkdownEvidenceItem> CreateSelectedEvidenceItems(CodeExploreResult result)
    {
        var items = new List<MarkdownEvidenceItem>();
        var selectedCandidates = (result.CandidateSummaries ?? [])
            .Where(candidate => candidate.Selected)
            .ToArray();
        var candidatesBySymbolId = selectedCandidates
            .Where(candidate => candidate.Symbol is not null)
            .GroupBy(candidate => candidate.Symbol?.Id ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in result.FileSections)
        {
            var matchedSectionCandidate = false;
            foreach (var symbol in section.SemanticIdentities)
            {
                if (!candidatesBySymbolId.TryGetValue(symbol.Id, out var candidate)
                    || !seenKeys.Add("symbol:" + symbol.Id))
                {
                    continue;
                }

                matchedSectionCandidate = true;
                items.Add(new MarkdownEvidenceItem(
                    candidate.Symbol?.DisplayName ?? section.FilePath,
                    section.FilePath,
                    section.Source.Range,
                    candidate.Reason));
            }

            if (!matchedSectionCandidate && seenKeys.Add($"section:{section.FilePath}:{section.Source.Range}"))
            {
                var label = section.SemanticIdentities.Count > 0
                    ? section.SemanticIdentities[0].DisplayName
                    : section.FilePath;
                items.Add(new MarkdownEvidenceItem(
                    label,
                    section.FilePath,
                    section.Source.Range,
                    section.SelectionReason));
            }
        }

        return items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Reason))
            .ToArray();
    }

    private void AppendFlow(StringBuilder builder, CodeExploreFlow? flow)
    {
        if (flow is null or { Edges.Count: 0, Boundaries.Count: 0 })
        {
            return;
        }

        var items = new StringBuilder();
        foreach (var edge in flow.Edges.Take(MaximumFlowEdges))
        {
            var promptFileName = edge.CallSite is null
                ? PromptFileNames.ToolCodeExploreFlowEdge
                : PromptFileNames.ToolCodeExploreFlowEdgeWithCallSite;
            var tokens = new List<(string Name, string Value)>
            {
                ("Caller", FormatCodeSpan(edge.CallerSymbolId)),
                ("Callee", FormatCodeSpan(edge.CalleeSymbolId)),
                ("DispatchKind", edge.DispatchKind.ToString()),
                ("Proof", BoundInline(edge.Proof, 280)),
            };
            if (edge.CallSite is { } callSite)
            {
                tokens.Add(("CallSiteFile", FormatCodeSpan(callSite.FilePath)));
                tokens.Add(("CallSiteRange", FormatRange(callSite.Range)));
            }

            items.AppendLine(RenderPrompt(promptFileName, Tokens([.. tokens])));
        }

        AppendHiddenCount(
            items,
            flow.Edges.Count,
            MaximumFlowEdges,
            PromptFileNames.ToolCodeExploreHiddenFlowEdges);
        foreach (var boundary in flow.Boundaries.Take(MaximumFlowBoundaries))
        {
            items.AppendLine(RenderPrompt(
                PromptFileNames.ToolCodeExploreFlowBoundary,
                Tokens(
                    ("BoundaryKind", boundary.Kind.ToString()),
                    ("Symbol", FormatCodeSpan(boundary.SymbolId)),
                    ("Reason", BoundInline(boundary.Reason, 280)))));
        }

        AppendHiddenCount(
            items,
            flow.Boundaries.Count,
            MaximumFlowBoundaries,
            PromptFileNames.ToolCodeExploreHiddenFlowBoundaries);
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreFlowEvidenceSection,
            Tokens(("Items", items.ToString().TrimEnd())));
    }

    private void AppendAssociatedArtifacts(
        StringBuilder builder,
        IReadOnlyList<CodeExploreAssociatedArtifact>? artifacts)
    {
        if (artifacts is not { Count: > 0 })
        {
            return;
        }

        var distinctArtifacts = artifacts
            .GroupBy(
                artifact => artifact.FilePath ?? "logical:" + artifact.LogicalName,
                PathComparer)
            .Select(group => group
                .OrderByDescending(artifact => artifact.Content?.NumberedLines.Count ?? 0)
                .ThenBy(artifact => artifact.Relationship)
                .First())
            .ToArray();
        var items = new StringBuilder();
        foreach (var artifact in distinctArtifacts.Take(MaximumAssociatedArtifacts))
        {
            var identity = artifact.FilePath
                ?? artifact.LogicalName
                ?? "artifact";
            items.AppendLine(RenderPrompt(
                PromptFileNames.ToolCodeExploreAssociatedArtifactItem,
                Tokens(
                    ("Identity", FormatCodeSpan(identity)),
                    ("Relationship", artifact.Relationship.ToString()),
                    ("OriginFile", FormatCodeSpan(artifact.OriginFilePath)))));
            if (artifact.Content is { } content && content.NumberedLines.Count > 0)
            {
                var fence = CreateFence(content.NumberedLines);
                items.AppendLine(fence + "text");
                foreach (var line in content.NumberedLines)
                {
                    items.AppendLine(line);
                }

                items.AppendLine(fence);
            }

            AppendArtifactCompleteness(items, artifact);
            items.AppendLine();
        }

        AppendHiddenCount(
            items,
            distinctArtifacts.Length,
            MaximumAssociatedArtifacts,
            PromptFileNames.ToolCodeExploreHiddenAssociatedArtifacts);
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreAssociatedArtifactsSection,
            Tokens(("Items", items.ToString().TrimEnd())));
    }

    private void AppendArtifactCompleteness(
        StringBuilder builder,
        CodeExploreAssociatedArtifact artifact)
    {
        var details = CreateArtifactCompletenessDetails(artifact);
        if (details.Count == 0)
        {
            return;
        }

        foreach (var detail in details.Take(MaximumArtifactOmissions))
        {
            builder.AppendLine(RenderPrompt(
                PromptFileNames.ToolCodeExploreArtifactNote,
                Tokens(("Detail", BoundInline(detail, 280)))));
        }

        AppendHiddenCount(
            builder,
            details.Count,
            MaximumArtifactOmissions,
            PromptFileNames.ToolCodeExploreHiddenArtifactNotes);
    }

    private IReadOnlyList<string> CreateArtifactCompletenessDetails(
        CodeExploreAssociatedArtifact artifact)
    {
        var details = new List<string>();
        var content = artifact.Content;
        if (content is null)
        {
            if (artifact.Omissions.Count == 0)
            {
                details.Add(RenderPrompt(PromptFileNames.ToolCodeExploreArtifactContentOmitted));
            }
        }
        else
        {
            if (content.NumberedLines.Count == 0)
            {
                details.Add(content.Completeness switch
                {
                    CodeExploreSourceCompleteness.Complete => RenderPrompt(PromptFileNames.ToolCodeExploreArtifactContentEmpty),
                    CodeExploreSourceCompleteness.Omitted => RenderPrompt(PromptFileNames.ToolCodeExploreArtifactContentOmitted),
                    CodeExploreSourceCompleteness.Drifted => RenderPrompt(PromptFileNames.ToolCodeExploreArtifactContentDrifted),
                    _ => RenderPrompt(PromptFileNames.ToolCodeExploreArtifactContentPartial),
                });
            }
            else if (content.Completeness != CodeExploreSourceCompleteness.Complete)
            {
                details.Add(content.Completeness switch
                {
                    CodeExploreSourceCompleteness.Omitted => RenderPrompt(PromptFileNames.ToolCodeExploreArtifactAdditionalContentOmitted),
                    CodeExploreSourceCompleteness.Drifted => RenderPrompt(PromptFileNames.ToolCodeExploreArtifactAdditionalContentDrifted),
                    _ => RenderPrompt(PromptFileNames.ToolCodeExploreArtifactAdditionalContentBounded),
                });
            }

            details.AddRange(content.OmittedRanges.Select(omission => RenderPrompt(
                PromptFileNames.ToolCodeExploreArtifactOmittedRange,
                Tokens(("Omission", omission)))));
            if (!string.IsNullOrWhiteSpace(content.ContinuationAnchor))
            {
                details.Add(RenderPrompt(PromptFileNames.ToolCodeExploreArtifactContinuationAvailable));
            }
        }

        var contentOmissions = content?.OmittedRanges ?? [];
        details.AddRange(artifact.Omissions.Where(omission =>
            !IsDuplicateArtifactContentOmission(omission, contentOmissions)));
        return details
            .Where(static detail => !string.IsNullOrWhiteSpace(detail))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private bool IsDuplicateArtifactContentOmission(
        string omission,
        IReadOnlyList<string> contentOmissions)
    {
        var normalized = omission.Trim();
        return contentOmissions.Any(contentOmission =>
            string.Equals(normalized, contentOmission.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                normalized,
                RenderPrompt(
                    PromptFileNames.ToolCodeExploreArtifactOmittedRange,
                    Tokens(("Omission", contentOmission.Trim()))),
                StringComparison.OrdinalIgnoreCase));
    }

    private void AppendBackReferences(
        StringBuilder builder,
        IReadOnlyList<CodeExploreBackReference>? backReferences)
    {
        if (backReferences is not { Count: > 0 })
        {
            return;
        }

        var items = new StringBuilder();
        foreach (var reference in backReferences.Take(MaximumBackReferences))
        {
            items.AppendLine(RenderPrompt(
                PromptFileNames.ToolCodeExploreBackReference,
                Tokens(
                    ("FilePath", FormatCodeSpan(reference.FilePath)),
                    ("SourceRange", FormatRange(reference.Range)),
                    ("ToolCallId", FormatCodeSpan(reference.ToolCallId)),
                    ("Reason", BoundInline(reference.Reason, 280)))));
        }

        AppendHiddenCount(
            items,
            backReferences.Count,
            MaximumBackReferences,
            PromptFileNames.ToolCodeExploreHiddenBackReferences);
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreBackReferencesSection,
            Tokens(("Items", items.ToString().TrimEnd())));
    }

    private void AppendContinuations(
        StringBuilder builder,
        CodeExploreResult result,
        string? query)
    {
        var continuations = CreateMarkdownContinuations(result, query);
        if (continuations.Count == 0 && result.OmittedSourceContinuationCount == 0)
        {
            return;
        }

        var visibleContinuations = SelectVisibleContinuations(continuations);
        AppendContinuationSection(
            builder,
            continuations,
            visibleContinuations,
            result.OmittedSourceContinuationCount,
            includeCursorDetails: true,
            compact: false);
    }

    private void AppendContinuationSection(
        StringBuilder builder,
        IReadOnlyList<MarkdownContinuationTarget> continuations,
        IReadOnlyList<MarkdownContinuationTarget> visibleContinuations,
        int omittedSourceContinuationCount,
        bool includeCursorDetails,
        bool compact)
    {
        var cursorSlots = includeCursorDetails
            ? SelectContinuationCursorSlots(visibleContinuations)
            : [];
        var omittedCursorCount = 0;
        var items = new StringBuilder();
        for (var index = 0; index < visibleContinuations.Count; index++)
        {
            var continuation = visibleContinuations[index];
            AppendContinuationPointer(items, continuation, compact);
            if (!includeCursorDetails)
            {
                continue;
            }

            if (continuation.Cursor is { } cursor && cursorSlots.Contains(index))
            {
                items.AppendLine(RenderPrompt(
                    PromptFileNames.ToolCodeExploreContinuationRetryQuery,
                    Tokens(("Cursor", FormatCodeSpan(cursor)))));
            }
            else if (continuation.Cursor is null)
            {
                items.AppendLine(RenderPrompt(
                    PromptFileNames.ToolCodeExploreContinuationCursorUnavailable));
            }
            else
            {
                omittedCursorCount++;
            }
        }

        if (omittedCursorCount > 0)
        {
            items.AppendLine(RenderPrompt(
                PromptFileNames.ToolCodeExploreContinuationCursorOmission,
                Tokens(
                    ("Count", omittedCursorCount.ToString(CultureInfo.InvariantCulture)),
                    ("PluralSuffix", PluralSuffix(omittedCursorCount)))));
        }

        AppendHiddenCount(
            items,
            continuations.Count + omittedSourceContinuationCount,
            visibleContinuations.Count,
            PromptFileNames.ToolCodeExploreHiddenFollowUpTargets);
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreContinuationsSection,
            Tokens(("Items", items.ToString().TrimEnd())));
    }

    private static void AppendContinuationPointer(
        StringBuilder builder,
        MarkdownContinuationTarget continuation,
        bool compact)
    {
        if (IsSourceContinuation(continuation))
        {
            var sourcePath = string.IsNullOrWhiteSpace(continuation.FilePath)
                ? continuation.Anchor
                : continuation.FilePath;
            builder.Append("- **");
            builder.Append(continuation.Kind);
            builder.Append(":** ");
            builder.Append(FormatCodeSpan(sourcePath));
            builder.Append(FormatOptionalRange(continuation.StartLine, continuation.EndLine));
            if (!compact && !string.IsNullOrWhiteSpace(continuation.Reason))
            {
                builder.Append(" — ");
                builder.Append(BoundInline(continuation.Reason, 160));
            }

            builder.AppendLine();
            return;
        }

        var location = string.IsNullOrWhiteSpace(continuation.FilePath)
            ? string.Empty
            : $" — {FormatCodeSpan(continuation.FilePath)}{FormatOptionalRange(continuation.StartLine, continuation.EndLine)}";
        builder.Append("- **");
        builder.Append(continuation.Kind);
        builder.Append(":** ");
        builder.Append(FormatCodeSpan(continuation.Anchor));
        builder.Append(location);
        if (!compact && !string.IsNullOrWhiteSpace(continuation.Reason))
        {
            builder.Append(" — ");
            builder.Append(BoundInline(continuation.Reason, 280));
        }

        builder.AppendLine();
    }

    private static IReadOnlyList<MarkdownContinuationTarget> SelectVisibleContinuations(
        IReadOnlyList<MarkdownContinuationTarget> continuations)
    {
        var sourceContinuations = continuations
            .Where(IsSourceContinuation)
            .ToArray();
        var optionalContinuations = new List<MarkdownContinuationTarget>();
        foreach (var group in continuations
            .Where(continuation => !IsSourceContinuation(continuation))
            .GroupBy(continuation => continuation.Kind))
        {
            var replayable = group
                .Where(continuation => continuation.Cursor is not null)
                .Take(MaximumOptionalContinuationsPerKind)
                .ToArray();
            if (replayable.Length > 0)
            {
                optionalContinuations.AddRange(replayable);
            }
            else
            {
                optionalContinuations.Add(group.First());
            }
        }

        return
        [
            .. sourceContinuations,
            .. optionalContinuations.Take(MaximumOptionalContinuations),
        ];
    }

    private static HashSet<int> SelectContinuationCursorSlots(
        IReadOnlyList<MarkdownContinuationTarget> continuations)
    {
        var slots = new HashSet<int>();
        var cursorCandidates = continuations
            .Select((continuation, index) => new { Continuation = continuation, Index = index })
            .Where(item => item.Continuation.Cursor is not null)
            .ToArray();
        foreach (var group in cursorCandidates.GroupBy(item => item.Continuation.Kind))
        {
            if (slots.Count >= MaximumContinuationCursors)
            {
                return slots;
            }

            _ = slots.Add(group.First().Index);
        }

        foreach (var item in cursorCandidates)
        {
            if (slots.Count >= MaximumContinuationCursors)
            {
                break;
            }

            _ = slots.Add(item.Index);
        }

        return slots;
    }

    private static bool IsSourceContinuation(MarkdownContinuationTarget continuation)
    {
        return continuation.Kind == MarkdownContinuationKind.Source;
    }

    private static IReadOnlyList<MarkdownContinuationTarget> CreateMarkdownContinuations(
        CodeExploreResult result,
        string? query)
    {
        var continuations = new List<MarkdownContinuationTarget>();
        foreach (var continuation in result.ContinuationTargets)
        {
            continuations.Add(new MarkdownContinuationTarget(
                MarkdownContinuationKind.Source,
                continuation.Anchor,
                continuation.FilePath,
                continuation.StartLine,
                continuation.EndLine,
                continuation.Reason,
                CodeExploreContinuationCursor.CreateSource(continuation)));
        }

        foreach (var continuation in result.BlastRadius?.ContinuationTargets ?? [])
        {
            continuations.Add(new MarkdownContinuationTarget(
                MarkdownContinuationKind.Impact,
                continuation.Anchor,
                continuation.FilePath,
                continuation.StartLine,
                continuation.EndLine,
                continuation.Reason,
                CodeExploreContinuationCursor.CreateImpact(continuation)));
        }

        foreach (var continuation in result.ArtifactCoverage?.ContinuationTargets ?? [])
        {
            var artifact = FindArtifactForContinuation(result.AssociatedArtifacts, continuation);
            continuations.Add(new MarkdownContinuationTarget(
                MarkdownContinuationKind.Artifact,
                continuation.FilePath,
                continuation.FilePath,
                continuation.StartLine,
                continuation.EndLine,
                continuation.Reason,
                CodeExploreContinuationCursor.CreateArtifact(continuation, artifact, query)));
        }

        return continuations
            .DistinctBy(static continuation => $"{continuation.Kind}:{continuation.Anchor}:{continuation.FilePath}:{continuation.StartLine}:{continuation.EndLine}:{continuation.Cursor}")
            .ToArray();
    }

    private static CodeExploreAssociatedArtifact? FindArtifactForContinuation(
        IReadOnlyList<CodeExploreAssociatedArtifact>? artifacts,
        CodeExploreArtifactContinuationTarget continuation)
    {
        return artifacts?.FirstOrDefault(artifact => artifact.FilePath is not null
            && PathComparer.Equals(artifact.FilePath, continuation.FilePath));
    }

    private void AppendNextActions(
        StringBuilder builder,
        IReadOnlyList<CodeExploreNextActionHint>? actions)
    {
        if (actions is not { Count: > 0 })
        {
            return;
        }

        var items = new StringBuilder();
        foreach (var action in actions.Take(MaximumNextActions))
        {
            items.AppendLine($"- {BoundInline(action.Message, 280)}");
        }

        AppendHiddenCount(
            items,
            actions.Count,
            MaximumNextActions,
            PromptFileNames.ToolCodeExploreHiddenNextActions);
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreRecommendedActionsSection,
            Tokens(("Items", items.ToString().TrimEnd())));
    }

    private void AppendOmissions(StringBuilder builder, CodeExploreResult result)
    {
        var omissions = result.Omissions
            .Concat(result.Coverage.Omissions)
            .Concat(result.BlastRadius?.Omissions ?? [])
            .Concat(result.Flow?.Traversal.Omissions ?? [])
            .Concat(result.ArtifactCoverage?.Omissions ?? [])
            .Where(omission => !string.IsNullOrWhiteSpace(omission))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (omissions.Length == 0)
        {
            return;
        }

        var items = new StringBuilder();
        foreach (var omission in omissions.Take(MaximumOmissions))
        {
            items.AppendLine("- " + BoundInline(omission, 280));
        }

        AppendHiddenCount(
            items,
            omissions.Length,
            MaximumOmissions,
            PromptFileNames.ToolCodeExploreHiddenOmissions);
        AppendPromptBlock(
            builder,
            PromptFileNames.ToolCodeExploreOmissionsSection,
            Tokens(("Items", items.ToString().TrimEnd())));
    }

    private static string FormatOptionalRange(int? startLine, int? endLine)
    {
        if (startLine is null)
        {
            return string.Empty;
        }

        return endLine is null || endLine == startLine
            ? $":L{startLine.Value.ToString(CultureInfo.InvariantCulture)}"
            : $":L{startLine.Value.ToString(CultureInfo.InvariantCulture)}-L{endLine.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatNullableRange(SourceRange? range)
    {
        return range is { } sourceRange
            ? ":" + FormatRange(sourceRange)
            : string.Empty;
    }

    private static string FormatRange(SourceRange range)
    {
        return range.StartLine == range.EndLine
            ? $"L{range.StartLine.ToString(CultureInfo.InvariantCulture)}"
            : $"L{range.StartLine.ToString(CultureInfo.InvariantCulture)}-L{range.EndLine.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string PluralSuffix(int count)
    {
        return count == 1
            ? string.Empty
            : "s";
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

    private string? ExtractExplorationQuery(string markdown)
    {
        var prefixIndex = markdown.IndexOf(_explorationPrefix, StringComparison.Ordinal);
        if (prefixIndex < 0)
        {
            return null;
        }

        var codeSpanStart = prefixIndex + _explorationPrefix.Length;
        var delimiterLength = 0;
        while (codeSpanStart + delimiterLength < markdown.Length
            && markdown[codeSpanStart + delimiterLength] == '`')
        {
            delimiterLength++;
        }

        if (delimiterLength == 0)
        {
            return null;
        }

        var delimiter = new string('`', delimiterLength);
        var closingIndex = markdown.IndexOf(delimiter, codeSpanStart + delimiterLength, StringComparison.Ordinal);
        while (closingIndex >= 0)
        {
            var suffixIndex = closingIndex + delimiterLength;
            if (markdown.AsSpan(suffixIndex).StartsWith(_explorationSuffix, StringComparison.Ordinal))
            {
                var query = markdown[(codeSpanStart + delimiterLength)..closingIndex];
                return query.Length >= 2 && query[0] == ' ' && query[^1] == ' '
                    ? query[1..^1]
                    : query;
            }

            closingIndex = markdown.IndexOf(delimiter, closingIndex + 1, StringComparison.Ordinal);
        }

        return null;
    }

    private void AppendHiddenCount(
        StringBuilder builder,
        int count,
        int maximum,
        string promptFileName)
    {
        var hidden = count - maximum;
        if (hidden > 0)
        {
            builder.AppendLine(RenderPrompt(
                promptFileName,
                Tokens(
                    ("Count", hidden.ToString(CultureInfo.InvariantCulture)),
                    ("PluralSuffix", PluralSuffix(hidden)))));
        }
    }

    private string GetModelBudgetOmission()
    {
        return RenderPrompt(PromptFileNames.ToolCodeExploreModelBudgetOmission);
    }

    private static (string Prefix, string Suffix) GetExplorationFrame(IPromptLoader prompts)
    {
        const string displayQueryToken = "{{DisplayQuery}}";
        var template = prompts.Get(PromptFileNames.ToolCodeExploreResultHeader)
            .ReplaceLineEndings(Environment.NewLine);
        var tokenIndex = template.IndexOf(displayQueryToken, StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            throw new InvalidOperationException("The code exploration result header is missing its display-query token.");
        }

        return (
            template[..tokenIndex],
            template[(tokenIndex + displayQueryToken.Length)..]);
    }

    private void AppendPromptBlock(
        StringBuilder builder,
        string fileName,
        IReadOnlyDictionary<string, string> tokens)
    {
        builder.AppendLine(RenderPrompt(fileName, tokens));
        builder.AppendLine();
    }

    private string RenderPrompt(
        string fileName,
        IReadOnlyDictionary<string, string>? tokens = null)
    {
        var rendered = tokens is null
            ? _prompts.Get(fileName)
            : _prompts.Render(fileName, tokens);
        return rendered.TrimEnd('\r', '\n').ReplaceLineEndings(Environment.NewLine);
    }

    private static IReadOnlyDictionary<string, string> Tokens(params (string Name, string Value)[] values)
    {
        return values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
    }

    private static string CreateFence(IReadOnlyList<string> lines)
    {
        var maximumRun = 0;
        foreach (var line in lines)
        {
            var currentRun = 0;
            foreach (var character in line)
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
        }

        return new string('`', Math.Max(3, maximumRun + 1));
    }

    private static string BoundInline(string? value, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var clean = CleanInline(value);
        return clean.Length <= maximumCharacters
            ? clean
            : clean[..Math.Max(0, maximumCharacters - 1)] + "…";
    }

    private static string CleanInline(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record MarkdownContinuationTarget(
        MarkdownContinuationKind Kind,
        string Anchor,
        string? FilePath,
        int? StartLine,
        int? EndLine,
        string Reason,
        string? Cursor);

    private enum MarkdownContinuationKind
    {
        Source,
        Impact,
        Artifact,
    }

    private sealed record MarkdownEvidenceItem(
        string Label,
        string? FilePath,
        SourceRange? Range,
        string Reason);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
