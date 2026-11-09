namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Retains terminal accounting and one deferred publication attempt for a live invocation.</summary>
internal sealed class ToolSuspensionCompletion
{
    private readonly Lock _gate = new();
    private readonly IToolTelemetryProducer? _producer;
    private readonly TimeProvider _timeProvider;
    private Task<ToolInvocationResult>? _completion;
    private int _recorded;

    /// <summary>Initializes a new instance of the <see cref="ToolSuspensionCompletion"/> class.</summary>
    internal ToolSuspensionCompletion(IToolTelemetryProducer? producer, RepositoryStorageBinding? binding, string toolId, TimeProvider timeProvider)
    {
        _producer = producer;
        Binding = binding;
        _timeProvider = timeProvider;
        ToolId = toolId;
    }

    /// <summary>Original ownership retained for deferred completion and its hooks.</summary>
    internal RepositoryStorageBinding? Binding { get; }

    /// <summary>Canonical identity resolved before terminal selection.</summary>
    internal string ToolId { get; set; }

    /// <summary>Execution measurement retained across the suspended user wait.</summary>
    internal long? SuspendedElapsedMilliseconds { get; set; }

    /// <summary>Attempts accounting once for the selected terminal outcome.</summary>
    internal void Record(ToolTelemetryStatus status, long? elapsedMilliseconds)
    {
        if (Interlocked.Exchange(ref _recorded, 1) != 0 || _producer is null || Binding is null)
        {
            return;
        }

        try
        {
            _ = _producer.TryRecord(new ToolCompletionEntry(
                Binding,
                ToolId,
                status,
                _timeProvider.GetUtcNow(),
                elapsedMilliseconds is >= 0 ? elapsedMilliseconds : null));
        }
        catch (Exception)
        {
            // Producer owns degraded-state reporting. Never retry uncertain admission or
            // log on the tool path; accounting cannot change execution behavior.
            return;
        }
    }

    /// <summary>Shares the original terminal attempt with concurrent callers and cleanup retries.</summary>
    internal Task<ToolInvocationResult> CompleteAsync(Func<Task<ToolInvocationResult>> complete)
    {
        lock (_gate)
        {
            return _completion ??= complete();
        }
    }
}
