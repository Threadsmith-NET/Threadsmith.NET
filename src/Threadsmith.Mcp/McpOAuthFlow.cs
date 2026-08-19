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

        if (oauth.DiscoveryUrl is not null)
        {
            throw new InvalidOperationException(
                $"MCP OAuth discoveryUrl is not supported for profile '{profile.Id}'; "
                + "the authorization server must be advertised by the MCP endpoint.");
        }

        string clientId = oauth.ClientId
            ?? throw new InvalidOperationException($"MCP OAuth profile '{profile.Id}' requires a pre-registered clientId.");
        string? clientSecretReference = oauth.ClientSecret;
        if (clientSecretReference is not null
            && (!clientSecretReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase)
                || !profile.SecretScope.Contains(clientSecretReference, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"MCP OAuth client secret reference '{clientSecretReference}' is outside profile '{profile.Id}' secretScope.");
        }

        string? clientSecret = null;
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

        return new ClientOAuthOptions
        {
            RedirectUri = redirectUri,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Scopes = oauth.Scopes,
            ScopeSelector = candidates => candidates?
                .Intersect(oauth.Scopes, StringComparer.Ordinal)
                .ToArray() ?? [],
            TokenCache = new SecretStoreTokenCache(profile.Id, _tokenStore),
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

    private static Uri CreateNoninteractiveRedirectUri(int requestedPort)
    {
        string authority = requestedPort == 0
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
        if (query.TryGetValue("error", out string? error))
        {
            throw new InvalidOperationException($"The authorization server rejected the OAuth request: {error}.");
        }

        query.TryGetValue("code", out string? code);
        query.TryGetValue("state", out string? state);
        query.TryGetValue("iss", out string? issuer);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            throw new InvalidOperationException("The OAuth callback did not contain the required code and state values.");
        }

        return new AuthorizationResult { Code = code, State = state, Iss = issuer };
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = item.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private sealed class SecretStoreTokenCache(string profileId, IMcpOAuthTokenStore tokenStore) : ITokenCache
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _prefix = $"mcp:oauth:{profileId}:";

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            string? accessToken = await tokenStore.GetAsync(_prefix + "accessToken", cancellationToken);
            string? obtainedAtValue = await tokenStore.GetAsync(_prefix + "obtainedAt", cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken)
                || !DateTimeOffset.TryParse(
                    obtainedAtValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var obtainedAt))
            {
                return null;
            }

            string? expiresAtValue = await tokenStore.GetAsync(_prefix + "expiresAt", cancellationToken);
            int? expiresIn = DateTimeOffset.TryParse(
                expiresAtValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt)
                ? Math.Max(0, (int)(expiresAt - obtainedAt).TotalSeconds)
                : null;
            return new TokenContainer
            {
                TokenType = await tokenStore.GetAsync(_prefix + "tokenType", cancellationToken) ?? "Bearer",
                AccessToken = accessToken,
                RefreshToken = await tokenStore.GetAsync(_prefix + "refreshToken", cancellationToken),
                ExpiresIn = expiresIn,
                Scope = await tokenStore.GetAsync(_prefix + "scope", cancellationToken),
                ObtainedAt = obtainedAt,
                ClientId = await tokenStore.GetAsync(_prefix + "clientId", cancellationToken),
                AuthorizationServer = await tokenStore.GetAsync(_prefix + "authorizationServer", cancellationToken),
            };
        }

        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(tokens);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await tokenStore.SetAsync(_prefix + "accessToken", tokens.AccessToken, cancellationToken);
                await tokenStore.SetAsync(_prefix + "refreshToken", tokens.RefreshToken ?? string.Empty, cancellationToken);
                await tokenStore.SetAsync(_prefix + "obtainedAt", tokens.ObtainedAt.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
                string expiresAt = tokens.ExpiresIn is int expiresIn
                    ? tokens.ObtainedAt.AddSeconds(expiresIn).ToString("O", CultureInfo.InvariantCulture)
                    : string.Empty;
                await tokenStore.SetAsync(_prefix + "expiresAt", expiresAt, cancellationToken);
                await tokenStore.SetAsync(_prefix + "tokenType", tokens.TokenType, cancellationToken);
                await tokenStore.SetAsync(_prefix + "scope", tokens.Scope ?? string.Empty, cancellationToken);
                await tokenStore.SetAsync(_prefix + "clientId", tokens.ClientId ?? string.Empty, cancellationToken);
                await tokenStore.SetAsync(
                    _prefix + "authorizationServer",
                    tokens.AuthorizationServer ?? string.Empty,
                    cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
