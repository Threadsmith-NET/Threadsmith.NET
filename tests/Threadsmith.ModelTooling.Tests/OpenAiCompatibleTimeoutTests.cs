namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Separates handler cancellation, caller cancellation, and the total model-request deadline.</summary>
public static class OpenAiCompatibleTimeoutTests
{
    private const string CompletedResponse = "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    /// <summary>A handler timeout retries under the same model deadline and may recover.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    public static async Task TransportCancellation_BeforeHeaders_RetriesAndRecovers(int timeoutSeconds)
    {
        var attempts = 0;
        var handler = new RecordingHandler((_, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            if (++attempts == 1)
            {
                throw new TaskCanceledException("Handler connection timeout.", new TimeoutException());
            }

            return Task.FromResult(Response(CompletedResponse));
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(client, Profile(timeoutSeconds));

        var chunks = await provider.StreamAsync(Request(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        Assert.Equal("answer", string.Concat(chunks.Select(chunk => chunk.Text)));
    }

    /// <summary>Exhausted independent transport cancellation never claims a configured deadline expired.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    public static async Task TransportCancellation_ExhaustsAttempts_ReportsTransportPhase(int timeoutSeconds)
    {
        var attempts = 0;
        var original = new TaskCanceledException("Private transport detail.", new TimeoutException());
        using var client = new HttpClient(new RecordingHandler((_, token) =>
        {
            attempts++;
            Assert.False(token.IsCancellationRequested);
            throw original;
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(client, Profile(timeoutSeconds));

        var exception = await Assert.ThrowsAsync<TransientModelException>(async () =>
            await provider.StreamAsync(Request(), TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, attempts);
        Assert.Contains("before response headers after 3 attempts", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exceeded", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Private transport detail", exception.Message, StringComparison.Ordinal);
        Assert.Same(original, exception.InnerException);
    }

    /// <summary>The profile deadline covers retry backoff and is not restarted for another attempt.</summary>
    [Fact]
    public static async Task ProfileDeadline_DuringRetryBackoff_StopsFurtherAttempts()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new RetryClock();
        var attempts = 0;
        using var client = new HttpClient(new RecordingHandler((_, token) =>
        {
            attempts++;
            Assert.False(token.IsCancellationRequested);
            throw new TaskCanceledException("Handler timeout.");
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var profile = Profile(600) with
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 3, Delay = TimeSpan.FromMinutes(1) },
        };
        var provider = new OpenAiCompatibleModelProvider(client, profile, timeProvider: clock);
        var pending = provider.StreamAsync(Request(), caller.Token).ToListAsync(caller.Token).AsTask();
        try
        {
            await Task.WhenAny(clock.BackoffEntered.Task, pending).WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(clock.BackoffEntered.Task.IsCompletedSuccessfully, "The request ended before entering retry backoff.");
            Assert.Equal(1, attempts);
            Assert.True(clock.DeadlineRegistered);
            clock.Advance(TimeSpan.FromMilliseconds(49));
            Assert.False(pending.IsCompleted);

            clock.Advance(TimeSpan.FromMilliseconds(1));

            Assert.False(caller.IsCancellationRequested);
            var exception = await Assert.ThrowsAsync<ModelProviderTimeoutException>(
                () => pending.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, attempts);
            Assert.Contains("00:00:00.0500000", exception.Message, StringComparison.Ordinal);
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        }
        finally
        {
            await caller.CancelAsync();
            // Cleanup has an independent backstop even when the runner cancellation already fired.
            await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        }
    }

    /// <summary>A user cancellation before headers remains cancellation and cannot trigger retries.</summary>
    [Fact]
    public static async Task CallerCancellation_BeforeHeaders_DoesNotRetry()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var client = new HttpClient(new RecordingHandler(async (_, token) =>
        {
            attempts++;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(CompletedResponse);
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(client, Profile(600));
        var pending = provider.StreamAsync(Request(), caller.Token).ToListAsync(caller.Token).AsTask();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, attempts);
    }

    /// <summary>A cancellation after streamed text is transport failure, without replaying the answer.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    public static async Task TransportCancellation_MidStream_DoesNotReplay(int timeoutSeconds)
    {
        var attempts = 0;
        using var stream = new ControlledStream(
            "data: {\"choices\":[{\"delta\":{\"content\":\"first\"}}]}\n\n",
            (_, _) => throw new TaskCanceledException("Transport stopped."));
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(client, Profile(timeoutSeconds));
        await using var chunks = provider.StreamAsync(Request(), TestContext.Current.CancellationToken).GetAsyncEnumerator();
        Assert.True(await chunks.MoveNextAsync());
        Assert.Equal("first", chunks.Current.Text);

        var exception = await Assert.ThrowsAsync<TransientModelException>(() => chunks.MoveNextAsync().AsTask());

        Assert.Contains("reading the response stream", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, attempts);
        Assert.True(stream.IsDisposed);
    }

    /// <summary>Slow headers and a gated first token can complete within the model's long request allowance.</summary>
    [Fact]
    public static async Task SlowHeadersAndFirstToken_CompleteWithinProfileDeadline()
    {
        var headersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = false;
        using var stream = new ControlledStream(string.Empty, async (buffer, token) =>
        {
            if (read)
            {
                return 0;
            }

            read = true;
            readEntered.TrySetResult();
            await releaseToken.Task.WaitAsync(token);
            var bytes = Encoding.UTF8.GetBytes(CompletedResponse);
            bytes.AsMemory().CopyTo(buffer);
            return bytes.Length;
        });
        using var client = new HttpClient(new RecordingHandler(async (_, token) =>
        {
            headersEntered.TrySetResult();
            await releaseHeaders.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(client, Profile(600));
        var pending = provider.StreamAsync(Request(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await headersEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            releaseHeaders.TrySetResult();
            await readEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            releaseToken.TrySetResult();

            var chunks = await pending;

            Assert.Equal("answer", string.Concat(chunks.Select(chunk => chunk.Text)));
        }
        finally
        {
            releaseHeaders.TrySetResult();
            releaseToken.TrySetResult();
        }
    }

    /// <summary>Both transport phases use one retry budget, while delivered output prevents replay.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public static async Task InterruptedBody_UsesRemainingAttemptsUnlessOutputDelivered(bool exhaustRetries, bool partialOutput, bool reasoningOnly = false)
    {
        var attempts = 0;
        var submissions = 0;
        var streams = new List<ControlledStream>();
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            Assert.All(streams, stream => Assert.True(stream.IsDisposed));
            if (++attempts == 1)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "private detail");
            }

            if (attempts == 2 || exhaustRetries)
            {
                var prefix = partialOutput
                    ? "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"
                    : "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n";
                if (reasoningOnly)
                {
                    prefix = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"partial reasoning\"}}]}\n\n" + prefix;
                }

                var stream = new ControlledStream(
                    prefix,
                    (_, _) => throw new HttpIOException(HttpRequestError.ResponseEnded, "private detail"));
                streams.Add(stream);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
            }

            return Task.FromResult(Response(CompletedResponse));
        }));
        var provider = new OpenAiCompatibleModelProvider(client, Profile(600));
        var request = Request() with { SubmissionObserver = () => submissions++ };
        var chunks = new List<ModelChunk>();
        async Task CollectAsync()
        {
            await foreach (var chunk in provider.StreamAsync(request, TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        }

        if (exhaustRetries || partialOutput)
        {
            var error = await Assert.ThrowsAsync<TransientModelException>(CollectAsync);
            Assert.Contains("ResponseEnded", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private detail", error.Message, StringComparison.Ordinal);
            Assert.Equal(partialOutput ? "partial" : string.Empty, string.Concat(chunks.Select(chunk => chunk.Text)));
        }
        else
        {
            await CollectAsync();
            Assert.Equal("answer", string.Concat(chunks.Select(chunk => chunk.Text)));
        }

        Assert.Equal(reasoningOnly && !partialOutput ? 1 : 0, chunks.Count(chunk => chunk.Reasoning == ModelStreamRetry.ReasoningBoundary().Reasoning));
        Assert.Equal(partialOutput ? 2 : 3, attempts);
        Assert.Equal(attempts, submissions);
        Assert.All(streams, stream => Assert.True(stream.IsDisposed));
    }

    /// <summary>Cancellation of a failed first body cannot trigger a second submission.</summary>
    [Fact]
    public static async Task InterruptedBody_CallerCancellation_DoesNotRetry()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var attempts = 0;
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ControlledStream(string.Empty, async (_, _) =>
                {
                    await caller.CancelAsync();
                    throw new HttpIOException(HttpRequestError.ResponseEnded, "private detail");
                })),
            });
        }));
        var provider = new OpenAiCompatibleModelProvider(client, Profile(600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.StreamAsync(Request(), caller.Token).ToListAsync(caller.Token));
        Assert.Equal(1, attempts);
    }

    private static ModelStreamRequest Request()
    {
        return new() { RunId = RunId.New(), Input = "Explain the design." };
    }

    private static ModelProfile Profile(int timeoutSeconds)
    {
        return new()
        {
            Id = ModelProfileId.New(),
            Name = "slow-model",
            Provider = "openai-compatible",
            ModelId = "test-model",
            Endpoint = new Uri("https://models.example/v1/chat/completions"),
            ContextWindow = 32000,
            MaximumOutputTokens = 4000,
            Capabilities = new ModelCapabilitySet { Streaming = true },
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 3, Delay = TimeSpan.Zero },
        };
    }

    private static HttpResponseMessage Response(string text)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/event-stream"),
        };
    }

    private sealed class RetryClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<RetryTimer> _timers = [];
        private long _ticks;

        public TaskCompletionSource BackoffEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DeadlineRegistered { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new RetryTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                DeadlineRegistered |= dueTime == TimeSpan.FromMilliseconds(50);
                if (dueTime == TimeSpan.FromMinutes(1))
                {
                    BackoffEntered.TrySetResult();
                }

                return timer;
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            RetryTimer[] due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                due = [.. _timers.Where(timer => timer.Due <= _ticks)];
                foreach (var timer in due)
                {
                    timer.Due = long.MaxValue;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class RetryTimer : ITimer
        {
            private readonly RetryClock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public RetryTimer(RetryClock clock, TimerCallback callback, object? state)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
            }

            public long Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (_clock._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _clock._ticks + dueTime.Ticks;
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _send(request, cancellationToken);
        }
    }

    private sealed class ControlledStream : Stream
    {
        private readonly byte[] _initial;
        private readonly Func<Memory<byte>, CancellationToken, Task<int>> _read;
        private bool _initialSent;

        public ControlledStream(string initial, Func<Memory<byte>, CancellationToken, Task<int>> read)
        {
            _initial = Encoding.UTF8.GetBytes(initial);
            _read = read;
        }

        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_initialSent && _initial.Length > 0)
            {
                _initialSent = true;
                _initial.AsMemory().CopyTo(buffer);
                return ValueTask.FromResult(_initial.Length);
            }

            return new ValueTask<int>(_read(buffer, cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
