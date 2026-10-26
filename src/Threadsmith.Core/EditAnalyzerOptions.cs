namespace Threadsmith.Core;

/// <summary>Startup-captured controls for selected analyzer experiments; automatic execution is not wired yet.</summary>
public sealed record EditAnalyzerOptions
{
    /// <summary>Reserved automatic-feedback switch. This release performs measurement only.</summary>
    public bool Enabled { get; init; }

    /// <summary>Exact rule IDs used to select entire analyzer instances; empty selects none.</summary>
    public string[] DiagnosticIds { get; init; } = ["CA2016", "CA2012", "VSTHRD002", "VSTHRD100"];

    /// <summary>End-to-end observation budget, including queue wait, in milliseconds.</summary>
    public int BudgetMilliseconds { get; init; } = 1000;

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
    }

    /// <summary>Captures independent normalized ID storage.</summary>
    public EditAnalyzerOptions Capture()
    {
        Validate();
        return this with { DiagnosticIds = [.. DiagnosticIds.Select(id => id.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)] };
    }
}
