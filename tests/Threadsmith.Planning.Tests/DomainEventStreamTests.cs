namespace Threadsmith.Planning.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Tests committed domain event delivery boundaries.</summary>
public sealed class DomainEventStreamTests
{
    /// <summary>A blocked subscriber exceeds its delivery bound only when the clock advances.</summary>
    [Fact]
    public static async Task CommittedDeliveryExpiresAfterSubscriberEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var bound = TimeSpan.FromSeconds(1);
        await using var events = new DomainEventStream(bound, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe(async (_, _) =>
        {
            entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(ct);
            }
            finally
            {
                left.TrySetResult();
            }
        });

        var committed = events.PublishCommittedBatchAsync([new RunCompleted(SessionId.New(), clock.GetUtcNow(), RunId.New(), true)], () => true, ct);
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, committed).WaitAsync(ct));
            clock.Advance(bound);
            var failure = await Assert.ThrowsAsync<CommittedDomainEventDeliveryException>(() => committed.WaitAsync(ct));
            Assert.IsType<TimeoutException>(failure.InnerException);
        }
        finally
        {
            release.TrySetResult();
            await left.Task.WaitAsync(ct);
        }
    }
}
