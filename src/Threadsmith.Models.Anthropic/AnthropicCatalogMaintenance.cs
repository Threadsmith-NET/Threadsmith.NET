namespace Threadsmith.Models.Anthropic;

using System.Collections.Concurrent;

/// <summary>Acquires trusted startup metadata and maintains explicit refresh/status without changing published catalogs.</summary>
public sealed class AnthropicCatalogMaintenance : IModelCatalogMaintenance
{
    private readonly IReadOnlyDictionary<string, AnthropicProviderConfiguration> _providers;
    private readonly Func<string, CancellationToken, Task<string?>> _resolveSecret;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, AnthropicCatalogSnapshot> _latest = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="AnthropicCatalogMaintenance"/> class with trusted startup providers.</summary>
    public AnthropicCatalogMaintenance(
        IEnumerable<AnthropicProviderConfiguration> providers,
        Func<string, CancellationToken, Task<string?>> resolveSecret,
        HttpClient httpClient,
        string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(resolveSecret);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _resolveSecret = resolveSecret;
        _httpClient = httpClient;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        foreach (var provider in _providers.Values)
        {
            if (provider.CatalogSnapshot is { } snapshot)
            {
                _latest[provider.Id] = snapshot;
            }
        }
    }

    /// <summary>Resolves only an explicitly supplied privileged secret boundary and hydrates a complete startup snapshot.</summary>
    public static async Task<AnthropicProviderConfiguration> HydrateStartupAsync(
        AnthropicProviderConfiguration provider,
        Func<string, CancellationToken, Task<string?>> resolveSecret,
        HttpClient httpClient,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        AnthropicCatalogHydrator.ValidateDescriptor(provider);
        if (provider.Models.Count != 0)
        {
            throw new InvalidOperationException("Trusted Anthropic descriptors require models: []; use exact modelOverrides for model policy.");
        }

        ArgumentNullException.ThrowIfNull(resolveSecret);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (!provider.Enabled)
        {
            return Unavailable(provider, "disabled by trusted provider policy");
        }

        var reference = provider.SecretKeyReference ?? throw new InvalidOperationException("Validated secret reference is missing.");
        var key = await resolveSecret(reference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            return Unavailable(provider, "credential unavailable");
        }

        AnthropicModelCatalogCache? cache = null;
        AnthropicModelCatalogCacheEntry? cached = null;
        try
        {
            cache = AnthropicModelCatalogCache.ForProvider(cacheDirectory, provider.Id);
            cached = await cache.LoadAsync(provider.Id, reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache failures are a defined cache miss; authenticated acquisition may still succeed.
        }

        var fresh = cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromHours(24);
        if (fresh && cached is not null)
        {
            return Hydrate(provider, cached.Models, new AnthropicCatalogSnapshot { FetchedAt = cached.FetchedAt, Status = "fresh cached metadata; account access not yet verified in this process" });
        }

        try
        {
            var models = await DiscoverAsync(httpClient, key, cancellationToken).ConfigureAwait(false);
            var fetchedAt = DateTimeOffset.UtcNow;
            var saved = cache is not null && await TryStoreAsync(cache, provider.Id, reference, models, fetchedAt, cancellationToken).ConfigureAwait(false);
            return Hydrate(provider, models, new AnthropicCatalogSnapshot
            {
                Authenticated = true,
                FetchedAt = fetchedAt,
                Status = saved ? "authenticated discovery complete" : "authenticated discovery complete; cache write unavailable",
            });
        }
        catch (AnthropicDiscoveryException exception)
        {
            if (exception.CredentialRejected && cache is not null)
            {
                await TryInvalidateAsync(cache, cancellationToken).ConfigureAwait(false);
            }

            if (exception.Transient && cached is not null)
            {
                return Hydrate(provider, cached.Models, new AnthropicCatalogSnapshot { FetchedAt = cached.FetchedAt, Stale = true, Status = "stale validated metadata; transient discovery failure" });
            }

            return Unavailable(provider, exception.Message, exception.CredentialRejected);
        }
        catch (ModelProviderException)
        {
            return Unavailable(provider, "discovery resource or protocol validation failed");
        }
    }

    /// <inheritdoc />
    public async Task<ModelCatalogProviderStatus> GetStatusAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        var reference = provider.SecretKeyReference ?? throw new InvalidOperationException("Configured secret reference is missing.");
        var secret = await _resolveSecret(reference, cancellationToken).ConfigureAwait(false);
        var snapshot = _latest.TryGetValue(provider.Id, out var latest) ? latest
            : new AnthropicCatalogSnapshot { Status = "metadata unavailable" };
        var hasCredential = !string.IsNullOrWhiteSpace(secret);
        var available = hasCredential && !snapshot.CredentialRejected
            && provider.Enabled && provider.Models.Any(model => model.Enabled);
        return new ModelCatalogProviderStatus
        {
            ProviderId = provider.Id,
            HasCredential = hasCredential,
            IsAuthenticated = hasCredential && snapshot.Authenticated && !snapshot.CredentialRejected,
            IsAvailable = available,
            CacheAge = snapshot.FetchedAt is { } fetchedAt ? DateTimeOffset.UtcNow - fetchedAt : null,
            Status = Describe(snapshot, hasCredential, available),
        };
    }

    /// <inheritdoc />
    public async Task<ModelCatalogRefreshResult> RefreshAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        var reference = provider.SecretKeyReference ?? throw new InvalidOperationException("Configured secret reference is missing.");
        var secret = await _resolveSecret(reference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            var prior = _latest.TryGetValue(provider.Id, out var known) ? known : new AnthropicCatalogSnapshot { Status = "metadata unavailable" };
            _latest[provider.Id] = prior with { Authenticated = false, Status = "credential unavailable" };
            return new ModelCatalogRefreshResult { ProviderId = provider.Id, Status = "credential unavailable" };
        }

        try
        {
            var cache = AnthropicModelCatalogCache.ForProvider(_cacheDirectory, provider.Id);
            var models = await DiscoverAsync(_httpClient, secret, cancellationToken).ConfigureAwait(false);
            var fetchedAt = DateTimeOffset.UtcNow;
            var hydrated = Hydrate(provider with { Enabled = true }, models, new AnthropicCatalogSnapshot
            {
                Authenticated = true,
                FetchedAt = fetchedAt,
                Status = "metadata refreshed; restart required to rebuild model selection",
            });
            var snapshot = hydrated.CatalogSnapshot ?? throw new InvalidOperationException("Hydrated catalog evidence is missing.");
            if (!await TryStoreAsync(cache, provider.Id, reference, models, fetchedAt, cancellationToken).ConfigureAwait(false))
            {
                _latest[provider.Id] = snapshot with { Status = "authenticated discovery complete; cache write failed; refresh not saved" };
                return new ModelCatalogRefreshResult { ProviderId = provider.Id, Status = "cache write failed; refresh not saved" };
            }

            _latest[provider.Id] = snapshot;
            return new ModelCatalogRefreshResult
            {
                ProviderId = provider.Id,
                Refreshed = true,
                RestartRequired = true,
                Status = Describe(snapshot, true, provider.Enabled && provider.Models.Any(model => model.Enabled)),
            };
        }
        catch (AnthropicDiscoveryException exception)
        {
            var old = _latest.TryGetValue(provider.Id, out var previous) ? previous
                : new AnthropicCatalogSnapshot { Status = "metadata unavailable" };
            if (exception.CredentialRejected)
            {
                await TryInvalidateAsync(AnthropicModelCatalogCache.ForProvider(_cacheDirectory, provider.Id), cancellationToken).ConfigureAwait(false);
            }

            _latest[provider.Id] = old with { Authenticated = false, CredentialRejected = old.CredentialRejected || exception.CredentialRejected, Status = exception.Message };
            return new ModelCatalogRefreshResult { ProviderId = provider.Id, Status = exception.Message };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ModelProviderException)
        {
            return new ModelCatalogRefreshResult { ProviderId = provider.Id, Status = "metadata refresh failed validation or cache access" };
        }
    }

    private static AnthropicProviderConfiguration Hydrate(
        AnthropicProviderConfiguration provider, IReadOnlyList<AnthropicDiscoveredModel> models, AnthropicCatalogSnapshot snapshot)
    {
        return AnthropicCatalogHydrator.Hydrate(provider with { CatalogSnapshot = snapshot }, models, AnthropicReviewedModels.All);
    }

    private static AnthropicProviderConfiguration Unavailable(AnthropicProviderConfiguration provider, string status, bool rejected = false)
    {
        return provider with { Enabled = false, Models = [], CatalogSnapshot = new AnthropicCatalogSnapshot { Status = status, CredentialRejected = rejected } };
    }

    private static Task<IReadOnlyList<AnthropicDiscoveredModel>> DiscoverAsync(HttpClient httpClient, string key, CancellationToken cancellationToken)
    {
        return new AnthropicModelDiscoveryService(AnthropicModelDiscoveryClient.Create(httpClient, key, TimeSpan.FromSeconds(15)))
            .DiscoverAsync(cancellationToken);
    }

    private static async Task<bool> TryStoreAsync(
        AnthropicModelCatalogCache cache, string providerId, string reference, IReadOnlyList<AnthropicDiscoveredModel> models, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        try
        {
            await cache.StoreAsync(
                new AnthropicModelCatalogCacheEntry
                {
                    ProviderId = providerId,
                    SecretReferenceIdentity = AnthropicModelCatalogCache.CreateSecretReferenceIdentity(reference),
                    FetchedAt = fetchedAt,
                    Models = models,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task TryInvalidateAsync(AnthropicModelCatalogCache cache, CancellationToken cancellationToken)
    {
        try
        {
            await cache.InvalidateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Rejection remains authoritative in memory even when disk cleanup is unavailable.
        }
    }

    private static string Describe(AnthropicCatalogSnapshot snapshot, bool hasCredential, bool available)
    {
        var eligible = snapshot.Models.Count(model => model.Eligible);
        var prefix = !hasCredential ? "credential unavailable" : snapshot.Status;
        return prefix + $"; selectable now={available}; eligible metadata={eligible}, excluded={snapshot.Models.Count - eligible}"
            + string.Concat(snapshot.Models.Select(model => $"\n{model.ModelId} | {model.ProfileId.Value:D} | {model.Reason}"));
    }

    private AnthropicProviderConfiguration GetProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providers.TryGetValue(providerId, out var provider) ? provider
            : throw new KeyNotFoundException($"Anthropic provider '{providerId}' is not configured.");
    }
}
