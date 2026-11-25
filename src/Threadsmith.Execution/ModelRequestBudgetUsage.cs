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

    /// <summary>Admits one estimated provider request and charges its call once.</summary>
    public void Start(RunRequestBudget budget, ModelStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(request);
        if (HasStarted)
        {
            return;
        }

        var estimate = ModelRequestAdmissionEstimator.Estimate(request);
        if (!budget.TryReserve(this, ModelRequestAdmissionEstimator.ToBudgetDimensions(estimate)))
        {
            throw CreateAdmissionFailure(estimate);
        }

        HasStarted = true;
    }

    /// <summary>Checks conservative request headroom without charging a rejected or hook-blocked call.</summary>
    public static void CheckAdmission(IBudget budget, ModelStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(request);
        var estimate = ModelRequestAdmissionEstimator.Estimate(request);
        var status = budget.Check(ModelRequestAdmissionEstimator.ToBudgetDimensions(estimate));
        if (status.IsExhausted)
        {
            throw CreateAdmissionFailure(estimate);
        }
    }

    /// <summary>Releases unused exposure after the provider no longer reports usage.</summary>
    public void Finish(RunRequestBudget budget) => budget.Release(this);

    /// <summary>Replaces reserved exposure with newly reported actual usage.</summary>
    public BudgetStatus Accrue(RunRequestBudget budget, ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.InputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.OutputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.EstimatedCost);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.ReportedCost ?? 0);
        var tokens = ModelRequestAdmissionEstimator.SaturatingTokenTotal(
            usage.InputTokens,
            usage.OutputTokens);
        var delta = new BudgetDimensions(
            Math.Max(0, tokens - _chargedTokens),
            0,
            TimeSpan.Zero,
            Math.Max(0, usage.BudgetCost - _chargedCost));
        _chargedTokens = Math.Max(_chargedTokens, tokens);
        _chargedCost = Math.Max(_chargedCost, usage.BudgetCost);
        return budget.AccrueReservedUsage(this, delta);
    }

    private static BudgetExceededException CreateAdmissionFailure(ModelRequestAdmissionEstimate estimate) => new(
        "Execution budget cannot admit a model request estimated to require "
        + $"{estimate.InputTokens} input tokens, {estimate.OutputTokens} output tokens, "
        + $"and {estimate.Calls} call.");
}
