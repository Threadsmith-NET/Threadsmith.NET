namespace Threadsmith.AnthropicProvider.Tests;

using System.Net;
using System.Text;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;

/// <summary>Transport ownership, cancellation, total deadlines, and decompressed stream bounds.</summary>
public sealed class AnthropicTransportTests
{
    /// <summary>Disposing successive SDK clients preserves the shared host pool and request-local API key.</summary>
    [Fact]
    public async Task StreamAsync_SdkDisposal_SharedClientRemainsUsable()
    {
        var handler = new TestAnthropicHandler(TestAnthropic.TextStream());
        using var client = new HttpClient(handler);
        await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(), "first-key", TestAnthropic.Compatibility()), TestAnthropic.Request());
        await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(), "second-key", TestAnthropic.Compatibility()), TestAnthropic.Request());
        Assert.Equal(new[] { "first-key", "second-key" }, handler.Keys);
        Assert.All(handler.Uris, uri => Assert.Equal(AnthropicProviderRegistration.MessagesEndpoint, uri));
        Assert.False(client.DefaultRequestHeaders.Contains("x-api-key"));
    }

    /// <summary>Cancellation interrupts a hidden-signature wait without waiting for a tool or visible text.</summary>
    [Fact]
    public async Task StreamAsync_CancelDuringSignatureWait_PropagatesCallerCancellation()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = TestAnthropic.Start() + TestAnthropic.Block(0, new { type = "thinking", thinking = string.Empty, signature = string.Empty })
            + TestAnthropic.Delta(0, new { type = "signature_delta", signature = "private-canary" });
        var handler = new TestAnthropicHandler((_, _) => Task.FromResult(Response(new WaitingStream(prefix, waiting))));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(true), "key", TestAnthropic.Compatibility(true)), TestAnthropic.Request(true), cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await cancellation.CancelAsync();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(handler.Requests);
    }

    /// <summary>A profile deadline covers the entire retry delay and is distinct from caller cancellation.</summary>
    [Fact]
    public async Task StreamAsync_DeadlineDuringRetryDelay_ReturnsProviderTimeout()
    {
        var handler = new TestAnthropicHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        using var client = new HttpClient(handler);
        var profile = TestAnthropic.Profile() with { Timeout = TimeSpan.FromSeconds(2), RetryPolicy = new ModelRetryPolicy { MaxAttempts = 3, Delay = TimeSpan.FromSeconds(10) } };
        await Assert.ThrowsAsync<ModelProviderTimeoutException>(() => TestAnthropic.CollectAsync(new AnthropicModelProvider(client, profile, "key", TestAnthropic.Compatibility()), TestAnthropic.Request()));
        Assert.Single(handler.Requests);
    }

    /// <summary>Even a disabled total byte ceiling preserves the bounded native SSE-frame parser boundary.</summary>
    [Fact]
    public async Task StreamAsync_SingleOversizedFrame_RejectsBeforeSdkDeserialization()
    {
        // This fixture intentionally exercises the exact production one-MiB single-event ceiling.
        var frame = "event: ping\ndata: " + new string(' ', 1024 * 1024) + "\n\n";
        var handler = new TestAnthropicHandler(frame);
        using var client = new HttpClient(handler);
        var profile = TestAnthropic.Profile() with { MaximumStreamedBytes = 0 };
        await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(new AnthropicModelProvider(client, profile, "key", TestAnthropic.Compatibility()), TestAnthropic.Request()));
        Assert.Single(handler.Requests);
    }

    /// <summary>A failed in-progress byte ceiling preserves trustworthy partial counters exactly once.</summary>
    [Fact]
    public async Task StreamAsync_StreamByteCeiling_ReportsPartialUsageBeforeFailure()
    {
        var prefix = TestAnthropic.Start(new { input_tokens = 5, output_tokens = 2 });
        var suffix = TestAnthropic.Block(0, new { type = "text", text = new string('x', 500) });
        var handler = new TestAnthropicHandler((_, _) => Task.FromResult(Response(new SegmentedStream(prefix, suffix))));
        using var client = new HttpClient(handler);
        var profile = TestAnthropic.Profile() with { MaximumStreamedBytes = Encoding.UTF8.GetByteCount(prefix) + 1 };
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in new AnthropicModelProvider(client, profile, "key", TestAnthropic.Compatibility()).StreamAsync(TestAnthropic.Request()))
            {
                chunks.Add(chunk);
            }
        });
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.Equal(5, usage.InputTokens);
        Assert.Equal(2, usage.OutputTokens);
        Assert.Single(handler.Requests);
    }

    /// <summary>Caller cancellation preserves estimated visible output despite a provisional zero counter.</summary>
    [Fact]
    public async Task StreamAsync_CancelAfterVisibleOutput_ReportsConservativePartialUsage()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = TestAnthropic.Start(new { input_tokens = 100, output_tokens = 0, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 })
            + TestAnthropic.Block(0, new { type = "text", text = string.Empty })
            + TestAnthropic.Delta(0, new { type = "text_delta", text = new string('x', 2000) });
        var handler = new TestAnthropicHandler((_, _) => Task.FromResult(Response(new WaitingStream(prefix, waiting))));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var chunks = new List<ModelChunk>();
        var task = CollectAsync();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await cancellation.CancelAsync();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(500, usage.OutputTokens);
        Assert.True(usage.IsEstimate);
        Assert.Equal(0.0052m, usage.EstimatedCost);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
        Assert.Single(handler.Requests);

        async Task CollectAsync()
        {
            await foreach (var chunk in new AnthropicModelProvider(client, TestAnthropic.Profile(), "key", TestAnthropic.Compatibility()).StreamAsync(TestAnthropic.Request(), cancellation.Token))
            {
                chunks.Add(chunk);
            }
        }
    }

    /// <summary>A server cannot opt out of frame bounds by omitting or changing the response media type.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task StreamAsync_OversizedFrameWithMisleadingContentType_StillFailsBeforeText(string? contentType)
    {
        var fixture = "event: ping\ndata: {\"type\":\"ping\",\"padding\":\"" + new string('x', 1024 * 1024) + "\"}\n\n"
            + TestAnthropic.TextStream("must not escape");
        var handler = new TestAnthropicHandler((_, _) =>
        {
            var content = new StringContent(fixture, Encoding.UTF8);
            content.Headers.ContentType = contentType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new HttpClient(handler);
        var profile = TestAnthropic.Profile() with { MaximumStreamedBytes = 0 };
        var chunks = new List<ModelChunk>();
        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in new AnthropicModelProvider(client, profile, "key", TestAnthropic.Compatibility()).StreamAsync(TestAnthropic.Request()))
            {
                chunks.Add(chunk);
            }
        });
        Assert.Empty(chunks);
        Assert.Single(handler.Requests);
        Assert.Null(error.InnerException);
    }

    private static HttpResponseMessage Response(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private sealed class WaitingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly TaskCompletionSource _waiting;
        private int _offset;

        internal WaitingStream(string prefix, TaskCompletionSource waiting)
        {
            _prefix = Encoding.UTF8.GetBytes(prefix);
            _waiting = waiting;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var length = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, length).CopyTo(buffer);
                _offset += length;
                return length;
            }

            _waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SegmentedStream : Stream
    {
        private readonly Queue<byte[]> _segments;
        private int _offset;

        internal SegmentedStream(params string[] segments)
        {
            _segments = new Queue<byte[]>(segments.Select(Encoding.UTF8.GetBytes));
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_segments.TryPeek(out var segment))
            {
                return ValueTask.FromResult(0);
            }

            var length = Math.Min(buffer.Length, segment.Length - _offset);
            segment.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            if (_offset == segment.Length)
            {
                _segments.Dequeue();
                _offset = 0;
            }

            return ValueTask.FromResult(length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Serializes process-environment fixtures with all other test collections.</summary>
[CollectionDefinition("Anthropic environment", DisableParallelization = true)]
public sealed class AnthropicEnvironmentCollection
{
}

/// <summary>Verifies the SDK cannot acquire ambient credential or endpoint authority.</summary>
[Collection("Anthropic environment")]
public sealed class AnthropicAmbientEnvironmentTests
{
    /// <summary>Explicit client options override hostile environment settings before SDK construction.</summary>
    [Fact]
    public async Task StreamAsync_HostileEnvironment_UsesCompiledAuthorityAndResolvedKey()
    {
        var names = new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_PROFILE" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://hostile.invalid/");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "ambient-key-canary");
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "ambient-token-canary");
            Environment.SetEnvironmentVariable("ANTHROPIC_PROFILE", "hostile-profile");
            var handler = new TestAnthropicHandler(TestAnthropic.TextStream());
            using var client = new HttpClient(handler);
            await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(), "resolved-key", TestAnthropic.Compatibility()), TestAnthropic.Request());
            Assert.Equal("resolved-key", Assert.Single(handler.Keys));
            Assert.Equal(AnthropicProviderRegistration.MessagesEndpoint, Assert.Single(handler.Uris));
        }
        finally
        {
            foreach (var entry in previous)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }
}


