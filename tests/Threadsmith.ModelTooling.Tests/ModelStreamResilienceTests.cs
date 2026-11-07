namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Threadsmith.Models;
using Xunit;

/// <summary>Exercises the shared resilience pipeline with deterministic delay scheduling and stream ownership.</summary>
public static class ModelStreamResilienceTests
{
    /// <summary>Configured attempts and exponential delays are enforced by the pipeline.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public static async Task PipelineOwnsAttemptLimitBackoffAndOneBusyNotice(int maximumAttempts)
    {
        var clock = new RecordingClock();
        var attempts = 0;
        var chunks = new List<ModelChunk>();
        var policy = new ModelRetryPolicy { MaxAttempts = maximumAttempts, Delay = TimeSpan.Zero };
        await Assert.ThrowsAsync<TransientModelException>(async () =>
        {
            await foreach (var chunk in ModelStreamResilience.ExecuteAsync(
                AttemptAsync, policy, clock, TimeSpan.Zero, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(maximumAttempts, attempts);
        Assert.Equal(maximumAttempts > 1 ? 1 : 0, chunks.Count(chunk => chunk.ProviderBusy));
        Assert.Equal(new[] { 250d, 500, 1000, 2000, 4000, 5000, 5000 }.Take(maximumAttempts - 1), clock.Delays.Select(delay => delay.TotalMilliseconds));

        async IAsyncEnumerable<ModelChunk> AttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts = attempt;
            await Task.CompletedTask;
            if (attempt > 0)
            {
                throw new TransientModelException("rate limited") { IsRateLimit = true };
            }

            yield break;
        }
    }

    /// <summary>Early disposal never consumes or replays another chunk.</summary>
    [Fact]
    public static async Task DisposingConsumerDisposesAttemptWithoutAdvancingOrRetrying()
    {
        var advanced = false;
        var disposed = false;
        var policy = new ModelRetryPolicy { MaxAttempts = 3, Delay = TimeSpan.Zero };
        await using (var stream = ModelStreamResilience.ExecuteAsync(
            (_, token) => AttemptAsync(token), policy, TimeProvider.System, TimeSpan.Zero, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.False(advanced);
            Assert.False(disposed);
        }

        Assert.True(disposed);
        Assert.False(advanced);

        async IAsyncEnumerable<ModelChunk> AttemptAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield return new ModelChunk { Text = "one" };
                advanced = true;
            }
            finally
            {
                disposed = true;
            }
        }
    }

    private sealed class RecordingClock : FakeTimeProvider
    {
        public ConcurrentQueue<TimeSpan> Delays { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Enqueue(dueTime);
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Advance(dueTime);
            return timer;
        }
    }
}
