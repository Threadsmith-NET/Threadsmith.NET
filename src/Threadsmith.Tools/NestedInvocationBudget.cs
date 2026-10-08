namespace Threadsmith.Tools;

/// <summary>Caps host-submitted descendant calls from one root tool operation.</summary>
internal sealed class NestedInvocationBudget
{
    private const int MaximumDescendants = 16;
    private int _reserved;

    /// <summary>Reserves one descendant without lending capacity to sibling operations.</summary>
    public bool TryReserve()
    {
        return Interlocked.Increment(ref _reserved) <= MaximumDescendants;
    }
}
