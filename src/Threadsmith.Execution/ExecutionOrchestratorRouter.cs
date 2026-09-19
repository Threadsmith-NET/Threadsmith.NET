namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>One-time composition bridge that breaks the session/orchestrator construction cycle.</summary>
public sealed class ExecutionOrchestratorRouter : IExecutionOrchestrator
{
    private IExecutionOrchestrator? _inner;

    /// <summary>Attaches the fully composed orchestrator exactly once.</summary>
    public void Attach(IExecutionOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        if (Interlocked.CompareExchange(ref _inner, orchestrator, null) is not null)
        {
            throw new InvalidOperationException("The execution orchestrator is already attached.");
        }
    }

    /// <inheritdoc />
    public Task<ExecutionContinuation> StartAsync(
        ExecutionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetInner().StartAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionContinuation> ContinueWithPlanAsync(
        ExecutionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetInner().ContinueWithPlanAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionOutcomeProjection> ContinueAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetInner().ContinueAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionContinuation> ResumeAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return GetInner().ResumeAsync(sessionId, runId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionStartRequest> GetResumeRequestAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return GetInner().GetResumeRequestAsync(sessionId, runId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionPlanBoundary> WaitForPlanCompletionAsync(
        RunId runId,
        int afterPlanOrdinal,
        CancellationToken cancellationToken = default)
    {
        return GetInner().WaitForPlanCompletionAsync(runId, afterPlanOrdinal, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionOutcomeProjection> CompleteObjectiveAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return GetInner().CompleteObjectiveAsync(sessionId, runId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionOutcomeProjection> WaitForOutcomeAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return GetInner().WaitForOutcomeAsync(runId, cancellationToken);
    }

    private IExecutionOrchestrator GetInner()
    {
        return Volatile.Read(ref _inner)
            ?? throw new InvalidOperationException("The execution orchestrator is not attached.");
    }
}
