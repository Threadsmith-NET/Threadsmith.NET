namespace Threadsmith.Validation;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Threadsmith.Core;

/// <summary>Coordinates affected-project compilation, classification, correlation, and acceptance gating.</summary>
public sealed class ValidationPipeline
{
    private static readonly Counter<long> _baselineDiagnostics = ValidationMetrics.Meter.CreateCounter<long>(
        "threadsmith.validation.diagnostics.baseline");

    private static readonly Counter<long> _introducedDiagnostics = ValidationMetrics.Meter.CreateCounter<long>(
        "threadsmith.validation.diagnostics.introduced");

    private readonly AcceptanceGate _acceptanceGate;
    private readonly BuildExecutor _buildExecutor;
    private readonly DiagnosticClassifier _classifier;
    private readonly DiagnosticCorrelator _correlator;
    private readonly IDomainEventStream _events;
    private readonly ISemanticEngineResolver? _semanticEngine;
    private readonly TestValidationPipeline _testPipeline;
    private readonly TimeSpan _testTimeout;

    /// <summary>Initializes a new instance of the <see cref="ValidationPipeline"/> class.</summary>
    public ValidationPipeline(
        BuildExecutor buildExecutor,
        DiagnosticClassifier classifier,
        DiagnosticCorrelator correlator,
        AcceptanceGate acceptanceGate,
        TestValidationPipeline testPipeline,
        IDomainEventStream events,
        ISemanticEngineResolver? semanticEngine = null,
        TimeSpan? testTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(buildExecutor);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(correlator);
        ArgumentNullException.ThrowIfNull(acceptanceGate);
        ArgumentNullException.ThrowIfNull(testPipeline);
        ArgumentNullException.ThrowIfNull(events);
        if (testTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(testTimeout));
        }

        _buildExecutor = buildExecutor;
        _classifier = classifier;
        _correlator = correlator;
        _acceptanceGate = acceptanceGate;
        _testPipeline = testPipeline;
        _events = events;
        _semanticEngine = semanticEngine;
        _testTimeout = testTimeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Captures semantic-only diagnostics against the pre-mutation source without launching a build.</summary>
    public async Task<BaselineCapture> CaptureSemanticBaselineAsync(
        BuildValidationRequest request,
        MutationSet mutationSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mutationSet);
        var semanticResult = await GetSemanticDiagnosticsAsync(
            request,
            mutationSet,
            SemanticCheckPhase.Baseline,
            "semantic baseline diagnostics",
            baselineCapture: null,
            cancellationToken);
        var diagnostics = semanticResult.Diagnostics;
        return new BaselineCapture(
            request.Baseline.WorkspaceId,
            request.Baseline.CapturedAt,
            DateTimeOffset.UtcNow,
            request.Confidence,
            diagnostics.Select(diagnostic => diagnostic with
            {
                Confidence = request.Confidence,
                IsBaselineDiagnostic = true,
                Classification = request.Confidence == SemanticConfidenceLevel.FullSemantic
                    ? DiagnosticClassification.Baseline
                    : DiagnosticClassification.ConfidenceDegraded,
            }).ToArray());
    }

    /// <summary>Validates one mutation set against its immutable pre-mutation baseline capture.</summary>
    /// <param name="request">Affected-project build request.</param>
    /// <param name="baselineCapture">Pre-mutation build evidence.</param>
    /// <param name="mutationSet">Mutation set being validated.</param>
    /// <param name="requiredApprovalsPresent">Whether required approvals are present.</param>
    /// <param name="finalDiffAvailable">Whether the exact final diff is available.</param>
    /// <param name="residualRisks">Known residual risks retained for acceptance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Structured build, classified diagnostics, and acceptance decision.</returns>
    public async Task<MutationValidationResult> ValidateAsync(
        BuildValidationRequest request,
        BaselineCapture baselineCapture,
        MutationSet mutationSet,
        bool requiredApprovalsPresent,
        bool finalDiffAvailable,
        IReadOnlyList<string>? residualRisks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baselineCapture);
        ArgumentNullException.ThrowIfNull(mutationSet);
        if (request.Baseline.WorkspaceId != baselineCapture.WorkspaceId
            || request.Baseline.CapturedAt != baselineCapture.BaselineCapturedAt
            || mutationSet.WorkspaceId != baselineCapture.WorkspaceId)
        {
            throw new InvalidOperationException(
                "Validation evidence, build request, and mutation set must share one immutable baseline identity.");
        }

        var buildRequired = RequiresBuildValidation(request.Stages);
        var build = buildRequired
            ? await _buildExecutor.ExecuteAsync(request, cancellationToken)
            : new BuildValidationResult(true, [], [], TimeSpan.Zero);
        var semanticResult = buildRequired
            ? new SemanticDiagnosticsResult(build.Diagnostics, Completed: true)
            : await GetSemanticDiagnosticsAsync(
                request,
                mutationSet,
                SemanticCheckPhase.PostMutation,
                "semantic post-mutation diagnostics",
                baselineCapture,
                cancellationToken);
        var currentDiagnostics = semanticResult.Diagnostics;
        var classified = DiagnosticClassifier.Classify(
            baselineCapture,
            currentDiagnostics,
            request.Confidence);
        var correlated = DiagnosticCorrelator.Correlate(classified, mutationSet);
        var semanticOnly = request.Stages.Contains(MutationValidationStage.Semantic)
            && !buildRequired;
        foreach (var diagnostic in correlated.Where(diagnostic => ShouldPublishDiagnostic(diagnostic, semanticOnly)))
        {
            await _events.PublishAsync(
                new DiagnosticObserved(
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic),
                cancellationToken);
        }

        _baselineDiagnostics.Add(correlated.LongCount(diagnostic => diagnostic.IsBaselineDiagnostic));
        _introducedDiagnostics.Add(correlated.LongCount(diagnostic => !diagnostic.IsBaselineDiagnostic));
        var hasClassifiedBuildErrors = correlated.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        var buildStageCompleted = !buildRequired || build.Succeeded || hasClassifiedBuildErrors;
        var testsRequired = request.Stages.Contains(MutationValidationStage.Tests);
        TestValidationResult tests;
        if (build.Succeeded && testsRequired && semanticResult.Completed)
        {
            tests = await _testPipeline.ValidateAsync(
                request,
                mutationSet,
                _testTimeout,
                cancellationToken);
        }
        else if (build.Succeeded && testsRequired)
        {
            tests = new TestValidationResult
            {
                Selection = new TestSelection
                {
                    RelatedMutationIds = mutationSet.Mutations
                        .Select(mutation => mutation.MutationId)
                        .Distinct()
                        .ToArray(),
                    Rationale =
                    [
                        "Test discovery and execution were skipped because required semantic validation did not complete.",
                    ],
                },
                Completed = false,
            };
        }
        else if (build.Succeeded)
        {
            tests = new TestValidationResult
            {
                Selection = new TestSelection
                {
                    RelatedMutationIds = mutationSet.Mutations
                        .Select(mutation => mutation.MutationId)
                        .Distinct()
                        .ToArray(),
                    Rationale =
                    [
                        "Test discovery and execution were skipped because the tests validation stage is not configured.",
                    ],
                },
                Completed = true,
            };
        }
        else
        {
            tests = new TestValidationResult
            {
                Selection = new TestSelection
                {
                    RelatedMutationIds = mutationSet.Mutations
                        .Select(mutation => mutation.MutationId)
                        .Distinct()
                        .ToArray(),
                    Rationale = buildStageCompleted
                        ?
                        [
                            "Test discovery and execution were skipped because classified compiler errors remain.",
                        ]
                        :
                        [
                            "Test discovery and execution were skipped because the affected build failed without classified diagnostics.",
                        ],
                },
                Completed = buildStageCompleted,
            };
        }

        var gate = AcceptanceGate.Evaluate(new AcceptanceGateRequest
        {
            Diagnostics = correlated,
            Tests = tests,
            RequiredStagesCompleted = buildStageCompleted
                && semanticResult.Completed
                && tests.Completed,
            FinalDiffAvailable = finalDiffAvailable,
            RequiredApprovalsPresent = requiredApprovalsPresent,
            ResidualRisks = residualRisks ?? [],
        });
        return new MutationValidationResult(build, correlated, tests, gate);
    }

    private async Task<SemanticDiagnosticsResult> GetSemanticDiagnosticsAsync(
        BuildValidationRequest request,
        MutationSet mutationSet,
        SemanticCheckPhase phase,
        string checkName,
        BaselineCapture? baselineCapture,
        CancellationToken cancellationToken)
    {
        if (!request.Stages.Contains(MutationValidationStage.Semantic))
        {
            return new SemanticDiagnosticsResult([], Completed: true);
        }

        var checkId = SemanticCheckId.New();
        var started = Stopwatch.GetTimestamp();
        await _events.PublishAsync(
            new SemanticCheckStarted(
                request.SessionId,
                DateTimeOffset.UtcNow,
                request.RunId,
                checkId,
                phase,
                checkName),
            CancellationToken.None);

        SemanticDiagnosticsResult result;
        try
        {
            if (_semanticEngine is null)
            {
                result = CreateUnavailableSemanticResult(
                    request,
                    "The semantic validation service is unavailable.");
            }
            else
            {
                string[] projectPaths = [.. request.Projects
                    .Select(project => project.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                var changedFiles = new HashSet<string>(
                    GetMutationPaths(mutationSet),
                    StringComparer.OrdinalIgnoreCase);
                var diagnostics = await _semanticEngine.GetDiagnosticsAsync(
                    request.Baseline.WorkspaceId,
                    projectPaths,
                    changedFiles.ToArray(),
                    cancellationToken);
                result = new SemanticDiagnosticsResult(
                    [.. diagnostics.Where(IsRelevantSemanticDiagnostic)],
                    Completed: true);
            }
        }
        catch (OperationCanceledException)
        {
            await PublishSemanticCheckCompletedAsync(
                request,
                checkId,
                phase,
                checkName,
                SemanticCheckOutcome.Cancelled,
                started,
                "cancelled before semantic diagnostics completed");
            throw;
        }
        catch (Exception exception)
        {
            result = CreateUnavailableSemanticResult(request, exception.Message);
        }

        var outcome = DetermineSemanticCheckOutcome(
            request,
            result,
            phase,
            baselineCapture);
        var blockingDiagnosticCount = CountBlockingSemanticDiagnostics(
            request,
            result,
            phase,
            baselineCapture);
        await PublishSemanticCheckCompletedAsync(
            request,
            checkId,
            phase,
            checkName,
            outcome,
            started,
            FormatSemanticDiagnosticDetail(request.Projects.Count, result, blockingDiagnosticCount));
        return result;
    }

    private static SemanticDiagnosticsResult CreateUnavailableSemanticResult(
        BuildValidationRequest request,
        string message)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new SemanticDiagnosticsResult(
        [
            new Diagnostic
            {
                Id = "semantic-validation-unavailable",
                Code = "SEMANTIC_VALIDATION_UNAVAILABLE",
                Severity = DiagnosticSeverity.Warning,
                Project = string.Empty,
                TargetFramework = string.Empty,
                Message = message,
                Confidence = request.Confidence,
                Classification = DiagnosticClassification.ConfidenceDegraded,
            },
        ],
        Completed: false);
    }

    private async Task PublishSemanticCheckCompletedAsync(
        BuildValidationRequest request,
        SemanticCheckId checkId,
        SemanticCheckPhase phase,
        string checkName,
        SemanticCheckOutcome outcome,
        long started,
        string detail)
    {
        await _events.PublishAsync(
            new SemanticCheckCompleted(
                request.SessionId,
                DateTimeOffset.UtcNow,
                request.RunId,
                checkId,
                phase,
                checkName,
                outcome,
                ToElapsedMilliseconds(Stopwatch.GetElapsedTime(started)),
                detail),
            CancellationToken.None);
    }

    private static SemanticCheckOutcome DetermineSemanticCheckOutcome(
        BuildValidationRequest request,
        SemanticDiagnosticsResult result,
        SemanticCheckPhase phase,
        BaselineCapture? baselineCapture)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Completed)
        {
            return SemanticCheckOutcome.Degraded;
        }

        if (phase != SemanticCheckPhase.PostMutation || baselineCapture is null)
        {
            return SemanticCheckOutcome.Completed;
        }

        var classified = DiagnosticClassifier.Classify(
            baselineCapture,
            result.Diagnostics,
            request.Confidence);
        if (classified.Any(IsIntroducedBlockingSemanticDiagnostic))
        {
            return SemanticCheckOutcome.Failed;
        }

        return classified.Any(IsPossiblyIntroducedSemanticError)
            ? SemanticCheckOutcome.Degraded
            : SemanticCheckOutcome.Completed;
    }

    private static int CountBlockingSemanticDiagnostics(
        BuildValidationRequest request,
        SemanticDiagnosticsResult result,
        SemanticCheckPhase phase,
        BaselineCapture? baselineCapture)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Completed || phase != SemanticCheckPhase.PostMutation || baselineCapture is null)
        {
            return 0;
        }

        var classified = DiagnosticClassifier.Classify(
            baselineCapture,
            result.Diagnostics,
            request.Confidence);
        return classified.Count(IsIntroducedBlockingSemanticDiagnostic);
    }

    private static bool IsIntroducedBlockingSemanticDiagnostic(Diagnostic diagnostic)
    {
        return diagnostic.Severity == DiagnosticSeverity.Error
            && diagnostic.Classification == DiagnosticClassification.Introduced;
    }

    private static bool IsPossiblyIntroducedSemanticError(Diagnostic diagnostic)
    {
        return diagnostic.Severity == DiagnosticSeverity.Error
            && diagnostic.Classification == DiagnosticClassification.ConfidenceDegraded
            && !diagnostic.IsBaselineDiagnostic;
    }

    private static string FormatSemanticDiagnosticDetail(
        int projectCount,
        SemanticDiagnosticsResult result,
        int blockingDiagnosticCount)
    {
        var completion = result.Completed ? "completed" : "incomplete";
        return $"{projectCount} projects, {result.Diagnostics.Count} diagnostics, {blockingDiagnosticCount} blocking, {completion}";
    }

    private static long? ToElapsedMilliseconds(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero || elapsed.TotalMilliseconds > long.MaxValue)
        {
            return null;
        }

        return (long)elapsed.TotalMilliseconds;
    }

    private static IEnumerable<string> GetMutationPaths(MutationSet mutationSet)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        return mutationSet.Mutations
            .SelectMany(mutation => mutation.DestinationRelativePath is null
                ? [mutation.RelativePath]
                : new[] { mutation.RelativePath, mutation.DestinationRelativePath })
            .Select(path => path.Replace('\\', '/'));
    }

    private static bool IsRelevantSemanticDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return diagnostic.Severity == DiagnosticSeverity.Error
            && !string.IsNullOrWhiteSpace(diagnostic.File);
    }

    private static bool ShouldPublishDiagnostic(Diagnostic diagnostic, bool semanticOnly)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!semanticOnly)
        {
            return true;
        }

        return diagnostic.Severity == DiagnosticSeverity.Error
            && !diagnostic.IsBaselineDiagnostic
            && diagnostic.Classification is DiagnosticClassification.Introduced
                or DiagnosticClassification.ConfidenceDegraded;
    }

    private sealed record SemanticDiagnosticsResult(
        IReadOnlyList<Diagnostic> Diagnostics,
        bool Completed);

    private static bool RequiresBuildValidation(IReadOnlyList<MutationValidationStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        return stages.Contains(MutationValidationStage.Compile)
            || stages.Contains(MutationValidationStage.Diagnostics);
    }
}
