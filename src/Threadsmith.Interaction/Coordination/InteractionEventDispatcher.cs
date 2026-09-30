namespace Threadsmith.Interaction.Coordination;

using System.Threading.Channels;
using Threadsmith.Core;

/// <summary>Bounded engine-to-UI dispatcher with redraw coalescing.</summary>
public sealed class InteractionEventDispatcher
{
    private readonly Channel<DispatchItem> _channel;

    /// <summary>Initializes a new instance of the <see cref="InteractionEventDispatcher"/> class.</summary>
    public InteractionEventDispatcher(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _channel = Channel.CreateBounded<DispatchItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Queues an event with backpressure.</summary>
    public async Task QueueAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        try
        {
            await _channel.Writer.WriteAsync(new DispatchItem(domainEvent, null), cancellationToken);
        }
        catch (ChannelClosedException)
        {
            // The UI observer has stopped. Its drain task owns any rendering failure;
            // engine publication must still finish so cancellation can join the run.
        }
    }

    /// <summary>Signals that no further UI events will be queued.</summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
    }

    /// <summary>Drains available events in one redraw batch.</summary>
    public async Task DrainAsync(
        Func<IReadOnlyList<IDomainEvent>, CancellationToken, Task> renderAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderAsync);
        var batch = new List<IDomainEvent>(64);
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.Work is { } work)
                {
                    await work(cancellationToken);
                    continue;
                }

                batch.Add(item.Event ?? throw new InvalidOperationException("A dispatch item has no operation."));
                while (batch.Count < 64 && _channel.Reader.TryPeek(out var next) && next.Work is null
                    && _channel.Reader.TryRead(out next))
                {
                    batch.Add(next.Event ?? throw new InvalidOperationException("A dispatch item has no event."));
                }

                await renderAsync(batch.ToArray(), cancellationToken);
                batch.Clear();
            }
        }
        finally
        {
            // A failed renderer must release publishers blocked by UI backpressure.
            Complete();
        }
    }

    /// <summary>Serializes frontend state restoration with queued engine events.</summary>
    internal async Task QueueWorkAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await _channel.Writer.WriteAsync(new DispatchItem(null, work), cancellationToken);
        }
        catch (ChannelClosedException)
        {
            // The drain owns terminal failure, exactly as for domain-event admission.
        }
    }

    private sealed record DispatchItem(IDomainEvent? Event, Func<CancellationToken, Task>? Work);
}
