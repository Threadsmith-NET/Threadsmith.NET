namespace Threadsmith.Models.Anthropic;

using System.Runtime.CompilerServices;
using System.Text.Json;
using global::Anthropic.Exceptions;
using Threadsmith.Models;

/// <summary>Owns one native request per host round, with a single deadline and classified retry loop.</summary>
internal sealed class AnthropicModelProvider : IModelProvider
{
    private readonly string _apiKey;
    private readonly AnthropicModelCompatibility _compatibility;
    private readonly HttpClient _httpClient;
    private readonly ModelProfile _profile;
    private readonly string _providerId;

    /// <summary>Initializes a new instance of the <see cref="AnthropicModelProvider"/> class.</summary>
    internal AnthropicModelProvider(HttpClient httpClient, ModelProfile profile, string apiKey, AnthropicModelCompatibility compatibility, string? providerId = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(compatibility);
        _httpClient = httpClient;
        _profile = profile;
        _apiKey = apiKey;
        _compatibility = compatibility;
        _providerId = providerId ?? profile.Provider;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var offeredEnvelopes = new List<ModelResponseReplayEnvelope>();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_profile.Timeout > TimeSpan.Zero)
            {
                deadline.CancelAfter(_profile.Timeout);
            }

            request = Prepare(request);
            if (deadline.IsCancellationRequested)
            {
                throw Classify(new OperationCanceledException(), deadline, cancellationToken).Exception;
            }

            var parameters = AnthropicRequestMapper.Create(request, _profile, _compatibility);
            for (var attempt = 1; attempt <= _profile.RetryPolicy.MaxAttempts; attempt++)
            {
                using var client = AnthropicSdkClientFactory.Create(_httpClient, _apiKey, _profile.Timeout, _profile.MaximumStreamedBytes, submissionObserver: request.SubmissionObserver);
                var adapter = new AnthropicStreamAdapter(request, _profile, _compatibility, _providerId, _apiKey);
                await using var events = client.Messages.CreateStreaming(parameters, deadline.Token).GetAsyncEnumerator(deadline.Token);
                var observed = false;
                Failure? failure = null;
                while (true)
                {
                    IReadOnlyList<ModelChunk> chunks = [];
                    var more = false;
                    try
                    {
                        more = await events.MoveNextAsync();
                        if (more)
                        {
                            observed = true;
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

                        yield return chunk;
                    }

                    if (!more)
                    {
                        yield break;
                    }
                }

                if (failure is { Retry: true } && !observed && attempt < _profile.RetryPolicy.MaxAttempts)
                {
                    try
                    {
                        await Task.Delay(failure.Delay ?? _profile.RetryPolicy.Delay, deadline.Token);
                    }
                    catch (OperationCanceledException exception)
                    {
                        throw Classify(exception, deadline, cancellationToken).Exception;
                    }

                    continue;
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
                    yield return new ModelChunk { Usage = partial };
                }

                throw failure?.Exception ?? new ModelProviderException("Anthropic request failed without a terminal result.");
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

    private static bool IsProviderFailure(Exception exception) => exception is AnthropicException or AnthropicHttpFailureException
        or HttpRequestException or IOException or OperationCanceledException or JsonException or ModelProviderException
        or MalformedModelOutputException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException;

    private static Failure Classify(Exception exception, CancellationTokenSource deadline, CancellationToken caller)
    {
        if (caller.IsCancellationRequested)
        {
            return new Failure(new OperationCanceledException(caller), false, null);
        }

        if (deadline.IsCancellationRequested || exception is OperationCanceledException)
        {
            return new Failure(new ModelProviderTimeoutException("The Anthropic request exceeded its total deadline."), false, null);
        }

        // Some SDK paths wrap transport exceptions. Inspect only type and status; never copy their messages.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AnthropicHttpFailureException http)
            {
                var status = (int)http.StatusCode;
                var retry = status is 408 or 409 or 429 or (>= 500 and <= 599);
                Exception safe = retry
                    ? new TransientModelException($"Anthropic request failed with retryable HTTP status {status}.")
                    : new ModelProviderException($"Anthropic request was rejected with HTTP status {status}.");
                return new Failure(safe, retry, http.RetryAfter);
            }

            if (current is HttpRequestException or IOException)
            {
                return new Failure(new TransientModelException("Anthropic connection failed."), true, null);
            }

            if (current is ModelProviderException or MalformedModelOutputException)
            {
                return new Failure(current, false, null);
            }
        }

        return exception switch
        {
            HttpRequestException or IOException => new Failure(new TransientModelException("Anthropic connection failed."), true, null),
            JsonException or KeyNotFoundException => new Failure(new MalformedModelOutputException("Anthropic returned malformed or incomplete protocol JSON."), false, null),
            _ => new Failure(new ModelProviderException("Anthropic returned an invalid or unsupported response."), false, null),
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

    private sealed record Failure(Exception Exception, bool Retry, TimeSpan? Delay);
}
