namespace Threadsmith.Execution;

using Threadsmith.Context;

/// <summary>Request-size tuning for ordinary subagents, not task or response limits.</summary>
public sealed record ChildAgentCompactionOptions
{
    /// <summary>Whether older completed exchanges may be summarized.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Estimated input tokens that trigger an attempt; zero disables this trigger.</summary>
    public int TriggerTokens { get; init; }

    /// <summary>Percentage of the input capacity that triggers an attempt; zero disables this trigger.</summary>
    public int TriggerPercent { get; init; } = 75;

    /// <summary>Advisory total-input target reported in compaction diagnostics; zero leaves it unspecified.</summary>
    public int TargetTokens { get; init; } = 20_000;

    /// <summary>Recent raw context retained independently of the total target; zero keeps only the newest complete exchange.</summary>
    public int RecentTokens { get; init; } = 12_000;

    /// <summary>Minimum rounds between attempts; zero allows an attempt at every boundary.</summary>
    public int MinimumRoundsBetweenAttempts { get; init; } = 3;

    /// <summary>Required estimated savings, including summary and evidence-index overhead; zero requires any reduction.</summary>
    public int MinimumSavingsTokens { get; init; } = 2_000;

    /// <summary>Settings for the shared summary generator. A rejected candidate never stops the child.</summary>
    public ActiveTurnCompactionPolicy Summary { get; init; } = new()
    {
        SummaryBudgetTokens = 3_000,
        MaximumInputTokens = 0,
        MaximumProviderRetries = 0,
        MaximumProviderCalls = 1,
    };

    /// <summary>Validates tuning values before execution.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(TriggerTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(TriggerPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TriggerPercent, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(TargetTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(RecentTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumRoundsBetweenAttempts);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumSavingsTokens);
        ArgumentNullException.ThrowIfNull(Summary);
        Summary.Validate();
    }
}
