namespace Threadsmith.AnthropicProvider.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;

/// <summary>Verifies native SDK discovery, immutable catalogs, trusted policy, and cache failure behavior.</summary>
public sealed class AnthropicCatalogTests
{
    /// <summary>Checks profile identifier is stable exact and provider scoped.</summary>
    [Fact]
    public void ProfileIdentifierIsStableExactAndProviderScoped()
    {
        var first = AnthropicProfileIdentifiers.Create("primary", "claude-opus-5");
        Assert.Equal(Guid.Parse("11ef9dc3-aaa1-5274-91cc-ff222603589a"), first.Value);
        Assert.Equal(first, AnthropicProfileIdentifiers.Create(" PRIMARY ", "claude-opus-5"));
        Assert.NotEqual(first, AnthropicProfileIdentifiers.Create("secondary", "claude-opus-5"));
        Assert.NotEqual(first, AnthropicProfileIdentifiers.Create("primary", "CLAUDE-OPUS-5"));
        Assert.Equal('5', first.Value.ToString("D")[14]);
    }

    /// <summary>Checks hydration uses api facts and copies cache prices.</summary>
    [Fact]
    public void HydrationUsesApiFactsAndCopiesCachePrices()
    {
        var provider = Hydrate(Model() with
        {
            MaximumInputTokens = 150000,
            MaximumOutputTokens = 32000,
            Capabilities = new AnthropicDiscoveredCapabilities
            {
                StructuredOutputs = false,
                EffortLevels = new Dictionary<string, bool> { ["max"] = false, ["xhigh"] = false },
            },
        });
        var model = Assert.IsType<AnthropicModelConfiguration>(Assert.Single(provider.Models));
        Assert.Equal(150000, model.ContextWindow);
        Assert.Equal(32000, model.MaximumOutputTokens);
        Assert.Equal(8192, model.RequestOutputTokenReserve);
        Assert.False(model.Capabilities.StructuredOutput);
        Assert.DoesNotContain(new ReasoningLevel("max"), model.SupportedReasoningLevels);
        Assert.DoesNotContain(new ReasoningLevel("xhigh"), model.SupportedReasoningLevels);
        Assert.Equal(6.25m, model.Cost.CachePricing?.WritePerMillionTokens);
        Assert.Equal("2026-09-10", model.Cost.CachePricing?.SourceDate);
        Assert.True(model.Cost.CalculateAdmission(1000, 1000) > model.Cost.Calculate(1000, 1000));
    }

    /// <summary>Checks missing metadata uses exact reviewed fallback and unknown model is excluded.</summary>
    [Fact]
    public void MissingMetadataUsesExactReviewedFallbackAndUnknownModelIsExcluded()
    {
        var provider = Hydrate(Model() with { MaximumInputTokens = null, MaximumOutputTokens = null }, Model("unreviewed-model"));
        Assert.Single(provider.Models);
        Assert.Equal(2, provider.CatalogSnapshot?.Models.Count);
        Assert.Contains(provider.CatalogSnapshot?.Models ?? [], model => model.ModelId == "unreviewed-model" && !model.Eligible);
        Assert.Equal(1000000, provider.Models[0].ContextWindow);
    }

    /// <summary>Checks missing prices exclude only affected model.</summary>
    [Fact]
    public void MissingPricesExcludeOnlyAffectedModel()
    {
        var policy = AnthropicReviewedModels.All["claude-opus-5"] with { Prices = new AnthropicModelPrices() };
        var provider = AnthropicCatalogHydrator.Hydrate(
            Provider(),
            [Model(), Model("claude-sonnet-5")],
            new Dictionary<string, AnthropicModelCompatibility>
            {
                ["claude-opus-5"] = policy,
                ["claude-sonnet-5"] = AnthropicReviewedModels.All["claude-sonnet-5"],
            });
        Assert.Equal("claude-sonnet-5", Assert.IsType<AnthropicModelConfiguration>(Assert.Single(provider.Models)).ModelId);
        Assert.Contains(provider.CatalogSnapshot?.Models ?? [], item => item.ModelId == "claude-opus-5" && item.Reason.Contains("pricing", StringComparison.Ordinal));
    }

    /// <summary>Checks required thinking preserves selectable effort without none.</summary>
    [Fact]
    public void RequiredThinkingPreservesSelectableEffortWithoutNone()
    {
        var hydrated = Hydrate(Model("claude-fable-5-1"));
        var profile = Assert.Single(new AnthropicProviderRegistration().CreateProfiles(hydrated));
        Assert.Equal(ReasoningControllability.Selectable, profile.ReasoningCapability?.Controllability);
        Assert.False(profile.ReasoningCapability?.SupportsReasoningOff);
        Assert.DoesNotContain(ReasoningLevel.None, profile.SupportedReasoningLevels);
    }

    /// <summary>Checks reserve and manual budget are validated without clamping.</summary>
    [Fact]
    public void ReserveAndManualBudgetAreValidatedWithoutClamping()
    {
        var provider = Provider() with { Defaults = new AnthropicModelDefaults { RequestOutputTokenReserve = 2048 } };
        var hydrated = AnthropicCatalogHydrator.Hydrate(provider, [Model("claude-haiku-4-5-20251001")], AnthropicReviewedModels.All);
        Assert.Empty(hydrated.Models);
        Assert.False(hydrated.Enabled);
        Assert.Contains("thinking", Assert.Single(hydrated.CatalogSnapshot?.Models ?? []).Reason, StringComparison.Ordinal);
    }

    /// <summary>Checks explicit default resolves only after descriptor hydration.</summary>
    [Fact]
    public void ExplicitDefaultResolvesOnlyAfterDescriptorHydration()
    {
        var modelId = AnthropicProfileIdentifiers.Create("primary", "claude-opus-5");
        var descriptor = new ModelProviderCatalogConfiguration { Providers = [Provider()], DefaultProviderId = "primary", DefaultModelId = modelId };
        var registry = Registry();
        Assert.Throws<InvalidOperationException>(() => ModelProviderConfigurationLoader.Materialize(descriptor, registry));
        var catalog = ModelProviderConfigurationLoader.Materialize(descriptor with { Providers = [Hydrate(Model())] }, registry);
        Assert.Equal(modelId, catalog.DefaultModelId);
    }

    /// <summary>Checks sdk reads two pages and detached api capabilities.</summary>
    [Fact]
    public async Task SdkReadsTwoPagesAndDetachedApiCapabilities()
    {
        using var handler = new QueueHandler(
            Response(Page(["claude-opus-5"], true)),
            Response(Page(["claude-sonnet-5"], false)));
        using var http = new HttpClient(handler);
        var models = await Discovery(http).DiscoverAsync(CancellationToken.None);
        Assert.Equal(2, models.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("limit=100", handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("after_id=claude-opus-5", handler.Requests[1], StringComparison.Ordinal);
        Assert.False(models[0].Capabilities?.StructuredOutputs);
        Assert.True(models[0].Capabilities?.AdaptiveThinking);
        Assert.Equal(false, models[0].Capabilities?.EffortLevels?["max"]);
        Assert.All(handler.ApiKeys, value => Assert.Equal("fixture-key", value));
    }

    /// <summary>Checks sdk rejects malformed cursor instead of publishing partial page.</summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "wrong-cursor")]
    public async Task SdkRejectsMalformedCursorInsteadOfPublishingPartialPage(bool hasMore, string? cursor)
    {
        using var handler = new QueueHandler(Response(Page(["claude-opus-5"], hasMore, cursor, explicitCursor: true)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => Discovery(http).DiscoverAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    /// <summary>Checks sdk rejects duplicate model on later page.</summary>
    [Fact]
    public async Task SdkRejectsDuplicateModelOnLaterPage()
    {
        using var handler = new QueueHandler(Response(Page(["claude-opus-5"], true)), Response(Page(["claude-opus-5"], false)));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Discovery(http).DiscoverAsync(CancellationToken.None));
        Assert.Contains("duplicate", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Checks sdk counts unknown payload bytes before parsing and bounds aggregate.</summary>
    [Fact]
    public async Task SdkCountsUnknownPayloadBytesBeforeParsingAndBoundsAggregate()
    {
        var payload = Page(["claude-opus-5"], true);
        payload = payload.Insert(payload.Length - 1, ",\"padding\":\"" + new string('x', 600000) + "\"");
        var second = Page(["claude-sonnet-5"], false);
        second = second.Insert(second.Length - 1, ",\"padding\":\"" + new string('x', 600000) + "\"");
        using var handler = new QueueHandler(Response(payload), Response(second));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Discovery(http).DiscoverAsync(CancellationToken.None));
        Assert.Contains("byte", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks discovery enforces page and provider model counts.</summary>
    [Theory]
    [InlineData(101, 1)]
    [InlineData(100, 29)]
    public async Task DiscoveryEnforcesPageAndProviderModelCounts(int firstCount, int secondCount)
    {
        var pages = new StubPages(
            DetachedPage(firstCount, "a", true), DetachedPage(secondCount, "b", false));
        await Assert.ThrowsAnyAsync<Exception>(() => new AnthropicModelDiscoveryService(pages).DiscoverAsync(CancellationToken.None));
    }

    /// <summary>Checks discovery enforces ten page ceiling.</summary>
    [Fact]
    public async Task DiscoveryEnforcesTenPageCeiling()
    {
        var pages = new StubPages([.. Enumerable.Range(0, 10).Select(index => DetachedPage(1, "m" + index, true))]);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => new AnthropicModelDiscoveryService(pages).DiscoverAsync(CancellationToken.None));
        Assert.Contains("page limit", failure.Message, StringComparison.Ordinal);
        Assert.Equal(10, pages.Calls);
    }

    /// <summary>Checks discovery deadline and caller cancellation are distinct.</summary>
    [Fact]
    public async Task DiscoveryDeadlineAndCallerCancellationAreDistinct()
    {
        var client = new BlockingDiscovery();
        var service = new AnthropicModelDiscoveryService(client, TimeSpan.FromMilliseconds(30));
        var timeout = await Assert.ThrowsAnyAsync<Exception>(() => service.DiscoverAsync(CancellationToken.None));
        Assert.IsNotType<OperationCanceledException>(timeout);
        Assert.Contains("deadline", timeout.Message, StringComparison.Ordinal);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(canceled.Token));
    }

    /// <summary>Checks auth failure excludes and invalidates stale cache without raw body.</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthFailureExcludesAndInvalidatesStaleCacheWithoutRawBody(int status)
    {
        using var temporary = new TemporaryDirectory();
        var cache = await SeedCacheAsync(temporary.Path, TimeSpan.FromHours(25));
        using var handler = new QueueHandler(Response("SECRET_ERROR_CANARY", (HttpStatusCode)status));
        using var http = new HttpClient(handler);
        var hydrated = await StartupAsync(temporary.Path, http);
        Assert.False(hydrated.Enabled);
        Assert.True(hydrated.CatalogSnapshot?.CredentialRejected);
        Assert.DoesNotContain("SECRET_ERROR_CANARY", hydrated.CatalogSnapshot?.Status ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(await cache.LoadAsync("primary", "secrets:models:anthropic", CancellationToken.None));
    }

    /// <summary>Checks transient later page failure retains whole previous snapshot.</summary>
    [Fact]
    public async Task TransientLaterPageFailureRetainsWholePreviousSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var cache = await SeedCacheAsync(temporary.Path, TimeSpan.FromHours(25));
        using var handler = new QueueHandler(Response(Page(["claude-sonnet-5"], true)), Response("private error", HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var hydrated = await StartupAsync(temporary.Path, http);
        Assert.True(hydrated.CatalogSnapshot?.Stale);
        Assert.Equal("claude-opus-5", Assert.IsType<AnthropicModelConfiguration>(Assert.Single(hydrated.Models)).ModelId);
        var cached = await cache.LoadAsync("primary", "secrets:models:anthropic", CancellationToken.None);
        Assert.Equal("claude-opus-5", Assert.Single(cached?.Models ?? []).ModelId);
    }

    /// <summary>Checks missing key does not use fresh cache or perform io.</summary>
    [Fact]
    public async Task MissingKeyDoesNotUseFreshCacheOrPerformIo()
    {
        using var temporary = new TemporaryDirectory();
        await SeedCacheAsync(temporary.Path, TimeSpan.FromHours(1));
        using var handler = new QueueHandler();
        using var http = new HttpClient(handler);
        var hydrated = await AnthropicCatalogMaintenance.HydrateStartupAsync(Provider(), (_, _) => Task.FromResult<string?>(null), http, temporary.Path, CancellationToken.None);
        Assert.False(hydrated.Enabled);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Checks fresh cache avoids discovery but does not claim authentication.</summary>
    [Fact]
    public async Task FreshCacheAvoidsDiscoveryButDoesNotClaimAuthentication()
    {
        using var temporary = new TemporaryDirectory();
        await SeedCacheAsync(temporary.Path, TimeSpan.FromHours(1));
        using var handler = new QueueHandler();
        using var http = new HttpClient(handler);
        var hydrated = await StartupAsync(temporary.Path, http);
        var maintenance = new AnthropicCatalogMaintenance([hydrated], SecretAsync, http, temporary.Path);
        var status = await maintenance.GetStatusAsync("primary");
        Assert.True(status.IsAvailable);
        Assert.False(status.IsAuthenticated);
        Assert.Contains("claude-opus-5", status.Status, StringComparison.Ordinal);
        Assert.Contains(AnthropicProfileIdentifiers.Create("primary", "claude-opus-5").Value.ToString("D"), status.Status, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Checks refresh reports exclusions and restart without publishing models.</summary>
    [Fact]
    public async Task RefreshReportsExclusionsAndRestartWithoutPublishingModels()
    {
        using var temporary = new TemporaryDirectory();
        using var handler = new QueueHandler(Response(Page(["claude-opus-5", "unreviewed"], false)));
        using var http = new HttpClient(handler);
        var maintenance = new AnthropicCatalogMaintenance([Provider() with { Enabled = false }], SecretAsync, http, temporary.Path);
        var result = await maintenance.RefreshAsync("primary");
        var status = await maintenance.GetStatusAsync("primary");
        Assert.True(result.Refreshed);
        Assert.True(result.RestartRequired);
        Assert.True(status.IsAuthenticated);
        Assert.False(status.IsAvailable);
        Assert.Contains("excluded=1", result.Status, StringComparison.Ordinal);
        Assert.Contains("unreviewed", result.Status, StringComparison.Ordinal);
        Assert.Contains("restart required", result.Status, StringComparison.Ordinal);
    }

    /// <summary>Checks rejected credential remains unavailable until successful refresh.</summary>
    [Fact]
    public async Task RejectedCredentialRemainsUnavailableUntilSuccessfulRefresh()
    {
        using var temporary = new TemporaryDirectory();
        using var handler = new QueueHandler(Response("rejected", HttpStatusCode.Unauthorized), Response("offline", HttpStatusCode.ServiceUnavailable), Response(Page(["claude-opus-5"], false)));
        using var http = new HttpClient(handler);
        var maintenance = new AnthropicCatalogMaintenance([Hydrate(Model())], SecretAsync, http, temporary.Path);
        Assert.False((await maintenance.RefreshAsync("primary")).Refreshed);
        Assert.False((await maintenance.RefreshAsync("primary")).Refreshed);
        Assert.False((await maintenance.GetStatusAsync("primary")).IsAvailable);
        Assert.True((await maintenance.RefreshAsync("primary")).Refreshed);
        Assert.True((await maintenance.GetStatusAsync("primary")).IsAvailable);
    }

    /// <summary>Checks cache rejects different provider secret future timestamp and duplicates.</summary>
    [Fact]
    public async Task CacheRejectsDifferentProviderSecretFutureTimestampAndDuplicates()
    {
        using var temporary = new TemporaryDirectory();
        var cache = await SeedCacheAsync(temporary.Path, TimeSpan.FromHours(1));
        Assert.Null(await cache.LoadAsync("other", "secrets:models:anthropic", CancellationToken.None));
        Assert.Null(await cache.LoadAsync("primary", "secrets:other", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.StoreAsync(Entry() with { FetchedAt = DateTimeOffset.UtcNow.AddHours(1) }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.StoreAsync(Entry() with { Models = [Model(), Model()] }, CancellationToken.None));
    }

    /// <summary>Checks cache treats malformed metadata as miss.</summary>
    [Theory]
    [InlineData("{\"schemaVersion\":2,\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":2,\"models\":null}")]
    [InlineData("not-json")]
    public async Task CacheTreatsMalformedMetadataAsMiss(string json)
    {
        using var temporary = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporary.Path, "cache.json");
        await File.WriteAllTextAsync(path, json);
        var cache = new AnthropicModelCatalogCache(path);
        Assert.Null(await cache.LoadAsync("primary", "secrets:models:anthropic", CancellationToken.None));
    }

    /// <summary>Checks cache rejects oversized file and supports empty complete snapshot.</summary>
    [Fact]
    public async Task CacheRejectsOversizedFileAndSupportsEmptyCompleteSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporary.Path, "cache.json");
        await File.WriteAllBytesAsync(path, new byte[AnthropicModelCatalogCache.MaximumBytes + 1]);
        var cache = new AnthropicModelCatalogCache(path);
        Assert.Null(await cache.LoadAsync("primary", "secrets:models:anthropic", CancellationToken.None));
        await cache.StoreAsync(Entry() with { Models = [] }, CancellationToken.None);
        Assert.Empty((await cache.LoadAsync("primary", "secrets:models:anthropic", CancellationToken.None))?.Models ?? throw new InvalidOperationException());
    }

    /// <summary>Checks repository model policy can change without changing trusted snapshot.</summary>
    [Fact]
    public void RepositoryModelPolicyCanChangeWithoutChangingTrustedSnapshot()
    {
        var trusted = Hydrate(Model());
        var original = Assert.IsType<AnthropicModelConfiguration>(Assert.Single(trusted.Models));
        var candidate = trusted with { Models = [original with { Name = "Repo name", DefaultReasoningLevel = ReasoningLevel.Low, ContextWindow = 180000 }] };
        var ordinary = AnthropicRepositoryModelOverrides.Apply(trusted, candidate);
        Assert.Equal("Repo name", ordinary.Models[0].Name);
        Assert.Equal("Claude fixture [primary/claude-opus-5]", trusted.Models[0].Name);
        Assert.Equal(ReasoningLevel.High, trusted.Models[0].DefaultReasoningLevel);
    }

    /// <summary>Checks repository cannot change same count policy secret capabilities or add model.</summary>
    [Fact]
    public void RepositoryCannotChangeSameCountPolicySecretCapabilitiesOrAddModel()
    {
        var trusted = Hydrate(Model());
        var original = Assert.IsType<AnthropicModelConfiguration>(Assert.Single(trusted.Models));
        Assert.Throws<InvalidOperationException>(() => AnthropicRepositoryModelOverrides.Apply(trusted, trusted with { SecretKeyReference = "secrets:other" }));
        Assert.Throws<InvalidOperationException>(() => AnthropicRepositoryModelOverrides.Apply(trusted, trusted with { Defaults = trusted.Defaults with { RetryMaxAttempts = 10 } }));
        Assert.Throws<InvalidOperationException>(() => AnthropicRepositoryModelOverrides.Apply(trusted, trusted with { Models = [original with { ContextWindow = 200001 }] }));
        Assert.Throws<InvalidOperationException>(() => AnthropicRepositoryModelOverrides.Apply(trusted, trusted with { Models = [original with { Cost = original.Cost with { InputPerMillionTokens = 0 } }] }));
        Assert.Throws<InvalidOperationException>(() => AnthropicRepositoryModelOverrides.Apply(trusted, trusted with { Models = [original with { ModelId = "other" }] }));
    }

    /// <summary>Checks descriptor repository merge uses hydrated ids and preserves other fields.</summary>
    [Fact]
    public async Task DescriptorRepositoryMergeUsesHydratedIdsAndPreservesOtherFields()
    {
        using var temporary = new TemporaryDirectory();
        var trusted = Hydrate(Model());
        var path = System.IO.Path.Combine(temporary.Path, "repository.json");
        await File.WriteAllTextAsync(path, $$"""{"schemaVersion":1,"providers":[{"id":"primary","models":[{"id":"{{trusted.Models[0].Id.Value:D}}","name":"Repository name","defaultReasoningLevel":"low"}]}]}""");
        var merged = ModelProviderConfigurationLoader.ApplyRepositoryOverrides(new ModelProviderCatalogConfiguration { Providers = [trusted] }, path, Registry());
        var candidate = Assert.IsType<AnthropicProviderConfiguration>(Assert.Single(merged.Providers));
        var ordinary = AnthropicRepositoryModelOverrides.Apply(trusted, candidate);
        var effective = ModelProviderConfigurationLoader.Materialize(merged with { Providers = [ordinary] }, Registry());
        Assert.Equal("Repository name", Assert.Single(effective.ModelCatalog.Profiles).Name);
        Assert.Equal(6.25m, Assert.Single(effective.ModelCatalog.Profiles).Cost.CachePricing?.WritePerMillionTokens);
    }

    private static AnthropicProviderConfiguration Provider()
    {
        return new AnthropicProviderConfiguration { Id = "primary", Name = "Anthropic", SecretKeyReference = "secrets:models:anthropic", Models = [] };
    }

    private static AnthropicDiscoveredModel Model(string id = "claude-opus-5")
    {
        return new AnthropicDiscoveredModel { ModelId = id, DisplayName = "Claude fixture", MaximumInputTokens = 200000, MaximumOutputTokens = 32000 };
    }

    private static AnthropicProviderConfiguration Hydrate(params AnthropicDiscoveredModel[] models)
    {
        return AnthropicCatalogHydrator.Hydrate(Provider(), models, AnthropicReviewedModels.All);
    }

    private static ModelProviderRegistry Registry() => new([new AnthropicProviderRegistration()]);

    private static Task<string?> SecretAsync(string reference, CancellationToken cancellationToken) => Task.FromResult<string?>("fixture-key");

    private static AnthropicModelDiscoveryService Discovery(HttpClient http) => new(AnthropicModelDiscoveryClient.Create(http, "fixture-key", TimeSpan.FromSeconds(15)));

    private static Task<AnthropicProviderConfiguration> StartupAsync(string directory, HttpClient http)
        => AnthropicCatalogMaintenance.HydrateStartupAsync(Provider(), SecretAsync, http, directory, CancellationToken.None);

    private static AnthropicModelCatalogCacheEntry Entry() => new()
    {
        ProviderId = "primary",
        SecretReferenceIdentity = AnthropicModelCatalogCache.CreateSecretReferenceIdentity("secrets:models:anthropic"),
        FetchedAt = DateTimeOffset.UtcNow.AddHours(-1),
        Models = [Model()],
    };

    private static async Task<AnthropicModelCatalogCache> SeedCacheAsync(string directory, TimeSpan age)
    {
        var cache = AnthropicModelCatalogCache.ForProvider(directory, "primary");
        await cache.StoreAsync(Entry() with { FetchedAt = DateTimeOffset.UtcNow - age }, CancellationToken.None);
        return cache;
    }

    private static string Page(string[] ids, bool hasMore, string? cursor = null, bool explicitCursor = false)
    {
        return JsonSerializer.Serialize(new
        {
            data = ids.Select(id => new
            {
                id,
                type = "model",
                created_at = "2026-07-24T00:00:00Z",
                display_name = "Claude fixture",
                max_input_tokens = 200000,
                max_tokens = 32000,
                capabilities = new
                {
                    structured_outputs = new { supported = false },
                    thinking = new { supported = true, types = new { adaptive = new { supported = true } } },
                    effort = new { supported = true, max = new { supported = false } },
                },
            }),
            has_more = hasMore,
            first_id = ids.FirstOrDefault(),
            last_id = explicitCursor ? cursor : cursor ?? ids.LastOrDefault(),
        });
    }

    private static AnthropicModelDiscoveryPage DetachedPage(int count, string prefix, bool hasMore)
    {
        var models = Enumerable.Range(0, count).Select(index => Model(prefix + index)).ToArray();
        return new AnthropicModelDiscoveryPage { Models = models, HasMore = hasMore, LastId = models.LastOrDefault()?.ModelId, ResponseBytes = 100 };
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        internal QueueHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        internal List<string> Requests { get; } = [];

        internal List<string> ApiKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            ApiKeys.Add(request.Headers.TryGetValues("x-api-key", out var keys) ? string.Join(',', keys) : string.Empty);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class StubPages : IAnthropicModelDiscoveryClient
    {
        private readonly Queue<AnthropicModelDiscoveryPage> _pages;

        internal StubPages(params AnthropicModelDiscoveryPage[] pages) => _pages = new Queue<AnthropicModelDiscoveryPage>(pages);

        internal int Calls { get; private set; }

        public Task<AnthropicModelDiscoveryPage> ListAsync(string? afterId, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed class BlockingDiscovery : IAnthropicModelDiscoveryClient
    {
        public async Task<AnthropicModelDiscoveryPage> ListAsync(string? afterId, int limit, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "threadsmith-anthropic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
