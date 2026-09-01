namespace Threadsmith.Execution;

using System.Threading.Channels;
using Threadsmith.Core;

/// <summary>Fan-out event stream with ordered bounded buffering per subscriber.</summary>
public sealed class DomainEventStream : IDomainEventStream
{
    private static readonly TimeSpan DefaultCommittedDeliveryTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _committedDeliveryTimeout;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Subscription> _subscriptions = [];
    private bool _disposed;
    private long _nextId;

    /// <summary>Initializes a new instance of the <see cref="DomainEventStream"/> class.</summary>
    public DomainEventStream(TimeSpan? committedDeliveryTimeout = null)
    {
        var effectiveTimeout = committedDeliveryTimeout ?? DefaultCommittedDeliveryTimeout;
        if (effectiveTimeout < TimeSpan.FromMilliseconds(10)
            || effectiveTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(committedDeliveryTimeout));
        }

        _committedDeliveryTimeout = effectiveTimeout;
    }

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
    public async Task PublishCommittedBatchAsync(
        IReadOnlyList<IDomainEvent> domainEvents,
        Func<bool> tryCommit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);
        ArgumentNullException.ThrowIfNull(tryCommit);
        if (domainEvents.Any(item => item is null))
        {
            throw new ArgumentException("A domain event batch cannot contain null entries.", nameof(domainEvents));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Subscription[] subscriptions;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscriptions = [.. _subscriptions.Values];
        }

        var decision = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = new List<Task>(subscriptions.Length);
        try
        {
            foreach (var subscription in subscriptions)
            {
                deliveries.Add(await subscription.PrepareAsync(
                    domainEvents,
                    decision.Task,
                    CancellationToken.None,
                    cancellationToken,
                    _committedDeliveryTimeout));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!tryCommit())
            {
                decision.TrySetResult(false);
                await Task.WhenAll(deliveries);
                return;
            }

            decision.TrySetResult(true);
        }
        catch
        {
            decision.TrySetResult(false);
            await Task.WhenAll(deliveries);
            throw;
        }

        try
        {
            await Task.WhenAll(deliveries);
        }
        catch (Exception exception)
        {
            throw new CommittedDomainEventDeliveryException(exception);
        }
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

        foreach (var subscription in subscriptions)
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
        IReadOnlyList<IDomainEvent> DomainEvents,
        CancellationToken CancellationToken,
        Task<bool> CommitDecision,
        TaskCompletionSource Completion,
        TimeSpan? Timeout);

    private sealed class Subscription : IDomainEventSubscription
    {
        private readonly Channel<Delivery> _channel;
        private readonly Func<IDomainEvent, CancellationToken, Task> _handler;
        private readonly long _id;
        private readonly DomainEventStream _owner;
        private readonly Task _worker;
        private int _disposed;
        private Exception? _terminalFailure;

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
            var completion = await PrepareAsync(
                [domainEvent],
                Task.FromResult(true),
                cancellationToken,
                cancellationToken,
                timeout: null);
            await completion;
        }

        public async Task<Task> PrepareAsync(
            IReadOnlyList<IDomainEvent> domainEvents,
            Task<bool> commitDecision,
            CancellationToken deliveryCancellationToken,
            CancellationToken enqueueCancellationToken,
            TimeSpan? timeout)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _terminalFailure) is { } terminalFailure)
            {
                throw new InvalidOperationException(
                    "The domain-event subscription is no longer available.",
                    terminalFailure);
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _channel.Writer.WriteAsync(
                new Delivery(
                    domainEvents,
                    deliveryCancellationToken,
                    commitDecision,
                    completion,
                    timeout),
                enqueueCancellationToken);
            return completion.Task;
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
            await foreach (var delivery in _channel.Reader.ReadAllAsync())
            {
                if (Volatile.Read(ref _terminalFailure) is { } terminalFailure)
                {
                    delivery.Completion.TrySetException(terminalFailure);
                    continue;
                }

                try
                {
#pragma warning disable VSTHRD003 // The decision task is created by the batch publisher for this delivery.
                    if (!await delivery.CommitDecision)
#pragma warning restore VSTHRD003
                    {
                        delivery.Completion.TrySetResult();
                        continue;
                    }

                    using var timeout = delivery.Timeout is { } duration
                        ? new CancellationTokenSource(duration)
                        : null;
                    var handlerCancellationToken = timeout?.Token
                        ?? delivery.CancellationToken;
                    foreach (var domainEvent in delivery.DomainEvents)
                    {
                        var handling = _handler(domainEvent, handlerCancellationToken);
                        try
                        {
                            await handling.WaitAsync(handlerCancellationToken);
                        }
                        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
                        {
                            var exception = new TimeoutException(
                                "A committed domain-event subscriber exceeded its delivery bound.");
                            Poison(exception);
                            _ = QuarantineHandlerAsync(handling);
                            throw exception;
                        }
                    }

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

        private void Poison(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _terminalFailure, exception, null) is not null)
            {
                return;
            }

            _owner.Remove(_id);
            _channel.Writer.TryComplete();
        }

        private static async Task QuarantineHandlerAsync(Task task)
        {
            try
            {
#pragma warning disable VSTHRD003 // A poisoned subscription observes its sole abandoned handler in quarantine.
                await task;
#pragma warning restore VSTHRD003
            }
            catch
            {
                // The delivery completion already reports the bounded subscriber failure.
            }
        }
    }
}
