namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Threadsmith.Models;
using Xunit;

/// <summary>Exercises the shared resilience pipeline with deterministic delay scheduling and stream ownership.</summary>
public static class ModelStreamResilienceTests
{
    /// <summary>Busy failures use the elapsed window, independently of the transient attempt cap.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(8, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(8, true)]
    public static async Task PipelineOwnsBusyWindowBackoffAndOneBusyNotice(int maximumAttempts, bool overloaded)
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

        Assert.Equal(TimeSpan.FromSeconds(15), policy.BusyTimeout);
        Assert.Equal(7, attempts);
        Assert.Single(chunks, chunk => chunk.ProviderBusy);
        Assert.Equal(new[] { 250d, 500, 1000, 2000, 4000, 5000, 2250 }, clock.Delays.Select(delay => delay.TotalMilliseconds));

        async IAsyncEnumerable<ModelChunk> AttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts = attempt;
            await Task.CompletedTask;
            if (attempt > 0)
            {
                throw new TransientModelException("provider busy") { IsRateLimit = !overloaded, IsOverloaded = overloaded };
            }

            yield break;
        }
    }

    /// <summary>Expiry clips the final wait and reports the original sanitized failure.</summary>
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 0, 3)]
    [InlineData(15, 30, 1)]
    [InlineData(20, 0, 8)]
    public static async Task BusyWindowClipsFinalDelayAndPreservesOriginalError(int seconds, int retryAfterSeconds, int expectedAttempts)
    {
        var clock = new RecordingClock();
        var started = clock.GetTimestamp();
        var failure = new TransientModelException("Provider returned error\nHTTP 429 · Code: 429")
        {
            IsRateLimit = true,
            RetryAfter = retryAfterSeconds > 0 ? TimeSpan.FromSeconds(retryAfterSeconds) : null,
        };
        var attempts = 0;
        var policy = new ModelRetryPolicy { BusyTimeout = TimeSpan.FromSeconds(seconds) };
        var error = await Assert.ThrowsAsync<TransientModelException>(async () =>
            await ModelStreamResilience.ExecuteAsync(
                AttemptAsync, policy, clock, TimeSpan.Zero, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(TimeSpan.FromSeconds(seconds), clock.GetElapsedTime(started));

        async IAsyncEnumerable<ModelChunk> AttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts = attempt;
            await Task.CompletedTask;
            if (attempt > 0)
            {
                throw failure;
            }

            yield break;
        }
    }

    /// <summary>Ordinary transport failures retain the configured attempt limit.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public static async Task NonBusyFailuresRetainAttemptLimit(int maximumAttempts)
    {
        var clock = new RecordingClock();
        var attempts = 0;
        var policy = new ModelRetryPolicy { MaxAttempts = maximumAttempts, Delay = TimeSpan.FromMilliseconds(10) };
        await Assert.ThrowsAsync<TransientModelException>(async () =>
            await ModelStreamResilience.ExecuteAsync(
                AttemptAsync, policy, clock, TimeSpan.Zero, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(maximumAttempts, attempts);
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(10), maximumAttempts - 1), clock.Delays);

        async IAsyncEnumerable<ModelChunk> AttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts = attempt;
            await Task.CompletedTask;
            if (attempt > 0)
            {
                throw new TransientModelException("transport failure");
            }

            yield break;
        }
    }

    /// <summary>The initial request duration is excluded and busy recovery bypasses the attempt cap.</summary>
    [Fact]
    public static async Task BusyWindowStartsAtFirstFailureAndCanRecoverBeyondAttemptLimit()
    {
        var clock = new RecordingClock();
        var policy = new ModelRetryPolicy { MaxAttempts = 1 };
        var chunks = await ModelStreamResilience.ExecuteAsync(
            AttemptAsync, policy, clock, TimeSpan.Zero, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks, chunk => chunk.ProviderBusy);
        Assert.Equal("recovered", chunks[^1].Text);
        Assert.Equal(new[] { 250d, 500, 1000 }, clock.Delays.Select(delay => delay.TotalMilliseconds));

        async IAsyncEnumerable<ModelChunk> AttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            if (attempt == 1)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
            }

            if (attempt < 4)
            {
                throw new TransientModelException("busy") { IsOverloaded = true };
            }

            yield return new ModelChunk { Text = "recovered" };
        }
    }

    /// <summary>A stalled retry is cancelled at expiry, even when an adapter translates cancellation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task BusyDeadlineCancelsStalledRetryAndPreservesBusyError(bool adapterTranslatesCancellation)
    {
        var clock = new RecordingClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TransientModelException("HTTP 429") { IsRateLimit = true };
        var attempts = 0;
        var pending = ModelStreamResilience.ExecuteAsync(
            AttemptAsync, new ModelRetryPolicy(), clock, TimeSpan.Zero, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromMilliseconds(14749));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        var error = await Assert.ThrowsAsync<TransientModelException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Same(failure, error);
        Assert.Equal(2, attempts);

        async IAsyncEnumerable<ModelChunk> AttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            attempts = attempt;
            if (attempt == 1)
            {
                throw failure;
            }

            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException exception) when (adapterTranslatesCancellation)
            {
                throw new ModelProviderTimeoutException("adapter timeout", exception);
            }

            yield break;
        }
    }

    /// <summary>Caller cancellation and shorter deadlines win even if busy expiry occurs before the attempt unwinds.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task PendingBusyRetryPreservesEarlierCancellation(bool cancelCaller, bool expireBusyBeforeCompletion)
    {
        // Arrange: the attempt acknowledges cancellation before a separate barrier allows it to unwind.
        var clock = new RecordingClock();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        await using var deadline = clock.CreateTimer(_ => request.Cancel(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var timeout = TimeSpan.FromSeconds(5);
        if (!cancelCaller)
        {
            deadline.Change(timeout, Timeout.InfiniteTimeSpan);
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var pending = ModelStreamResilience.ExecuteAsync(
            AttemptAsync, new ModelRetryPolicy(), clock, timeout, request.Token, caller.Token)
            .ToListAsync(caller.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

            // Act: the first retry has consumed 250 ms of the total deadline.
            if (cancelCaller)
            {
                await caller.CancelAsync();
            }
            else
            {
                clock.Advance(TimeSpan.FromMilliseconds(4750));
            }

            await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (expireBusyBeforeCompletion)
            {
                clock.Advance(TimeSpan.FromSeconds(15));
            }

            release.TrySetResult();

            // Assert: retain the earlier cancellation's classification and never submit another attempt.
            if (cancelCaller)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
                Assert.Equal(caller.Token, error.CancellationToken);
            }
            else
            {
                var error = await Assert.ThrowsAsync<ModelProviderTimeoutException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
                Assert.Contains("00:00:05", error.Message, StringComparison.Ordinal);
                Assert.IsType<OperationCanceledException>(error.InnerException, exactMatch: false);
            }

            Assert.Equal(2, attempts);
        }
        finally
        {
            release.TrySetResult();
            await caller.CancelAsync();
            await Record.ExceptionAsync(() => pending.WaitAsync(TestContext.Current.CancellationToken));
        }

        async IAsyncEnumerable<ModelChunk> AttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            attempts = attempt;
            if (attempt == 1)
            {
                throw new TransientModelException("HTTP 429") { IsRateLimit = true };
            }

            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await release.Task.WaitAsync(TestContext.Current.CancellationToken);
                throw;
            }

            yield break;
        }
    }

    /// <summary>Only an armed busy timer is disarmed, once, regardless of subsequent stream length.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task RecoveredAnswerDisarmsBusyDeadline(bool initiallyBusy)
    {
        var clock = new RecordingClock();
        var chunks = await ModelStreamResilience.ExecuteAsync(
            AttemptAsync, new ModelRetryPolicy(), clock, TimeSpan.Zero, TestContext.Current.CancellationToken, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("first" + new string('x', 128) + "last", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Equal(initiallyBusy ? new[] { TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan } : [], clock.TimerChanges);

        async IAsyncEnumerable<ModelChunk> AttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (initiallyBusy && attempt == 1)
            {
                throw new TransientModelException("busy") { IsRateLimit = true };
            }

            yield return new ModelChunk { Text = "first" };
            for (var index = 0; index < 128; index++)
            {
                yield return new ModelChunk { Text = "x" };
            }

            clock.Advance(TimeSpan.FromSeconds(20));
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelChunk { Text = "last" };
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

        public ConcurrentQueue<TimeSpan> TimerChanges { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero)
            {
                Delays.Enqueue(dueTime);
                Advance(dueTime);
            }

            return new RecordingTimer(timer, TimerChanges);
        }

        private sealed class RecordingTimer : ITimer
        {
            private readonly ITimer _timer;
            private readonly ConcurrentQueue<TimeSpan> _changes;

            public RecordingTimer(ITimer timer, ConcurrentQueue<TimeSpan> changes)
            {
                _timer = timer;
                _changes = changes;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _changes.Enqueue(dueTime);
                return _timer.Change(dueTime, period);
            }

            public void Dispose() => _timer.Dispose();

            public ValueTask DisposeAsync() => _timer.DisposeAsync();
        }
    }
}
