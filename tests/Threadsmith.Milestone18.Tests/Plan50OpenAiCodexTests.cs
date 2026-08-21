namespace Threadsmith.Milestone18.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCodex;
using Xunit;

/// <summary>Plan 50 native Codex provider and OAuth acceptance coverage.</summary>
public sealed class Plan50OpenAiCodexTests
{
    /// <summary>Authenticated discovery projects every distinct model returned by the backend.</summary>
    [Fact]
    public async Task Discovery_ProjectsEveryReturnedModelWithoutCompiledList()
    {
        const string response = """
            {"models":[
              {"slug":"codex-a","display_name":"Codex A","context_window":128000,"default_reasoning_level":"medium","supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"}]},
              {"slug":"future-model","display_name":"Future Model","max_context_window":272000,"default_reasoning_level":"high","supported_reasoning_levels":[{"effort":"high"}]}
            ]}
            """;
        var handler = new RecordingHandler(_ => JsonResponse(response));
        var client = new OpenAiCodexCatalogClient(new HttpClient(handler));

        var catalog = await client.DiscoverAsync(
            CreateJwt("account-1"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, catalog.Models.Count);
        Assert.Contains(catalog.Models, model => model.Name == "Future Model");
        Assert.Contains("client_version=", handler.Request?.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Equal("account-1", handler.Request?.Headers.GetValues("ChatGPT-Account-Id").Single());
    }

    /// <summary>Catalog snapshots contain metadata only and round-trip dynamic models.</summary>
    [Fact]
    public async Task CatalogCache_RoundTripsDynamicModelsWithoutToken()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-codex-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "models.json");
        var cache = new OpenAiCodexCatalogCache(path);
        var configuration = await new OpenAiCodexCatalogClient(
            new HttpClient(new RecordingHandler(_ => JsonResponse(
                "{\"models\":[{\"slug\":\"dynamic\",\"display_name\":\"Dynamic\",\"context_window\":128000}]}"))))
            .DiscoverAsync("not-a-jwt", cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            await cache.SaveAsync(configuration, TestContext.Current.CancellationToken);
            var loaded = await cache.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal("dynamic", Assert.IsType<OpenAiCodexModelConfiguration>(Assert.Single(loaded?.Models ?? [])).ModelId);
            Assert.DoesNotContain("not-a-jwt", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Malformed catalog payloads are ignored before model projection.</summary>
    [Theory]
    [InlineData("{\"SchemaVersion\":1,\"Models\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Models\":[{}]}")]
    public async Task CatalogCache_MalformedPayload_ReturnsNull(string payload)
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-codex-cache-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "models.json");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(path, payload, TestContext.Current.CancellationToken);
            var cache = new OpenAiCodexCatalogCache(path);

            var loaded = await cache.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Null(loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Browser OAuth uses protected OpenAI authorities, PKCE, state, and loopback only.</summary>
    [Fact]
    public void BrowserChallenge_IsProtectedAndRejectsNonLoopbackRedirect()
    {
        var challenge = OpenAiCodexOAuthManager.CreateBrowserChallenge(
            new Uri("http://localhost:1455/auth/callback"));

        Assert.Equal("auth.openai.com", challenge.AuthorizationUri.Host);
        Assert.Contains("code_challenge_method=S256", challenge.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.Contains("state=", challenge.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => OpenAiCodexOAuthManager.CreateBrowserChallenge(
            new Uri("https://attacker.example/callback")));
    }

    /// <summary>Browser completion validates state, exchanges through the protected token endpoint, and persists independently.</summary>
    [Fact]
    public async Task BrowserCompletion_ValidatesStateAndStoresThreadsmithGrant()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-codex-oauth-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "token.json");
        var handler = new RecordingHandler(_ => JsonResponse(
            "{\"access_token\":\"access-value\",\"refresh_token\":\"refresh-value\",\"expires_in\":3600}"));
        using var oauth = new OpenAiCodexOAuthManager(new HttpClient(handler), path);
        var challenge = OpenAiCodexOAuthManager.CreateBrowserChallenge(
            new Uri("http://localhost:1455/auth/callback"));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.CompleteBrowserAsync(
                challenge,
                new Uri("http://localhost:1455/auth/callback?code=secret-code&state=wrong"),
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.CompleteBrowserAsync(
                challenge,
                new Uri($"http://localhost:1455/other?code=secret-code&state={challenge.State}"),
                TestContext.Current.CancellationToken));
            await oauth.CompleteBrowserAsync(
                challenge,
                new Uri($"http://localhost:1455/auth/callback?code=secret-code&state={challenge.State}"),
                TestContext.Current.CancellationToken);

            Assert.Equal("access-value", await oauth.GetAccessTokenAsync(TestContext.Current.CancellationToken));
            Assert.Equal("auth.openai.com", handler.Request?.RequestUri?.Host);
            Assert.DoesNotContain("secret-code", handler.Request?.RequestUri?.AbsoluteUri ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Malformed credential JSON reports an unauthenticated status without throwing.</summary>
    [Fact]
    public async Task AuthenticationStatus_MalformedCredentialPayload_IsUnauthenticated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-codex-status-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "token.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
        using var oauth = new OpenAiCodexOAuthManager(new HttpClient(new RecordingHandler(_ => JsonResponse("{}"))), path);

        try
        {
            var status = await oauth.GetStatusAsync(TestContext.Current.CancellationToken);

            Assert.False(status.IsAuthenticated);
            Assert.Null(status.ExpiresAt);
            Assert.Null(status.AccountId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Expired-token refresh observes caller cancellation even when the shared HTTP client has no timeout.</summary>
    [Fact]
    public async Task TokenRefresh_HangingEndpoint_ObservesBoundedCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-codex-timeout-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "token.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            path,
            "{\"AccessToken\":\"old\",\"RefreshToken\":\"refresh\",\"ExpiresAt\":\"2020-01-01T00:00:00Z\"}",
            TestContext.Current.CancellationToken);
        using var oauth = new OpenAiCodexOAuthManager(new HttpClient(new HangingHandler()), path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oauth.GetAccessTokenAsync(timeout.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Native Responses requests and SSE events remain provider-neutral at the project boundary.</summary>
    [Fact]
    public async Task Provider_UsesResponsesAndNormalizesTextReasoningToolAndUsage()
    {
        const string stream = """
            data: {"type":"response.reasoning_summary_text.delta","delta":"think"}

            data: {"type":"response.output_text.delta","delta":"answer"}

            data: {"type":"response.output_item.done","item":{"type":"function_call","name":"read","arguments":"{\"path\":\"a.cs\"}"}}

            data: {"type":"response.completed","response":{"usage":{"input_tokens":10,"output_tokens":4,"input_tokens_details":{"cached_tokens":3}}}}

            data: [DONE]

            """;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        });
        var configuration = await new OpenAiCodexCatalogClient(
            new HttpClient(new RecordingHandler(_ => JsonResponse(
                "{\"models\":[{\"slug\":\"dynamic\",\"display_name\":\"Dynamic\",\"context_window\":128000}]}"))))
            .DiscoverAsync("token", cancellationToken: TestContext.Current.CancellationToken);
        var registration = new OpenAiCodexProviderRegistration();
        var profile = Assert.Single(registration.CreateProfiles(configuration));
        var provider = registration.CreateProvider(new ModelProviderActivationContext
        {
            HttpClient = new HttpClient(handler),
            Profile = profile,
            ProviderConfiguration = configuration,
            ModelConfiguration = Assert.Single(configuration.Models),
            ResolvedSecret = CreateJwt("account-2"),
        });

        var streamRequest = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            ReasoningLevel = ReasoningLevel.High,
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "read",
                    Description = "Read",
                    ArgumentsJsonSchema = "{\"type\":\"object\"}",
                },
            ],
        };
        var chunks = await provider.StreamAsync(
            streamRequest,
            TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(chunks, chunk => chunk.Reasoning == "think");
        Assert.Contains(chunks, chunk => chunk.Text == "answer");
        Assert.Contains(chunks, chunk => chunk.Output is ToolRequestModelOutput { ToolName: "read" });
        var usage = Assert.IsType<ModelUsage>(
            Assert.Single(chunks, chunk => chunk.Usage is not null).Usage);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(3, usage.Cache?.CacheReadTokens);
        Assert.Equal(CacheReadInputSemantics.IncludedInInput, usage.Cache?.ReadInputSemantics);
        Assert.Equal(OpenAiCodexProviderRegistration.ResponsesEndpoint, handler.Request?.RequestUri);
        var requestBody = handler.RequestBody ?? string.Empty;
        Assert.Contains("\"model\":\"dynamic\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"effort\":\"high\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"strict\":true", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"parallel_tool_calls\":false", requestBody, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", requestBody, StringComparison.Ordinal);
    }

    /// <summary>A pre-stream authentication rejection refreshes and safely replays exactly once.</summary>
    [Fact]
    public async Task Provider_AuthenticationRejection_RefreshesAndReplaysOnce()
    {
        var handler = new RecordingHandler(request => request.Headers.Authorization?.Parameter == "old-token"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : StreamingResponse());
        var refreshCount = 0;
        var provider = await CreateProviderAsync(
            handler,
            "old-token",
            (rejectedToken, _) =>
            {
                Assert.Equal("old-token", rejectedToken);
                refreshCount++;
                return Task.FromResult<string?>("new-token");
            });

        var chunks = await provider.StreamAsync(
            CreateStreamRequest(),
            TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, refreshCount);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(["old-token", "new-token"], handler.AuthorizationParameters);
        Assert.Contains(chunks, chunk => chunk.FinishReason == ModelFinishReason.Stop);
    }

    /// <summary>Configured transient attempts are exhausted only after bounded retry delays.</summary>
    [Fact]
    public async Task Provider_TransientResponse_HonorsRetryPolicy()
    {
        var responseCount = 0;
        var handler = new RecordingHandler(_ => ++responseCount == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : StreamingResponse());
        var provider = await CreateProviderAsync(handler, "token");

        var chunks = await provider.StreamAsync(
            CreateStreamRequest(),
            TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.RequestCount);
        Assert.Contains(chunks, chunk => chunk.FinishReason == ModelFinishReason.Stop);
    }

    /// <summary>Sensitive content prohibited by the profile never reaches the Codex endpoint.</summary>
    [Fact]
    public async Task Provider_ProhibitedSensitiveRequest_IsRejectedBeforeDispatch()
    {
        var handler = new RecordingHandler(_ => StreamingResponse());
        var provider = await CreateProviderAsync(handler, "token");
        var request = CreateStreamRequest() with { ContainsSensitiveData = true };

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await provider.StreamAsync(request, TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Contains("prohibits sensitive request content", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    private static HttpResponseMessage JsonResponse(string value)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage StreamingResponse()
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(
            "data: {\"type\":\"response.completed\",\"response\":{}}\n\ndata: [DONE]\n\n",
            Encoding.UTF8,
            "text/event-stream"),
        };
    }

    private static async Task<IModelProvider> CreateProviderAsync(
        HttpMessageHandler handler,
        string accessToken,
        Func<string, CancellationToken, Task<string?>>? refreshAccessTokenAsync = null)
    {
        var configuration = await new OpenAiCodexCatalogClient(
            new HttpClient(new RecordingHandler(_ => JsonResponse(
                "{\"models\":[{\"slug\":\"dynamic\",\"display_name\":\"Dynamic\",\"context_window\":128000}]}"))))
            .DiscoverAsync("token", cancellationToken: TestContext.Current.CancellationToken);
        var registration = new OpenAiCodexProviderRegistration();
        var profile = Assert.Single(registration.CreateProfiles(configuration)) with
        {
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 2, Delay = TimeSpan.Zero },
        };
        return registration.CreateProvider(new ModelProviderActivationContext
        {
            HttpClient = new HttpClient(handler),
            Profile = profile,
            ProviderConfiguration = configuration,
            ModelConfiguration = Assert.Single(configuration.Models),
            ResolvedSecret = accessToken,
            RefreshResolvedSecretAsync = refreshAccessTokenAsync,
        });
    }

    private static ModelStreamRequest CreateStreamRequest()
    {
        return new()
        {
            RunId = RunId.New(),
            Input = "hello",
            ReasoningLevel = ReasoningLevel.Medium,
        };
    }

    private static string CreateJwt(string accountId)
    {
        var header = Base64Url("{\"alg\":\"none\"}");
        var payload = Base64Url(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, string>
            {
                ["chatgpt_account_id"] = accountId,
            },
        }));
        return $"{header}.{payload}.signature";
    }

    private static string Base64Url(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string?> AuthorizationParameters { get; } = [];

        public HttpRequestMessage? Request { get; private set; }

        public int RequestCount { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Request = request;
            AuthorizationParameters.Add(request.Headers.Authorization?.Parameter);
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
