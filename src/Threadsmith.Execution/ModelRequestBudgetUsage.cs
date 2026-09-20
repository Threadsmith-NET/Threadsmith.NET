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
    public void Start(IBudget budget, ModelStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(request);
        if (HasStarted)
        {
            return;
        }

        var estimatedInputTokens = EstimateInputTokens(request);
        var admission = new BudgetDimensions(estimatedInputTokens, 1, TimeSpan.Zero);
        var status = budget.Check(admission);
        if (status.IsExhausted)
        {
            throw new BudgetExceededException(
                $"Execution budget cannot admit a model request estimated to require {estimatedInputTokens} input tokens.");
        }

        budget.Accrue(new BudgetDimensions(0, 1, TimeSpan.Zero));
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

    private static int EstimateInputTokens(ModelStreamRequest request)
    {
        if (request.WireEstimate is { } prepared)
        {
            return prepared.WireInputTokens;
        }

        IReadOnlyList<ModelMessage> messages = request.Messages.Count > 0
            ? request.Messages
            :
            [
                new ModelMessage
                {
                    Role = ModelMessageRole.User,
                    SectionId = "current-user",
                    Content = [new ModelContentPart { Content = request.Input }],
                },
            ];
        var stablePrefixMessageCount = Math.Min(
            request.Layout?.StablePrefixMessageCount ?? 0,
            messages.Count);
        return ModelWireEstimator.Estimate(
            messages,
            request.Tools,
            request.ToolTransportMode,
            stablePrefixMessageCount,
            request.MaximumOutputTokens ?? 0,
            request.ProviderInstructions).WireInputTokens;
    }
}
