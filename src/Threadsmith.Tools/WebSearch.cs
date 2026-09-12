namespace Threadsmith.Tools;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Provider-neutral bounded web-search request.</summary>
public sealed record WebSearchRequest
{
    /// <summary>Plain text disclosed to the provider, within the configured character bound and the provider's 75-word bound.</summary>
    public required string Query { get; init; }

    /// <summary>Requested result count, from one through twenty.</summary>
    public int MaximumResults { get; init; } = 5;

    /// <summary>Optional supported search language with a region hint, such as en-US or zh-Hant.</summary>
    public string? Locale { get; init; }

    /// <summary>Optional maximum result age in days, from one through 365.</summary>
    public int? FreshnessDays { get; init; }
}

/// <summary>One normalized, externally sourced and untrusted search result.</summary>
public sealed record WebSearchResult(
    string Title,
    string CanonicalUrl,
    string Snippet,
    int Rank,
    string Provider,
    string? SearchResultId = null);

/// <summary>Bounded normalized web-search response with source provenance.</summary>
public sealed record WebSearchResponse
{
    /// <summary>SHA-256 identity of the normalized query; the raw query is not retained.</summary>
    public required string QueryIdentity { get; init; }

    /// <summary>Compiled provider registration id.</summary>
    public required string ProviderId { get; init; }

    /// <summary>UTC retrieval time.</summary>
    public DateTimeOffset RetrievedAt { get; init; }

    /// <summary>Normalized untrusted results.</summary>
    public IReadOnlyList<WebSearchResult> Results { get; init; } = [];

    /// <summary>Whether provider output was reduced to host bounds.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>Mandatory instruction boundary for consumers.</summary>
    public required string TrustBoundary { get; init; }
}

/// <summary>Provider-neutral compiled web-search boundary.</summary>
public interface IWebSearchClient
{
    /// <summary>Searches an external index and returns normalized host-owned data.</summary>
    Task<WebSearchResponse> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Validated operational configuration for the compiled Brave Search adapter.</summary>
public sealed record WebSearchOptions
{
    /// <summary>Stable provider id.</summary>
    public string ProviderId { get; init; } = "brave";

    /// <summary>Allowlisted compiled provider kind.</summary>
    public string Kind { get; init; } = "brave";

    /// <summary>HTTPS search endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.search.brave.com/res/v1/web/search");

    /// <summary>Logical secret reference resolved only at the transport boundary.</summary>
    public string SecretReference { get; init; } = "secrets:BRAVE_SEARCH_API_KEY";

    /// <summary>Whole-operation timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum response bytes read from the provider.</summary>
    public int MaximumResponseBytes { get; init; } = 1_048_576;

    /// <summary>Maximum bounded retries for transient responses.</summary>
    public int RetryLimit { get; init; } = 1;

    /// <summary>Minimum interval between requests in this process.</summary>
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Maximum provider response JSON depth.</summary>
    public int MaximumJsonDepth { get; init; } = 16;

    /// <summary>Maximum query characters, at most the provider's 600-character limit.</summary>
    public int MaximumQueryCharacters { get; init; } = 500;

    /// <summary>Maximum requested freshness window in days.</summary>
    public int MaximumFreshnessDays { get; init; } = 365;

    /// <summary>Maximum normalized result title characters.</summary>
    public int MaximumTitleCharacters { get; init; } = 300;

    /// <summary>Maximum normalized result snippet characters.</summary>
    public int MaximumSnippetCharacters { get; init; } = 1000;

    /// <summary>Binds and validates trusted layered provider settings.</summary>
    public static WebSearchOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var kind = configuration["webSearch:provider:kind"] ?? "brave";
        if (!string.Equals(kind, "brave", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The configured web-search provider kind is not compiled into this host.");
        }

        var endpointText = configuration["webSearch:provider:endpoint"]
            ?? "https://api.search.brave.com/res/v1/web/search";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException("Web-search endpoints must be absolute HTTPS URLs without credentials.");
        }

        var timeoutSeconds = configuration.GetValue("webSearch:provider:timeoutSeconds", 15);
        var maximumBytes = configuration.GetValue("webSearch:provider:maximumResponseBytes", 1_048_576);
        var retries = configuration.GetValue("webSearch:provider:retryLimit", 1);
        var intervalMilliseconds = configuration.GetValue("webSearch:provider:minimumRequestIntervalMilliseconds", 200);
        if (timeoutSeconds <= 0
            || maximumBytes <= 0
            || retries < 0
            || intervalMilliseconds < 0)
        {
            throw new InvalidOperationException("Web-search provider limits are outside host bounds.");
        }

        var secretReference = configuration["webSearch:provider:secretReference"]
            ?? "secrets:BRAVE_SEARCH_API_KEY";
        if (!secretReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Web-search authentication must use a secrets: reference.");
        }

        var maximumQueryCharacters = ReadPositive("webSearch:provider:maximumQueryCharacters", 500);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumQueryCharacters, WebSearchRequestContract.MaximumQueryCharacters);
        return new WebSearchOptions
        {
            MaximumQueryCharacters = maximumQueryCharacters,
            ProviderId = configuration["webSearch:provider:id"] ?? "brave",
            Kind = "brave",
            MaximumJsonDepth = ReadPositive("webSearch:provider:maximumJsonDepth", 16),
            MaximumFreshnessDays = ReadPositive("webSearch:provider:maximumFreshnessDays", 365),
            MaximumTitleCharacters = ReadPositive("webSearch:provider:maximumTitleCharacters", 300),
            MaximumSnippetCharacters = ReadPositive("webSearch:provider:maximumSnippetCharacters", 1000),

            Endpoint = endpoint,
            SecretReference = secretReference,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            MaximumResponseBytes = maximumBytes,
            RetryLimit = retries,
            MinimumRequestInterval = TimeSpan.FromMilliseconds(intervalMilliseconds),
        };

        int ReadPositive(string key, int fallback)
        {
            var value = configuration.GetValue(key, fallback);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, key);
            return value;
        }
    }
}

/// <summary>Cancellable, bounded adapter for the compiled Brave Search HTTP API.</summary>
public sealed class BraveWebSearchClient : IWebSearchClient
{
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly WebSearchOptions _options;
    private readonly IPromptLoader _prompts;
    private readonly ISecretResolver _secretResolver;
    private DateTimeOffset _lastRequest;

    /// <summary>Initializes a new instance of the <see cref="BraveWebSearchClient"/> class.</summary>
    public BraveWebSearchClient(
        HttpClient httpClient,
        ISecretResolver secretResolver,
        WebSearchOptions options,
        IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        _httpClient = httpClient;
        _secretResolver = secretResolver;
        _options = options;
        _prompts = prompts;
    }

    /// <summary>Initializes a new instance of the <see cref="BraveWebSearchClient"/> class for legacy hosts and tests.</summary>
    public BraveWebSearchClient(
        HttpClient httpClient,
        ISecretStore secretStore,
        WebSearchOptions options,
        IPromptLoader prompts)
        : this(httpClient, new LegacySecretStoreResolver(secretStore), options, prompts)
    {
    }

    /// <inheritdoc />
    public async Task<WebSearchResponse> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        WebSearchRequestContract.Validate(request, _options.MaximumFreshnessDays, _options.MaximumQueryCharacters);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        var secretRequest = new SecretResolutionRequest
        {
            Reference = SecretReference.Parse(_options.SecretReference),
            ComponentId = "web-search:brave",
            Purpose = "authenticate a governed web-search request",
            MinimumTrust = SecretProviderTrust.UserOwned,
        };
        var secret = await _secretResolver.ResolveAsync(secretRequest, deadline.Token);
        var apiKey = secret.RequireValue(secretRequest);
        for (var attempt = 0; ; attempt++)
        {
            await ApplyRateLimitAsync(deadline.Token);
            using var message = CreateRequest(request, apiKey);
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token);
            ValidateFinalUri(response.RequestMessage?.RequestUri);
            if (IsTransient(response.StatusCode) && attempt < _options.RetryLimit)
            {
                await Task.Delay(GetRetryDelay(response, attempt), deadline.Token);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var payload = await ReadBoundedAsync(response.Content, deadline.Token);
            return Normalize(payload, request);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var providerDelay = response.Headers.RetryAfter?.Delta;
        return providerDelay is { } delay && delay <= TimeSpan.FromSeconds(2)
            ? delay
            : TimeSpan.FromMilliseconds(100 * (attempt + 1));
    }

    private HttpRequestMessage CreateRequest(WebSearchRequest request, string apiKey)
    {
        var parameters = new List<string>
        {
            "q=" + Uri.EscapeDataString(request.Query),
            "count=" + request.MaximumResults.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (request.Locale is not null)
        {
            var (language, country) = WebSearchRequestContract.ResolveLocale(request.Locale);
            parameters.Add("search_lang=" + Uri.EscapeDataString(language));
            if (country is not null)
            {
                parameters.Add("country=" + Uri.EscapeDataString(country));
            }
        }

        if (request.FreshnessDays is { } days)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var freshness = days switch
            {
                1 => "pd",
                7 => "pw",
                31 => "pm",
                365 => "py",
                _ => today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + "to" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            parameters.Add("freshness=" + Uri.EscapeDataString(freshness));
        }

        var builder = new UriBuilder(_options.Endpoint) { Query = string.Join('&', parameters) };
        var message = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        message.Headers.Add("X-Subscription-Token", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    private async Task ApplyRateLimitAsync(CancellationToken cancellationToken)
    {
        await _rateGate.WaitAsync(cancellationToken);
        try
        {
            var remaining = _options.MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequest);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
            }

            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateGate.Release();
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumResponseBytes)
        {
            throw new InvalidOperationException("The web-search provider response exceeded the configured byte limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > _options.MaximumResponseBytes)
            {
                throw new InvalidOperationException("The web-search provider response exceeded the configured byte limit.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return memory.ToArray();
    }

    private WebSearchResponse Normalize(byte[] payload, WebSearchRequest request)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = _options.MaximumJsonDepth });
        var results = document.RootElement.GetProperty("web").GetProperty("results");
        var normalized = new List<WebSearchResult>();
        foreach (var item in results.EnumerateArray().Take(request.MaximumResults))
        {
            if (!Uri.TryCreate(item.GetProperty("url").GetString(), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(url.UserInfo))
            {
                continue;
            }

            normalized.Add(new WebSearchResult(
                NormalizeText(item.GetProperty("title").GetString(), _options.MaximumTitleCharacters),
                url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                NormalizeText(item.TryGetProperty("description", out var description) ? description.GetString() : null, _options.MaximumSnippetCharacters),
                normalized.Count + 1,
                _options.ProviderId));
        }

        return new WebSearchResponse
        {
            QueryIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Query.Trim()))).ToLowerInvariant(),
            ProviderId = _options.ProviderId,
            RetrievedAt = DateTimeOffset.UtcNow,
            Results = normalized,
            IsTruncated = results.GetArrayLength() > normalized.Count,
            TrustBoundary = _prompts.Get(PromptFileNames.ToolWebSearchTrustBoundary),
        };
    }

    private static string NormalizeText(string? value, int maximumCharacters)
    {
        var plain = Regex.Replace(value ?? string.Empty, "<[^>]*>", " ", RegexOptions.CultureInvariant);
        plain = new string([.. plain.Where(character => !char.IsControl(character) || char.IsWhiteSpace(character))]);
        plain = Regex.Replace(plain, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return plain.Length <= maximumCharacters ? plain : plain[..maximumCharacters];
    }

    private void ValidateFinalUri(Uri? uri)
    {
        if (uri is null
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.Equals(uri.Host, _options.Endpoint.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The web-search provider redirected outside its configured HTTPS origin.");
        }
    }
}

/// <summary>Host-owned default-disabled governed web-search tool.</summary>
public sealed class WebSearchTool : Tool<WebSearchRequest, WebSearchResponse>, ITransientToolActivityDetail
{
    private readonly IWebSearchClient _client;
    private readonly WebSearchOptions _options;
    private readonly IOutputSanitizer _sanitizer;
    private readonly WebFetchAuthorizationAuthority? _fetchAuthorization;

    /// <summary>Initializes a new instance of the <see cref="WebSearchTool"/> class.</summary>
    public WebSearchTool(
        IWebSearchClient client,
        WebSearchOptions options,
        IOutputSanitizer sanitizer,
        IPromptLoader promptLoader,
        WebFetchAuthorizationAuthority? fetchAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(promptLoader);
        _client = client;
        _options = options;
        _sanitizer = sanitizer;
        _fetchAuthorization = fetchAuthorization;
        var definition = ToolDefinitionFactory.Create<WebSearchRequest, WebSearchResponse>(
            "web_search",
            promptLoader.Get(PromptFileNames.ToolWebSearchDescription),
            ToolCategory.ExternalSearch,
            RepositoryTrustLevel.UntrustedInspection,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            options.Timeout,
            128 * 1024) with
        {
            DisplayName = "Web Search",
            EnabledByDefault = false,
            RequiresOutboundConsent = true,
        };
        Definition = AddInputBounds(definition);
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<WebSearchResponse>> ExecuteAsync(
        WebSearchRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        var response = await _client.SearchAsync(input, cancellationToken);
        if (_fetchAuthorization is not null)
        {
            response = response with
            {
                Results = [.. response.Results.Select((result, index) => result with
                {
                    SearchResultId = TryIssueFetchReference(result, index, response, context),
                })],
            };
        }

        return new ToolExecution<WebSearchResponse>(
            response,
            [.. response.Results.Select(result => new ToolProvenanceSource(
                "external-web-search-untrusted",
                result.CanonicalUrl,
                $"provider={response.ProviderId};query={response.QueryIdentity};rank={result.Rank};retrieved={response.RetrievedAt:O}"))],
            response.IsTruncated);
    }

    /// <inheritdoc />
    string? ITransientToolActivityDetail.GetTransientActivityDetail(object input, ToolExecutionContext context)
    {
        return ((WebSearchRequest)input).Query;
    }

    /// <inheritdoc />
    protected override void ValidateInput(WebSearchRequest input)
    {
        WebSearchRequestContract.Validate(input, _options.MaximumFreshnessDays, _options.MaximumQueryCharacters);
        RejectSensitiveQuery(input.Query);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetSecretReferences(WebSearchRequest input)
    {
        return [_options.SecretReference];
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetNetworkHosts(WebSearchRequest input)
    {
        return [_options.Endpoint.Host];
    }

    private ToolDefinition AddInputBounds(ToolDefinition definition)
    {
        var schema = JsonNode.Parse(definition.InputSchema.JsonSchema)?.AsObject()
            ?? throw new InvalidOperationException("The generated web-search schema was empty.");
        var properties = schema["properties"]?.AsObject()
            ?? throw new InvalidOperationException("The generated web-search schema has no properties.");
        var query = properties["query"]?.AsObject()
            ?? throw new InvalidOperationException("The generated web-search schema has no query.");
        query["minLength"] = 1;
        query["maxLength"] = _options.MaximumQueryCharacters;

        // Keep the advertised expression usable by finite-state generation grammars.
        // The host independently enforces all query bounds before credentials or HTTP.
        query["pattern"] = @"^[^\S\u0000-\u001F\u007F-\u009F]*"
            + @"[^\s\u0000-\u001F\u007F-\u009F]+(?:[^\S\u0000-\u001F\u007F-\u009F]+[^\s\u0000-\u001F\u007F-\u009F]+){0,74}"
            + @"[^\S\u0000-\u001F\u007F-\u009F]*$";
        var count = properties["maximumResults"]?.AsObject()
            ?? throw new InvalidOperationException("The generated web-search schema has no result count.");
        count["minimum"] = 1;
        count["maximum"] = WebSearchRequestContract.MaximumResults;
        count["default"] = 5;
        var locale = properties["locale"]?.AsObject()
            ?? throw new InvalidOperationException("The generated web-search schema has no locale.");
        locale["pattern"] = WebSearchRequestContract.LocalePattern;
        var freshness = properties["freshnessDays"]?.AsObject()
            ?? throw new InvalidOperationException("The generated web-search schema has no freshness window.");
        freshness["minimum"] = 1;
        freshness["maximum"] = _options.MaximumFreshnessDays;
        return definition with
        {
            InputSchema = definition.InputSchema with { JsonSchema = schema.ToJsonString() },
        };
    }

    private string? TryIssueFetchReference(
        WebSearchResult result,
        int index,
        WebSearchResponse response,
        ToolExecutionContext context)
    {
        try
        {
            return _fetchAuthorization?.IssueSearchResult(
                context.Invocation.RepositoryPath,
                new Uri(result.CanonicalUrl, UriKind.Absolute),
                context.SessionId,
                context.RunId,
                context.ToolInvocationId,
                response.ProviderId,
                response.QueryIdentity,
                index + 1);
        }
        catch (WebFetchException exception) when (exception.Kind == WebFetchFailureKind.InvalidRequest)
        {
            return null;
        }
    }

    private void RejectSensitiveQuery(string query)
    {
        if (!string.Equals(query, _sanitizer.Sanitize(query), StringComparison.Ordinal))
        {
            throw new ToolArgumentValidationException("The outbound search query was blocked by the sensitive-data policy.");
        }
    }
}
