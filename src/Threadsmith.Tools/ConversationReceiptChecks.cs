namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Bounded observations owned by one conversation run, captured before display projection.</summary>
internal sealed class ConversationReceiptChecks
{
    private readonly Lock _gate = new();
    private readonly List<ConversationCheckObservation> _checks = [];
    private int _total;

    /// <summary>Whether a consequential check was attempted.</summary>
    internal bool HasObservations
    {
        get
        {
            lock (_gate)
            {
                return _total > 0;
            }
        }
    }

    /// <summary>Copies at most eight observations.</summary>
    internal (ConversationCheckObservation[] Checks, int Total) Snapshot(WorkspaceBaseline? finalBaseline)
    {
        lock (_gate)
        {
            return ([.. _checks.Select(check => check with { Applicability = check.Applicability == "Unknown" && check.Baseline is not null && finalBaseline is not null && check.Baseline != finalBaseline.CapturedAt ? "EarlierBaseline" : check.Applicability })], _total);
        }
    }

    /// <summary>Captures typed completion, cancellation, and infrastructure failure at the native adapter.</summary>
    internal static async Task<T> ObserveAsync<T>(ToolExecutionContext context, string toolId, string target, string arguments, Func<CancellationToken, Task<T>> execute, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var baseline = context.Invocation.ReceiptBaseline?.Invoke();
        T result;
        try
        {
            result = await execute(cancellationToken);
        }
        catch (Exception exception)
        {
            var timedOut = exception is OperationCanceledException && context.CallerCancellation is { IsCancellationRequested: false };
            Record(toolId + ":" + (timedOut ? "TimedOut" : exception is OperationCanceledException ? "Cancelled" : "Incomplete"), false, null, timedOut, false);
            throw;
        }

        switch (result)
        {
            case ValidationToolResult validation:
                target = validation.EffectiveScope;
                arguments = string.Join(" ", validation.EffectiveArguments);
                Record(validation.Kind + ":" + (validation.Succeeded ? "Succeeded" : "Failed"), validation.Succeeded, null, validation.TimedOut, validation.IsTruncated, validation.Authority, exitCode: validation.ExitCode);
                break;
            case TargetedTestResult test:
                target = test.Test.ProjectPath;
                Record("TargetedTest:" + (test.Outcome == TestOutcome.Passed && test.Passed + test.Failed + test.Skipped == 0 ? "NoCountsRecorded" : test.Outcome.ToString()), test.Outcome == TestOutcome.Passed && test.Passed > 0 && !test.TimedOut, test.Passed + test.Failed + test.Skipped > 0 ? $"passed={test.Passed},failed={test.Failed},skipped={test.Skipped}" : null, test.TimedOut, test.IsTruncated, test.Authority, test.EffectiveFilter);
                break;
        }

        return result;

        void Record(string outcome, bool succeeded, string? counts, bool timedOut, bool truncated, ValidationAuthority authority = ValidationAuthority.Exploratory, string? filter = null, int? exitCode = null)
        {
            if (context.Invocation.IsChildAgent || context.Invocation.ReceiptChecks is not { } owner)
            {
                return;
            }

            // Oversized identifying values are unknown, never a clipped selector claiming a different scope.
            static string Bound(string value) => value.Length <= 256 ? value : "[omitted: overlong identity]";
            var observation = new ConversationCheckObservation(context.ToolInvocationId.Value.ToString(), Bound(context.Invocation.RepositoryPath), context.Invocation.WorkspaceId?.Value.ToString(), started, Bound(target), Bound(arguments), filter is null ? null : Bound(filter), outcome, authority.ToString(), OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Unknown", counts, timedOut, truncated, succeeded)
            {
                Baseline = baseline?.CapturedAt,
                Revision = baseline?.GitRevision,
                ExitCode = exitCode,
                SourceProof = baseline is null ? "Unknown" : "RecordedBaseline; external changes and built inputs unverified",
                Applicability = baseline is not null && context.Invocation.ReceiptBaseline?.Invoke()?.CapturedAt != baseline.CapturedAt ? "Indeterminate: baseline changed during check" : "Unknown",
            };
            lock (owner._gate)
            {
                if (owner._checks.Any(check => check.Invocation == observation.Invocation))
                {
                    return;
                }

                owner._total++;
                if (owner._checks.Count == 8)
                {
                    var removable = owner._checks.FindIndex(check => check.Succeeded);
                    if (removable < 0)
                    {
                        return;
                    }

                    owner._checks.RemoveAt(removable);
                }

                owner._checks.Add(observation);
            }
        }
    }
}

/// <summary>Immutable historical check; workspace identity does not prove source or built-input equivalence.</summary>
internal sealed record ConversationCheckObservation(string Invocation, string Repository, string? Workspace, DateTimeOffset Started, string Target, string Arguments, string? Filter, string Result, string Authority, string Platform, string? Counts, bool TimedOut, bool OutputTruncated, bool Succeeded)
{
    /// <summary>Existing baseline identity at dispatch, without scanning its files.</summary>
    public DateTimeOffset? Baseline { get; init; }

    /// <summary>Revision observed by the baseline owner, not a proof of working tree equivalence.</summary>
    public string? Revision { get; init; }

    /// <summary>Typed process exit when available.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Limits of source identity captured at dispatch.</summary>
    public string SourceProof { get; init; } = "Unknown: external changes and built inputs unverified";

    /// <summary>Historical check applicability to final code.</summary>
    public string Applicability { get; init; } = "Unknown";
}
