namespace Threadsmith.Planning.Tests;

using Threadsmith.Execution;
using Xunit;

/// <summary>Exercises retirement before and during cancellation without timing assumptions.</summary>
public static class RunCancellationTests
{
    /// <summary>Retirement inside a callback defers disposal even when that callback fails.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompletionDuringCancellation_DefersDisposalUntilCallbacksFinish(bool callbackFails)
    {
        var source = new CancellationTokenSource();
        using var registration = new RunCancellationLifetime(source);
        var callbackFinished = false;
        await using var callback = source.Token.Register(() =>
        {
            // Simulate the waiter retiring this registration while cancellation is in flight.
            registration.Dispose();
            Assert.True(source.Token.IsCancellationRequested);
            callbackFinished = true;
            if (callbackFails)
            {
                throw new InvalidOperationException("callback failed");
            }
        });

        if (callbackFails)
        {
            await Assert.ThrowsAsync<AggregateException>(registration.CancelAsync);
        }
        else
        {
            Assert.True(await registration.CancelAsync());
        }

        Assert.True(callbackFinished);
        Assert.False(await registration.CancelAsync());
        Assert.Throws<ObjectDisposedException>(() => source.Token);
        registration.Dispose();
    }

    /// <summary>A stale reference cannot cancel an already disposed source.</summary>
    [Fact]
    public static async Task RetiredRegistration_RejectsCancellationThroughStaleReference()
    {
        var source = new CancellationTokenSource();
        using var registration = new RunCancellationLifetime(source);

        registration.Dispose();

        Assert.False(await registration.CancelAsync());
        Assert.Throws<ObjectDisposedException>(() => source.Token);
    }
}
