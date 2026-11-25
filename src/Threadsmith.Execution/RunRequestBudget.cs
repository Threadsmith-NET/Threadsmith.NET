namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Adds in-flight model reservations to admission without treating estimates as actual usage.</summary>
internal sealed class RunRequestBudget : IBudget
{
    private readonly IBudget _actual;
    private readonly Lock _gate = new();
    private readonly Dictionary<ModelRequestBudgetUsage, BudgetDimensions> _reservations = [];

    /// <summary>Initializes a new instance of the <see cref="RunRequestBudget"/> class for a run's actual budget.</summary>
    public RunRequestBudget(IBudget actual)
    {
        _actual = actual ?? throw new ArgumentNullException(nameof(actual));
    }

    /// <inheritdoc />
    public BudgetStatus Check(BudgetDimensions delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ExecutionBudget.ValidateDimensions(delta, nameof(delta));
        lock (_gate)
        {
            return CheckWithReservations(delta);
        }
    }

    /// <inheritdoc />
    public BudgetStatus Accrue(BudgetDimensions delta)
    {
        lock (_gate)
        {
            return _actual.Accrue(delta);
        }
    }

    /// <summary>Checks admission and charges a call while reserving its token and cost exposure.</summary>
    public bool TryReserve(ModelRequestBudgetUsage owner, BudgetDimensions estimate)
    {
        lock (_gate)
        {
            if (CheckWithReservations(estimate).IsExhausted)
            {
                return false;
            }

            _actual.Accrue(new BudgetDimensions(0, estimate.Calls, TimeSpan.Zero));
            _reservations.Add(owner, new BudgetDimensions(estimate.Tokens, 0, TimeSpan.Zero, estimate.Cost));
            return true;
        }
    }

    /// <summary>Replaces the matching reservation with actual incremental usage, up to the remaining hold.</summary>
    public BudgetStatus AccrueReservedUsage(ModelRequestBudgetUsage owner, BudgetDimensions delta)
    {
        lock (_gate)
        {
            var remaining = _reservations[owner];
            _reservations[owner] = remaining with
            {
                Tokens = Math.Max(0, remaining.Tokens - delta.Tokens),
                Cost = Math.Max(0, remaining.Cost - delta.Cost),
            };
            return _actual.Accrue(delta);
        }
    }

    /// <summary>Releases only the unused hold for the settled request.</summary>
    public void Release(ModelRequestBudgetUsage owner)
    {
        lock (_gate)
        {
            _reservations.Remove(owner);
        }
    }

    private BudgetStatus CheckWithReservations(BudgetDimensions delta)
    {
        var tokens = delta.Tokens;
        var cost = delta.Cost;
        foreach (var reservation in _reservations.Values)
        {
            tokens = tokens > long.MaxValue - reservation.Tokens ? long.MaxValue : tokens + reservation.Tokens;
            cost = cost > decimal.MaxValue - reservation.Cost ? decimal.MaxValue : cost + reservation.Cost;
        }

        return _actual.Check(delta with { Tokens = tokens, Cost = cost });
    }
}
