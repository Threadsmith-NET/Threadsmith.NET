namespace Threadsmith.Models.Anthropic;

using System.Runtime.CompilerServices;
using System.Text.Json;
using global::Anthropic.Exceptions;
using Threadsmith.Models;

/// <summary>Owns one native request per host round, with a single deadline and classified retry loop.</summary>
internal sealed class AnthropicModelProvider : IModelProvider
{
    private readonly TimeProvider _timeProvider;
    private readonly AnthropicResourceLimits _limits;
    private readonly string _apiKey;
    private readonly AnthropicModelCompatibility _compatibility;
    private readonly HttpClient _httpClient;
    private readonly ModelProfile _profile;
    private readonly string _providerId;

    /// <summary>Initializes a new instance of the <see cref="AnthropicModelProvider"/> class.</summary>
    internal AnthropicModelProvider(HttpClient httpClient, ModelProfile profile, string apiKey, AnthropicModelCompatibility compatibility, string? providerId = null, AnthropicResourceLimits? limits = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(compatibility);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _limits = limits ?? new();
        _limits.Validate();
        _httpClient = httpClient;
        _profile = profile;
        _apiKey = apiKey;
        _compatibility = compatibility;
        _providerId = providerId ?? profile.Provider;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ModelChunk> StreamAsync(
        ModelStreamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return StreamCoreAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<ModelChunk> StreamCoreAsync(
        ModelStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var offeredEnvelopes = new List<ModelResponseReplayEnvelope>();
        try
        {
            using var totalDeadline = new CancellationTokenSource(_profile.Timeout > TimeSpan.Zero ? _profile.Timeout : Timeout.InfiniteTimeSpan, _timeProvider);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalDeadline.Token);

            request = Prepare(request);
            if (deadline.IsCancellationRequested)
            {
                throw Classify(new OperationCanceledException(), deadline, cancellationToken);
            }

            request = await ModelImages.ResolveAsync(request, _profile, deadline.Token);
            var parameters = AnthropicRequestMapper.Create(request, _profile, _compatibility);
            ModelUsage? interruptedUsage = null;
            await foreach (var chunk in ModelStreamResilience.ExecuteAsync(
                (_, token) => SendAttemptAsync(token), _profile.RetryPolicy, _timeProvider, _profile.Timeout, deadline.Token, cancellationToken))
            {
                yield return chunk;
            }

            async IAsyncEnumerable<ModelChunk> SendAttemptAsync([EnumeratorCancellation] CancellationToken token)
            {
                var adapter = new AnthropicStreamAdapter(request, _profile, _compatibility, _providerId, _apiKey, _limits);
                Exception? failure = null;
                using (var client = AnthropicSdkClientFactory.Create(_httpClient, _apiKey, Timeout.InfiniteTimeSpan, _profile.MaximumStreamedBytes, submissionObserver: request.SubmissionObserver, maximumSseFrameBytes: _limits.MaximumSseFrameBytes, timeProvider: _timeProvider))
                await using (var events = client.Messages.CreateStreaming(parameters, token).GetAsyncEnumerator(token))
                {
                    while (true)
                    {
                        IReadOnlyList<ModelChunk> chunks = [];
                        var more = false;
                        try
                        {
                            more = await events.MoveNextAsync();
                            if (more)
                            {
                                chunks = adapter.Accept(events.Current);
                            }
                            else
                            {
                                chunks = adapter.Complete();
                            }
                        }
                        catch (Exception exception) when (IsProviderFailure(exception))
                        {
                            failure = Classify(exception, deadline, cancellationToken);
                        }

                        if (failure is not null)
                        {
                            break;
                        }

                        foreach (var chunk in chunks)
                        {
                            if (chunk.ResponseEnvelope is { } envelope)
                            {
                                offeredEnvelopes.Add(envelope);
                            }

                            yield return chunk.Usage is { } usage
                                ? chunk with { Usage = ModelStreamRetry.CombineUsage(interruptedUsage, usage) }
                                : chunk;
                        }

                        if (!more)
                        {
                            yield break;
                        }
                    }
                }

                ModelUsage? partial = null;
                try
                {
                    partial = adapter.PartialUsage();
                }
                catch (Exception exception) when (exception is ModelProviderException or MalformedModelOutputException or OverflowException)
                {
                    // Invalid counters cannot be emitted. The original terminal failure remains authoritative.
                }

                if (partial is not null)
                {
                    interruptedUsage = ModelStreamRetry.CombineUsage(interruptedUsage, partial);
                    yield return new ModelChunk { Usage = interruptedUsage, IsInterruptedUsage = true };
                }

                throw failure ?? new ModelProviderException("Anthropic request failed without a terminal result.");
            }
        }
        finally
        {
            foreach (var envelope in offeredEnvelopes.Where(envelope => !envelope.IsRetained))
            {
                envelope.Dispose();
            }
        }
    }

    private static bool IsProviderFailure(Exception exception)
    {
        return exception is AnthropicException or AnthropicHttpFailureException
        or HttpRequestException or IOException or OperationCanceledException or JsonException or ModelProviderException or TransientModelException
        or MalformedModelOutputException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException;
    }

    private Exception Classify(Exception exception, CancellationTokenSource deadline, CancellationToken caller)
    {
        if (caller.IsCancellationRequested)
        {
            return new OperationCanceledException(caller);
        }

        if (deadline.IsCancellationRequested || exception is OperationCanceledException)
        {
            return new ModelProviderTimeoutException("The Anthropic request exceeded its total deadline.");
        }

        // Some SDK paths wrap transport exceptions. Parse only structured SSE fields, never SDK ToString().
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AnthropicSseException sse)
            {
                try
                {
                    // The pinned SDK wraps the original SSE JSON in this fixed exception prefix.
                    const string prefix = "SSE error returned from server: '";
                    var payload = sse.Message;
                    if (payload.StartsWith(prefix, StringComparison.Ordinal) && payload.EndsWith('\''))
                    {
                        payload = payload[prefix.Length..^1];
                    }

                    using var document = JsonDocument.Parse(payload);
                    var details = ProviderErrorDetails.Format(document.RootElement, _apiKey);
                    var rateLimit = sse.ErrorType == global::Anthropic.Models.ErrorType.RateLimitError;
                    return rateLimit
                        ? new TransientModelException(ProviderErrorDetails.PreferMessage(details, "Anthropic stream reported a rate limit (code 429).")) { IsRateLimit = true }
                        : new ModelProviderException(ProviderErrorDetails.PreferMessage(details, "Anthropic returned an invalid or unsupported response."));
                }
                catch (JsonException)
                {
                    return new ModelProviderException("Anthropic returned an invalid or unsupported response.");
                }
            }

            if (current is AnthropicHttpFailureException http)
            {
                var status = (int)http.StatusCode;
                var retry = status is 408 or 409 or 429 or (>= 500 and <= 599);
                Exception safe = retry
                    ? new TransientModelException(ProviderErrorDetails.PreferMessage(http.Details, $"Anthropic request failed with retryable HTTP status {status}. ")) { IsRateLimit = status == 429, RetryAfter = http.RetryAfter }
                    : new ModelProviderException(ProviderErrorDetails.PreferMessage(http.Details, $"Anthropic request was rejected with HTTP status {status}. "));
                return safe;
            }

            if (current is HttpRequestException or IOException)
            {
                return new TransientModelException($"Anthropic connection failed ({TransportFailureKind(current)}).");
            }

            if (current is TransientModelException)
            {
                return current;
            }

            if (current is ModelProviderException or MalformedModelOutputException)
            {
                return current;
            }
        }

        return exception switch
        {
            HttpRequestException or IOException => new TransientModelException("Anthropic connection failed."),
            JsonException or KeyNotFoundException => new MalformedModelOutputException("Anthropic returned malformed or incomplete protocol JSON."),
            _ => new ModelProviderException("Anthropic returned an invalid or unsupported response."),
        };
    }

    private static string TransportFailureKind(Exception exception)
    {
        return exception switch
        {
            HttpIOException io => $"HTTP {io.HttpRequestError}",
            HttpRequestException http => $"HTTP {http.HttpRequestError}",
            _ => "I/O failure",
        };
    }

    private ModelStreamRequest Prepare(ModelStreamRequest request)
    {
        try
        {
            request = request with { ResolvedProfileId = request.ResolvedProfileId ?? _profile.Id };
            AnthropicReplayIdentity.Validate(request, _profile, _compatibility, _providerId, _apiKey);
            var preparation = AnthropicRequestPreparer.Prepare(request, _profile, _compatibility, _providerId);
            if (request.Preparation is { } admitted && admitted.WireDigest != preparation.WireDigest)
            {
                throw new ModelProviderException("Anthropic request changed after capacity preparation; reassemble before submission.");
            }

            if (preparation.WireEstimate.TotalCapacityTokens > _profile.ContextWindow)
            {
                throw new ModelProviderException("Anthropic native request, private replay, and output reserve exceed the selected context window.");
            }

            return request with { Preparation = preparation, WireEstimate = preparation.WireEstimate, CacheCapabilities = preparation.CacheCapabilities, CachePlan = preparation.CachePlan };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            throw new ModelProviderException("Anthropic request projection failed local schema or capacity validation.");
        }
    }
}
