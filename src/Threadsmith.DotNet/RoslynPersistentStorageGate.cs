namespace Threadsmith.DotNet;

/// <summary>Coordinates access to Roslyn's process-shared non-Windows SQLite write cache.</summary>
/// <remarks>
/// Roslyn 5.6 attaches every non-Windows persistent store to the same
/// <c>file::memory:?cache=shared</c> write cache while synchronizing only within each workspace.
/// Keep MSBuild loading and compilation parallel; gate only operations that use persisted symbol indexes.
/// </remarks>
internal static class RoslynPersistentStorageGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Acquires the cache gate when Roslyn uses its process-shared non-Windows URI.</summary>
    internal static async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return NoopLease.Instance;
        }

        await Gate.WaitAsync(cancellationToken);
        return new GateLease();
    }

    private sealed class GateLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Gate.Release();
            }
        }
    }

    private sealed class NoopLease : IDisposable
    {
        internal static readonly NoopLease Instance = new();

        public void Dispose()
        {
        }
    }
}
