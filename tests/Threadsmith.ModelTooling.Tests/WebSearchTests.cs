namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies the milestone 7.5 consent, preflight, and provider boundaries.</summary>
public sealed class WebSearchTests
{
    /// <summary>Verifies repository configuration cannot manufacture consent.</summary>
    [Fact]
    public async Task RepositoryPreEnable_WithoutUserConsent_RemainsUnresolvable()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, ".threadsmith", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(configPath, "{\"tools\":{\"defaultEnabledOverrides\":[\"web_search\"]}}");
            var consentPath = Path.Combine(root, "user", "consent.json");
            var tool = CreateTool(new StubWebSearchClient());
            IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var state = new ToolStateManager([tool.Definition], configuration, configPath, consentPath);
            var registry = new ToolRegistry([tool], state);

            Assert.False(state.IsEnabled("web_search"));
            Assert.True(Assert.Single(state.GetAllStates()).ConsentRequired);
            Assert.Empty(registry.Definitions);
            Assert.Throws<KeyNotFoundException>(() => registry.Get("web_search"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies schema-two consent cannot be written without retrieval disclosure acknowledgement.</summary>
    [Fact]
    public async Task ExplicitGrant_WithoutRetrievalDisclosure_IsRejected()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, ".threadsmith", "config.json");
            var consentPath = Path.Combine(root, "user", "consent.json");
            var tool = CreateTool(new StubWebSearchClient());
            var state = new ToolStateManager(
                [tool.Definition],
                new ConfigurationBuilder().Build(),
                configPath,
                consentPath);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => state.GrantConsentAndEnableAsync("web_search"));

            Assert.False(File.Exists(consentPath));
            Assert.False(state.IsEnabled("web_search"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies consent is repository-bound, durable, and revocable.</summary>
    [Fact]
    public async Task ExplicitGrant_RestoresForSameRepository_AndDisableRevokes()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, ".threadsmith", "config.json");
            var consentPath = Path.Combine(root, "user", "consent.json");
            var tool = CreateTool(new StubWebSearchClient());
            IConfiguration configuration = new ConfigurationBuilder().Build();
            var state = new ToolStateManager([tool.Definition], configuration, configPath, consentPath);

            await state.GrantConsentAndEnableAsync("web_search", retrievalDisclosureAcknowledged: true);

            Assert.True(state.IsEnabled("web_search"));
            var restored = new ToolStateManager([tool.Definition], new ConfigurationBuilder().AddJsonFile(configPath).Build(), configPath, consentPath);
            Assert.True(restored.IsEnabled("web_search"));
            await restored.DisableAsync("web_search");
            Assert.False(restored.IsEnabled("web_search"));
            var restarted = new ToolStateManager([tool.Definition], new ConfigurationBuilder().AddJsonFile(configPath).Build(), configPath, consentPath);
            Assert.False(restarted.IsEnabled("web_search"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies sensitive queries never reach the provider.</summary>
    [Fact]
    public void SensitiveQuery_IsRejectedBeforeProviderCall()
    {
        var client = new StubWebSearchClient();
        var tool = CreateTool(client);

        string[] queries =
        [
            "password: hunter2",
            "client_secret=secret-value",
            "eyJabcdefgh.eyJabcdefgh.abcdefgh",
            "-----BEGIN PRIVATE KEY-----",
        ];

        foreach (var query in queries)
        {
            Assert.Throws<ToolArgumentValidationException>(
                () => tool.DeserializeInput(System.Text.Json.JsonSerializer.Serialize(new { query })));
        }

        Assert.Equal(0, client.CallCount);
    }

    /// <summary>Search results outside the fetch port policy remain available without fetch eligibility.</summary>
    [Fact]
    public async Task ExecuteAsync_NonDefaultHttpsPort_PreservesSearchResultWithoutReference()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var client = new StubWebSearchClient
            {
                Results =
                [
                    new WebSearchResult("Alternative port", "https://example.com:8443/page", "Snippet", 1, "stub"),
                    new WebSearchResult("Fetchable", "https://example.com/page", "Snippet", 2, "stub"),
                ],
            };
            var authority = new WebFetchAuthorizationAuthority(new WebFetchOptions());
            var tool = new WebSearchTool(
                client,
                new WebSearchOptions(),
                new SecretOutputSanitizer(),
                authority);
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                new ToolInvocationContext
                {
                    RepositoryPath = repository,
                    RequestedBy = "test",
                });

            var execution = await tool.ExecuteAsync(
                new WebSearchRequest { Query = "threadsmith" },
                context);

            Assert.Collection(
                execution.Value.Results,
                result =>
                {
                    Assert.Equal("https://example.com:8443/page", result.CanonicalUrl);
                    Assert.Null(result.SearchResultId);
                },
                result =>
                {
                    Assert.Equal("https://example.com/page", result.CanonicalUrl);
                    Assert.NotNull(result.SearchResultId);
                });
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Verifies provider wire data is normalized into bounded untrusted host DTOs.</summary>
    [Fact]
    public async Task BraveAdapter_NormalizesAndBoundsUntrustedResults()
    {
        const string json = "{\"web\":{\"results\":[{\"title\":\"<b>Example</b>\",\"url\":\"https://example.com/a\",\"description\":\"remote text\"}]}}";
        var handler = new StubHttpHandler(json);
        var options = new WebSearchOptions { MinimumRequestInterval = TimeSpan.Zero };
        var client = new BraveWebSearchClient(
            new HttpClient(handler),
            new StubSecretStore(),
            options);

        var response = await client.SearchAsync(new WebSearchRequest { Query = "threadsmith", MaximumResults = 1 });

        var result = Assert.Single(response.Results);
        Assert.Equal("Example", result.Title);
        Assert.Equal("https://example.com/a", result.CanonicalUrl);
        Assert.Contains("UNTRUSTED EXTERNAL EVIDENCE", response.TrustBoundary, StringComparison.Ordinal);
        Assert.Equal("brave", result.Provider);
        Assert.Equal("secret", handler.SeenCredential);
    }

    /// <summary>Verifies Brave authentication excludes repository-owned credentials.</summary>
    [Fact]
    public async Task BraveAdapter_RequiresUserOwnedCredential()
    {
        const string json = "{\"web\":{\"results\":[]}}";
        var handler = new StubHttpHandler(json);
        var resolver = new CapturingSecretResolver();
        var client = new BraveWebSearchClient(
            new HttpClient(handler),
            resolver,
            new WebSearchOptions { MinimumRequestInterval = TimeSpan.Zero });

        await client.SearchAsync(new WebSearchRequest { Query = "threadsmith" });

        Assert.Equal(SecretProviderTrust.UserOwned, resolver.MinimumTrust);
        Assert.Equal("user-secret", handler.SeenCredential);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "threadsmith-web-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static WebSearchTool CreateTool(IWebSearchClient client)
    {
        return new(client, new WebSearchOptions(), new SecretOutputSanitizer());
    }

    private sealed class StubWebSearchClient : IWebSearchClient
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<WebSearchResult> Results { get; init; } = [];

        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new WebSearchResponse
            {
                QueryIdentity = "query",
                ProviderId = "stub",
                RetrievedAt = DateTimeOffset.UnixEpoch,
                Results = Results,
            });
        }
    }

    private sealed class CapturingSecretResolver : ISecretResolver
    {
        public Task<SecretResolutionResult> ResolveAsync(
            SecretResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MinimumTrust = request.MinimumTrust;
            return Task.FromResult(new SecretResolutionResult
            {
                Value = new SecretValue("user-secret"),
                ProviderId = "user-file",
                Failure = SecretResolutionFailure.None,
            });
        }

        internal SecretProviderTrust? MinimumTrust { get; private set; }
    }

    private sealed class StubSecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("secret");
        }
    }

    private sealed class StubHttpHandler(string response) : HttpMessageHandler
    {
        public string? SeenCredential { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenCredential = request.Headers.GetValues("X-Subscription-Token").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }
}
