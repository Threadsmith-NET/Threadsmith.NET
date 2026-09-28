namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Tools;

// Functional integration tests exercise the real process lifecycle without imposing
// a speed requirement. Timeout-policy tests use the unmodified process manager.
internal sealed class CancellationOnlyProcessManager : IProcessManager
{
    private readonly ProcessManager _inner;

    public CancellationOnlyProcessManager(ProcessManager inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => _inner.ActiveProcesses;

    public Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        return _inner.RunAsync(request with { Timeout = Timeout.InfiniteTimeSpan }, cancellationToken);
    }
}
