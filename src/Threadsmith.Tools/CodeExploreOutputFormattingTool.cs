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

    /// <summary>Initializes a new instance of the <see cref="CodeExploreOutputFormattingTool" /> class.</summary>
    public CodeExploreOutputFormattingTool(
        ITool inner,
        CodeExploreOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _options = options;
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

        var markdown = CodeExploreMarkdownRenderer.Render(
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
                    CodeExploreTool.BoundResultToMaximumBytes(sanitizedResult, maximumBytes));
            }

            if (markdownWasTruncated && modelResultContent is not null)
            {
                modelResultContent = CodeExploreMarkdownRenderer.RenderAfterSanitization(
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
internal static class CodeExploreMarkdownRenderer
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
    private const string ExplorationPrefix = "**Exploration:** ";
    private const string SourceContinuationKind = "Source";
    private const string ModelBudgetOmission = "_Output note: additional Markdown was omitted to fit the selected model input budget._";

    /// <summary>Renders one concise source-first exploration result.</summary>
    internal static string Render(
        CodeExploreResult result,
        string? query = null,
        int? maximumUtf8Bytes = null)
    {
        return Render(result, query, maximumUtf8Bytes, out _);
    }

    /// <summary>Re-renders sanitized output while preserving its sanitized exploration query.</summary>
    internal static string RenderAfterSanitization(
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
    internal static string Render(
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

        if (projected.AssociatedArtifacts is { Count: > 0 })
        {
            projected = RemoveArtifactsWithContinuations(projected);
            markdown = RenderUnbounded(projected, query);
        }

        if (Encoding.UTF8.GetByteCount(markdown) > maximumBytes)
        {
            projected = CompactSecondaryEvidenceForMarkdown(projected);
            markdown = RenderUnbounded(projected, query);
        }

        if (Encoding.UTF8.GetByteCount(markdown) > maximumBytes
            && projected.FileSections.Count > 0)
        {
            projected = FitSourceSections(projected, query, maximumBytes);
            markdown = RenderWithCompactContinuationFooter(projected, query);
        }

        return Encoding.UTF8.GetByteCount(markdown) > maximumBytes
            ? BoundMarkdownPreservingContinuations(projected, query, maximumBytes)
            : AppendModelBudgetOmission(markdown, maximumBytes);
    }

    /// <summary>Bounds Markdown by complete UTF-8 lines while preserving fence balance.</summary>
    internal static string BoundMarkdownToUtf8Bytes(string markdown, int maximumBytes)
    {
        return BoundMarkdownToUtf8BytesCore(markdown, maximumBytes);
    }

    private static CodeExploreResult FitSourceSections(
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

    private static bool FitsWithCompactContinuationFooter(
        CodeExploreResult result,
        string? query,
        int maximumBytes)
    {
        return Encoding.UTF8.GetByteCount(RenderWithCompactContinuationFooter(result, query)) <= maximumBytes;
    }

    private static CodeExploreResult ProjectSourceSectionPrefix(
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
                "The Markdown budget omitted this emitted source section; retry this exact path range."));
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

    private static CodeExploreResult RemoveArtifactsWithContinuations(CodeExploreResult result)
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
                ["Associated artifact content was omitted from Markdown to preserve higher-priority source."],
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

    private static IEnumerable<CodeExploreArtifactContinuationTarget> CreateArtifactContinuationTargets(
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
                "The Markdown budget omitted this associated artifact; retry this exact artifact path range.")
            {
                OriginSymbolId = artifact.OriginSymbolId,
                OriginFilePath = artifact.OriginFilePath,
                OriginRange = artifact.OriginRange,
            };
        }
    }

    private static string BoundMarkdownPreservingContinuations(
        CodeExploreResult result,
        string? query,
        int maximumBytes)
    {
        var continuationMarkdown = CreateBoundedContinuationMarkdown(result, query, maximumBytes);
        if (continuationMarkdown.Length == 0)
        {
            return BoundMarkdownToUtf8Bytes(RenderUnbounded(result, query), maximumBytes);
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
        var body = BoundMarkdownToUtf8Bytes(RenderUnbounded(resultWithoutContinuations, query), bodyBudget);
        return CombineMarkdownBodyAndContinuations(body, continuationMarkdown, separator);
    }

    private static string RenderWithCompactContinuationFooter(
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

    private static string CreateBoundedContinuationMarkdown(
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

        return BoundTextToUtf8Bytes(ModelBudgetOmission, maximumBytes);
    }

    private static string ContinuationIdentity(CodeExploreContinuationTarget target)
    {
        return $"{target.Kind}:{target.Anchor}:{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.ExpectedFileSha256}";
    }

    private static string ArtifactContinuationIdentity(CodeExploreArtifactContinuationTarget target)
    {
        return $"{target.FilePath}:{target.StartLine}:{target.EndLine}:{target.ExpectedFileSha256}";
    }

    private static string BoundMarkdownToUtf8BytesCore(string markdown, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(markdown) <= maximumBytes)
        {
            return markdown;
        }

        var newline = Environment.NewLine;
        var omission = ModelBudgetOmission + newline;
        if (Encoding.UTF8.GetByteCount(omission) > maximumBytes)
        {
            return BoundTextToUtf8Bytes(ModelBudgetOmission, maximumBytes);
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

    private static string RenderUnbounded(CodeExploreResult result, string? query)
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

    private static string AppendModelBudgetOmission(string markdown, int maximumBytes)
    {
        var withOmission = markdown.TrimEnd()
            + Environment.NewLine
            + Environment.NewLine
            + ModelBudgetOmission
            + Environment.NewLine;
        return Encoding.UTF8.GetByteCount(withOmission) <= maximumBytes
            ? withOmission
            : markdown;
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

    private static void AppendHeader(StringBuilder builder, CodeExploreResult result, string? query)
    {
        builder.Append(ExplorationPrefix);
        builder.AppendLine(FormatCodeSpan(string.IsNullOrWhiteSpace(query) ? "C# code" : BoundInline(query, 240)));
        builder.AppendLine();

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
        builder.AppendLine(
            $"Found {FormatCount(symbols.Count, "symbol")} across {FormatCount(fileCount, "file")}.");
        builder.AppendLine();
    }

    private static void AppendAvailability(StringBuilder builder, CodeExploreResult result)
    {
        if (result.Availability is not { } availability
            || availability.Status == CodeExploreAvailabilityStatus.Available)
        {
            return;
        }

        builder.AppendLine("**Availability**");
        builder.AppendLine();
        builder.AppendLine($"- **{availability.Status}:** {BoundInline(availability.Reason, 320)}");
        builder.AppendLine();
    }

    private static void AppendBlastRadius(StringBuilder builder, CodeExploreBlastRadius? blastRadius)
    {
        if (blastRadius is null)
        {
            return;
        }

        builder.AppendLine("**Blast radius — what depends on these**");
        builder.AppendLine();
        builder.AppendLine(
            $"- **Coverage:** {FormatImpactCoverage(blastRadius.ReturnedCallers, blastRadius.TotalCallers, "caller")}, "
            + $"{FormatImpactCoverage(blastRadius.ReturnedImplementations, blastRadius.TotalImplementations, "implementation")}, "
            + $"{FormatImpactCoverage(blastRadius.ReturnedProjects, blastRadius.TotalProjects, "project")}, and "
            + $"{FormatImpactCoverage(blastRadius.ReturnedTests, blastRadius.TotalTests, "test")} returned by bounded analysis.");
        var visibleItems = SelectRepresentativeImpactItems(blastRadius.Items);
        foreach (var item in visibleItems)
        {
            var identity = item.Symbol?.DisplayName
                ?? item.ProjectName
                ?? item.AnchorSymbolId;
            var location = item.Location is null
                ? string.Empty
                : $" — {FormatCodeSpan(item.Location.FilePath)}:{FormatRange(item.Location.Range)}";
            builder.AppendLine(
                $"- **{item.Kind}:** {FormatCodeSpan(identity)}{location} — {BoundInline(item.Reason, 280)}");
        }

        AppendHiddenCount(builder, blastRadius.Items.Count, visibleItems.Count, "impact item");
        builder.AppendLine();
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

    private static string FormatImpactCoverage(int returned, int total, string noun)
    {
        return $"{returned.ToString(CultureInfo.InvariantCulture)} of "
            + $"{total.ToString(CultureInfo.InvariantCulture)} {noun}{(total == 1 ? string.Empty : "s")}";
    }

    private static void AppendSourceCode(
        StringBuilder builder,
        IReadOnlyList<CodeExploreFileSection> sections)
    {
        if (sections.Count == 0)
        {
            return;
        }

        builder.AppendLine("**Source Code**");
        builder.AppendLine();
        foreach (var section in sections)
        {
            var symbols = section.SemanticIdentities
                .Take(MaximumSemanticIdentities)
                .Select(symbol => FormatCodeSpan(symbol.DisplayName))
                .ToArray();
            var symbolSummary = symbols.Length == 0
                ? string.Empty
                : " — " + string.Join(", ", symbols);
            builder.AppendLine($"**{FormatCodeSpan(section.FilePath)}**{symbolSummary}");

            if (section.Source.NumberedLines.Count > 0)
            {
                var fence = CreateFence(section.Source.NumberedLines);
                builder.AppendLine(fence + "csharp");
                foreach (var line in section.Source.NumberedLines)
                {
                    builder.AppendLine(line);
                }

                builder.AppendLine(fence);
            }
            else
            {
                builder.AppendLine("_No source text was emitted for this section._");
            }

            if (section.Source.Completeness != CodeExploreSourceCompleteness.Complete)
            {
                builder.AppendLine(
                    $"_Partial source: {section.Source.Completeness}; shown range {FormatRange(section.Source.Range)}._");
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

                builder.AppendLine("_Classification: " + string.Join(", ", classifications) + "._");
            }

            builder.AppendLine();
        }
    }

    private static void AppendSelectedEvidence(StringBuilder builder, CodeExploreResult result)
    {
        var items = CreateSelectedEvidenceItems(result);
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine("**Selected evidence**");
        builder.AppendLine();
        foreach (var item in items.Take(MaximumSelectedEvidenceItems))
        {
            var location = string.IsNullOrWhiteSpace(item.FilePath)
                ? string.Empty
                : $" — {FormatCodeSpan(item.FilePath)}{FormatNullableRange(item.Range)}";
            builder.AppendLine(
                $"- {FormatCodeSpan(item.Label)}{location} — {BoundInline(item.Reason, 280)}");
        }

        AppendHiddenCount(builder, items.Count, MaximumSelectedEvidenceItems, "selected evidence item");
        builder.AppendLine();
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
                var label = section.SemanticIdentities.FirstOrDefault()?.DisplayName ?? section.FilePath;
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

    private static void AppendFlow(StringBuilder builder, CodeExploreFlow? flow)
    {
        if (flow is null or { Edges.Count: 0, Boundaries.Count: 0 })
        {
            return;
        }

        builder.AppendLine("**Call flow**");
        builder.AppendLine();
        foreach (var edge in flow.Edges.Take(MaximumFlowEdges))
        {
            var callSite = edge.CallSite is null
                ? string.Empty
                : $" at {FormatCodeSpan(edge.CallSite.FilePath)}:{FormatRange(edge.CallSite.Range)}";
            builder.AppendLine(
                $"- {FormatCodeSpan(edge.CallerSymbolId)} → {FormatCodeSpan(edge.CalleeSymbolId)}"
                + $" ({edge.DispatchKind}){callSite} — {BoundInline(edge.Proof, 280)}");
        }

        AppendHiddenCount(builder, flow.Edges.Count, MaximumFlowEdges, "flow edge");
        foreach (var boundary in flow.Boundaries.Take(MaximumFlowBoundaries))
        {
            builder.AppendLine(
                $"- **Boundary {boundary.Kind}:** {FormatCodeSpan(boundary.SymbolId)} — {BoundInline(boundary.Reason, 280)}");
        }

        AppendHiddenCount(builder, flow.Boundaries.Count, MaximumFlowBoundaries, "flow boundary");
        builder.AppendLine();
    }

    private static void AppendAssociatedArtifacts(
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
        builder.AppendLine("**Associated artifacts**");
        builder.AppendLine();
        foreach (var artifact in distinctArtifacts.Take(MaximumAssociatedArtifacts))
        {
            var identity = artifact.FilePath ?? artifact.LogicalName ?? "artifact";
            builder.AppendLine(
                $"**{FormatCodeSpan(identity)}** — {artifact.Relationship}; from {FormatCodeSpan(artifact.OriginFilePath)}");
            if (artifact.Content is { } content && content.NumberedLines.Count > 0)
            {
                var fence = CreateFence(content.NumberedLines);
                builder.AppendLine(fence + "text");
                foreach (var line in content.NumberedLines)
                {
                    builder.AppendLine(line);
                }

                builder.AppendLine(fence);
            }

            AppendArtifactCompleteness(builder, artifact);
            builder.AppendLine();
        }

        AppendHiddenCount(builder, distinctArtifacts.Length, MaximumAssociatedArtifacts, "associated artifact");
    }

    private static void AppendArtifactCompleteness(
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
            builder.AppendLine($"_Artifact note: {BoundInline(detail, 280)}_");
        }

        AppendHiddenCount(builder, details.Count, MaximumArtifactOmissions, "artifact note");
    }

    private static IReadOnlyList<string> CreateArtifactCompletenessDetails(
        CodeExploreAssociatedArtifact artifact)
    {
        var details = new List<string>();
        var content = artifact.Content;
        if (content is null)
        {
            if (artifact.Omissions.Count == 0)
            {
                details.Add("content was omitted by host safety or budget policy.");
            }
        }
        else
        {
            if (content.NumberedLines.Count == 0)
            {
                details.Add(content.Completeness switch
                {
                    CodeExploreSourceCompleteness.Complete => "content was empty or contained no displayable lines.",
                    CodeExploreSourceCompleteness.Omitted => "content was omitted by host safety or budget policy.",
                    CodeExploreSourceCompleteness.Drifted => "content was omitted because the artifact changed before it could be read safely.",
                    _ => "content was partially omitted by host output bounds.",
                });
            }
            else if (content.Completeness != CodeExploreSourceCompleteness.Complete)
            {
                details.Add(content.Completeness switch
                {
                    CodeExploreSourceCompleteness.Omitted => "additional content was omitted by host safety or budget policy.",
                    CodeExploreSourceCompleteness.Drifted => "additional content was omitted because the artifact changed before it could be read safely.",
                    _ => "additional content was omitted by host output bounds.",
                });
            }

            details.AddRange(content.OmittedRanges.Select(omission => "omitted range: " + omission));
            if (!string.IsNullOrWhiteSpace(content.ContinuationAnchor))
            {
                details.Add("continuation anchor available: use the matching Artifact follow-up target to replay exact omitted content.");
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

    private static bool IsDuplicateArtifactContentOmission(
        string omission,
        IReadOnlyList<string> contentOmissions)
    {
        var normalized = omission.Trim();
        return contentOmissions.Any(contentOmission =>
            string.Equals(normalized, contentOmission.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                normalized,
                "omitted range: " + contentOmission.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendBackReferences(
        StringBuilder builder,
        IReadOnlyList<CodeExploreBackReference>? backReferences)
    {
        if (backReferences is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("**Source already visible in this request**");
        builder.AppendLine();
        foreach (var reference in backReferences.Take(MaximumBackReferences))
        {
            builder.AppendLine(
                $"- {FormatCodeSpan(reference.FilePath)}:{FormatRange(reference.Range)}"
                + $" from tool call {FormatCodeSpan(reference.ToolCallId)} — {BoundInline(reference.Reason, 280)}");
        }

        AppendHiddenCount(builder, backReferences.Count, MaximumBackReferences, "back-reference");
        builder.AppendLine();
    }

    private static void AppendContinuations(
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

    private static void AppendContinuationSection(
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
        builder.AppendLine("**Follow-up targets**");
        builder.AppendLine();
        for (var index = 0; index < visibleContinuations.Count; index++)
        {
            var continuation = visibleContinuations[index];
            AppendContinuationPointer(builder, continuation, compact);
            if (!includeCursorDetails)
            {
                continue;
            }

            if (continuation.Cursor is { } cursor && cursorSlots.Contains(index))
            {
                builder.AppendLine($"  - Retry query: {FormatCodeSpan(cursor)}");
            }
            else if (continuation.Cursor is null)
            {
                builder.AppendLine("  - Retry query cursor omitted because it exceeded the query length limit; use the shown path/range only if no exact cursor is available.");
            }
            else
            {
                omittedCursorCount++;
            }
        }

        if (omittedCursorCount > 0)
        {
            builder.AppendLine(
                $"- _{FormatCount(omittedCursorCount, "retry query cursor")} omitted to keep follow-up targets compact._");
        }

        AppendHiddenCount(
            builder,
            continuations.Count + omittedSourceContinuationCount,
            visibleContinuations.Count,
            "follow-up target");
        builder.AppendLine();
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
            builder.Append("- **Source:** ");
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
            .GroupBy(continuation => continuation.Kind, StringComparer.Ordinal))
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
        foreach (var group in cursorCandidates.GroupBy(item => item.Continuation.Kind, StringComparer.Ordinal))
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
        return string.Equals(continuation.Kind, SourceContinuationKind, StringComparison.Ordinal);
    }

    private static IReadOnlyList<MarkdownContinuationTarget> CreateMarkdownContinuations(
        CodeExploreResult result,
        string? query)
    {
        var continuations = new List<MarkdownContinuationTarget>();
        foreach (var continuation in result.ContinuationTargets)
        {
            continuations.Add(new MarkdownContinuationTarget(
                SourceContinuationKind,
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
                "Impact",
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
                "Artifact",
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

    private static void AppendNextActions(
        StringBuilder builder,
        IReadOnlyList<CodeExploreNextActionHint>? actions)
    {
        if (actions is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("**Next actions**");
        builder.AppendLine();
        foreach (var action in actions.Take(MaximumNextActions))
        {
            builder.AppendLine($"- {BoundInline(action.Message, 280)}");
        }

        AppendHiddenCount(builder, actions.Count, MaximumNextActions, "next action");
        builder.AppendLine();
    }

    private static void AppendOmissions(StringBuilder builder, CodeExploreResult result)
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

        builder.AppendLine("**Not shown**");
        builder.AppendLine();
        foreach (var omission in omissions.Take(MaximumOmissions))
        {
            builder.AppendLine("- " + BoundInline(omission, 280));
        }

        AppendHiddenCount(builder, omissions.Length, MaximumOmissions, "omission");
        builder.AppendLine();
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

    private static string FormatCount(int count, string noun)
    {
        return $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";
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

    private static string? ExtractExplorationQuery(string markdown)
    {
        var lineEnd = markdown.IndexOfAny(['\r', '\n']);
        var firstLine = lineEnd < 0 ? markdown : markdown[..lineEnd];
        if (!firstLine.StartsWith(ExplorationPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var codeSpan = firstLine[ExplorationPrefix.Length..];
        var delimiterLength = 0;
        while (delimiterLength < codeSpan.Length && codeSpan[delimiterLength] == '`')
        {
            delimiterLength++;
        }

        if (delimiterLength == 0 || codeSpan.Length <= delimiterLength * 2)
        {
            return null;
        }

        var closingDelimiter = codeSpan[^delimiterLength..];
        if (closingDelimiter.Any(character => character != '`'))
        {
            return null;
        }

        var query = codeSpan[delimiterLength..^delimiterLength];
        return query.Length >= 2 && query[0] == ' ' && query[^1] == ' '
            ? query[1..^1]
            : query;
    }

    private static void AppendHiddenCount(
        StringBuilder builder,
        int count,
        int maximum,
        string noun)
    {
        var hidden = count - maximum;
        if (hidden > 0)
        {
            builder.AppendLine($"- _{FormatCount(hidden, noun)} not shown._");
        }
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
        string Kind,
        string Anchor,
        string? FilePath,
        int? StartLine,
        int? EndLine,
        string Reason,
        string? Cursor);

    private sealed record MarkdownEvidenceItem(
        string Label,
        string? FilePath,
        SourceRange? Range,
        string Reason);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
