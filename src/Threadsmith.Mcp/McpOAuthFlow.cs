namespace Threadsmith.Mcp;

using System.Globalization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using Threadsmith.Tools;

/// <summary>Adapts host-owned OAuth UX and secret storage to the MCP SDK's PKCE OAuth provider.</summary>
public sealed class McpOAuthFlow
{
    private readonly IBrowserLauncher _browserLauncher;
    private readonly IOAuthCallbackListener _callbackListener;
    private readonly IMcpOAuthTokenStore _tokenStore;
    private readonly ISecretResolver _secretResolver;
    private readonly ILogger<McpOAuthFlow> _logger;

    /// <summary>Initializes a new instance of the <see cref="McpOAuthFlow"/> class.</summary>
    public McpOAuthFlow(
        IBrowserLauncher browserLauncher,
        IOAuthCallbackListener callbackListener,
        IMcpOAuthTokenStore tokenStore,
        ISecretResolver secretResolver,
        ILogger<McpOAuthFlow> logger)
    {
        ArgumentNullException.ThrowIfNull(browserLauncher);
        ArgumentNullException.ThrowIfNull(callbackListener);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(logger);
        _browserLauncher = browserLauncher;
        _callbackListener = callbackListener;
        _tokenStore = tokenStore;
        _secretResolver = secretResolver;
        _logger = logger;
    }

    /// <summary>Initializes a new instance of the <see cref="McpOAuthFlow"/> class for legacy hosts and tests.</summary>
    public McpOAuthFlow(
        IBrowserLauncher browserLauncher,
        IOAuthCallbackListener callbackListener,
        IMcpOAuthTokenStore tokenStore,
        ISecretStore secretStore,
        ILogger<McpOAuthFlow> logger)
        : this(browserLauncher, callbackListener, tokenStore, new LegacySecretStoreResolver(secretStore), logger)
    {
    }

    /// <summary>Creates SDK OAuth options while keeping SDK types inside the MCP adapter assembly.</summary>
    internal async Task<ClientOAuthOptions> CreateOptionsAsync(
        McpConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var oauth = profile.OAuth
            ?? throw new InvalidOperationException($"MCP profile '{profile.Id}' has no OAuth configuration.");
        if (!oauth.Enabled)
        {
            throw new InvalidOperationException($"MCP profile '{profile.Id}' has OAuth disabled.");
        }

        if (oauth.ClientId is not null && string.IsNullOrWhiteSpace(oauth.ClientId))
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' contains an empty configured clientId.");
        }

        if (oauth.ClientSecret is not null && string.IsNullOrWhiteSpace(oauth.ClientSecret))
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' contains an empty configured clientSecret reference.");
        }

        if (oauth.DiscoveryUrl is not null)
        {
            throw new InvalidOperationException(
                $"MCP OAuth discoveryUrl is not supported for profile '{profile.Id}'; "
                + "the authorization server must be advertised by the MCP endpoint.");
        }

        if (oauth.ClientMetadataDocumentUri is not null
            && !McpOAuthClientMetadataDocumentUri.IsValid(oauth.ClientMetadataDocumentUri))
        {
            throw new InvalidOperationException(
                $"MCP OAuth clientMetadataDocumentUri in profile '{profile.Id}' "
                + McpOAuthClientMetadataDocumentUri.Requirements + ".");
        }

        if (oauth.ClientMetadataDocumentUri is not null && !string.IsNullOrWhiteSpace(oauth.ClientId))
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' cannot configure both clientId and clientMetadataDocumentUri.");
        }

        if (oauth.ClientMetadataDocumentUri is not null && oauth.RedirectPort == 0)
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' must configure a fixed redirectPort when using clientMetadataDocumentUri.");
        }

        var clientSecretReference = oauth.ClientSecret;
        if (clientSecretReference is not null && string.IsNullOrWhiteSpace(oauth.ClientId))
        {
            throw new InvalidOperationException(
                $"MCP OAuth profile '{profile.Id}' cannot configure clientSecret without clientId.");
        }

        if (clientSecretReference is not null
            && (!clientSecretReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase)
                || !profile.SecretScope.Contains(clientSecretReference, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"MCP OAuth client secret reference '{clientSecretReference}' is outside profile '{profile.Id}' secretScope.");
        }

        // Client metadata is attempted first by the SDK and may fall back to DCR when the
        // authorization server does not support metadata-document client identifiers.
        var dynamicRegistrationEnabled = oauth.ClientMode != McpOAuthClientMode.PreRegistered;
        var clientId = oauth.ClientId;
        string? clientSecret = null;
        var clientMetadataDocumentUri = oauth.ClientMetadataDocumentUri;
        if (clientSecretReference is not null)
        {
            var request = new SecretResolutionRequest
            {
                Reference = SecretReference.Parse(clientSecretReference),
                ComponentId = SecretResolutionRequest.CreateConfiguredComponentId("mcp:oauth", profile.Id),
                Purpose = "authenticate the registered MCP OAuth client",
                MinimumTrust = SecretProviderTrust.UserOwned,
            };
            var resolution = await _secretResolver.ResolveAsync(request, cancellationToken);
            clientSecret = resolution.RequireValue(request);
        }

        var redirectUri = profile.AllowOAuthUserInteraction
            ? _callbackListener.ReserveRedirectUri(oauth.RedirectPort)
            : CreateNoninteractiveRedirectUri(oauth.RedirectPort);
        var tokenCache = new SecretStoreTokenCache(
            profile.Id,
            redirectUri,
            dynamicRegistrationEnabled,
            requireExactRedirectUri: true,
            _tokenStore);
        if (oauth.ClientMode == McpOAuthClientMode.DynamicRegistration
            && !profile.AllowOAuthUserInteraction)
        {
            var cachedTokens = await tokenCache.GetTokensAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(cachedTokens?.ClientId)
                || string.IsNullOrWhiteSpace(cachedTokens.AuthorizationServer))
            {
                throw new InvalidOperationException(
                    $"MCP OAuth profile '{profile.Id}' requires explicit connect or authentication before dynamic client registration.");
            }

            clientId = cachedTokens.ClientId;
            clientSecret = cachedTokens.ClientSecret;
        }

        return new ClientOAuthOptions
        {
            RedirectUri = redirectUri,
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientMetadataDocumentUri = clientMetadataDocumentUri,
            Scopes = oauth.Scopes,
            ScopeSelector = candidates => candidates?
                .Intersect(oauth.Scopes, StringComparer.Ordinal)
                .ToArray() ?? [],
            DynamicClientRegistration = dynamicRegistrationEnabled && profile.AllowOAuthUserInteraction
                ? CreateDynamicClientRegistrationOptions(profile.Id, redirectUri)
                : null,
            TokenCache = tokenCache,
            AuthorizationCallbackHandler = async (context, token) =>
            {
                if (!profile.AllowOAuthUserInteraction)
                {
                    throw new InvalidOperationException(
                        $"MCP OAuth profile '{profile.Id}' requires explicit connect or authentication before user interaction.");
                }

                _logger.LogInformation("OAuth flow started for MCP profile {ProfileId}.", profile.Id);
                var callbackTask = _callbackListener.WaitForCallbackAsync(context.RedirectUri, token);
                await _browserLauncher.LaunchAsync(context.AuthorizationUri, token);
                var callbackUri = await callbackTask;
                var result = ParseCallback(context.RedirectUri, callbackUri);
                _logger.LogInformation("OAuth flow completed for MCP profile {ProfileId}.", profile.Id);
                return result;
            },
        };
    }

    /// <summary>Releases any loopback reservation owned by an SDK OAuth option set.</summary>
    internal void ReleaseRedirectReservation(ClientOAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _callbackListener.ReleaseRedirectUri(options.RedirectUri);
    }

    private DynamicClientRegistrationOptions CreateDynamicClientRegistrationOptions(string profileId, Uri redirectUri)
    {
        return new DynamicClientRegistrationOptions
        {
            ClientName = "Threadsmith.NET",
            ApplicationType = "native",
            ResponseDelegate = async (response, cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(response);
                await StageRegistrationAsync(
                    Prefix(profileId),
                    redirectUri,
                    response.ClientId,
                    response.ClientSecret,
                    response.TokenEndpointAuthMethod,
                    cancellationToken);
            },
        };
    }

    private async Task StageRegistrationAsync(
        string prefix,
        Uri redirectUri,
        string clientId,
        string? clientSecret,
        string? tokenEndpointAuthMethod,
        CancellationToken cancellationToken)
    {
        var pendingPrefix = PendingRegistrationPrefix(prefix);
        await _tokenStore.ApplyAsync(
            new McpOAuthTokenStoreMutation
            {
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [pendingPrefix + "clientSecret"] = clientSecret ?? string.Empty,
                    [pendingPrefix + "tokenEndpointAuthMethod"] = tokenEndpointAuthMethod ?? string.Empty,
                    [pendingPrefix + "redirectUri"] = redirectUri.AbsoluteUri,
                    [pendingPrefix + "clientId"] = clientId,
                },
                RemovedReferences = [prefix + "pendingRegistrationGeneration"],
                RemovedPrefixes = [pendingPrefix, prefix + "registration:"],
            },
            cancellationToken);
    }

    private static string Prefix(string profileId)
    {
        return $"mcp:oauth:{profileId}:";
    }

    private static string GrantPrefix(string prefix)
    {
        return $"{prefix}grant:";
    }

    private static string PendingRegistrationPrefix(string prefix)
    {
        return $"{prefix}pendingRegistration:";
    }

    private static Uri CreateNoninteractiveRedirectUri(int requestedPort)
    {
        var authority = requestedPort == 0
            ? "localhost"
            : $"localhost:{requestedPort.ToString(CultureInfo.InvariantCulture)}";
        return new Uri($"http://{authority}/callback", UriKind.Absolute);
    }

    private static AuthorizationResult ParseCallback(Uri expectedRedirectUri, Uri callbackUri)
    {
        if (!string.Equals(expectedRedirectUri.Scheme, callbackUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expectedRedirectUri.Host, callbackUri.Host, StringComparison.OrdinalIgnoreCase)
            || expectedRedirectUri.Port != callbackUri.Port
            || !string.Equals(expectedRedirectUri.AbsolutePath, callbackUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The OAuth callback URI does not match the configured loopback redirect URI.");
        }

        var query = ParseQuery(callbackUri.Query);
        if (query.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException($"The authorization server rejected the OAuth request: {error}.");
        }

        query.TryGetValue("code", out var code);
        query.TryGetValue("state", out var state);
        query.TryGetValue("iss", out var issuer);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            throw new InvalidOperationException("The OAuth callback did not contain the required code and state values.");
        }

        return new AuthorizationResult { Code = code, State = state, Iss = issuer };
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private sealed class SecretStoreTokenCache(
        string profileId,
        Uri redirectUri,
        bool dynamicRegistrationEnabled,
        bool requireExactRedirectUri,
        IMcpOAuthTokenStore tokenStore) : ITokenCache
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _prefix = $"mcp:oauth:{profileId}:";
        private readonly Uri _redirectUri = redirectUri;

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            var snapshot = await tokenStore.GetSnapshotAsync(
                _prefix,
                cancellationToken);
            var current = LoadGrant(snapshot, GrantPrefix(_prefix));
            if (current is not null)
            {
                return current;
            }

            var activeGeneration = GetValue(snapshot, _prefix + "activeGrantGeneration");
            if (!string.IsNullOrWhiteSpace(activeGeneration))
            {
                var generated = LoadGrant(snapshot, $"{_prefix}grant:{activeGeneration}:");
                if (generated is not null)
                {
                    return generated;
                }
            }

            return LoadLegacyGrant(snapshot);
        }

        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(tokens);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var mutation = await CreateGrantMutationAsync(tokens, cancellationToken);
                await tokenStore.ApplyAsync(mutation, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private TokenContainer? LoadGrant(
            IReadOnlyDictionary<string, string> snapshot,
            string grantPrefix)
        {
            var token = LoadTokenFields(snapshot, grantPrefix);
            if (token is null)
            {
                return null;
            }

            var registration = LoadRegistration(snapshot, grantPrefix);
            if (dynamicRegistrationEnabled
                && requireExactRedirectUri
                && !string.Equals(registration.RedirectUri, _redirectUri.AbsoluteUri, StringComparison.Ordinal))
            {
                registration = CachedClientRegistration.Empty;
            }

            token.ClientId = registration.ClientId;
            token.ClientSecret = registration.ClientSecret;
            token.TokenEndpointAuthMethod = registration.TokenEndpointAuthMethod;
            return token;
        }

        private TokenContainer? LoadLegacyGrant(IReadOnlyDictionary<string, string> snapshot)
        {
            var token = LoadTokenFields(snapshot, _prefix);
            if (token is null)
            {
                return null;
            }

            token.ClientId = GetValue(snapshot, _prefix + "clientId");
            token.ClientSecret = GetValue(snapshot, _prefix + "clientSecret");
            token.TokenEndpointAuthMethod = GetValue(snapshot, _prefix + "tokenEndpointAuthMethod");
            if (dynamicRegistrationEnabled
                && requireExactRedirectUri
                && !string.Equals(GetValue(snapshot, _prefix + "redirectUri"), _redirectUri.AbsoluteUri, StringComparison.Ordinal))
            {
                token.ClientId = null;
                token.ClientSecret = null;
                token.TokenEndpointAuthMethod = null;
            }

            return token;
        }

        private static TokenContainer? LoadTokenFields(
            IReadOnlyDictionary<string, string> snapshot,
            string prefix)
        {
            var accessToken = GetValue(snapshot, prefix + "accessToken");
            var obtainedAtValue = GetValue(snapshot, prefix + "obtainedAt");
            if (string.IsNullOrWhiteSpace(accessToken)
                || !DateTimeOffset.TryParse(
                    obtainedAtValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var obtainedAt))
            {
                return null;
            }

            var expiresAtValue = GetValue(snapshot, prefix + "expiresAt");
            int? expiresIn = DateTimeOffset.TryParse(
                expiresAtValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt)
                ? Math.Max(0, (int)(expiresAt - obtainedAt).TotalSeconds)
                : null;
            return new TokenContainer
            {
                TokenType = GetValue(snapshot, prefix + "tokenType") ?? "Bearer",
                AccessToken = accessToken,
                RefreshToken = GetValue(snapshot, prefix + "refreshToken"),
                ExpiresIn = expiresIn,
                Scope = GetValue(snapshot, prefix + "scope"),
                ObtainedAt = obtainedAt,
                AuthorizationServer = GetValue(snapshot, prefix + "authorizationServer"),
            };
        }

        private async Task<McpOAuthTokenStoreMutation> CreateGrantMutationAsync(
            TokenContainer tokens,
            CancellationToken cancellationToken)
        {
            var grantPrefix = GrantPrefix(_prefix);
            var registration = dynamicRegistrationEnabled
                ? await LoadPendingRegistrationAsync(tokens.ClientId, cancellationToken)
                : CachedClientRegistration.Empty;
            if (string.IsNullOrWhiteSpace(registration.ClientId))
            {
                registration = CachedClientRegistration.FromToken(tokens, _redirectUri);
            }

            var expiresAt = tokens.ExpiresIn is int expiresIn
                ? tokens.ObtainedAt.AddSeconds(expiresIn).ToString("O", CultureInfo.InvariantCulture)
                : string.Empty;
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [grantPrefix + "accessToken"] = tokens.AccessToken,
                [grantPrefix + "refreshToken"] = tokens.RefreshToken ?? string.Empty,
                [grantPrefix + "obtainedAt"] = tokens.ObtainedAt.ToString("O", CultureInfo.InvariantCulture),
                [grantPrefix + "expiresAt"] = expiresAt,
                [grantPrefix + "tokenType"] = tokens.TokenType,
                [grantPrefix + "scope"] = tokens.Scope ?? string.Empty,
                [grantPrefix + "authorizationServer"] = tokens.AuthorizationServer ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(registration.ClientId))
            {
                values[grantPrefix + "clientSecret"] = registration.ClientSecret ?? string.Empty;
                values[grantPrefix + "tokenEndpointAuthMethod"] = registration.TokenEndpointAuthMethod ?? string.Empty;
                values[grantPrefix + "redirectUri"] = registration.RedirectUri ?? _redirectUri.AbsoluteUri;
                values[grantPrefix + "clientId"] = registration.ClientId;
            }

            return new McpOAuthTokenStoreMutation
            {
                Values = values,
                RemovedReferences =
                [
                    _prefix + "activeGrantGeneration",
                    _prefix + "pendingRegistrationGeneration",
                    _prefix + "accessToken",
                    _prefix + "refreshToken",
                    _prefix + "obtainedAt",
                    _prefix + "expiresAt",
                    _prefix + "tokenType",
                    _prefix + "scope",
                    _prefix + "authorizationServer",
                    _prefix + "clientId",
                    _prefix + "clientSecret",
                    _prefix + "tokenEndpointAuthMethod",
                ],
                RemovedPrefixes =
                [
                    grantPrefix,
                    PendingRegistrationPrefix(_prefix),
                    _prefix + "registration:",
                ],
            };
        }

        private async Task<CachedClientRegistration> LoadPendingRegistrationAsync(
            string? expectedClientId,
            CancellationToken cancellationToken)
        {
            var snapshot = await tokenStore.GetSnapshotAsync(
                _prefix,
                cancellationToken);
            var registration = LoadRegistration(snapshot, PendingRegistrationPrefix(_prefix));
            if (string.IsNullOrWhiteSpace(registration.ClientId))
            {
                var generation = GetValue(snapshot, _prefix + "pendingRegistrationGeneration");
                if (!string.IsNullOrWhiteSpace(generation))
                {
                    registration = LoadRegistration(snapshot, $"{_prefix}registration:{generation}:");
                }
            }

            return string.Equals(registration.ClientId, expectedClientId, StringComparison.Ordinal)
                && string.Equals(registration.RedirectUri, _redirectUri.AbsoluteUri, StringComparison.Ordinal)
                ? registration
                : CachedClientRegistration.Empty;
        }

        private static CachedClientRegistration LoadRegistration(
            IReadOnlyDictionary<string, string> snapshot,
            string registrationPrefix)
        {
            var clientId = GetValue(snapshot, registrationPrefix + "clientId");
            return string.IsNullOrWhiteSpace(clientId)
                ? CachedClientRegistration.Empty
                : new CachedClientRegistration(
                    clientId,
                    GetValue(snapshot, registrationPrefix + "clientSecret"),
                    GetValue(snapshot, registrationPrefix + "tokenEndpointAuthMethod"),
                    GetValue(snapshot, registrationPrefix + "redirectUri"));
        }

        private static string? GetValue(IReadOnlyDictionary<string, string> snapshot, string key)
        {
            return snapshot.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }
    }

    private sealed record CachedClientRegistration(
        string? ClientId,
        string? ClientSecret,
        string? TokenEndpointAuthMethod,
        string? RedirectUri)
    {
        public static CachedClientRegistration Empty { get; } = new(null, null, null, null);

        public static CachedClientRegistration FromToken(TokenContainer tokens, Uri redirectUri)
        {
            return new(
                tokens.ClientId,
                tokens.ClientSecret,
                tokens.TokenEndpointAuthMethod,
                redirectUri.AbsoluteUri);
        }
    }
}
