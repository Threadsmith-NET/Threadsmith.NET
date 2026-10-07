namespace Threadsmith.Planning.Tests;

using System.Collections.Concurrent;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies bounded, ordered publication and round-owned cleanup.</summary>
public static class ModelOutputCoalescerTests
{
    /// <summary>Preserves text and Unicode while reducing fragment count.</summary>
    [Fact]
    public static async Task SmallFragments_PublishFirstImmediately_ThenBoundedBatchesAndFinalTail()
    {
        await using var events = new DomainEventStream();
        var received = new List<string>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            received.Add(Assert.IsType<ModelOutputObserved>(item).Text);
            return Task.CompletedTask;
        });
        var clock = new ManualClock();
        await using (var output = Create(events, clock, maximumCharacters: 8))
        {
            await output.AppendAsync("first", output.Token);
            Assert.Equal("first", Assert.Single(received));
            foreach (var text in new[] { "ab", "cd", "efg", "😀", "tail" })
            {
                await output.AppendAsync(text, output.Token);
            }

            Assert.Equal(2, received.Count);
        }

        Assert.Equal("firstabcdefg😀tail", string.Concat(received));
        Assert.All(received, text => Assert.InRange(text.Length, 1, 8));
        Assert.DoesNotContain(received, text => char.IsHighSurrogate(text[^1]));
        Assert.Equal(3, received.Count);
    }

    /// <summary>Flushes idle provider output using the configured clock.</summary>
    [Fact]
    public static async Task Timer_PublishesBufferedTextWithoutAnotherProviderChunk()
    {
        await using var events = new DomainEventStream();
        var tailPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (Assert.IsType<ModelOutputObserved>(item).Text == "tail")
            {
                tailPublished.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var clock = new ManualClock();
        await using var output = Create(events, clock);
        await output.AppendAsync("first", output.Token);
        await output.AppendAsync("tail", output.Token);

        clock.Advance(TimeSpan.FromMilliseconds(49));
        Assert.False(tailPublished.Task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await tailPublished.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Keeps subscriber completion ahead of subsequent observations.</summary>
    [Fact]
    public static async Task Flush_WaitsForSubscribersBeforeFollowingBoundary()
    {
        await using var events = new DomainEventStream();
        var received = new List<IDomainEvent>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe(async (item, token) =>
        {
            if (item is ModelOutputObserved { Text: "tail" })
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            received.Add(item);
        });
        await using var output = Create(events, new ManualClock());
        await output.AppendAsync("first", output.Token);
        await output.AppendAsync("tail", output.Token);
        var flush = output.FlushAsync(output.Token);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(flush.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await flush;
        await events.PublishAsync(new ModelReasoningObserved(SessionId.New(), DateTimeOffset.UtcNow, "reasoning"), output.Token);
        Assert.Collection(
            received,
            item => Assert.Equal("first", Assert.IsType<ModelOutputObserved>(item).Text),
            item => Assert.Equal("tail", Assert.IsType<ModelOutputObserved>(item).Text),
            item => Assert.IsType<ModelReasoningObserved>(item));
    }

    /// <summary>Retains accepted text when the owning run is cancelled.</summary>
    [Fact]
    public static async Task Cancellation_DrainsAcceptedTailAndStopsTimer()
    {
        await using var events = new DomainEventStream();
        var received = new ConcurrentQueue<string>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            received.Enqueue(Assert.IsType<ModelOutputObserved>(item).Text);
            return Task.CompletedTask;
        });
        var clock = new ManualClock();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var output = new ModelOutputCoalescer(events, SessionId.New(), new(), clock, cancellation.Token))
        {
            await output.AppendAsync("first", output.Token);
            await output.AppendAsync("tail", output.Token);
            await cancellation.CancelAsync();
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(["first", "tail"], received);
        Assert.Equal(0, clock.ActiveTimers);
    }

    /// <summary>Surfaces timer failures and stops upstream consumption.</summary>
    [Fact]
    public static async Task TimedPublicationFailure_CancelsStreamAndPropagatesWithoutRetry()
    {
        await using var events = new DomainEventStream();
        var attempts = 0;
        await using var subscription = events.Subscribe((item, _) =>
        {
            attempts++;
            if (Assert.IsType<ModelOutputObserved>(item).Text == "tail")
            {
                throw new IOException("persistence failed");
            }

            return Task.CompletedTask;
        });
        var clock = new ManualClock();
        var output = Create(events, clock);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = output.Token.Register(() => cancelled.TrySetResult());
        await output.AppendAsync("first", output.Token);
        await output.AppendAsync("tail", output.Token);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<CommittedDomainEventDeliveryException>(() => output.DisposeAsync().AsTask());

        Assert.Equal("persistence failed", Assert.IsType<IOException>(exception.InnerException).Message);
        Assert.Equal(2, attempts);
        Assert.Equal(0, clock.ActiveTimers);
    }

    /// <summary>Cancellation joins an in-flight delivery before ending the round.</summary>
    [Fact]
    public static async Task CancellationDuringTimedDelivery_JoinsAcceptedText()
    {
        await using var events = new DomainEventStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        await using var subscription = events.Subscribe(async (item, token) =>
        {
            var text = Assert.IsType<ModelOutputObserved>(item).Text;
            if (text == "tail")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            received.Add(text);
        });
        var clock = new ManualClock();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new ModelOutputCoalescer(events, SessionId.New(), new(), clock, cancellation.Token);
        await output.AppendAsync("first", output.Token);
        await output.AppendAsync("tail", output.Token);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var dispose = output.DisposeAsync().AsTask();
        try
        {
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await dispose;
        Assert.Equal(["first", "tail"], received);
    }

    /// <summary>Bounds stalled persistence without retrying partial delivery.</summary>
    [Fact]
    public static async Task StalledSubscriber_HasBoundedDeliveryAndIsNotRetried()
    {
        var clock = new ManualClock();
        await using var events = new DomainEventStream(timeProvider: clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var subscription = events.Subscribe(async (_, token) =>
        {
            attempts++;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var output = Create(events, clock);
        var append = output.AppendAsync("first", output.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<CommittedDomainEventDeliveryException>(
            () => append.WaitAsync(TestContext.Current.CancellationToken));
        Assert.IsType<TimeoutException>(exception.InnerException);
        await output.DisposeAsync();
        Assert.Equal(1, attempts);
    }

    /// <summary>Quarantines a non-cooperative handler before terminal delivery without retrying output.</summary>
    [Fact]
    public static async Task StalledSubscriberIgnoringCancellation_DoesNotReceiveRunCompleted()
    {
        var clock = new ManualClock();
        await using var events = new DomainEventStream(timeProvider: clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalledReceived = new ConcurrentQueue<IDomainEvent>();
        var healthyReceived = new ConcurrentQueue<IDomainEvent>();
        await using var stalled = events.Subscribe(async (item, _) =>
        {
            stalledReceived.Enqueue(item);
            entered.TrySetResult();
            try
            {
                // Deliberately ignore the delivery token to exercise quarantine.
                await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            finally
            {
                finished.TrySetResult();
            }
        });
        await using var healthy = events.Subscribe((item, _) =>
        {
            healthyReceived.Enqueue(item);
            return Task.CompletedTask;
        });
        await using var output = Create(events, clock);
        try
        {
            var append = output.AppendAsync("first", output.Token);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(5));
            var exception = await Assert.ThrowsAsync<CommittedDomainEventDeliveryException>(
                () => append.WaitAsync(TestContext.Current.CancellationToken));
            Assert.IsType<TimeoutException>(exception.InnerException);

            await events.PublishAsync(
                new RunCompleted(SessionId.New(), clock.GetUtcNow(), RunId.New(), false),
                TestContext.Current.CancellationToken);

            Assert.False(finished.Task.IsCompleted);
            Assert.IsType<ModelOutputObserved>(Assert.Single(stalledReceived));
            Assert.Collection(
                healthyReceived,
                item => Assert.Equal("first", Assert.IsType<ModelOutputObserved>(item).Text),
                item => Assert.IsType<RunCompleted>(item));
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Rejects invalid buffering policy.</summary>
    [Theory]
    [InlineData(1, 50)]
    [InlineData(4096, 0)]
    public static void InvalidLimits_AreRejected(int characters, int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionLimits
        {
            MaxModelOutputBatchCharacters = characters,
            ModelOutputFlushIntervalMilliseconds = milliseconds,
        }.Validate());
    }

    private static ModelOutputCoalescer Create(IDomainEventStream events, TimeProvider clock, int maximumCharacters = 4096)
    {
        var limits = new ExecutionLimits
        {
            MaxModelOutputBatchCharacters = maximumCharacters,
        };
        return new ModelOutputCoalescer(events, SessionId.New(), limits, clock, TestContext.Current.CancellationToken);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public int ActiveTimers
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Interlocked.Read(ref _ticks);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                due = [.. _timers.Where(timer => timer.Due <= _ticks)];
                foreach (var timer in due)
                {
                    timer.Due = timer.Period > TimeSpan.Zero ? _ticks + timer.Period.Ticks : long.MaxValue;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualClock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
            }

            public long Due { get; set; }

            public TimeSpan Period { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_clock._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _clock._ticks + dueTime.Ticks;
                    Period = period;
                    return true;
                }
            }

            public void Fire()
            {
                _callback(_state);
            }

            public void Dispose()
            {
                lock (_clock._gate)
                {
                    _clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
