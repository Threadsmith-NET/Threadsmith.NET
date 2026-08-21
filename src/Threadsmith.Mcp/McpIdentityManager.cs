namespace Threadsmith.Mcp;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Host-owned OAuth identity lifecycle boundary for MCP profiles.</summary>
public interface IMcpIdentityManager
{
    /// <summary>Gets a coarse identity state without exposing tokens or account claims.</summary>
    Task<McpAuthenticationState> GetStateAsync(
        McpConnectionProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically removes the exact profile's local OAuth token namespace.</summary>
    Task<McpIdentityMutationResult> LogoutAsync(
        McpConnectionProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts advertised RFC 7009 revocation and applies explicit local-cleanup policy.</summary>
    Task<McpIdentityMutationResult> RevokeAsync(
        McpConnectionProfile profile,
        bool allowLocalCleanupAfterUnconfirmedRevocation,
        CancellationToken cancellationToken = default);
}

/// <summary>Sanitized outcome of an MCP identity mutation.</summary>
public sealed record McpIdentityMutationResult
{
    /// <summary>Whether the requested identity mutation completed.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Closed failure classification.</summary>
    public McpManagementFailureKind FailureKind { get; init; }

    /// <summary>Whether local cached identity fields were removed.</summary>
    public bool LocalIdentityCleared { get; init; }

    /// <summary>Whether remote revocation was protocol-confirmed.</summary>
    public bool RemoteRevocationConfirmed { get; init; }

    /// <summary>Bounded sanitized user-facing outcome.</summary>
    public required string Message { get; init; }
}

/// <summary>OAuth cache inspection, local logout, and advertised remote revocation implementation.</summary>
public sealed class McpIdentityManager : IMcpIdentityManager, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ISecretResolver _secretResolver;
    private readonly IMcpOAuthTokenStore _tokenStore;

    /// <summary>Initializes a new instance of the <see cref="McpIdentityManager"/> class.</summary>
    public McpIdentityManager(
        IMcpOAuthTokenStore tokenStore,
        ISecretResolver secretResolver,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(secretResolver);
        _tokenStore = tokenStore;
        _secretResolver = secretResolver;
        _httpClient = httpClient ?? new HttpClient(new McpBoundedHttpResponseHandler(
            SdkHttpTransport.CreateMetadataCompatibilityHandler()))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ownsHttpClient = httpClient is null;
    }

    /// <inheritdoc />
    public async Task<McpAuthenticationState> GetStateAsync(
        McpConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.OAuth?.Enabled is not true)
        {
            return McpAuthenticationState.NotApplicable;
        }

        var cachedGrant = await LoadCachedGrantAsync(Prefix(profile), cancellationToken);
        return string.IsNullOrWhiteSpace(cachedGrant.AccessToken) && string.IsNullOrWhiteSpace(cachedGrant.RefreshToken)
            ? McpAuthenticationState.SignedOut
            : McpAuthenticationState.Cached;
    }

    /// <inheritdoc />
    public async Task<McpIdentityMutationResult> LogoutAsync(
        McpConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.OAuth?.Enabled is not true)
        {
            return Unsupported();
        }

        await _tokenStore.RemovePrefixAsync(Prefix(profile), cancellationToken);
        return new McpIdentityMutationResult
        {
            Succeeded = true,
            FailureKind = McpManagementFailureKind.None,
            LocalIdentityCleared = true,
            Message = "Local OAuth identity cleared; remote authorization was not revoked.",
        };
    }

    /// <inheritdoc />
    public async Task<McpIdentityMutationResult> RevokeAsync(
        McpConnectionProfile profile,
        bool allowLocalCleanupAfterUnconfirmedRevocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.OAuth?.Enabled is not true)
        {
            return Unsupported();
        }

        var prefix = Prefix(profile);
        var cachedGrant = await LoadCachedGrantAsync(prefix, cancellationToken);
        var accessToken = cachedGrant.AccessToken;
        var refreshToken = cachedGrant.RefreshToken;
        var authorizationServer = cachedGrant.AuthorizationServer;
        if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
        {
            await _tokenStore.RemovePrefixAsync(prefix, cancellationToken);
            return new McpIdentityMutationResult
            {
                Succeeded = true,
                FailureKind = McpManagementFailureKind.None,
                LocalIdentityCleared = true,
                Message = "No cached OAuth identity remained to revoke.",
            };
        }

        if (!TryCreateSecureUri(authorizationServer, out Uri? issuer))
        {
            return new McpIdentityMutationResult
            {
                Succeeded = false,
                FailureKind = McpManagementFailureKind.RevocationUnsupported,
                Message = "The cached identity has no valid HTTPS authorization-server metadata origin; remote revocation is unsupported.",
            };
        }

        Uri validatedIssuer = issuer
            ?? throw new InvalidOperationException("The authorization-server URI was not available after validation.");
        Uri metadataUri = BuildMetadataUri(validatedIssuer);
        Uri? revocationEndpoint;
        using var metadataCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        metadataCancellation.CancelAfter(profile.RequestTimeout);
        try
        {
            using var metadata = await ReadRevocationMetadataAsync(
                profile,
                metadataUri,
                metadataCancellation.Token);
            if (metadata is null)
            {
                return await HandleUnconfirmedAsync(
                    profile,
                    allowLocalCleanupAfterUnconfirmedRevocation,
                    "Authorization-server metadata could not confirm a revocation endpoint.",
                    cancellationToken);
            }

            var endpoint = metadata.RootElement.TryGetProperty("revocation_endpoint", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            if (!TryCreateSecureUri(endpoint, out revocationEndpoint)
                || revocationEndpoint is null
                || !HasSameOrigin(validatedIssuer, revocationEndpoint))
            {
                return new McpIdentityMutationResult
                {
                    Succeeded = false,
                    FailureKind = McpManagementFailureKind.RevocationUnsupported,
                    Message = "The authorization server does not advertise a same-origin HTTPS RFC 7009 revocation endpoint.",
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await HandleUnconfirmedAsync(
                profile,
                allowLocalCleanupAfterUnconfirmedRevocation,
                "Authorization-server metadata retrieval timed out; remote revocation is unconfirmed.",
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return await HandleUnconfirmedAsync(
                profile,
                allowLocalCleanupAfterUnconfirmedRevocation,
                "Authorization-server metadata retrieval failed; remote revocation is unconfirmed.",
                cancellationToken);
        }

        var token = refreshToken ?? accessToken ?? string.Empty;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["token"] = token,
            ["token_type_hint"] = refreshToken is null ? "access_token" : "refresh_token",
        };
        McpOAuthOptions oauth = profile.OAuth
            ?? throw new InvalidOperationException("The OAuth profile configuration became unavailable.");
        var dynamicallyRegistered = oauth.ClientMode == McpOAuthClientMode.DynamicRegistration;
        var clientId = dynamicallyRegistered ? cachedGrant.ClientId : oauth.ClientId;
        string? clientSecret = dynamicallyRegistered ? cachedGrant.ClientSecret : null;

        if (oauth.ClientSecret is { } secretReference)
        {
            var request = new SecretResolutionRequest
            {
                Reference = SecretReference.Parse(secretReference),
                ComponentId = SecretResolutionRequest.CreateConfiguredComponentId("mcp:oauth-revoke", profile.Id),
                Purpose = "authenticate an MCP OAuth token-revocation request",
                MinimumTrust = SecretProviderTrust.UserOwned,
            };
            SecretResolutionResult resolution = await _secretResolver.ResolveAsync(request, cancellationToken);
            clientSecret = resolution.RequireValue(request);
        }

        var authenticationMethod = cachedGrant.TokenEndpointAuthMethod
            ?? (string.IsNullOrWhiteSpace(clientSecret) ? "none" : "client_secret_post");
        AuthenticationHeaderValue? authorization = null;
        switch (authenticationMethod)
        {
            case "none":
                if (!string.IsNullOrWhiteSpace(clientId))
                {
                    fields["client_id"] = clientId;
                }

                break;
            case "client_secret_post" when !string.IsNullOrWhiteSpace(clientId)
                && !string.IsNullOrWhiteSpace(clientSecret):
                fields["client_id"] = clientId;
                fields["client_secret"] = clientSecret;
                break;
            case "client_secret_basic" when !string.IsNullOrWhiteSpace(clientId)
                && !string.IsNullOrWhiteSpace(clientSecret):
                var userName = WebUtility.UrlEncode(clientId);
                var password = WebUtility.UrlEncode(clientSecret);
                var parameter = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
                authorization = new AuthenticationHeaderValue("Basic", parameter);
                break;
            default:
            {
                return new McpIdentityMutationResult
                {
                    Succeeded = false,
                    FailureKind = McpManagementFailureKind.RevocationUnsupported,
                    Message = "The cached OAuth client authentication method is unsupported for RFC 7009 revocation.",
                };
            }
        }

        using var revokeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        revokeCancellation.CancelAfter(profile.RequestTimeout);
        try
        {
            using var revokeRequest = new HttpRequestMessage(HttpMethod.Post, revocationEndpoint)
            {
                Content = new FormUrlEncodedContent(fields),
            };
            revokeRequest.Headers.Authorization = authorization;
            using HttpResponseMessage revokeResponse = await _httpClient.SendAsync(
                revokeRequest,
                HttpCompletionOption.ResponseHeadersRead,
                revokeCancellation.Token);
            if (revokeResponse.IsSuccessStatusCode)
            {
                await _tokenStore.RemovePrefixAsync(prefix, cancellationToken);
                return new McpIdentityMutationResult
                {
                    Succeeded = true,
                    FailureKind = McpManagementFailureKind.None,
                    LocalIdentityCleared = true,
                    RemoteRevocationConfirmed = true,
                    Message = "Remote OAuth revocation confirmed and the local identity cleared.",
                };
            }

            if (revokeResponse.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                return await HandleUnconfirmedAsync(
                    profile,
                    allowLocalCleanupAfterUnconfirmedRevocation,
                    "The authorization server did not confirm remote revocation.",
                    cancellationToken);
            }

            return await HandleUnconfirmedAsync(
                profile,
                allowLocalCleanupAfterUnconfirmedRevocation,
                "The remote revocation service returned a transient or ambiguous failure.",
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await HandleUnconfirmedAsync(
                profile,
                allowLocalCleanupAfterUnconfirmedRevocation,
                "The remote revocation request timed out; remote revocation is unconfirmed.",
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return await HandleUnconfirmedAsync(
                profile,
                allowLocalCleanupAfterUnconfirmedRevocation,
                "The remote revocation request failed; remote revocation is unconfirmed.",
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<McpIdentityMutationResult> HandleUnconfirmedAsync(
        McpConnectionProfile profile,
        bool allowLocalCleanup,
        string message,
        CancellationToken cancellationToken)
    {
        if (allowLocalCleanup)
        {
            await _tokenStore.RemovePrefixAsync(Prefix(profile), cancellationToken);
        }

        return new McpIdentityMutationResult
        {
            Succeeded = allowLocalCleanup,
            FailureKind = McpManagementFailureKind.RemoteRevocationUnconfirmed,
            LocalIdentityCleared = allowLocalCleanup,
            Message = allowLocalCleanup
                ? message + " Local identity was cleared by explicit request."
                : message + " Retry or explicitly allow local-only cleanup.",
        };
    }

    private async Task<JsonDocument?> ReadRevocationMetadataAsync(
        McpConnectionProfile profile,
        Uri metadataUri,
        CancellationToken cancellationToken)
    {
        var metadata = await TryReadMetadataAsync(metadataUri, cancellationToken);
        if (metadata is not null
            && metadata.RootElement.TryGetProperty("revocation_endpoint", out var endpoint)
            && endpoint.ValueKind == JsonValueKind.String)
        {
            return metadata;
        }

        // A prior authentication may have followed validated protected-resource metadata to a
        // proxy-owned authorization document. Re-run that same compatibility path after restart,
        // then retry the canonical location served from the handler's validated metadata cache.
        if (Uri.TryCreate(profile.Command, UriKind.Absolute, out var resourceUri)
            && resourceUri.Scheme == Uri.UriSchemeHttps)
        {
            var protectedResourceMetadataUri = BuildProtectedResourceMetadataUri(resourceUri);
            using var compatibilityRequest = new HttpRequestMessage(HttpMethod.Get, protectedResourceMetadataUri);
            using var compatibilityResponse = await _httpClient.SendAsync(
                compatibilityRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (compatibilityResponse.IsSuccessStatusCode)
            {
                var compatibleMetadata = await TryReadMetadataAsync(metadataUri, cancellationToken);
                if (compatibleMetadata is not null)
                {
                    metadata?.Dispose();
                    return compatibleMetadata;
                }
            }
        }

        return metadata;
    }

    private async Task<JsonDocument?> TryReadMetadataAsync(
        Uri metadataUri,
        CancellationToken cancellationToken)
    {
        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, metadataUri);
        using var metadataResponse = await _httpClient.SendAsync(
            metadataRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return metadataResponse.IsSuccessStatusCode
            ? await ReadBoundedMetadataAsync(metadataResponse.Content, cancellationToken)
            : null;
    }

    private static async Task<JsonDocument> ReadBoundedMetadataAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 64 * 1024;
        if (content.Headers.ContentLength is > maximumBytes)
        {
            throw new InvalidDataException("Authorization-server metadata exceeds the host bound.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maximumBytes + 1];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset > maximumBytes)
        {
            throw new InvalidDataException("Authorization-server metadata exceeds the host bound.");
        }

        return JsonDocument.Parse(
            buffer.AsMemory(0, offset),
            new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static Uri BuildMetadataUri(Uri issuer)
    {
        var issuerPath = issuer.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(issuer.Scheme, issuer.Host, issuer.Port)
        {
            Path = "/.well-known/oauth-authorization-server" + issuerPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static Uri BuildProtectedResourceMetadataUri(Uri resourceUri)
    {
        var resourcePath = resourceUri.AbsolutePath.TrimStart('/');
        var builder = new UriBuilder(resourceUri.Scheme, resourceUri.Host, resourceUri.Port)
        {
            Path = "/.well-known/oauth-protected-resource"
                + (resourcePath.Length == 0 ? string.Empty : "/" + resourcePath),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static bool HasSameOrigin(Uri first, Uri second)
    {
        return string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase)
                && first.Port == second.Port;
    }

    private static bool TryCreateSecureUri(string? value, out Uri? uri)
    {
        var valid = Uri.TryCreate(value, UriKind.Absolute, out uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);
        if (!valid)
        {
            uri = null;
        }

        return valid;
    }

    private async Task<CachedGrant> LoadCachedGrantAsync(string prefix, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> snapshot = await _tokenStore.GetSnapshotAsync(prefix, cancellationToken);
        var currentGrantPrefix = $"{prefix}grant:";
        var currentAccessToken = GetValue(snapshot, currentGrantPrefix + "accessToken");
        if (!string.IsNullOrWhiteSpace(currentAccessToken))
        {
            return LoadCachedGrantFields(snapshot, currentGrantPrefix);
        }

        var generation = GetValue(snapshot, prefix + "activeGrantGeneration");
        var grantPrefix = string.IsNullOrWhiteSpace(generation)
            ? prefix
            : $"{prefix}grant:{generation}:";
        return LoadCachedGrantFields(snapshot, grantPrefix);
    }

    private static CachedGrant LoadCachedGrantFields(
        IReadOnlyDictionary<string, string> snapshot,
        string grantPrefix)
    {
        return new CachedGrant(
            GetValue(snapshot, grantPrefix + "accessToken"),
            GetValue(snapshot, grantPrefix + "refreshToken"),
            GetValue(snapshot, grantPrefix + "authorizationServer"),
            GetValue(snapshot, grantPrefix + "clientId"),
            GetValue(snapshot, grantPrefix + "clientSecret"),
            GetValue(snapshot, grantPrefix + "tokenEndpointAuthMethod"));
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> snapshot, string key)
    {
        return snapshot.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string Prefix(McpConnectionProfile profile)
    {
        return $"mcp:oauth:{profile.Id}:";
    }

    private sealed record CachedGrant(
        string? AccessToken,
        string? RefreshToken,
        string? AuthorizationServer,
        string? ClientId,
        string? ClientSecret,
        string? TokenEndpointAuthMethod);

    private static McpIdentityMutationResult Unsupported()
    {
        return new McpIdentityMutationResult
        {
            Succeeded = false,
            FailureKind = McpManagementFailureKind.UnsupportedAuthentication,
            Message = "This profile does not use host-owned OAuth; rotate or remove its external static credential instead.",
        };
    }
}
