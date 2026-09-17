namespace Threadsmith.Tools;

/// <summary>Owns transient tool resources for one primary operation, shared with its descendants.</summary>
public sealed class ToolOperationScope : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<object, IAsyncDisposable> _state = [];
    private readonly CancellationTokenSource _lifetime;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ToolOperationScope"/> class linked to its owning operation.</summary>
    public ToolOperationScope(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>Cancellation of the owning operation and all its acquired resources.</summary>
    public CancellationToken CancellationToken => _lifetime.Token;

    /// <summary>Creates one resource per tool identity under this operation's lifetime.</summary>
    public T GetOrCreate<T>(object key, Func<T> create)
        where T : class, IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(create);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state.TryGetValue(key, out var state))
            {
                return (T)state;
            }

            var result = create();
            _state.Add(key, result);
            return result;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] resources;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            resources = [.. _state.Values];
            _state.Clear();
        }

        await _lifetime.CancelAsync();
        foreach (var resource in resources)
        {
            await resource.DisposeAsync();
        }

        _lifetime.Dispose();
    }
}
