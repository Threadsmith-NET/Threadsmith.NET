namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text.Json;
using Threadsmith.Models;
using Xunit;

public static partial class OpenRouterProviderTests
{
    /// <summary>Transport categories survive both failure phases and the existing retry budget without exposing upstream prose.</summary>
    [Theory]
    [InlineData(false, HttpRequestError.NameResolutionError)]
    [InlineData(false, HttpRequestError.ConnectionError)]
    [InlineData(false, HttpRequestError.HttpProtocolError)]
    [InlineData(false, HttpRequestError.ResponseEnded)]
    [InlineData(true, HttpRequestError.NameResolutionError)]
    [InlineData(true, HttpRequestError.ConnectionError)]
    [InlineData(true, HttpRequestError.HttpProtocolError)]
    [InlineData(true, HttpRequestError.ResponseEnded)]
    public static async Task TransportFailure_PreservesSafeCategoryAfterRetries(bool bodyRead, HttpRequestError category)
    {
        var failure = new HttpRequestException(category, "private-upstream-message https://private.invalid/?credential=secret");
        using var handler = new TransportFailureHandler(failure, bodyRead);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var model = Model() with { RetryMaxAttempts = 3, RetryDelayMilliseconds = 0 };
        var chunks = new List<ModelChunk>();

        var error = await Assert.ThrowsAsync<TransientModelException>(() => CollectAsync(Provider(client, model), Request(), chunks));

        var expectedMessage = bodyRead ? $"OpenRouter connection ended before completion (HTTP {category})."
            : $"OpenRouter transport remained unavailable after 3 attempts (HTTP {category}).";
        Assert.Equal(expectedMessage, error.Message);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(chunks), StringComparison.Ordinal);
        Assert.Empty(chunks);
        Assert.Equal(3, handler.Calls);
    }

    /// <summary>HTTP I/O errors and wrapped categories retain bounded diagnostics; unclassified I/O stays generic.</summary>
    [Theory]
    [InlineData("http-io", "HTTP ResponseEnded")]
    [InlineData("wrapped-http", "HTTP ConnectionError")]
    [InlineData("io", "I/O failure")]
    public static async Task ResponseBodyFailure_ClassifiesIoWithoutCopyingMessages(string kind, string category)
    {
        const string privateMessage = "private-response-body";
        var failure = kind switch
        {
            "http-io" => new HttpIOException(HttpRequestError.ResponseEnded, privateMessage),
            "wrapped-http" => new IOException(privateMessage, new HttpRequestException(HttpRequestError.ConnectionError, privateMessage)),
            _ => new IOException(privateMessage),
        };
        using var handler = new TransportFailureHandler(failure, bodyRead: true);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        var error = await Assert.ThrowsAsync<TransientModelException>(() => CollectAsync(Provider(client, Model() with { RetryMaxAttempts = 3, RetryDelayMilliseconds = 0 }), Request(), chunks));

        Assert.Equal($"OpenRouter connection ended before completion ({category}).", error.Message);
        Assert.DoesNotContain(privateMessage, error.Message, StringComparison.Ordinal);
        Assert.Empty(chunks);
        Assert.Equal(3, handler.Calls);
    }

    /// <summary>Restoring diagnostics does not turn TLS or authentication faults into retryable failures.</summary>
    [Theory]
    [InlineData(false, HttpRequestError.SecureConnectionError)]
    [InlineData(true, HttpRequestError.SecureConnectionError)]
    [InlineData(false, HttpRequestError.UserAuthenticationError)]
    [InlineData(true, HttpRequestError.UserAuthenticationError)]
    public static async Task NontransientTransportFailure_DoesNotRetry(bool bodyRead, HttpRequestError category)
    {
        using var handler = new TransportFailureHandler(new HttpRequestException(category, "fixture"), bodyRead);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CollectAsync(Provider(client, Model() with { RetryMaxAttempts = 3, RetryDelayMilliseconds = 0 }), Request(), chunks));

        Assert.Equal(category, error.HttpRequestError);
        Assert.Empty(chunks);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class TransportFailureHandler : HttpMessageHandler
    {
        private readonly Exception _failure;
        private readonly bool _bodyRead;

        public TransportFailureHandler(Exception failure, bool bodyRead)
        {
            _failure = failure;
            _bodyRead = bodyRead;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return _bodyRead ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TransportFailureStream(_failure)) })
                : Task.FromException<HttpResponseMessage>(_failure);
        }
    }

    private sealed class TransportFailureStream : MemoryStream
    {
        private readonly Exception _failure;

        public TransportFailureStream(Exception failure) => _failure = failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<int>(_failure);
        }
    }
}
