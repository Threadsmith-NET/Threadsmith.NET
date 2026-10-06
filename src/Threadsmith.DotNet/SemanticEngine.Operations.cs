namespace Threadsmith.DotNet;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

/// <summary>Retains compiler resources through actual completion, including work abandoned by its caller.</summary>
public sealed partial class SemanticEngine
{
    private readonly Dictionary<Workspace, CompilerWorkspaceLifetime> _compilerWorkspaces = [];
    private SemaphoreSlim? _actualCompilerSlots;

    /// <summary>Observes physical disposal independently of logical retirement for lifecycle verification.</summary>
    internal Action<Workspace>? WorkspaceDisposalObserver { get; init; }

    /// <summary>Runs snapshot work through the existing compiler queue without demanding additional compilation.</summary>
    internal Task<T> RunSnapshotOperationAsync<T>(Solution solution, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        SemanticPreparationReceipt receipt;
        lock (_gate)
        {
            if (!ReferenceEquals(solution, _solution))
            {
                throw new InvalidOperationException("The semantic snapshot was superseded before operation admission.");
            }

            receipt = new(solution, _solutionGeneration);
        }

        return RunPreparedOperationAsync(receipt, operation, cancellationToken);
    }

    /// <summary>Preserves completed partial query results after a deadline while discarding owner-cancelled work.</summary>
    internal Task<T> RunSnapshotQueryAsync<T>(Solution solution, Func<CancellationToken, Task<T>> operation, CancellationToken queryDeadline, CancellationToken cancellationToken)
    {
        SemanticPreparationReceipt receipt;
        lock (_gate)
        {
            if (!ReferenceEquals(solution, _solution))
            {
                throw new InvalidOperationException("The semantic snapshot was superseded before query admission.");
            }

            receipt = new(solution, _solutionGeneration);
        }

        return RunPreparedQueryOperationAsync(receipt, operation, queryDeadline, cancellationToken);
    }

    private async Task AcquireCompilerResourcesAsync(Solution? solution, CancellationToken cancellationToken)
    {
        SemaphoreSlim slots;
        lock (_gate)
        {
            // Actual tasks retain admission across logical abandonment and source replacement.
            slots = _actualCompilerSlots ??= new(_preparationLimits.Workers + 1, _preparationLimits.Workers + 1);
        }

        // Capacity pressure remains pending; it must never be cached as a failed project or analyzer.
        await slots.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (solution is not null && !ReferenceEquals(solution, _solution))
                {
                    throw new InvalidOperationException("The compiler input generation was superseded.");
                }

                if (solution is not null)
                {
                    if (!_compilerWorkspaces.TryGetValue(solution.Workspace, out var lifetime))
                    {
                        lifetime = new();
                        _compilerWorkspaces.Add(solution.Workspace, lifetime);
                    }

                    lifetime.Users++;
                }
            }
        }
        catch
        {
            slots.Release();
            throw;
        }
    }

    private void ReleaseCompilerResources(Solution? solution)
    {
        Workspace? dispose = null;
        lock (_gate)
        {
            if (solution is not null && _compilerWorkspaces.TryGetValue(solution.Workspace, out var lifetime))
            {
                lifetime.Users--;
                if (lifetime.Users == 0)
                {
                    _compilerWorkspaces.Remove(solution.Workspace);
                    dispose = lifetime.Retired ? solution.Workspace : null;
                }
            }
        }

        try
        {
            if (dispose is not null)
            {
                DisposeCompilerWorkspace(dispose);
            }
        }
        finally
        {
            _actualCompilerSlots?.Release();
        }
    }

    private void RetireCompilerWorkspace(Workspace? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_compilerWorkspaces.TryGetValue(workspace, out var lifetime))
            {
                lifetime.Retired = true;
                return;
            }
        }

        DisposeCompilerWorkspace(workspace);
    }

    private void DisposeCompilerWorkspace(Workspace workspace)
    {
        workspace.Dispose();
        WorkspaceDisposalObserver?.Invoke(workspace);
    }

    private sealed class CompilerOperationCancellation : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _caller;
        private readonly CancellationTokenRegistration _deadline;
        private readonly ILogger _logger;
        private Task _callbacks = Task.CompletedTask;

        public CompilerOperationCancellation(CancellationToken caller, CancellationToken deadline, ILogger logger)
        {
            _logger = logger;
            Token = _source.Token;
            _caller = caller.Register(RequestCancellation);
            _deadline = deadline.Register(RequestCancellation);
        }

        public CancellationToken Token { get; }

        public async ValueTask DisposeAsync()
        {
            await _caller.DisposeAsync();
            await _deadline.DisposeAsync();
            Task callbacks;
            lock (_gate)
            {
                callbacks = _callbacks;
            }

            try
            {
#pragma warning disable VSTHRD003 // This lifetime owns callback completion; the actual compiler task retains resources while it awaits.
                await callbacks;
#pragma warning restore VSTHRD003
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Compiler cancellation callback failed");
            }
            finally
            {
                _source.Dispose();
            }
        }

        private void RequestCancellation()
        {
            lock (_gate)
            {
                if (!_source.IsCancellationRequested)
                {
                    _callbacks = _source.CancelAsync();
                }
            }
        }
    }

    private sealed class CompilerWorkspaceLifetime
    {
        public int Users { get; set; }

        public bool Retired { get; set; }
    }
}
