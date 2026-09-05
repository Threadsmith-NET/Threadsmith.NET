namespace Threadsmith.Interaction.Coordination;

/// <summary>Owns one cancellable refresh lifetime for a fixed repository/session context.</summary>
internal sealed class RetainedStatusRefresh : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private Task _refresh = Task.CompletedTask;

    /// <summary>Initializes a new instance of the <see cref="RetainedStatusRefresh"/> class.</summary>
    internal RetainedStatusRefresh(CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>Cancels and observes the refresh before allowing the next context to publish.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _refresh.WaitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Scope cancellation intentionally ends the bounded refresh loop.
        }
        finally
        {
            _stop.Dispose();
        }
    }

    /// <summary>Starts one refresh loop; failures wake the coordinator and propagate at disposal.</summary>
    internal void Start(Func<CancellationToken, Task> refresh, CancellationTokenSource lifetime)
    {
        _refresh = RunAsync(refresh, lifetime, _stop.Token);
    }

    /// <summary>Invalidates the captured context before a session or repository transition.</summary>
    internal void Cancel() => _stop.Cancel();

    private static async Task RunAsync(Func<CancellationToken, Task> refresh, CancellationTokenSource lifetime, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await refresh(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The owning interaction iteration ended.
        }
        catch
        {
            await lifetime.CancelAsync();
            throw;
        }
    }
}
