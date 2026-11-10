namespace Threadsmith.Models.OpenAiCompatible.OpenRouter;

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Threadsmith.Models;

/// <summary>Streams native OpenRouter Responses through the shared host resilience lifecycle.</summary>
internal sealed class OpenRouterModelProvider : IModelProvider
{
    private readonly HttpClient _client;
    private readonly ModelProfile _profile;
    private readonly string _secret;
    private readonly OpenRouterModelConfiguration _model;
    private readonly TimeProvider _timeProvider;
    private readonly string _protocol;

    /// <summary>Initializes a new instance of the <see cref="OpenRouterModelProvider"/> class.</summary>
    internal OpenRouterModelProvider(HttpClient client, ModelProfile profile, string secret, string providerId, OpenRouterModelConfiguration model, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(model);
        _client = client;
        _profile = profile;
        _secret = secret;
        _model = model;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _protocol = "openrouter-responses-v1:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { providerId, model })));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ResolvedProfileId is { } id && id != _profile.Id)
        {
            throw new ModelProviderException("The resolved model profile does not match the OpenRouter adapter.");
        }

        if (!_profile.Capabilities.Streaming || (request.Tools.Count > 0 && !_profile.Capabilities.ToolCalls))
        {
            throw new InvalidOperationException("The OpenRouter profile does not advertise the required streaming/tool capability.");
        }

        if (request.ContainsSensitiveData && _profile.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed)
        {
            throw new ModelProviderException("The OpenRouter profile prohibits sensitive request content.");
        }

        var maximum = request.MaximumOutputTokens ?? _model.EffectiveRequestOutputTokenReserve;
        if (maximum <= 0 || maximum > _profile.MaximumOutputTokens)
        {
            throw new ModelProviderException("The requested output ceiling exceeds the resolved profile limits.");
        }

        return StreamCoreAsync(request, maximum, cancellationToken);
    }

    /// <summary>Uses dispatch projection with size-only image placeholders before credentials are resolved.</summary>
    internal static byte[] PrepareWire(ModelStreamRequest request, ModelProfile profile, OpenRouterModelConfiguration model)
    {
        var images = request.Messages.SelectMany(message => message.Content)
            .Where(part => part.IsModelVisible && part.Image is not null)
            .Select(part => part.Image ?? throw new ModelProviderException("Missing image metadata."))
            .DistinctBy(image => image.Id).ToDictionary(image => image.Id, _ => ReadOnlyMemory<byte>.Empty);
        return OpenRouterResponsesProjection.Create(request with { ImageData = images }, profile, model);
    }

    private async IAsyncEnumerable<ModelChunk> StreamCoreAsync(ModelStreamRequest request, int maximum, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request = request with { ResolvedProfileId = request.ResolvedProfileId ?? _profile.Id };
        if (!_model.SupportsReasoningReplay && request.TransientState is { HasResponses: true })
        {
            throw new ModelProviderException("This model does not support private reasoning replay; start a fresh turn.");
        }

        ProviderResponseReplay.Validate(request, _profile, _secret, _protocol);
        var capacity = ProviderResponseReplay.Estimate(request, _profile).TotalCapacityTokens;
        if (Math.Max(capacity, request.WireEstimate?.TotalCapacityTokens ?? 0) > _profile.ContextWindow)
        {
            throw new ModelProviderException("OpenRouter input and output reserve exceed the selected context window.");
        }

        request = await ModelImages.ResolveAsync(request, _profile, cancellationToken);
        var names = ModelToolWireNameMap.Create(ModelToolCanonicalizer.Canonicalize(request.Tools));
        using var deadline = _profile.Timeout > TimeSpan.Zero ? new CancellationTokenSource(_profile.Timeout, _timeProvider) : null;
        using var linked = deadline is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var content = OpenRouterResponsesProjection.Create(request, _profile, _model);
        ModelUsage? interruptedUsage = null;
        try
        {
            await foreach (var chunk in ModelStreamResilience.ExecuteAsync(SendAttemptAsync, _profile.RetryPolicy, _timeProvider, _profile.Timeout, linked.Token, cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }

        async IAsyncEnumerable<ModelChunk> SendAttemptAsync(int attempt, [EnumeratorCancellation] CancellationToken token)
        {
            var previousUsage = interruptedUsage;
            using var message = new HttpRequestMessage(HttpMethod.Post, _profile.Endpoint);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            message.Content = new ReadOnlyMemoryContent(content);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            HttpResponseMessage response;
            try
            {
                token.ThrowIfCancellationRequested();
                var pending = _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                request.SubmissionObserver?.Invoke();
                response = await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw token.IsCancellationRequested ? TimeoutFailure(exception)
                    : new TransientModelException("OpenRouter transport was cancelled before response headers.", exception);
            }
            catch (HttpRequestException exception) when (IsTransient(exception))
            {
                throw new TransientModelException($"OpenRouter transport remained unavailable after {attempt} attempts.", exception);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable || (int)response.StatusCode == 529)
                    {
                        throw new TransientModelException($"OpenRouter remained unavailable after {attempt} attempts (HTTP {(int)response.StatusCode}).")
                        {
                            IsRateLimit = response.StatusCode == HttpStatusCode.TooManyRequests,
                            RetryAfter = ModelStreamRetry.ReadRetryAfter(response.Headers, _timeProvider.GetUtcNow()),
                        };
                    }

                    throw new ModelProviderException($"OpenRouter rejected the request with HTTP {(int)response.StatusCode}.");
                }

                await using var chunks = new OpenRouterResponsesStream(_profile, _model, _secret, _protocol).ReadAsync(response, request, names, maximum, token).GetAsyncEnumerator(token);
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await chunks.MoveNextAsync();
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw token.IsCancellationRequested ? TimeoutFailure(exception)
                            : new TransientModelException("OpenRouter transport was cancelled while reading the response.", exception);
                    }
                    catch (Exception exception) when (exception is IOException || (exception is HttpRequestException http && IsTransient(http)))
                    {
                        token.ThrowIfCancellationRequested();
                        throw new TransientModelException("OpenRouter connection ended before completion.", exception);
                    }

                    if (!hasNext)
                    {
                        yield break;
                    }

                    var chunk = chunks.Current;
                    if (chunk.Usage is { } usage)
                    {
                        var cumulative = ModelStreamRetry.CombineUsage(previousUsage, usage);
                        interruptedUsage = cumulative;
                        chunk = chunk with { Usage = cumulative };
                    }

                    yield return chunk;
                }
            }
        }
    }

    private static bool IsTransient(HttpRequestException exception) => exception.HttpRequestError is HttpRequestError.NameResolutionError
        or HttpRequestError.ConnectionError or HttpRequestError.HttpProtocolError or HttpRequestError.ResponseEnded;

    private ModelProviderTimeoutException TimeoutFailure(Exception exception) => new($"Model profile '{_profile.Name}' exceeded its {_profile.Timeout} request timeout.", exception);
}
