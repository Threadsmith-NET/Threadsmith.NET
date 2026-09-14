namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Charges one request's cumulative provider reports without charging repeated snapshots again.</summary>
internal sealed class ModelRequestBudgetUsage
{
    private long _chargedTokens;
    private decimal _chargedCost;

    /// <summary>Gets whether this request was admitted for provider execution.</summary>
    public bool HasStarted { get; private set; }

    /// <summary>Charges the request once even if the provider never reports token usage.</summary>
    public void Start(IBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (HasStarted)
        {
            return;
        }

        var delta = new BudgetDimensions(0, 1, TimeSpan.Zero);
        var status = budget.Check(delta);
        if (status.IsExhausted)
        {
            throw new BudgetExceededException(status.Reason ?? "Execution budget exhausted.");
        }

        budget.Accrue(delta);
        HasStarted = true;
    }

    /// <summary>Accrues only newly reported usage; operational budgets do not refund earlier estimates.</summary>
    public BudgetStatus Accrue(IBudget budget, ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.InputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.OutputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.EstimatedCost);
        var tokens = usage.InputTokens > long.MaxValue - usage.OutputTokens
            ? long.MaxValue : usage.InputTokens + usage.OutputTokens;
        var delta = new BudgetDimensions(
            Math.Max(0, tokens - _chargedTokens),
            0,
            TimeSpan.Zero,
            Math.Max(0, usage.EstimatedCost - _chargedCost));
        _chargedTokens = Math.Max(_chargedTokens, tokens);
        _chargedCost = Math.Max(_chargedCost, usage.EstimatedCost);
        return budget.Accrue(delta);
    }
}
