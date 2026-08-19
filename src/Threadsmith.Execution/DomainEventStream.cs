namespace Threadsmith.Execution;

using System.Threading.Channels;
using Threadsmith.Core;

/// <summary>Fan-out event stream with ordered bounded buffering per subscriber.</summary>
public sealed class DomainEventStream : IDomainEventStream
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Subscription> _subscriptions = [];
    private bool _disposed;
    private long _nextId;

    /// <inheritdoc />
    public IDomainEventSubscription Subscribe(
        Func<IDomainEvent, CancellationToken, Task> handler,
        int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = ++_nextId;
            var subscription = new Subscription(this, id, handler, capacity);
            _subscriptions.Add(id, subscription);
            return subscription;
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();
        Subscription[] subscriptions;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscriptions = [.. _subscriptions.Values];
        }

        await Task.WhenAll(subscriptions.Select(item =>
            item.EnqueueAsync(domainEvent, cancellationToken)));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = [.. _subscriptions.Values];
            _subscriptions.Clear();
        }

        foreach (Subscription subscription in subscriptions)
        {
            await subscription.DisposeCoreAsync();
        }
    }

    private void Remove(long id)
    {
        lock (_gate)
        {
            _subscriptions.Remove(id);
        }
    }

    private sealed record Delivery(
        IDomainEvent DomainEvent,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);

    private sealed class Subscription : IDomainEventSubscription
    {
        private readonly Channel<Delivery> _channel;
        private readonly Func<IDomainEvent, CancellationToken, Task> _handler;
        private readonly long _id;
        private readonly DomainEventStream _owner;
        private readonly Task _worker;
        private int _disposed;

        public Subscription(
            DomainEventStream owner,
            long id,
            Func<IDomainEvent, CancellationToken, Task> handler,
            int capacity)
        {
            _owner = owner;
            _id = id;
            _handler = handler;
            _channel = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            _worker = ProcessAsync();
        }

        public async Task EnqueueAsync(
            IDomainEvent domainEvent,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _channel.Writer.WriteAsync(
                new Delivery(domainEvent, cancellationToken, completion),
                cancellationToken);
            await completion.Task;
        }

        public async ValueTask DisposeAsync()
        {
            _owner.Remove(_id);
            await DisposeCoreAsync();
        }

        public async ValueTask DisposeCoreAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _channel.Writer.TryComplete();
#pragma warning disable VSTHRD003 // The worker is started by this subscription's constructor.
            await _worker;
#pragma warning restore VSTHRD003
        }

        private async Task ProcessAsync()
        {
            await foreach (Delivery delivery in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    await _handler(delivery.DomainEvent, delivery.CancellationToken);
                    delivery.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (delivery.CancellationToken.IsCancellationRequested)
                {
                    delivery.Completion.TrySetCanceled(delivery.CancellationToken);
                }
                catch (Exception exception)
                {
                    delivery.Completion.TrySetException(exception);
                }
            }
        }
    }
}
