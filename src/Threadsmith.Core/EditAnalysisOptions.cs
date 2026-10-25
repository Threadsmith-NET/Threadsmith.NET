namespace Threadsmith.Core;

/// <summary>Startup-captured compiler advisory policy and bounds; these do not suppress compilation or validation.</summary>
public sealed record EditAnalysisOptions
{
    /// <summary>Selected effective warnings; an empty list includes only errors.</summary>
    public string[] CompilerWarningIds { get; init; } = ["CS4014", "CS8602", "CS8604", "CS8618"];

    /// <summary>Exact diagnostic IDs excluded from advisory output regardless of severity.</summary>
    public string[] ExcludedDiagnosticIds { get; init; } = [];

    /// <summary>Maximum retained findings per project and aggregate candidate.</summary>
    public int MaximumRetainedDiagnostics { get; init; } = 64;

    /// <summary>Maximum findings projected in an edit receipt.</summary>
    public int MaximumReportedDiagnostics { get; init; } = 8;

    /// <summary>Maximum cached project diagnostic bases and carried project scope.</summary>
    public int MaximumCachedProjects { get; init; } = 32;

    /// <summary>Maximum distinct omission descriptions.</summary>
    public int MaximumOmissions { get; init; } = 16;

    /// <summary>Maximum diagnostic message UTF-16 units.</summary>
    public int MaximumDiagnosticMessageCharacters { get; init; } = 384;

    /// <summary>Maximum diagnostic path UTF-16 units.</summary>
    public int MaximumDiagnosticPathCharacters { get; init; } = 256;

    /// <summary>Maximum project name UTF-16 units.</summary>
    public int MaximumDiagnosticProjectCharacters { get; init; } = 128;

    /// <summary>Maximum target framework UTF-16 units.</summary>
    public int MaximumDiagnosticFrameworkCharacters { get; init; } = 64;

    /// <summary>Immediate observation allowance; zero returns without waiting.</summary>
    public int ImmediateObservationMilliseconds { get; init; } = 500;

    /// <summary>Maximum wait for pending compiler feedback before finishing a conversation turn; zero disables waiting.</summary>
    public int FinalFeedbackWaitMilliseconds { get; init; } = 30000;

    /// <summary>Maximum candidate lifetime in milliseconds.</summary>
    public int CandidateLifetimeMilliseconds { get; init; } = 30000;

    /// <summary>Checks real API bounds and relationships without restricting the diagnostic ID vocabulary.</summary>
    public void Validate()
    {
        ValidateIds(CompilerWarningIds);
        ValidateIds(ExcludedDiagnosticIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRetainedDiagnostics);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumReportedDiagnostics);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCachedProjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumOmissions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiagnosticMessageCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiagnosticPathCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiagnosticProjectCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiagnosticFrameworkCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(ImmediateObservationMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(FinalFeedbackWaitMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CandidateLifetimeMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumReportedDiagnostics, MaximumRetainedDiagnostics);
    }

    /// <summary>Copies and normalizes configured IDs for immutable startup ownership.</summary>
    public EditAnalysisOptions Capture()
    {
        Validate();
        return this with
        {
            CompilerWarningIds = [.. CompilerWarningIds.Select(id => id.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)],
            ExcludedDiagnosticIds = [.. ExcludedDiagnosticIds.Select(id => id.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }

    private static void ValidateIds(string[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
        }
    }
}
