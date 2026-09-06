namespace Threadsmith.Tools;

using Microsoft.Extensions.Configuration;

/// <summary>Resolves logical secrets at the final invocation boundary.</summary>
public interface ISecretStore
{
    /// <summary>Resolves an allowed <c>secrets:</c> reference without persisting it.</summary>
    Task<string?> GetAsync(
        string secretReference,
        CancellationToken cancellationToken = default);
}

/// <summary>Compatibility adapter for lifecycle-owned stores that still consume the legacy read contract.</summary>
public sealed class SecretResolverStoreAdapter : ISecretStore
{
    private readonly string _componentId;
    private readonly SecretProviderTrust _minimumTrust;
    private readonly ISecretResolver _resolver;

    /// <summary>Initializes a new instance of the <see cref="SecretResolverStoreAdapter"/> class.</summary>
    public SecretResolverStoreAdapter(
        ISecretResolver resolver,
        string componentId,
        SecretProviderTrust minimumTrust = SecretProviderTrust.UserOwned)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        _resolver = resolver;
        _componentId = componentId;
        _minimumTrust = minimumTrust;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        if (!SecretReference.TryParse(secretReference, out var reference) || reference is null)
        {
            return null;
        }

        var result = await _resolver.ResolveAsync(
            new SecretResolutionRequest
            {
                Reference = reference,
                ComponentId = _componentId,
                Purpose = "lifecycle compatibility lookup",
                MinimumTrust = _minimumTrust,
            },
            cancellationToken).ConfigureAwait(false);
        return result.Value?.Reveal();
    }
}

/// <summary>Adapts a legacy test or lifecycle secret store to the provider-neutral resolver contract.</summary>
public sealed class LegacySecretStoreResolver : ISecretResolver
{
    private readonly ISecretStore _store;

    /// <summary>Initializes a new instance of the <see cref="LegacySecretStoreResolver"/> class.</summary>
    public LegacySecretStoreResolver(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<SecretResolutionResult> ResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var value = await _store.GetAsync(request.Reference.CanonicalName, cancellationToken).ConfigureAwait(false);
        return value is null
            ? new SecretResolutionResult
            {
                Failure = SecretResolutionFailure.NotFound,
                Diagnostics = ["legacy-store:not-found"],
            }
            : new SecretResolutionResult
            {
                Value = new SecretValue(value),
                ProviderId = "legacy-store",
                Failure = SecretResolutionFailure.None,
                Diagnostics = ["legacy-store:found"],
            };
    }
}

/// <summary>Secret store backed by the final Microsoft.Extensions.Configuration view.</summary>
public sealed class ConfigurationSecretStore : ISecretStore
{
    private readonly IConfiguration _configuration;

    /// <summary>Initializes a new instance of the <see cref="ConfigurationSecretStore"/> class.</summary>
    public ConfigurationSecretStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!secretReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Secret references must use the secrets: scope.");
        }

        return Task.FromResult(_configuration[secretReference]);
    }
}
