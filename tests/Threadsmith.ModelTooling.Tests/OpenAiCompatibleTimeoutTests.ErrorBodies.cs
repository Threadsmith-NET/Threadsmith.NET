namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

public static partial class OpenAiCompatibleTimeoutTests
{
    /// <summary>Abandoned content opening reports failure promptly and reclaims late stream ownership.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ErrorBody_NonCooperativeOpeningIsAbandonedAndLateStreamDisposed(bool cancelCaller)
    {
        // Arrange
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new RetryClock();
        var opening = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new ControlledStream(string.Empty, (_, _) => Task.FromResult(0));
        using var client = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new DelayedOpeningContent(opening) })))
        { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(client, Profile(0), timeProvider: clock);
        var pending = provider.StreamAsync(Request(), caller.Token).ToListAsync(caller.Token).AsTask();
        try
        {
            await clock.DiagnosticsEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            // Act: neither expiration nor caller cancellation depends on completing the open.
            if (cancelCaller)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
            }
            else
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                var error = await Assert.ThrowsAsync<ModelProviderException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
                Assert.Contains("HTTP 400", error.Message, StringComparison.Ordinal);
                Assert.Contains("Error body: unavailable", error.Message, StringComparison.Ordinal);
            }

            // Assert: late ownership is reclaimed without waiting before reporting the HTTP failure.
            Assert.False(opening.Task.IsCompleted);
            Assert.False(stream.IsDisposed);
            opening.TrySetResult(stream);
            await stream.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            opening.TrySetResult(stream);
            await caller.CancelAsync();
            await Record.ExceptionAsync(() => pending.WaitAsync(TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Retry admission does not wait for an abandoned read or publish its late result.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ErrorBody_NonCooperativeReadDoesNotDelayRetry(bool lateFault)
    {
        // Arrange
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new RetryClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? lateRead = null;
        await using var stream = new ControlledStream(string.Empty, (buffer, _) => lateRead = ReadLateAsync(buffer));
        var calls = 0;
        using var client = new HttpClient(new RecordingHandler((_, _) =>
        {
            if (++calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StreamContent(stream) });
            }

            Assert.True(stream.IsDisposed);
            Assert.False(release.Task.IsCompleted);
            return Task.FromResult(Response(CompletedResponse));
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(client, Profile(0), timeProvider: clock);
        var pending = provider.StreamAsync(Request(), caller.Token).ToListAsync(caller.Token).AsTask();
        try
        {
            await clock.DiagnosticsEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            // Act
            clock.Advance(TimeSpan.FromMilliseconds(100));
            var chunks = await pending.WaitAsync(TestContext.Current.CancellationToken);

            // Assert: retry and success do not join the abandoned read or publish its late bytes.
            Assert.Equal(2, calls);
            Assert.Equal("answer", string.Concat(chunks.Select(chunk => chunk.Text)));
            var read = Assert.IsType<Task<int>>(lateRead, exactMatch: false);
            Assert.False(read.IsCompleted);
            release.TrySetResult();
            var fault = await Record.ExceptionAsync(() => read.WaitAsync(TestContext.Current.CancellationToken));
            if (lateFault)
            {
                Assert.IsType<IOException>(fault);
            }
            else
            {
                Assert.Null(fault);
            }
        }
        finally
        {
            release.TrySetResult();
            await caller.CancelAsync();
            await Record.ExceptionAsync(() => pending.WaitAsync(TestContext.Current.CancellationToken));
            if (lateRead is { } read)
            {
                await Record.ExceptionAsync(() => read.WaitAsync(TestContext.Current.CancellationToken));
            }
        }

        async Task<int> ReadLateAsync(Memory<byte> buffer)
        {
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (lateFault)
            {
                throw new IOException("LATE_PRIVATE_FAILURE");
            }

            var bytes = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"LATE_PRIVATE_BODY\"}}");
            bytes.AsMemory().CopyTo(buffer);
            return bytes.Length;
        }
    }

    /// <summary>Partial progress shares the original diagnostic waiting budget.</summary>
    [Fact]
    public static async Task ErrorBody_SlowDripDoesNotRestartDiagnosticBudget()
    {
        // Arrange
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new RetryClock();
        var fragment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var stream = new ControlledStream(string.Empty, async (buffer, token) =>
        {
            if (++reads == 1)
            {
                await fragment.Task.WaitAsync(token);
                buffer.Span[0] = (byte)'{';
                return 1;
            }

            secondRead.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        using var client = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(stream) })))
        { Timeout = Timeout.InfiniteTimeSpan };
        var provider = new OpenAiCompatibleModelProvider(client, Profile(0), timeProvider: clock);
        var pending = provider.StreamAsync(Request(), caller.Token).ToListAsync(caller.Token).AsTask();
        try
        {
            await clock.DiagnosticsEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            // Act: deliver a fragment halfway through the single budget, then stall again.
            clock.Advance(TimeSpan.FromMilliseconds(50));
            fragment.TrySetResult();
            await secondRead.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(49));
            Assert.False(pending.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));

            // Assert
            var error = await Assert.ThrowsAsync<ModelProviderException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Contains("Error body: unavailable", error.Message, StringComparison.Ordinal);
            Assert.Equal(2, reads);
            Assert.True(stream.IsDisposed);
        }
        finally
        {
            fragment.TrySetResult();
            await caller.CancelAsync();
            await Record.ExceptionAsync(() => pending.WaitAsync(TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Immediate diagnostics retain sanitized explanations without starting a waiting timer.</summary>
    [Fact]
    public static async Task ErrorBody_ImmediateDiagnosticsRemainSanitizedWithoutTimer()
    {
        using var client = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Rejected DIAGNOSTIC_CREDENTIAL_CANARY\",\"code\":\"invalid_request\"},\"request_id\":\"req-immediate\"}"),
            })))
        { Timeout = Timeout.InfiniteTimeSpan };
        var clock = new RetryClock();
        var provider = new OpenAiCompatibleModelProvider(client, Profile(0), apiKey: "DIAGNOSTIC_CREDENTIAL_CANARY", timeProvider: clock);

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await provider.StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Rejected [REDACTED]", error.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", error.Message, StringComparison.Ordinal);
        Assert.Contains("Code: invalid_request", error.Message, StringComparison.Ordinal);
        Assert.Contains("Request ID: req-immediate", error.Message, StringComparison.Ordinal);
        Assert.False(clock.DiagnosticsEntered.Task.IsCompleted);
    }

    private sealed class DelayedOpeningContent : HttpContent
    {
        private readonly TaskCompletionSource<Stream> _opening;

        public DelayedOpeningContent(TaskCompletionSource<Stream> opening)
        {
            _opening = opening;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => _opening.Task.WaitAsync(TestContext.Current.CancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => _opening.Task.WaitAsync(TestContext.Current.CancellationToken);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
