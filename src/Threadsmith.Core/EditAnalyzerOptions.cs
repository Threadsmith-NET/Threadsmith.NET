namespace Threadsmith.Core;

/// <summary>Startup-captured controls for project-provided optional edit analyzers.</summary>
public sealed record EditAnalyzerOptions
{
    /// <summary>Enables project-provided analyzer feedback without extending the edit's immediate observation window.</summary>
    public bool Enabled { get; init; }

    /// <summary>Rule selection for explicit performance experiments only; automatic feedback uses project configuration.</summary>
    public string[] DiagnosticIds { get; init; } = [];

    /// <summary>Deadline for explicit performance experiments only; automatic feedback has no analyzer time budget.</summary>
    public int BudgetMilliseconds { get; init; } = 1000;

    /// <summary>Legacy configuration retained for compatibility; automatic feedback does not impose a first-use deadline.</summary>
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
