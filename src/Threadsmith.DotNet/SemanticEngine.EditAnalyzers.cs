namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Threadsmith.Core;

/// <summary>Selected document analyzer operations shared by experiments and future edit feedback.</summary>
public sealed partial class SemanticEngine
{
    /// <summary>Measures matching immutable document coverage through the production snapshot queue and lifetime owner.</summary>
    internal async Task<EditAnalyzerComparison> AnalyzeAnalyzerCandidateAsync(
        Solution source,
        Solution candidate,
        IReadOnlyList<DocumentId> documents,
        EditAnalyzerOptions options,
        bool includeFullReference,
        bool reuseBaseline,
        CancellationToken cancellationToken)
    {
        var captured = options.Capture();
        if (!ReferenceEquals(source.Workspace, candidate.Workspace) || source.Id != candidate.Id)
        {
            throw new ArgumentException("Analyzer candidates must derive from the captured workspace snapshot.", nameof(candidate));
        }

        var ids = documents.Distinct().ToArray();
        if (ids.Length == 0 || ids.Any(id => source.GetDocument(id) is null || candidate.GetDocument(id) is null))
        {
            throw new ArgumentException("Matching baseline and candidate document coverage is required.", nameof(documents));
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(captured.BudgetMilliseconds);
        var started = Stopwatch.GetTimestamp();
        var result = await RunSnapshotOperationAsync(
            source,
            async token =>
        {
            var queueMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var before = new List<EditAnalyzerProjectResult>();
            var after = new List<EditAnalyzerProjectResult>();
            foreach (var group in ids.GroupBy(id => id.ProjectId))
            {
                var scope = group.ToImmutableHashSet();
                EditAnalyzerProjectResult? retained = null;
                if (reuseBaseline && !includeFullReference)
                {
                    lock (_gate)
                    {
                        var basis = _editDiagnosticBases.GetValueOrDefault(group.Key);
                        retained = basis?.AnalyzerCandidate?.Match(source, scope, captured)
                            ?? basis?.AnalyzerBaseline?.Match(source, scope, captured);
                    }
                }

                before.Add(retained is null
                    ? await AnalyzeDocumentAnalyzersAsync(source.GetProject(group.Key) ?? throw new InvalidOperationException("Baseline project unavailable."), [.. group], captured, includeFullReference, token)
                    : retained with
                    {
                        Reused = true,
                        PreparationMilliseconds = 0,
                        AnalysisMilliseconds = 0,
                        Analyzers = retained.Analyzers.Select(analyzer => analyzer with { ExecutionMilliseconds = 0 }).ToImmutableArray(),
                    });
                after.Add(await AnalyzeDocumentAnalyzersAsync(candidate.GetProject(group.Key) ?? throw new InvalidOperationException("Candidate project unavailable."), [.. group], captured, includeFullReference, token));
            }

            token.ThrowIfCancellationRequested();
            return new EditAnalyzerComparison(before, after, queueMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        },
            budget.Token);

        // Publish only after the production wrapper has accepted actual completion and generation.
        // Failed, abandoned or superseded operations never seed a reusable basis.
        if (reuseBaseline && !includeFullReference)
        {
            lock (_gate)
            {
                budget.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (!ReferenceEquals(source, _solution))
                {
                    throw new InvalidOperationException("The analyzer snapshot was superseded before evidence retention.");
                }

                var index = 0;
                foreach (var group in ids.GroupBy(id => id.ProjectId))
                {
                    var baseline = result.Before[index];
                    var analyzed = result.After[index++];

                    // Reuse complete evidence only; the existing retention bound never silently truncates it.
                    if (baseline.UnavailableIds.Count != 0 || analyzed.UnavailableIds.Count != 0
                        || baseline.Diagnostics.Count > _editOptions.MaximumRetainedDiagnostics
                        || analyzed.Diagnostics.Count > _editOptions.MaximumRetainedDiagnostics)
                    {
                        continue;
                    }

                    var scope = group.ToImmutableHashSet();
                    var basis = GetEditProjectBasis(group.Key);
                    _editDiagnosticBases[group.Key] = basis with
                    {
                        AnalyzerBaseline = new(new(source), scope, [.. captured.DiagnosticIds], captured.ConcurrentAnalysis, baseline),
                        AnalyzerCandidate = new(new(candidate), scope, [.. captured.DiagnosticIds], captured.ConcurrentAnalysis, analyzed),
                    };
                }
            }
        }

        result = result with { AvailableMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        SemanticLoadMetrics.EditAnalyzerDuration.Record(result.AvailableMilliseconds);
        return result;
    }

    /// <summary>Executes entire selected instances on whole changed documents; callers own queue admission and cancellation.</summary>
    internal static async Task<EditAnalyzerProjectResult> AnalyzeDocumentAnalyzersAsync(
        Project project,
        IReadOnlyList<DocumentId> documents,
        EditAnalyzerOptions options,
        bool includeFullReference,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var requested = options.DiagnosticIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = project.AnalyzerReferences.SelectMany(reference => reference.GetAnalyzers(project.Language)).Distinct().ToArray();
        var selected = available
            .Where(analyzer => analyzer.SupportedDiagnostics.Any(rule => requested.Contains(rule.Id)))
            .ToArray();
        var executedIds = selected.SelectMany(analyzer => analyzer.SupportedDiagnostics.Select(rule => rule.Id)).ToHashSet(StringComparer.Ordinal);
        var analyzers = selected.Concat(available.OfType<DiagnosticSuppressor>()
            .Where(suppressor => suppressor.SupportedSuppressions.Any(rule => executedIds.Contains(rule.SuppressedDiagnosticId))))
            .ToImmutableArray();
        var missing = requested.Except(analyzers.SelectMany(analyzer => analyzer.SupportedDiagnostics.Select(rule => rule.Id)), StringComparer.OrdinalIgnoreCase).ToArray();
        var identities = new List<EditAnalyzerIdentity>();
        var findings = new List<EditAnalyzerFinding>();
        var referenceFindings = new List<EditAnalyzerFinding>();
        var configuration = new List<EditAnalyzerRuleConfiguration>();
        var omissions = new List<string>
        {
            "Whole changed documents only; other documents, additional-file actions, compilation and compilation-end diagnostics are omitted from scoped results.",
            "Absence in scoped results is not a clean project check. All supported rules of selected instances execute under effective project configuration.",
        };
        if (analyzers.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(project.Name, [], missing.ToImmutableArray(), [], [], [], omissions.ToImmutableArray(), 0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var compilation = await project.GetCompilationAsync(cancellationToken)
            ?? throw new InvalidOperationException("Analyzer compilation is unavailable.");
        var preparationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var failures = new ConcurrentQueue<Exception>();
        var driver = compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
            project.AnalyzerOptions,
            (exception, _, _) => failures.Enqueue(exception),
            options.ConcurrentAnalysis,
            logAnalyzerExecutionTime: true,
            reportSuppressedDiagnostics: true));
        var analysisStarted = Stopwatch.GetTimestamp();
        foreach (var id in documents)
        {
            var document = project.GetDocument(id) ?? throw new InvalidOperationException("Analyzer document is unavailable.");
            var tree = await document.GetSyntaxTreeAsync(cancellationToken)
                ?? throw new InvalidOperationException("Analyzer syntax tree is unavailable.");
            foreach (var rule in analyzers.SelectMany(analyzer => analyzer.SupportedDiagnostics).DistinctBy(rule => rule.Id))
            {
                var effective = rule.GetEffectiveSeverity(compilation.Options);
                if (compilation.Options.SyntaxTreeOptionsProvider?.TryGetDiagnosticValue(tree, rule.Id, cancellationToken, out var treeSeverity) == true)
                {
                    effective = treeSeverity;
                }

                project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue("dotnet_diagnostic." + rule.Id + ".severity", out var configured);
                configuration.Add(new(document.FilePath ?? document.Name, rule.Id, effective.ToString(), configured));
            }

            findings.AddRange((await driver.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken)).Select(ToAnalyzerFinding));
            findings.AddRange((await driver.GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(tree), null, cancellationToken)).Select(ToAnalyzerFinding));
        }

        var analysisMilliseconds = Stopwatch.GetElapsedTime(analysisStarted).TotalMilliseconds;

        // Capture telemetry before the optional reference pass, so timings describe scoped work only.
        foreach (var analyzer in analyzers)
        {
            var telemetry = await driver.GetAnalyzerTelemetryInfoAsync(analyzer, cancellationToken);
            var actions = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["CompilationStart"] = telemetry.CompilationStartActionsCount,
                ["Compilation"] = telemetry.CompilationActionsCount,
                ["CompilationEnd"] = telemetry.CompilationEndActionsCount,
                ["SyntaxTree"] = telemetry.SyntaxTreeActionsCount,
                ["SemanticModel"] = telemetry.SemanticModelActionsCount,
                ["AdditionalFile"] = telemetry.AdditionalFileActionsCount,
                ["Symbol"] = telemetry.SymbolActionsCount,
                ["SymbolStart"] = telemetry.SymbolStartActionsCount,
                ["SymbolEnd"] = telemetry.SymbolEndActionsCount,
                ["SyntaxNode"] = telemetry.SyntaxNodeActionsCount,
                ["CodeBlockStart"] = telemetry.CodeBlockStartActionsCount,
                ["CodeBlock"] = telemetry.CodeBlockActionsCount,
                ["CodeBlockEnd"] = telemetry.CodeBlockEndActionsCount,
                ["Operation"] = telemetry.OperationActionsCount,
                ["OperationBlockStart"] = telemetry.OperationBlockStartActionsCount,
                ["OperationBlock"] = telemetry.OperationBlockActionsCount,
                ["OperationBlockEnd"] = telemetry.OperationBlockEndActionsCount,
                ["Suppression"] = telemetry.SuppressionActionsCount,
            };
            var type = analyzer.GetType();
            identities.Add(new(
                type.Assembly.GetName().Name ?? "unknown",
                type.Assembly.GetName().Version?.ToString() ?? "unknown",
                type.FullName ?? type.Name,
                analyzer.SupportedDiagnostics.Select(rule => rule.Id).ToImmutableArray(),
                analyzer is DiagnosticSuppressor suppressor ? suppressor.SupportedSuppressions.Select(rule => rule.Id + ":" + rule.SuppressedDiagnosticId).ToImmutableArray() : [],
                actions.ToImmutableDictionary(StringComparer.Ordinal),
                telemetry.ExecutionTime.TotalMilliseconds));
        }

        if (includeFullReference)
        {
            referenceFindings.AddRange((await driver.GetAnalyzerDiagnosticsAsync(cancellationToken)).Select(ToAnalyzerFinding));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!failures.IsEmpty)
        {
            throw new AggregateException("Selected analyzer execution failed; no clean result is available.", failures);
        }

        SemanticLoadMetrics.EditAnalyzerPasses.Add(1);
        return new(project.Name, identities.ToImmutableArray(), missing.ToImmutableArray(), findings.Distinct().ToImmutableArray(), referenceFindings.Distinct().ToImmutableArray(), configuration.ToImmutableArray(), omissions.ToImmutableArray(), preparationMilliseconds, analysisMilliseconds);
    }

    private static EditAnalyzerFinding ToAnalyzerFinding(Microsoft.CodeAnalysis.Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new(diagnostic.Id, diagnostic.Severity.ToString(), diagnostic.IsSuppressed, span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, diagnostic.GetMessage());
    }

    // Weak snapshot identity avoids retaining compiler graphs. Any source, reference, configuration,
    // additional-file or generator-input snapshot change is a conservative miss. Exact promotion
    // preserves candidate identity, as it does for the existing compiler feedback path.
    private sealed record EditAnalyzerBasis(WeakReference<Solution> Snapshot, ImmutableHashSet<DocumentId> Documents,
        ImmutableArray<string> DiagnosticIds, bool ConcurrentAnalysis, EditAnalyzerProjectResult Result)
    {
        public EditAnalyzerProjectResult? Match(Solution snapshot, ImmutableHashSet<DocumentId> documents, EditAnalyzerOptions options)
        {
            return Snapshot.TryGetTarget(out var retained) && ReferenceEquals(retained, snapshot)
                && Documents.SetEquals(documents) && DiagnosticIds.SequenceEqual(options.DiagnosticIds, StringComparer.Ordinal)
                && ConcurrentAnalysis == options.ConcurrentAnalysis ? Result : null;
        }
    }
}

/// <summary>Host-owned experiment evidence, never persisted as engine state.</summary>
internal sealed record EditAnalyzerComparison(IReadOnlyList<EditAnalyzerProjectResult> Before, IReadOnlyList<EditAnalyzerProjectResult> After, double QueueMilliseconds, double AvailableMilliseconds);

/// <summary>Whole-document coverage and unfiltered evidence for every executed analyzer instance.</summary>
internal sealed record EditAnalyzerProjectResult(string Project, IReadOnlyList<EditAnalyzerIdentity> Analyzers, IReadOnlyList<string> UnavailableIds,
    IReadOnlyList<EditAnalyzerFinding> Diagnostics, IReadOnlyList<EditAnalyzerFinding> FullReferenceDiagnostics,
    IReadOnlyList<EditAnalyzerRuleConfiguration> Configuration, IReadOnlyList<string> Omissions, double PreparationMilliseconds, double AnalysisMilliseconds)
{
    /// <summary>Gets whether evidence was reused without a new baseline pass; execution timings are zero in that case.</summary>
    public bool Reused { get; init; }
}

/// <summary>Identity and registered action telemetry for the entire selected instance.</summary>
internal sealed record EditAnalyzerIdentity(string Assembly, string Version, string Type, IReadOnlyList<string> SupportedIds, IReadOnlyList<string> Suppressions, IReadOnlyDictionary<string, int> Actions, double ExecutionMilliseconds);

/// <summary>Effective configuration per supported rule and checked document.</summary>
internal sealed record EditAnalyzerRuleConfiguration(string Document, string Id, string EffectiveSeverity, string? EditorConfigSeverity);

/// <summary>Unfiltered experiment finding with effective severity and suppression state.</summary>
internal sealed record EditAnalyzerFinding(string Id, string Severity, bool Suppressed, string Path, int Line, int Column, string Message);
