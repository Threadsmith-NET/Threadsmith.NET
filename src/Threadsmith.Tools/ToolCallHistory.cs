namespace Threadsmith.Tools;

/// <summary>Tracks identical model tool calls across an execution and within one pending response batch.</summary>
public sealed class ToolCallHistory
{
    private readonly HashSet<(string ToolName, string ArgumentsJson)>? _currentBatchCalls;
    private readonly HashSet<(string ToolName, string ArgumentsJson)> _calls;

    /// <summary>Initializes a new instance of the <see cref="ToolCallHistory"/> class with an empty execution history.</summary>
    public ToolCallHistory()
    {
        _calls = [];
    }

    /// <summary>Initializes a new instance of the <see cref="ToolCallHistory"/> class for batch validation from accepted calls.</summary>
    public ToolCallHistory(ToolCallHistory accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        _calls = [.. accepted._calls];
        _currentBatchCalls = [];
    }

    /// <summary>Records a call, returning false if its name and arguments have already been recorded.</summary>
    /// <remarks>Callers retain their existing argument normalization policy.</remarks>
    public bool TryAdd(string toolName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(argumentsJson);
        var call = (toolName.ToUpperInvariant(), argumentsJson);
        return TrackCurrentBatch(call) && _calls.Add(call);
    }

    /// <summary>Applies host-owned reuse metadata while always rejecting identical siblings in the current batch.</summary>
    public bool TryAdd(ToolDefinition definition, string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(argumentsJson);
        var call = (definition.Id.ToUpperInvariant(), argumentsJson);
        if (!TrackCurrentBatch(call))
        {
            return false;
        }

        var added = _calls.Add(call);
        return added || definition.AllowDuplicateInvocations;
    }

    /// <summary>Reports whether a tool has an accepted call in this execution's history.</summary>
    public bool ContainsTool(string toolName) => _calls.Any(call => string.Equals(call.ToolName, toolName, StringComparison.OrdinalIgnoreCase));

    private bool TrackCurrentBatch((string ToolName, string ArgumentsJson) call)
        => _currentBatchCalls?.Add(call) ?? true;
}
