namespace Threadsmith.Mcp;

using System.Net;
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
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            // Bounded pool lifetime refreshes DNS/endpoint changes while reusing connections;
            // matches the model-transport host default. See Plan 67 (AR-04).
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        })
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

        var accessToken = await _tokenStore.GetAsync(Prefix(profile) + "accessToken", cancellationToken);
        var refreshToken = await _tokenStore.GetAsync(Prefix(profile) + "refreshToken", cancellationToken);
        return string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken)
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
        var accessToken = await _tokenStore.GetAsync(prefix + "accessToken", cancellationToken);
        var refreshToken = await _tokenStore.GetAsync(prefix + "refreshToken", cancellationToken);
        var authorizationServer = await _tokenStore.GetAsync(prefix + "authorizationServer", cancellationToken);
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
            using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, metadataUri);
            using HttpResponseMessage metadataResponse = await _httpClient.SendAsync(
                metadataRequest,
                HttpCompletionOption.ResponseHeadersRead,
                metadataCancellation.Token);
            if (!metadataResponse.IsSuccessStatusCode)
            {
                return await HandleUnconfirmedAsync(
                    profile,
                    allowLocalCleanupAfterUnconfirmedRevocation,
                    "Authorization-server metadata could not confirm a revocation endpoint.",
                    cancellationToken);
            }

            using JsonDocument metadata = await ReadBoundedMetadataAsync(
                metadataResponse.Content,
                metadataCancellation.Token);
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
        if (!string.IsNullOrWhiteSpace(oauth.ClientId))
        {
            fields["client_id"] = oauth.ClientId;
        }

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
            fields["client_secret"] = resolution.RequireValue(request);
        }

        using var revokeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        revokeCancellation.CancelAfter(profile.RequestTimeout);
        try
        {
            using var revokeRequest = new HttpRequestMessage(HttpMethod.Post, revocationEndpoint)
            {
                Content = new FormUrlEncodedContent(fields),
            };
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

    private static string Prefix(McpConnectionProfile profile)
    {
        return $"mcp:oauth:{profile.Id}:";
    }

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
