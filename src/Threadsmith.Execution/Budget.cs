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
            _used.Tokens + delta.Tokens,
            _used.Calls + delta.Calls,
            _used.WallClock + delta.WallClock,
            _used.Cost + delta.Cost);
        bool exhausted = prospective.Tokens > _limit.Tokens
            || prospective.Calls > _limit.Calls
            || prospective.WallClock > _limit.WallClock
            || (_limit.Cost > 0 && prospective.Cost > _limit.Cost);
        return new BudgetStatus(
            exhausted,
            prospective,
            exhausted ? "Execution budget exhausted; pause required." : null);
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
