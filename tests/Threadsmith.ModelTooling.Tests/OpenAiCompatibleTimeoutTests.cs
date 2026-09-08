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
        })) { Timeout = Timeout.InfiniteTimeSpan };
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
        var attempts = 0;
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            attempts++;
            throw new TaskCanceledException("Handler timeout.");
        })) { Timeout = Timeout.InfiniteTimeSpan };
        var profile = Profile(600) with
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 3, Delay = TimeSpan.FromMinutes(1) },
        };
        var provider = new OpenAiCompatibleModelProvider(client, profile);

        var exception = await Assert.ThrowsAsync<ModelProviderTimeoutException>(async () =>
            await provider.StreamAsync(Request(), TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Contains("00:00:00.0500000", exception.Message, StringComparison.Ordinal);
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
        })) { Timeout = Timeout.InfiniteTimeSpan };
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
        })) { Timeout = Timeout.InfiniteTimeSpan };
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
        })) { Timeout = Timeout.InfiniteTimeSpan };
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

    private static ModelStreamRequest Request() => new() { RunId = RunId.New(), Input = "Explain the design." };

    private static ModelProfile Profile(int timeoutSeconds) => new()
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

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/event-stream"),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _send(request, cancellationToken);
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

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
