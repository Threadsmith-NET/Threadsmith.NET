namespace Threadsmith.Validation;

using System.Diagnostics.Metrics;
using Threadsmith.Core;

/// <summary>Runs selected-test corrections through bounded governed evidence and a hard retry budget.</summary>
public sealed class TestCorrectionLoop
{
    private static readonly Histogram<int> _attempts = ValidationMetrics.Meter.CreateHistogram<int>(
        "threadsmith.validation.test_correction.attempts");

    /// <summary>Corrects selected test failures until validation succeeds or the budget is exhausted.</summary>
    /// <param name="changedCode">Only the relevant changed source fragment.</param>
    /// <param name="contract">Task or code contract that corrective mutations must preserve.</param>
    /// <param name="initialValidation">Initial explained selected-test evidence.</param>
    /// <param name="maximumAttempts">Maximum corrective model attempts.</param>
    /// <param name="attemptCorrectionAsync">Host callback that proposes one correction and reruns selected tests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Final test evidence and exact attempt count.</returns>
    public static async Task<TestCorrectionLoopResult> RunAsync(
        string changedCode,
        string contract,
        TestValidationResult initialValidation,
        int maximumAttempts,
        Func<TestCorrectionContext, CancellationToken, Task<TestCorrectionAttemptResult>> attemptCorrectionAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentNullException.ThrowIfNull(initialValidation);
        ArgumentNullException.ThrowIfNull(attemptCorrectionAsync);
        if (maximumAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        var currentCode = changedCode;
        var validation = initialValidation;
        var attempts = 0;
        while (attempts < maximumAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = validation.Results.FirstOrDefault(result =>
                result.Outcome == TestOutcome.Failed);
            if (failure is null && validation.Completed)
            {
                _attempts.Record(attempts);
                return new TestCorrectionLoopResult(true, attempts, false, validation);
            }

            if (failure is null)
            {
                _attempts.Record(attempts);
                return new TestCorrectionLoopResult(false, attempts, false, validation);
            }

            attempts++;
            var result = await attemptCorrectionAsync(
                new TestCorrectionContext(currentCode, failure, contract, attempts),
                cancellationToken);
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(result.Validation);
            currentCode = result.ChangedCode;
            validation = result.Validation;
        }

        var hasFailures = !validation.Completed
            || validation.Results.Any(result => result.Outcome == TestOutcome.Failed);
        _attempts.Record(attempts);
        return new TestCorrectionLoopResult(!hasFailures, attempts, hasFailures, validation);
    }
}
