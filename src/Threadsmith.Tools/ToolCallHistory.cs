namespace Threadsmith.Tools;

/// <summary>Tracks identical model tool calls within one execution, including pending batch siblings.</summary>
public sealed class ToolCallHistory
{
    private readonly HashSet<(string ToolName, string ArgumentsJson)> _calls;

    /// <summary>Initializes a new instance of the <see cref="ToolCallHistory"/> class with an empty execution history.</summary>
    public ToolCallHistory()
    {
        _calls = [];
    }

    /// <summary>Initializes a new instance of the <see cref="ToolCallHistory"/> class from accepted calls for batch validation.</summary>
    public ToolCallHistory(ToolCallHistory accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        _calls = [.. accepted._calls];
    }

    /// <summary>Records a call, returning false if its name and arguments have already been recorded.</summary>
    /// <remarks>Callers retain their existing argument normalization policy.</remarks>
    public bool TryAdd(string toolName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(argumentsJson);
        return _calls.Add((toolName, argumentsJson));
    }

    /// <summary>Reports whether a tool has an accepted call in this execution's history.</summary>
    public bool ContainsTool(string toolName) => _calls.Any(call => string.Equals(call.ToolName, toolName, StringComparison.OrdinalIgnoreCase));
}
