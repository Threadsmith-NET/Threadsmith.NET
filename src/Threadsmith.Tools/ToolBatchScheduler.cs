namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Failure behavior for a concurrently executing sibling tool batch.</summary>
public enum ToolBatchFailureMode
{
    /// <summary>Allow every admitted sibling to reach its own terminal outcome.</summary>
    CompleteStarted,

    /// <summary>Cancel remaining admitted siblings after the first failure.</summary>
    CancelBatchOnFailure,
}

/// <summary>Bounded host configuration for sibling tool execution.</summary>
public sealed record ToolParallelOptions
{
    /// <summary>Default bounded scheduler configuration.</summary>
    public static ToolParallelOptions Default { get; } = new();

    /// <summary>Whether independent sibling calls may overlap.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum calls admitted to one wave.</summary>
    public int MaximumConcurrency { get; init; } = 4;

    /// <summary>Batch failure behavior.</summary>
    public ToolBatchFailureMode FailureMode { get; init; } = ToolBatchFailureMode.CompleteStarted;
}

/// <summary>One model-ordered request in a sibling tool batch.</summary>
/// <param name="Ordinal">Original sibling ordinal.</param>
/// <param name="CorrelationId">Provider correlation identifier.</param>
/// <param name="Invocation">Host invocation request.</param>
public sealed record ToolBatchRequest(int Ordinal, string CorrelationId, ToolInvocationRequest Invocation);

/// <summary>One model-ordered terminal result in a sibling tool batch.</summary>
/// <param name="Ordinal">Original sibling ordinal.</param>
/// <param name="CorrelationId">Provider correlation identifier.</param>
/// <param name="Result">Terminal invocation result.</param>
public sealed record ToolBatchResult(int Ordinal, string CorrelationId, ToolInvocationResult Result);

/// <summary>Opaque prepared snapshot produced by preflight and consumed by batch invocation.</summary>
public sealed class ToolBatchPreparation
{
    /// <summary>Empty prepared snapshot.</summary>
    public static ToolBatchPreparation Empty { get; } = new(Array.Empty<IReadOnlyList<PlannedToolInvocation>>());

    /// <summary>Initializes a new instance of the <see cref="ToolBatchPreparation"/> class.</summary>
    internal ToolBatchPreparation(IReadOnlyList<IReadOnlyList<PlannedToolInvocation>> waves)
    {
        ArgumentNullException.ThrowIfNull(waves);
        Waves = waves;
        Requests = waves
            .SelectMany(static wave => wave)
            .Select(static planned => planned.Request)
            .OrderBy(static request => request.Ordinal)
            .ToArray();
    }

    /// <summary>Gets the original model-ordered requests represented by this preparation.</summary>
    internal IReadOnlyList<ToolBatchRequest> Requests { get; }

    /// <summary>Gets the prepared conflict-free waves.</summary>
    internal IReadOnlyList<IReadOnlyList<PlannedToolInvocation>> Waves { get; }
}

/// <summary>No-side-effect validation result for a complete sibling tool batch.</summary>
public sealed record ToolBatchPreflightResult
{
    /// <summary>Successful preflight result.</summary>
    public static ToolBatchPreflightResult Success { get; } = new()
    {
        Succeeded = true,
        Preparation = ToolBatchPreparation.Empty,
    };

    /// <summary>Whether the entire batch can enter the invocation pipeline.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Original model ordinal of the first failed sibling when known.</summary>
    public int? FailedOrdinal { get; init; }

    /// <summary>Tool id of the first failed sibling when known.</summary>
    public string? FailedToolId { get; init; }

    /// <summary>Normalized error classification for the preflight failure.</summary>
    public ToolErrorClassification ErrorClassification { get; init; }

    /// <summary>Sanitized, bounded reason that excludes raw arguments and secrets.</summary>
    public string? SafeReason { get; init; }

    /// <summary>Prepared registration snapshot to invoke when preflight succeeds.</summary>
    public ToolBatchPreparation? Preparation { get; init; }
}

/// <summary>One validated invocation and its immutable scheduling snapshot.</summary>
/// <param name="Request">Original request.</param>
/// <param name="Registration">Generation-fenced registration, or <see langword="null" /> when preparation failed.</param>
/// <param name="Claims">Normalized host claims.</param>
/// <param name="PreparationError">Ordinary validation failure captured while preparing the invocation.</param>
internal sealed record PlannedToolInvocation(
    ToolBatchRequest Request,
    ToolRegistration? Registration,
    IReadOnlyList<ToolResourceClaim> Claims,
    string? PreparationError = null);

/// <summary>Enforces registration-declared source caps across every caller of one pipeline.</summary>
internal sealed class ToolSourceConcurrencyLimiter
{
    private readonly Dictionary<ToolActivitySource, SourceState> _states = [];
    private readonly Lock _sync = new();

    /// <summary>Waits until the source can admit an invocation without broadening any active cap.</summary>
    public async ValueTask<IDisposable> AcquireAsync(
        ToolActivitySource source,
        int maximumConcurrency,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_sync)
            {
                if (!_states.TryGetValue(source, out var state))
                {
                    state = new SourceState();
                    _states.Add(source, state);
                }

                var effectiveMaximum = state.ActiveCaps.Count == 0
                    ? maximumConcurrency
                    : Math.Min(maximumConcurrency, state.ActiveCaps.Min());
                if (state.ActiveCaps.Count < effectiveMaximum)
                {
                    state.ActiveCaps.Add(maximumConcurrency);
                    return new Lease(this, source, maximumConcurrency);
                }

                waitTask = state.Changed.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    private void Release(ToolActivitySource source, int maximumConcurrency)
    {
        lock (_sync)
        {
            var state = _states[source];
            _ = state.ActiveCaps.Remove(maximumConcurrency);
            var changed = state.Changed;
            state.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (state.ActiveCaps.Count == 0)
            {
                _states.Remove(source);
            }

            changed.TrySetResult();
        }
    }

    private sealed class SourceState
    {
        public List<int> ActiveCaps { get; } = [];

        public TaskCompletionSource Changed { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease : IDisposable
    {
        private readonly int _maximumConcurrency;
        private readonly ToolSourceConcurrencyLimiter _owner;
        private readonly ToolActivitySource _source;
        private int _released;

        public Lease(ToolSourceConcurrencyLimiter owner, ToolActivitySource source, int maximumConcurrency)
        {
            _owner = owner;
            _source = source;
            _maximumConcurrency = maximumConcurrency;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release(_source, _maximumConcurrency);
            }
        }
    }
}

/// <summary>Builds deterministic conflict-free waves from trusted registration metadata and validated inputs.</summary>
internal sealed class ToolConflictPlanner
{
    private readonly IToolRegistry _registry;
    private readonly ToolParallelOptions _options;

    /// <summary>Initializes a new instance of the <see cref="ToolConflictPlanner"/> class.</summary>
    public ToolConflictPlanner(IToolRegistry registry, ToolParallelOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumConcurrency is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Tool concurrency must be between 1 and 16.");
        }

        _registry = registry;
        _options = options;
    }

    /// <summary>Places requests into the earliest deterministic conflict-free wave.</summary>
    public IReadOnlyList<IReadOnlyList<PlannedToolInvocation>> Plan(IEnumerable<ToolBatchRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var waves = new List<List<PlannedToolInvocation>>();
        foreach (var request in requests.OrderBy(item => item.Ordinal))
        {
            var planned = Prepare(request);
            var earliestWave = FindEarliestAllowedWave(waves, planned);
            var selected = _options.Enabled
                ? waves
                    .Skip(earliestWave)
                    .FirstOrDefault(wave => Admits(wave, planned))
                : null;
            if (selected is null)
            {
                selected = [];
                waves.Add(selected);
            }

            selected.Add(planned);
        }

        return waves;
    }

    private PlannedToolInvocation Prepare(ToolBatchRequest request)
    {
        try
        {
            var registration = _registry.GetRegistration(request.Invocation.ToolId);
            var input = registration.Tool.DeserializeInput(request.Invocation.ArgumentsJson);
            IReadOnlyList<ToolResourceClaim> claims = registration.Tool
                .GetSchedulingClaims(input, request.Invocation.Context)
                .OrderBy(claim => claim.ResourceKind)
                .ThenBy(claim => claim.CanonicalIdentity, StringComparer.Ordinal)
                .ToArray();
            return new PlannedToolInvocation(request, registration, claims);
        }
        catch (Exception exception)
        {
            return new PlannedToolInvocation(request, null, [], exception.Message);
        }
    }

    private static int FindEarliestAllowedWave(
        IReadOnlyList<List<PlannedToolInvocation>> waves,
        PlannedToolInvocation planned)
    {
        var earliestWave = 0;
        for (var waveIndex = 0; waveIndex < waves.Count; waveIndex++)
        {
            if (waves[waveIndex].Any(existing => Conflicts(existing, planned)))
            {
                earliestWave = waveIndex + 1;
            }
        }

        return earliestWave;
    }

    private bool Admits(
        IReadOnlyList<PlannedToolInvocation> wave,
        PlannedToolInvocation planned)
    {
        if (wave.Count >= _options.MaximumConcurrency
            || wave.Any(existing => Conflicts(existing, planned))
            || planned.Registration is null)
        {
            return false;
        }

        var source = planned.Registration.Source;
        PlannedToolInvocation[] sameSource = [.. wave.Where(existing => existing.Registration?.Source == source)];
        var sourceCount = sameSource.Length + 1;
        return sourceCount <= planned.Registration.Tool.Definition.Scheduling.MaximumSourceConcurrency
            && sameSource.All(existing => existing.Registration is { } registration
                && sourceCount <= registration.Tool.Definition.Scheduling.MaximumSourceConcurrency);
    }

    private static bool Conflicts(PlannedToolInvocation left, PlannedToolInvocation right)
    {
        if (left.Registration is null || right.Registration is null)
        {
            return true;
        }

        var leftDescriptor = left.Registration.Tool.Definition.Scheduling;
        var rightDescriptor = right.Registration.Tool.Definition.Scheduling;
        if (leftDescriptor.SchemaVersion != ToolSchedulingDescriptor.CurrentSchemaVersion
            || rightDescriptor.SchemaVersion != ToolSchedulingDescriptor.CurrentSchemaVersion
            || leftDescriptor.MaximumSourceConcurrency <= 1
            || rightDescriptor.MaximumSourceConcurrency <= 1
            || leftDescriptor.ConcurrencyMode != ToolConcurrencyMode.ParallelSafe
            || rightDescriptor.ConcurrencyMode != ToolConcurrencyMode.ParallelSafe
            || left.Registration.Source.Kind != ToolActivitySourceKind.BuiltIn
            || right.Registration.Source.Kind != ToolActivitySourceKind.BuiltIn
            || left.Registration.Tool.Definition.RequiredApproval != ApprovalLevel.None
            || right.Registration.Tool.Definition.RequiredApproval != ApprovalLevel.None)
        {
            return true;
        }

        return left.Claims.Any(leftClaim => right.Claims.Any(rightClaim => ClaimsConflict(leftClaim, rightClaim)));
    }

    private static bool ClaimsConflict(ToolResourceClaim left, ToolResourceClaim right)
    {
        if (left.ResourceKind != right.ResourceKind || !IdentitiesOverlap(left, right))
        {
            return false;
        }

        return left.AccessMode != ToolAccessMode.Read || right.AccessMode != ToolAccessMode.Read;
    }

    private static bool IdentitiesOverlap(ToolResourceClaim left, ToolResourceClaim right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(left.CanonicalIdentity, right.CanonicalIdentity, comparison))
        {
            return true;
        }

        if (left.ResourceKind != ToolResourceKind.Path)
        {
            return false;
        }

        var leftPath = Path.TrimEndingDirectorySeparator(left.CanonicalIdentity);
        var rightPath = Path.TrimEndingDirectorySeparator(right.CanonicalIdentity);
        return leftPath.StartsWith(string.Concat(rightPath, Path.DirectorySeparatorChar), comparison)
            || rightPath.StartsWith(string.Concat(leftPath, Path.DirectorySeparatorChar), comparison);
    }
}
