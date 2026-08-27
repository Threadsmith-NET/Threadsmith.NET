namespace Threadsmith.Validation;

using System.Diagnostics.Metrics;
using Threadsmith.Core;

/// <summary>Runs compilation corrections through a minimal governed context and a hard retry budget.</summary>
[Obsolete("Compatibility helper only; production correction is coordinated by Threadsmith.Execution.")]
public sealed class CorrectionLoop
{
    private static readonly Histogram<int> _attempts = ValidationMetrics.Meter.CreateHistogram<int>(
        "threadsmith.validation.correction.attempts");

    /// <summary>Corrects introduced errors until validation succeeds or the configured budget is exhausted.</summary>
    /// <param name="changedCode">Only the relevant changed source fragment.</param>
    /// <param name="contract">Task or code contract that corrective mutations must preserve.</param>
    /// <param name="initialDiagnostics">Initial classified diagnostics.</param>
    /// <param name="maximumAttempts">Maximum corrective model attempts.</param>
    /// <param name="attemptCorrectionAsync">Host callback that proposes one correction and recompiles it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Final validation evidence and exact attempt count.</returns>
    public static async Task<CorrectionLoopResult> RunAsync(
        string changedCode,
        string contract,
        IReadOnlyList<Diagnostic> initialDiagnostics,
        int maximumAttempts,
        Func<CorrectionContext, CancellationToken, Task<CorrectionAttemptResult>> attemptCorrectionAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentNullException.ThrowIfNull(initialDiagnostics);
        ArgumentNullException.ThrowIfNull(attemptCorrectionAsync);
        if (maximumAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        var currentCode = changedCode;
        var diagnostics = initialDiagnostics;
        var attempts = 0;
        while (attempts < maximumAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var introducedError = diagnostics.FirstOrDefault(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error
                && (diagnostic.Classification == DiagnosticClassification.Introduced
                    || (diagnostic.Classification == DiagnosticClassification.ConfidenceDegraded
                        && !diagnostic.IsBaselineDiagnostic)));
            if (introducedError is null)
            {
                _attempts.Record(attempts);
                return new CorrectionLoopResult(true, attempts, false, diagnostics);
            }

            attempts++;
            var result = await attemptCorrectionAsync(
                new CorrectionContext(currentCode, introducedError, contract, attempts),
                cancellationToken);
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(result.Diagnostics);
            currentCode = result.ChangedCode;
            diagnostics = result.Diagnostics;
        }

        var hasIntroducedErrors = diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error
            && (diagnostic.Classification == DiagnosticClassification.Introduced
                || (diagnostic.Classification == DiagnosticClassification.ConfidenceDegraded
                    && !diagnostic.IsBaselineDiagnostic)));
        _attempts.Record(attempts);
        return new CorrectionLoopResult(!hasIntroducedErrors, attempts, hasIntroducedErrors, diagnostics);
    }
}

/// <summary>Shared validation metric instruments.</summary>
internal static class ValidationMetrics
{
    /// <summary>Validation subsystem meter.</summary>
    public static readonly Meter Meter = new("Threadsmith.Validation");
}
