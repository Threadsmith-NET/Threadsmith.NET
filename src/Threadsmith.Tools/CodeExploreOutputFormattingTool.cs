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

        return execution.Value is CodeExploreResult result
            ? execution with { ModelResultContent = CodeExploreMarkdownRenderer.Render(result) }
            : execution;
    }
}

/// <summary>Renders a model-facing Markdown projection from authoritative code_explore DTOs.</summary>
internal static class CodeExploreMarkdownRenderer
{
    private const int MaximumNotShownTargets = 16;
    private const int MaximumNextActions = 12;
    private const int MaximumContinuations = 24;
    private const int MaximumBackReferences = 24;
    private const int MaximumFileRelevanceRows = 24;
    private const int MaximumOmissions = 16;
    private const int MaximumSemanticIdentities = 8;
    private const int MaximumResolvedAnchors = 24;
    private const int MaximumAnchorAlternatives = 8;
    private const int MaximumCandidateSummaries = 24;
    private const int MaximumBlastRadiusItems = 32;
    private const int MaximumBlastRadiusContinuations = 12;
    private const int MaximumFlowEdges = 24;
    private const int MaximumFlowBoundaries = 16;
    private const int MaximumAssociatedArtifacts = 16;

    /// <summary>Renders a bounded Markdown projection for one code_explore result.</summary>
    internal static string Render(CodeExploreResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        builder.AppendLine("# code_explore result");
        builder.AppendLine();
        AppendSummary(builder, result);
        AppendHowToUse(builder, result);
        AppendResolvedAnchors(builder, result.ResolvedAnchors);
        AppendCandidateSummaries(builder, result.CandidateSummaries);
        AppendBlastRadius(builder, result.BlastRadius);
        AppendSourceCode(builder, result);
        AppendBackReferences(builder, result.BackReferences);
        AppendFlow(builder, result.Flow);
        AppendAssociatedArtifacts(builder, result.AssociatedArtifacts, result.ArtifactCoverage);
        AppendFileRelevance(builder, result.FileRelevance);
        AppendNotShown(builder, result.Presentation?.NotShownTargets);
        AppendContinuations(builder, result.ContinuationTargets);
        AppendNextActions(builder, result.Presentation?.NextActions ?? result.Availability?.RecommendedActions);
        AppendOmissions(builder, result.Omissions, result.Coverage, result.ArtifactCoverage);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendSummary(StringBuilder builder, CodeExploreResult result)
    {
        var availability = result.Availability;
        if (!string.IsNullOrWhiteSpace(result.Presentation?.ModelSummary))
        {
            builder.AppendLine(result.Presentation.ModelSummary.Trim());
        }

        if (availability is not null)
        {
            builder.AppendLine(
                $"Availability: **{availability.Status}** — {CleanInline(availability.Reason)}");
        }

        builder.AppendLine(
            $"Confidence: **{result.Confidence}**; workspace generation: `{result.WorkspaceGeneration.ToString(CultureInfo.InvariantCulture)}`.");
        builder.AppendLine(
            $"Returned source sections: {result.FileSections.Count.ToString(CultureInfo.InvariantCulture)}; "
            + $"back-references: {(result.BackReferences?.Count ?? 0).ToString(CultureInfo.InvariantCulture)}; "
            + $"continuations: {result.ContinuationTargets.Count.ToString(CultureInfo.InvariantCulture)}.");
        if (result.AdaptiveBudget is { } budget)
        {
            builder.AppendLine(
                $"Adaptive envelope: {budget.RepositoryScale.Tier}; max files {budget.EffectiveMaximumFiles.ToString(CultureInfo.InvariantCulture)}, "
                + $"source {budget.EffectiveMaximumSourceCharacters.ToString(CultureInfo.InvariantCulture)} chars, "
                + $"per-file {budget.EffectiveMaximumPerFileSourceCharacters.ToString(CultureInfo.InvariantCulture)} chars.");
        }

        builder.AppendLine();
    }

    private static void AppendHowToUse(StringBuilder builder, CodeExploreResult result)
    {
        builder.AppendLine("## How to use this result");
        if (result.Presentation?.SourceGuarantees is { Count: > 0 } guarantees)
        {
            var readEquivalent = guarantees.Count(guarantee => guarantee.IsReadEquivalent);
            builder.AppendLine(
                $"- {readEquivalent.ToString(CultureInfo.InvariantCulture)} returned or referenced range(s) are source-identity backed for their advertised line spans. Returned source text is the host-sanitized model-visible projection; digests identify the current source bytes before sanitization.");
        }
        else
        {
            builder.AppendLine("- Treat complete `Source Code` sections as current, line-numbered, host-sanitized source projections for their advertised ranges; digests identify source bytes before sanitization.");
        }

        builder.AppendLine("- Use `Back-references already visible` instead of re-reading unchanged source already present in this model request.");
        builder.AppendLine("- Use `Continuation targets` for focused follow-up rather than broad search or adjacent pagination.");
        builder.AppendLine("- The structured JSON result remains the host authority; this Markdown is derived from that DTO.");
        builder.AppendLine();
    }

    private static void AppendResolvedAnchors(
        StringBuilder builder,
        IReadOnlyList<CodeExploreAnchorResolution> resolutions)
    {
        if (resolutions.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Resolved anchors");
        foreach (var resolution in resolutions.Take(MaximumResolvedAnchors))
        {
            builder.AppendLine(
                $"- {resolution.Kind} {FormatCodeSpan(resolution.Input)}: **{resolution.Outcome}** — {BoundInline(resolution.Reason, 240)}");
            if (resolution.SelectedSymbol is { } selectedSymbol)
            {
                builder.AppendLine($"  - Selected symbol: {FormatSymbol(selectedSymbol)}");
            }

            if (resolution.SelectedLocation is { } selectedLocation)
            {
                builder.AppendLine($"  - Selected location: {FormatLocation(selectedLocation)}");
            }

            if (resolution.Alternatives.Count > 0)
            {
                builder.AppendLine("  - Alternatives:");
                foreach (var alternative in resolution.Alternatives.Take(MaximumAnchorAlternatives))
                {
                    builder.AppendLine(
                        $"    - {FormatSymbol(alternative.Symbol)}"
                        + (alternative.Location is null ? string.Empty : $" at {FormatLocation(alternative.Location)}"));
                }

                AppendAdditionalCountLine(
                    builder,
                    resolution.Alternatives.Count,
                    MaximumAnchorAlternatives,
                    "anchor alternative");
            }
        }

        AppendAdditionalCountLine(builder, resolutions.Count, MaximumResolvedAnchors, "resolved anchor");
        builder.AppendLine();
    }

    private static void AppendCandidateSummaries(
        StringBuilder builder,
        IReadOnlyList<CodeExploreCandidateSummary>? candidates)
    {
        if (candidates is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("## Candidate summaries");
        builder.AppendLine("| Rank | Candidate | Tier | Selected | Location | Reasons | Explanation |");
        builder.AppendLine("|---:|---|---|---|---|---|---|");
        foreach (var candidate in candidates.Take(MaximumCandidateSummaries))
        {
            var identity = candidate.Symbol is null
                ? candidate.FilePath ?? "candidate"
                : FormatSymbol(candidate.Symbol);
            var location = candidate.Location is null
                ? candidate.FilePath ?? "no exact location"
                : FormatLocation(candidate.Location);
            builder.AppendLine(
                $"| {candidate.Rank.ToString(CultureInfo.InvariantCulture)} "
                + $"| {EscapeTableCell(identity)} "
                + $"| {candidate.Tier} "
                + $"| {FormatBool(candidate.Selected)} "
                + $"| {EscapeTableCell(location)} "
                + $"| {EscapeTableCell(BoundInline(candidate.Reasons.ToString(), 160))} "
                + $"| {EscapeTableCell(BoundInline(candidate.Reason, 240))} |");
        }

        AppendAdditionalCountLine(builder, candidates.Count, MaximumCandidateSummaries, "candidate summary");
        builder.AppendLine();
    }

    private static void AppendBlastRadius(StringBuilder builder, CodeExploreBlastRadius? blastRadius)
    {
        if (blastRadius is null)
        {
            return;
        }

        builder.AppendLine("## Impact / blast-radius evidence");
        builder.AppendLine(
            $"Callers: {blastRadius.ReturnedCallers.ToString(CultureInfo.InvariantCulture)}/{blastRadius.TotalCallers.ToString(CultureInfo.InvariantCulture)}; "
            + $"implementations: {blastRadius.ReturnedImplementations.ToString(CultureInfo.InvariantCulture)}/{blastRadius.TotalImplementations.ToString(CultureInfo.InvariantCulture)}; "
            + $"projects: {blastRadius.ReturnedProjects.ToString(CultureInfo.InvariantCulture)}/{blastRadius.TotalProjects.ToString(CultureInfo.InvariantCulture)}; "
            + $"tests: {blastRadius.ReturnedTests.ToString(CultureInfo.InvariantCulture)}/{blastRadius.TotalTests.ToString(CultureInfo.InvariantCulture)}.");
        if (blastRadius.Items.Count > 0)
        {
            builder.AppendLine("| Anchor | Kind | Symbol | Location | Project | Reason |");
            builder.AppendLine("|---|---|---|---|---|---|");
            foreach (var item in blastRadius.Items.Take(MaximumBlastRadiusItems))
            {
                builder.AppendLine(
                    $"| {EscapeTableCell(FormatCodeSpan(item.AnchorSymbolId))} "
                    + $"| {item.Kind} "
                    + $"| {EscapeTableCell(item.Symbol is null ? "n/a" : FormatSymbol(item.Symbol))} "
                    + $"| {EscapeTableCell(item.Location is null ? "no exact location" : FormatLocation(item.Location))} "
                    + $"| {EscapeTableCell(item.ProjectName ?? "n/a")} "
                    + $"| {EscapeTableCell(BoundInline(item.Reason, 240))} |");
            }

            AppendAdditionalCountLine(builder, blastRadius.Items.Count, MaximumBlastRadiusItems, "impact item");
        }

        if (blastRadius.ContinuationTargets.Count > 0)
        {
            builder.AppendLine("Impact continuations:");
            foreach (var target in blastRadius.ContinuationTargets.Take(MaximumBlastRadiusContinuations))
            {
                builder.AppendLine(
                    $"- {FormatCodeSpan(target.Anchor)} ({target.Kind}) {FormatTargetPath(target.FilePath, CreateRange(target))} — {BoundInline(target.Reason, 240)}");
            }

            AppendAdditionalCountLine(
                builder,
                blastRadius.ContinuationTargets.Count,
                MaximumBlastRadiusContinuations,
                "impact continuation");
        }

        if (blastRadius.Omissions.Count > 0)
        {
            builder.AppendLine("Impact omissions:");
            foreach (var omission in blastRadius.Omissions.Take(MaximumOmissions))
            {
                builder.AppendLine("- " + BoundInline(omission, 240));
            }

            AppendAdditionalCountLine(builder, blastRadius.Omissions.Count, MaximumOmissions, "impact omission");
        }

        builder.AppendLine();
    }

    private static void AppendSourceCode(StringBuilder builder, CodeExploreResult result)
    {
        builder.AppendLine("## Source Code");
        if (result.FileSections.Count == 0)
        {
            builder.AppendLine("_No new C# source was returned._");
            builder.AppendLine();
            return;
        }

        foreach (var section in result.FileSections)
        {
            var range = FormatRange(section.Source.Range);
            builder.AppendLine($"### {FormatCodeSpan(section.FilePath)} {range}");
            builder.AppendLine(
                $"Project: {FormatCodeSpan(section.ProjectName)}; target framework: {FormatCodeSpan(section.TargetFramework)}; "
                + $"completeness: **{section.Source.Completeness}**; generated: {FormatBool(section.IsGenerated)}; linked: {FormatBool(section.IsLinked)}.");
            builder.AppendLine($"Reason: {CleanInline(section.SelectionReason)}");
            if (!string.IsNullOrWhiteSpace(section.Source.FileSha256))
            {
                builder.AppendLine(
                    $"Identity: file sha256 {FormatCodeSpan(section.Source.FileSha256)}"
                    + (string.IsNullOrWhiteSpace(section.Source.RangeSha256)
                        ? "."
                        : $"; range sha256 {FormatCodeSpan(section.Source.RangeSha256)}."));
            }

            if (section.SemanticIdentities.Count > 0)
            {
                builder.AppendLine(
                    "Symbols: "
                    + string.Join(
                        ", ",
                        section.SemanticIdentities
                            .Take(MaximumSemanticIdentities)
                            .Select(identity => $"{FormatCodeSpan(identity.DisplayName)} ({CleanInline(identity.Kind)})"))
                    + FormatAdditionalCount(section.SemanticIdentities.Count, MaximumSemanticIdentities));
            }

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
                builder.AppendLine("_Source text was not emitted for this section._");
            }

            if (section.Source.OmittedRanges.Count > 0)
            {
                builder.AppendLine(
                    "Omitted ranges: "
                    + string.Join(", ", section.Source.OmittedRanges.Select(CleanInline)));
            }

            if (!string.IsNullOrWhiteSpace(section.Source.ContinuationAnchor))
            {
                builder.AppendLine($"Continuation anchor: {FormatCodeSpan(section.Source.ContinuationAnchor)}");
            }

            builder.AppendLine();
        }
    }

    private static void AppendBackReferences(
        StringBuilder builder,
        IReadOnlyList<CodeExploreBackReference>? backReferences)
    {
        if (backReferences is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("## Back-references already visible");
        foreach (var reference in backReferences.Take(MaximumBackReferences))
        {
            builder.AppendLine(
                $"- {FormatCodeSpan(reference.FilePath)} {FormatRange(reference.Range)} — already visible from tool call {FormatCodeSpan(reference.ToolCallId)}; "
                + $"file sha256 {FormatCodeSpan(reference.FileSha256)}. {CleanInline(reference.Reason)}");
        }

        AppendAdditionalCountLine(builder, backReferences.Count, MaximumBackReferences, "back-reference");
        builder.AppendLine();
    }

    private static void AppendFlow(StringBuilder builder, CodeExploreFlow? flow)
    {
        if (flow is null)
        {
            return;
        }

        builder.AppendLine("## Compiler-proven flow evidence");
        builder.AppendLine(
            $"Nodes: {flow.Nodes.Count.ToString(CultureInfo.InvariantCulture)}; "
            + $"edges: {flow.Edges.Count.ToString(CultureInfo.InvariantCulture)}; "
            + $"paths: {flow.Paths.Count.ToString(CultureInfo.InvariantCulture)}; "
            + $"dispatch branches: {flow.DispatchBranches.Count.ToString(CultureInfo.InvariantCulture)}.");
        foreach (var edge in flow.Edges.Take(MaximumFlowEdges))
        {
            var callSite = edge.CallSite is null
                ? string.Empty
                : $" at {FormatCodeSpan(edge.CallSite.FilePath)} {FormatRange(edge.CallSite.Range)}";
            builder.AppendLine(
                $"- {FormatCodeSpan(edge.CallerSymbolId)} -> {FormatCodeSpan(edge.CalleeSymbolId)} "
                + $"({edge.DispatchKind}, {edge.ProofKind}){callSite}. {CleanInline(edge.Proof)}");
        }

        AppendAdditionalCountLine(builder, flow.Edges.Count, MaximumFlowEdges, "flow edge");
        if (flow.Boundaries.Count > 0)
        {
            builder.AppendLine("Boundaries:");
            foreach (var boundary in flow.Boundaries.Take(MaximumFlowBoundaries))
            {
                builder.AppendLine(
                    $"- {boundary.Kind}: {FormatCodeSpan(boundary.SymbolId)} — {CleanInline(boundary.Reason)}");
            }

            AppendAdditionalCountLine(builder, flow.Boundaries.Count, MaximumFlowBoundaries, "flow boundary");
        }

        builder.AppendLine();
    }

    private static void AppendAssociatedArtifacts(
        StringBuilder builder,
        IReadOnlyList<CodeExploreAssociatedArtifact>? artifacts,
        CodeExploreArtifactCoverage? coverage)
    {
        if (artifacts is not { Count: > 0 } && coverage is null)
        {
            return;
        }

        builder.AppendLine("## Associated artifacts");
        if (coverage is not null)
        {
            builder.AppendLine(
                $"Returned {coverage.ReturnedCount.ToString(CultureInfo.InvariantCulture)} of {coverage.CandidateCount.ToString(CultureInfo.InvariantCulture)} candidate artifact(s); "
                + $"spent {coverage.SpentCharacters.ToString(CultureInfo.InvariantCulture)} chars; complete: {FormatBool(coverage.Complete)}.");
        }

        if (artifacts is { Count: > 0 })
        {
            foreach (var artifact in artifacts.Take(MaximumAssociatedArtifacts))
            {
                var identity = artifact.FilePath ?? artifact.LogicalName ?? "artifact";
                builder.AppendLine($"### {FormatCodeSpan(identity)}");
                builder.AppendLine(
                    $"Relationship: {artifact.Relationship}; evidence: {artifact.Evidence}; media: {artifact.MediaKind}; origin {FormatCodeSpan(artifact.OriginFilePath)} {FormatRange(artifact.OriginRange)}.");
                if (artifact.SelectionReasons.Count > 0)
                {
                    builder.AppendLine("Reasons: " + string.Join("; ", artifact.SelectionReasons.Select(CleanInline)));
                }

                if (artifact.Content is { } content)
                {
                    builder.AppendLine(
                        $"Completeness: **{content.Completeness}**; file sha256 {FormatCodeSpan(content.FileSha256)}"
                        + (string.IsNullOrWhiteSpace(content.RangeSha256)
                            ? "."
                            : $"; range sha256 {FormatCodeSpan(content.RangeSha256)}."));
                    var fence = CreateFence(content.NumberedLines);
                    builder.AppendLine(fence + "text");
                    foreach (var line in content.NumberedLines)
                    {
                        builder.AppendLine(line);
                    }

                    builder.AppendLine(fence);
                }

                if (artifact.Omissions.Count > 0)
                {
                    builder.AppendLine("Omissions: " + string.Join("; ", artifact.Omissions.Select(CleanInline)));
                }

                builder.AppendLine();
            }

            AppendAdditionalCountLine(builder, artifacts.Count, MaximumAssociatedArtifacts, "associated artifact");
        }

        builder.AppendLine();
    }

    private static void AppendFileRelevance(
        StringBuilder builder,
        IReadOnlyList<CodeExploreFileRelevanceSummary>? fileRelevance)
    {
        if (fileRelevance is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("## File relevance and output status");
        builder.AppendLine("| Rank | File | Band | Output | Source | Reason |");
        builder.AppendLine("|---:|---|---|---|---:|---|");
        foreach (var summary in fileRelevance.Take(MaximumFileRelevanceRows))
        {
            builder.AppendLine(
                $"| {summary.Rank.ToString(CultureInfo.InvariantCulture)} "
                + $"| `{EscapeTableCell(summary.FilePath)}` "
                + $"| {summary.Band} "
                + $"| {summary.OutputStatus} "
                + $"| {summary.SpentCharacters.ToString(CultureInfo.InvariantCulture)}/{summary.AllocatedCharacters.ToString(CultureInfo.InvariantCulture)} "
                + $"| {EscapeTableCell(summary.Reason)} |");
        }

        AppendAdditionalCountLine(builder, fileRelevance.Count, MaximumFileRelevanceRows, "file-relevance row");
        builder.AppendLine();
    }

    private static void AppendNotShown(
        StringBuilder builder,
        IReadOnlyList<CodeExploreNotShownTarget>? targets)
    {
        if (targets is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("## Not shown targets");
        foreach (var target in targets.Take(MaximumNotShownTargets))
        {
            builder.AppendLine(
                $"- {target.Kind}: {FormatTargetPath(target.FilePath, target.Range)} — {CleanInline(target.Reason)}"
                + FormatContinuationAnchorSuffix(target.ContinuationAnchor));
        }

        AppendAdditionalCountLine(builder, targets.Count, MaximumNotShownTargets, "not-shown target");
        builder.AppendLine();
    }

    private static void AppendContinuations(
        StringBuilder builder,
        IReadOnlyList<CodeExploreContinuationTarget> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Continuation targets");
        foreach (var target in targets.Take(MaximumContinuations))
        {
            builder.AppendLine(
                $"- {FormatCodeSpan(target.Anchor)} ({target.Kind}) {FormatTargetPath(target.FilePath, CreateRange(target))} — "
                + $"{CleanInline(target.Reason)}"
                + (target.StartAtLine ? " Start at the supplied line." : string.Empty)
                + (target.SelectionMode is null ? string.Empty : $" Selection mode: {FormatCodeSpan(target.SelectionMode.ToString() ?? string.Empty)}.")
                + (string.IsNullOrWhiteSpace(target.ExpectedFileSha256) ? string.Empty : $" Expected file sha256 {FormatCodeSpan(target.ExpectedFileSha256)}.")
                + (target.WorkspaceGeneration is null ? string.Empty : $" Workspace generation {FormatCodeSpan(target.WorkspaceGeneration.Value.ToString(CultureInfo.InvariantCulture))}."));
        }

        AppendAdditionalCountLine(builder, targets.Count, MaximumContinuations, "continuation target");
        builder.AppendLine();
    }

    private static void AppendNextActions(
        StringBuilder builder,
        IReadOnlyList<CodeExploreNextActionHint>? actions)
    {
        if (actions is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("## Recommended next actions");
        foreach (var action in actions.Take(MaximumNextActions))
        {
            builder.AppendLine(
                $"- {action.Kind}: {CleanInline(action.Message)}"
                + (action.FilePath is null ? string.Empty : $" {FormatCodeSpan(action.FilePath)}")
                + (action.Range is null ? string.Empty : $" {FormatRange(action.Range)}")
                + FormatContinuationAnchorSuffix(action.ContinuationAnchor));
        }

        AppendAdditionalCountLine(builder, actions.Count, MaximumNextActions, "next action");
        builder.AppendLine();
    }

    private static void AppendOmissions(
        StringBuilder builder,
        IReadOnlyList<string> omissions,
        CodeExploreCoverage coverage,
        CodeExploreArtifactCoverage? artifactCoverage)
    {
        var combined = omissions
            .Concat(coverage.Omissions)
            .Concat(artifactCoverage?.Omissions ?? [])
            .Where(omission => !string.IsNullOrWhiteSpace(omission))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (combined.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Omissions");
        foreach (var omission in combined.Take(MaximumOmissions))
        {
            builder.AppendLine("- " + CleanInline(omission));
        }

        AppendAdditionalCountLine(builder, combined.Length, MaximumOmissions, "omission");
        builder.AppendLine();
    }

    private static SourceRange? CreateRange(CodeExploreContinuationTarget target)
    {
        return target.StartLine is { } startLine && target.EndLine is { } endLine
            ? new SourceRange(startLine, 1, endLine, 1)
            : null;
    }

    private static string FormatSymbol(SemanticSymbolIdentity symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return $"{FormatCodeSpan(symbol.DisplayName)} ({CleanInline(symbol.Kind)}, id {FormatCodeSpan(symbol.Id)})";
    }

    private static string FormatLocation(CodeExploreLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return $"{FormatCodeSpan(location.FilePath)} {FormatRange(location.Range)}; "
            + $"project {FormatCodeSpan(location.ProjectName)}; target {FormatCodeSpan(location.TargetFramework)}";
    }

    private static string FormatRange(SourceRange range)
    {
        return range.StartLine == range.EndLine
            ? $"L{range.StartLine.ToString(CultureInfo.InvariantCulture)}"
            : $"L{range.StartLine.ToString(CultureInfo.InvariantCulture)}-L{range.EndLine.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatTargetPath(string? filePath, SourceRange? range)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "no exact file path";
        }

        return range is null
            ? FormatCodeSpan(filePath)
            : $"{FormatCodeSpan(filePath)} {FormatRange(range)}";
    }

    private static string FormatContinuationAnchorSuffix(string? anchor)
    {
        return string.IsNullOrWhiteSpace(anchor)
            ? string.Empty
            : $" Continuation anchor: {FormatCodeSpan(anchor)}.";
    }

    private static string FormatBool(bool value)
    {
        return value ? "yes" : "no";
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

    private static string FormatAdditionalCount(int count, int maximum)
    {
        var hidden = count - maximum;
        return hidden <= 0
            ? string.Empty
            : $", plus {hidden.ToString(CultureInfo.InvariantCulture)} more";
    }

    private static void AppendAdditionalCountLine(
        StringBuilder builder,
        int count,
        int maximum,
        string noun)
    {
        var hidden = count - maximum;
        if (hidden <= 0)
        {
            return;
        }

        builder.AppendLine($"_Plus {hidden.ToString(CultureInfo.InvariantCulture)} more {noun}(s) not shown in this Markdown projection._");
    }

    private static string CreateFence(IReadOnlyList<string> lines)
    {
        var maximumRun = 0;
        foreach (var line in lines)
        {
            var current = 0;
            foreach (var character in line)
            {
                if (character == '`')
                {
                    current++;
                    maximumRun = Math.Max(maximumRun, current);
                }
                else
                {
                    current = 0;
                }
            }
        }

        return new string('`', Math.Max(3, maximumRun + 1));
    }

    private static string BoundInline(string? value, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var clean = CleanInline(value);
        return clean.Length <= maximumCharacters
            ? clean
            : clean[..Math.Max(0, maximumCharacters - 1)] + "…";
    }

    private static string CleanInline(string value)
    {
        return value.Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static string EscapeTableCell(string value)
    {
        return CleanInline(value).Replace("|", "\\|", StringComparison.Ordinal);
    }
}
