namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Thread-safe execution budget.</summary>
public sealed class ExecutionBudget : IBudget
{
    private readonly Lock _gate = new();
    private readonly BudgetDimensions _limit;
    private BudgetDimensions _used = new(0, 0, TimeSpan.Zero);

    /// <summary>Initializes a new instance of the <see cref="ExecutionBudget"/> class.</summary>
    public ExecutionBudget(BudgetDimensions limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        ValidateDimensions(limit, nameof(limit));
        _limit = limit;
    }

    /// <summary>Creates an unused budget with the same configured limits.</summary>
    public ExecutionBudget CreateScope()
    {
        return new(_limit);
    }

    /// <inheritdoc />
    public BudgetStatus Check(BudgetDimensions delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ValidateDimensions(delta, nameof(delta));
        lock (_gate)
        {
            return CalculateStatus(delta);
        }
    }

    /// <inheritdoc />
    public BudgetStatus Accrue(BudgetDimensions delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ValidateDimensions(delta, nameof(delta));
        lock (_gate)
        {
            var status = CalculateStatus(delta);
            _used = status.Used;
            return status;
        }
    }

    /// <summary>Rejects negative budget dimensions.</summary>
    internal static void ValidateDimensions(BudgetDimensions value, string parameterName)
    {
        if (value.Tokens < 0
            || value.Calls < 0
            || value.WallClock < TimeSpan.Zero
            || value.Cost < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Budget dimensions cannot be negative.");
        }
    }

    private BudgetStatus CalculateStatus(BudgetDimensions delta)
    {
        var prospective = new BudgetDimensions(
            SaturatingAdd(_used.Tokens, delta.Tokens),
            SaturatingAdd(_used.Calls, delta.Calls),
            TimeSpan.FromTicks(SaturatingAdd(_used.WallClock.Ticks, delta.WallClock.Ticks)),
            SaturatingAdd(_used.Cost, delta.Cost));
        var exhaustedDimensions = BudgetExhaustionDimension.None;
        if (WouldExceed(_used.Tokens, delta.Tokens, _limit.Tokens))
        {
            exhaustedDimensions |= BudgetExhaustionDimension.Tokens;
        }

        if (WouldExceed(_used.Calls, delta.Calls, _limit.Calls))
        {
            exhaustedDimensions |= BudgetExhaustionDimension.Calls;
        }

        if (WouldExceed(_used.WallClock.Ticks, delta.WallClock.Ticks, _limit.WallClock.Ticks))
        {
            exhaustedDimensions |= BudgetExhaustionDimension.WallClock;
        }

        if (_limit.Cost > 0 && WouldExceed(_used.Cost, delta.Cost, _limit.Cost))
        {
            exhaustedDimensions |= BudgetExhaustionDimension.Cost;
        }

        var exhausted = exhaustedDimensions != BudgetExhaustionDimension.None;
        return new BudgetStatus(
            exhausted,
            prospective,
            exhausted ? $"Execution budget exhausted ({exhaustedDimensions}); pause required." : null)
        {
            ExhaustedDimensions = exhaustedDimensions,
        };
    }

    private static bool WouldExceed(long used, long delta, long limit)
    {
        return used > limit || delta > limit - used;
    }

    private static bool WouldExceed(int used, int delta, int limit)
    {
        return used > limit || delta > limit - used;
    }

    private static bool WouldExceed(decimal used, decimal delta, decimal limit)
    {
        return used > limit || delta > limit - used;
    }

    private static long SaturatingAdd(long first, long second)
    {
        return first > long.MaxValue - second ? long.MaxValue : first + second;
    }

    private static int SaturatingAdd(int first, int second)
    {
        return first > int.MaxValue - second ? int.MaxValue : first + second;
    }

    private static decimal SaturatingAdd(decimal first, decimal second)
    {
        return first > decimal.MaxValue - second ? decimal.MaxValue : first + second;
    }
}

/// <summary>A non-cumulative budget for operations governed by other explicit bounds.</summary>
public sealed class UnboundedBudget : IBudget
{
    private UnboundedBudget()
    {
    }

    /// <summary>Gets the shared unbounded budget.</summary>
    public static UnboundedBudget Instance { get; } = new();

    /// <inheritdoc />
    public BudgetStatus Check(BudgetDimensions delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ExecutionBudget.ValidateDimensions(delta, nameof(delta));
        return new BudgetStatus(false, delta, null);
    }

    /// <inheritdoc />
    public BudgetStatus Accrue(BudgetDimensions delta)
    {
        return Check(delta);
    }
}
