namespace Threadsmith.Interaction.Coordination;

using System.Text;
using System.Threading.Channels;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Bounded engine-to-UI dispatcher with redraw coalescing.</summary>
public sealed class InteractionEventDispatcher
{
    private readonly Channel<IDomainEvent> _channel;

    /// <summary>Initializes a new instance of the <see cref="InteractionEventDispatcher"/> class.</summary>
    public InteractionEventDispatcher(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _channel = Channel.CreateBounded<IDomainEvent>(new BoundedChannelOptions(capacity)
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
        await _channel.Writer.WriteAsync(domainEvent, cancellationToken);
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
        await foreach (var domainEvent in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            batch.Add(domainEvent);
            while (batch.Count < 64 && _channel.Reader.TryRead(out var next))
            {
                batch.Add(next);
            }

            await renderAsync(batch.ToArray(), cancellationToken);
            batch.Clear();
        }
    }
}
