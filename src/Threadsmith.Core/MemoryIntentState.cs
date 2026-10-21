namespace Threadsmith.Core;

/// <summary>Run-owned bounded evidence accumulated only after an admitted tool batch completes.</summary>
public sealed class MemoryIntentState
{
    private readonly List<MemoryIntentHint> _hints = [];
    private readonly HashSet<string> _concepts = new(StringComparer.Ordinal);
    private bool _overflowed;
    private bool _mixedOrigins;

    /// <summary>Current instruction revision; batches capture it before execution.</summary>
    public long Epoch { get; private set; }

    /// <summary>Clears eligibility evidence on steering or repository change.</summary>
    public void Advance()
    {
        Epoch++;
        _hints.Clear();
        _concepts.Clear();
        _overflowed = false;
        _mixedOrigins = false;
    }

    /// <summary>Retains admitted provenance and returns whether effective eligibility changed.</summary>
    public bool Observe(long epoch, int round, ToolInvocationId invocationId, bool succeeded, IReadOnlyList<string> concepts)
    {
        ArgumentNullException.ThrowIfNull(concepts);
        if (epoch != Epoch)
        {
            return false;
        }

        var normalized = MemoryConcepts.Normalize(concepts);
        var wasEligible = !_overflowed && !_mixedOrigins && _hints.Count > 0;
        if (_hints.Count >= MemoryConcepts.MaximumPerTurn)
        {
            _overflowed |= normalized.Count > 0;
            return wasEligible && _overflowed;
        }

        var admitted = normalized.Where(concept => _concepts.Contains(concept)
            || (_concepts.Count < MemoryConcepts.MaximumPerTurn && _concepts.Add(concept))).ToArray();
        _overflowed |= admitted.Length != normalized.Count;
        if (admitted.Length == 0)
        {
            return wasEligible && _overflowed;
        }

        _mixedOrigins |= _hints.Count > 0 && !_hints[0].Concepts.SequenceEqual(admitted, StringComparer.Ordinal);
        _hints.Add(new(round, invocationId, succeeded, admitted));
        var isEligible = !_overflowed && !_mixedOrigins;
        return wasEligible != isEligible;
    }

    /// <summary>Detaches immutable evidence at the next assembly boundary.</summary>
    public MemoryIntentSnapshot Capture(string repositoryIdentity, RunId runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        return new(repositoryIdentity, runId, Epoch, _hints.ToArray()) { IsComplete = !_overflowed };
    }
}
