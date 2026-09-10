namespace Threadsmith.Models.Anthropic;

using System.Net;
using Threadsmith.Models;

/// <summary>Safe status metadata without provider bodies, headers, or credentials.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Internal transport status carrier requires a status and deliberately excludes raw exception bodies.")]
internal sealed class AnthropicHttpFailureException(HttpStatusCode statusCode, TimeSpan? retryAfter)
    : Exception("Anthropic HTTP request failed.")
{
    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Forwards direct API traffic without owning the host HTTP pool.</summary>
internal sealed class AnthropicBorrowedHttpHandler : HttpMessageHandler
{
    private readonly HttpClient _sharedHttpClient;
    private readonly long _maximumResponseBytes;
    private readonly Action<long>? _responseBytesObserved;
    private readonly Action? _submissionObserver;

    /// <summary>Initializes a new instance of the <see cref="AnthropicBorrowedHttpHandler"/> class.</summary>
    internal AnthropicBorrowedHttpHandler(HttpClient sharedHttpClient, long maximumResponseBytes = 1048576, Action<long>? responseBytesObserved = null, Action? submissionObserver = null)
    {
        ArgumentNullException.ThrowIfNull(sharedHttpClient);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResponseBytes);
        _sharedHttpClient = sharedHttpClient;
        _maximumResponseBytes = maximumResponseBytes;
        _responseBytesObserved = responseBytesObserved;
        _submissionObserver = submissionObserver;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, AnthropicSdkClientFactory.ApiBaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443 || uri.UserInfo.Length != 0
            || (request.Method == HttpMethod.Post
                ? uri.AbsolutePath is not ("/v1/messages" or "/v1/messages/count_tokens")
                : request.Method != HttpMethod.Get || uri.AbsolutePath != "/v1/models"))
        {
            throw new ModelProviderException("The Anthropic SDK attempted a request outside its compiled API surfaces.");
        }

        // HttpClient marks a message as sent before dispatching handlers. A second client needs its own
        // message and content ownership, while every authority/header value still comes from the SDK request.
        using var forwarded = new HttpRequestMessage(request.Method, uri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach (var header in request.Headers)
        {
            forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is { } requestContent)
        {
            forwarded.Content = new ByteArrayContent(await requestContent.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in requestContent.Headers)
            {
                forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _submissionObserver?.Invoke();
        var response = await _sharedHttpClient.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                // Error bodies are never read, parsed, or copied into an exception.
                var delay = response.Headers.RetryAfter?.Delta;
                if (delay is null && response.Headers.RetryAfter?.Date is { } date)
                {
                    delay = date - DateTimeOffset.UtcNow;
                }

                throw new AnthropicHttpFailureException(response.StatusCode, delay is { } value ? TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, 0, 30000)) : null);
            }

            var original = response.Content;
            if (_maximumResponseBytes > 0 && original.Headers.ContentLength > _maximumResponseBytes)
            {
                throw new ModelProviderException("Anthropic response exceeded its byte ceiling.");
            }

            var stream = await original.ReadAsStreamAsync(cancellationToken);

            // The host invokes streaming Messages on this surface. Untrusted response media headers
            // cannot disable the frame ceiling that must run before the SDK's SSE parser.
            var expectsSse = request.Method == HttpMethod.Post && uri.AbsolutePath == "/v1/messages";
            var bounded = new StreamContent(new AnthropicBoundedReadStream(stream, original, _maximumResponseBytes, _responseBytesObserved, expectsSse));
            foreach (var header in original.Headers)
            {
                bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = bounded;
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}


