namespace Threadsmith.Tools;

/// <summary>Retains one terminal publication attempt for a live invocation, including its failure.</summary>
internal sealed class ToolSuspensionCompletion
{
    private readonly Lock _gate = new();
    private Task<ToolInvocationResult>? _completion;

    /// <summary>Shares the original terminal attempt with concurrent callers and cleanup retries.</summary>
    internal Task<ToolInvocationResult> CompleteAsync(Func<Task<ToolInvocationResult>> complete)
    {
        lock (_gate)
        {
            return _completion ??= complete();
        }
    }
}
