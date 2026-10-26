namespace Threadsmith.Core;

/// <summary>Startup-captured controls for qualified optional edit analyzers.</summary>
public sealed record EditAnalyzerOptions
{
    /// <summary>Enables qualified analyzer feedback without extending the edit's immediate observation window.</summary>
    public bool Enabled { get; init; }

    /// <summary>Exact rule IDs used to select entire analyzer instances; empty selects none.</summary>
    public string[] DiagnosticIds { get; init; } = ["CA2016", "CA2012", "VSTHRD002", "VSTHRD100"];

    /// <summary>Observation budget after advisory admission, including worker queue wait, in milliseconds.</summary>
    public int BudgetMilliseconds { get; init; } = 1000;

    /// <summary>First-use allowance per analyzer identity; the effective allowance is at least the warm budget.</summary>
    public int ColdStartBudgetMilliseconds { get; init; } = 10000;

    /// <summary>Maximum physically running advisory operations, including abandoned work.</summary>
    public int MaximumConcurrentOperations { get; init; } = 1;

    /// <summary>Allows Roslyn internal concurrency during explicit experiments; outer workers normally own admission.</summary>
    public bool ConcurrentAnalysis { get; init; }

    /// <summary>Validates API bounds and exact ID values.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(DiagnosticIds);
        foreach (var id in DiagnosticIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BudgetMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ColdStartBudgetMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumConcurrentOperations);
    }

    /// <summary>Captures independent normalized ID storage.</summary>
    public EditAnalyzerOptions Capture()
    {
        Validate();
        return this with { DiagnosticIds = [.. DiagnosticIds.Select(id => id.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)] };
    }
}
