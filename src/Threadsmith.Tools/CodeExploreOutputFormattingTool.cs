namespace Threadsmith.Tools;

using System.Globalization;
using System.Text;
using Threadsmith.Core;

/// <summary>Decorates code_explore with configurable model-visible output formatting.</summary>
public sealed class CodeExploreOutputFormattingTool : ITool
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
        return execution.Value is CodeExploreResult result
            ? execution with { ModelResultContent = CodeExploreMarkdownRenderer.Render(result, query) }
            : execution;
    }
}

/// <summary>Renders a compact CodeGraph-style model-facing projection from authoritative code-explore DTOs.</summary>
internal static class CodeExploreMarkdownRenderer
{
    private const int MaximumImpactItems = 32;
    private const int MaximumFlowEdges = 24;
    private const int MaximumFlowBoundaries = 12;
    private const int MaximumAssociatedArtifacts = 12;
    private const int MaximumArtifactOmissions = 4;
    private const int MaximumBackReferences = 16;
    private const int MaximumContinuations = 16;
    private const int MaximumNextActions = 8;
    private const int MaximumOmissions = 12;
    private const int MaximumSemanticIdentities = 8;

    /// <summary>Renders one concise source-first exploration result.</summary>
    internal static string Render(CodeExploreResult result, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        AppendHeader(builder, result, query);
        AppendAvailability(builder, result);
        AppendBlastRadius(builder, result.BlastRadius);
        AppendSourceCode(builder, result.FileSections);
        AppendFlow(builder, result.Flow);
        AppendAssociatedArtifacts(builder, result.AssociatedArtifacts);
        AppendBackReferences(builder, result.BackReferences);
        AppendContinuations(builder, result, query);
        AppendNextActions(builder, result.Presentation?.NextActions ?? result.Availability?.RecommendedActions);
        AppendOmissions(builder, result);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendHeader(StringBuilder builder, CodeExploreResult result, string? query)
    {
        builder.Append("**Exploration:** ");
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
        if (blastRadius is not { Items.Count: > 0 })
        {
            return;
        }

        builder.AppendLine("**Blast radius — what depends on these**");
        builder.AppendLine();
        foreach (var item in blastRadius.Items.Take(MaximumImpactItems))
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

        AppendHiddenCount(builder, blastRadius.Items.Count, MaximumImpactItems, "impact item");
        builder.AppendLine();
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

        builder.AppendLine("**Associated artifacts**");
        builder.AppendLine();
        foreach (var artifact in artifacts.Take(MaximumAssociatedArtifacts))
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

        AppendHiddenCount(builder, artifacts.Count, MaximumAssociatedArtifacts, "associated artifact");
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

        details.AddRange(artifact.Omissions);
        return details
            .Where(static detail => !string.IsNullOrWhiteSpace(detail))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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
        if (continuations.Count == 0)
        {
            return;
        }

        builder.AppendLine("**Follow-up targets**");
        builder.AppendLine();
        foreach (var continuation in continuations.Take(MaximumContinuations))
        {
            var location = string.IsNullOrWhiteSpace(continuation.FilePath)
                ? string.Empty
                : $" — {FormatCodeSpan(continuation.FilePath)}{FormatOptionalRange(continuation.StartLine, continuation.EndLine)}";
            builder.AppendLine(
                $"- **{continuation.Kind}:** {FormatCodeSpan(continuation.Anchor)}{location} — {BoundInline(continuation.Reason, 280)}");
            if (continuation.Cursor is { } cursor)
            {
                builder.AppendLine($"  - Retry query: {FormatCodeSpan(cursor)}");
            }
            else
            {
                builder.AppendLine("  - Retry query cursor omitted because it exceeded the query length limit; use the shown path/range only if no exact cursor is available.");
            }
        }

        AppendHiddenCount(builder, continuations.Count, MaximumContinuations, "follow-up target");
        builder.AppendLine();
    }

    private static IReadOnlyList<MarkdownContinuationTarget> CreateMarkdownContinuations(
        CodeExploreResult result,
        string? query)
    {
        var continuations = new List<MarkdownContinuationTarget>();
        foreach (var continuation in result.ContinuationTargets)
        {
            continuations.Add(new MarkdownContinuationTarget(
                "Source",
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

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
