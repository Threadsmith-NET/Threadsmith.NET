namespace Threadsmith.Models.OpenAiCodex;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Public browser authorization challenge.</summary>
public sealed record OpenAiCodexAuthorizationChallenge(Uri AuthorizationUri, Uri RedirectUri, string State, string CodeVerifier);

/// <summary>Public device authorization challenge.</summary>
public sealed record OpenAiCodexDeviceChallenge(string DeviceAuthId, string UserCode, Uri VerificationUri, TimeSpan PollInterval);

/// <summary>Secret-free Codex authentication status.</summary>
public sealed record OpenAiCodexAuthenticationStatus(bool IsAuthenticated, DateTimeOffset? ExpiresAt, string? AccountId);

/// <summary>Threadsmith-owned OpenAI Codex OAuth and token-cache manager.</summary>
public sealed class OpenAiCodexOAuthManager : IDisposable
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private static readonly Uri AuthorizationEndpoint = new("https://auth.openai.com/oauth/authorize");
    private static readonly Uri TokenEndpoint = new("https://auth.openai.com/oauth/token");
    private static readonly Uri DeviceCodeEndpoint = new("https://auth.openai.com/api/accounts/deviceauth/usercode");
    private static readonly Uri DeviceTokenEndpoint = new("https://auth.openai.com/api/accounts/deviceauth/token");
    private static readonly Uri BrowserRedirectUri = new("http://localhost:1455/auth/callback");
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="OpenAiCodexOAuthManager"/> class.</summary>
    public OpenAiCodexOAuthManager(HttpClient httpClient, string? cachePath = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".threadsmith",
            "credentials",
            "openai-codex.json");
    }

    /// <summary>Creates a PKCE browser authorization challenge.</summary>
    public static OpenAiCodexAuthorizationChallenge CreateBrowserChallenge(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (redirectUri != BrowserRedirectUri)
        {
            throw new ArgumentException("The Codex redirect URI must match the compiled localhost callback.", nameof(redirectUri));
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var query = BuildQuery(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "threadsmith",
        });
        return new OpenAiCodexAuthorizationChallenge(
            new UriBuilder(AuthorizationEndpoint) { Query = query }.Uri,
            redirectUri,
            state,
            verifier);
    }

    /// <summary>Validates the callback and stores the exchanged Threadsmith grant.</summary>
    public async Task CompleteBrowserAsync(
        OpenAiCodexAuthorizationChallenge challenge,
        Uri callbackUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(callbackUri);
        var callbackTarget = new Uri(callbackUri.GetLeftPart(UriPartial.Path));
        if (callbackTarget != challenge.RedirectUri)
        {
            throw new InvalidOperationException("The Codex OAuth callback redirect target is invalid.");
        }

        var query = ParseQuery(callbackUri.Query);
        if (!query.TryGetValue("state", out var state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(challenge.State)))
        {
            throw new InvalidOperationException("The Codex OAuth callback state is invalid.");
        }

        if (query.TryGetValue("iss", out var issuer)
            && !string.Equals(issuer.TrimEnd('/'), "https://auth.openai.com", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Codex OAuth callback issuer is invalid.");
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("The Codex OAuth callback does not contain an authorization code.");
        }

        Dictionary<string, string> fields = new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = challenge.RedirectUri.AbsoluteUri,
            ["code_verifier"] = challenge.CodeVerifier,
        };
        var tokens = await PostTokenAsync(fields, cancellationToken).ConfigureAwait(false);
        await SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts the headless device authorization flow.</summary>
    public async Task<OpenAiCodexDeviceChallenge> StartDeviceAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateJsonRequest(DeviceCodeEndpoint, new { client_id = ClientId });
        using var document = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var deviceAuthId = RequiredString(root, "device_auth_id");
        var userCode = RequiredString(root, "user_code");
        var interval = root.TryGetProperty("interval", out var value) && value.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 1, 30)
            : 5;
        return new OpenAiCodexDeviceChallenge(
            deviceAuthId,
            userCode,
            new Uri("https://auth.openai.com/codex/device"),
            TimeSpan.FromSeconds(interval));
    }

    /// <summary>Polls and stores a device-flow grant.</summary>
    public async Task CompleteDeviceAsync(
        OpenAiCodexDeviceChallenge challenge,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        while (true)
        {
            using var deviceRequest = CreateJsonRequest(DeviceTokenEndpoint, new
            {
                device_auth_id = challenge.DeviceAuthId,
                user_code = challenge.UserCode,
            });
            try
            {
                using var device = await SendJsonAsync(deviceRequest, bounded.Token).ConfigureAwait(false);
                var authorizationCode = RequiredString(device.RootElement, "authorization_code");
                var verifier = RequiredString(device.RootElement, "code_verifier");
                Dictionary<string, string> fields = new()
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = ClientId,
                    ["code"] = authorizationCode,
                    ["code_verifier"] = verifier,
                    ["redirect_uri"] = "https://auth.openai.com/deviceauth/callback",
                };
                var tokens = await PostTokenAsync(fields, bounded.Token).ConfigureAwait(false);
                await SaveAsync(tokens, bounded.Token).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                await Task.Delay(challenge.PollInterval, bounded.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Returns a valid access token, refreshing it when required.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (envelope is null)
        {
            return null;
        }

        if (envelope.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return envelope.AccessToken;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            envelope = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                return null;
            }

            if (envelope.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return envelope.AccessToken;
            }

            return await RefreshAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Refreshes a conclusively rejected access token without overwriting a newer token generation.</summary>
    public async Task<string?> RefreshAccessTokenAsync(
        string rejectedAccessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedAccessToken);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                return null;
            }

            if (!string.Equals(envelope.AccessToken, rejectedAccessToken, StringComparison.Ordinal)
                && envelope.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return envelope.AccessToken;
            }

            return await RefreshAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Returns secret-free current authentication status.</summary>
    public async Task<OpenAiCodexAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return envelope is null
            ? new OpenAiCodexAuthenticationStatus(false, null, null)
            : new OpenAiCodexAuthenticationStatus(
                envelope.ExpiresAt > DateTimeOffset.UtcNow,
                envelope.ExpiresAt,
                OpenAiCodexTokenClaims.TryGetAccountId(envelope.AccessToken));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _refreshGate.Dispose();
    }

    /// <summary>Removes only Threadsmith's Codex grant.</summary>
    public Task LogoutAsync()
    {
        if (File.Exists(_cachePath))
        {
            File.Delete(_cachePath);
        }

        return Task.CompletedTask;
    }

    private async Task<TokenEnvelope> PostTokenAsync(
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        using var document = await SendJsonAsync(request, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var accessToken = RequiredString(root, "access_token");
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 60, 86_400 * 30)
            : 3600;
        return new TokenEnvelope(accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Codex authentication failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(TokenEnvelope envelope, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
        var content = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Options = FileOptions.Asynchronous,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<TokenEnvelope?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_cachePath);
            var envelope = await JsonSerializer.DeserializeAsync<TokenEnvelope>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return IsValid(envelope) ? envelope : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string?> RefreshAsync(TokenEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(envelope.RefreshToken))
        {
            return null;
        }

        Dictionary<string, string> fields = new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = envelope.RefreshToken,
        };
        var refreshed = await PostTokenAsync(fields, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshed.RefreshToken))
        {
            refreshed = refreshed with { RefreshToken = envelope.RefreshToken };
        }

        await SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
        return refreshed.AccessToken;
    }

    private static bool IsValid(TokenEnvelope? envelope)
    {
        return envelope is not null
        && !string.IsNullOrWhiteSpace(envelope.AccessToken)
        && envelope.ExpiresAt > DateTimeOffset.UnixEpoch
        && envelope.ExpiresAt <= DateTimeOffset.UtcNow.AddDays(31);
    }

    private static HttpRequestMessage CreateJsonRequest(Uri endpoint, object value)
    {
        return new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.GetString() is { Length: > 0 } result
            ? result
            : throw new InvalidDataException($"The Codex authentication response is missing {propertyName}.");
    }

    private static string BuildQuery(IReadOnlyDictionary<string, string> values)
    {
        return string.Join(
        "&",
        values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?').Split(
        '&', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Split('=', 2)).ToDictionary(
            pair => Uri.UnescapeDataString(pair[0]),
            pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty,
            StringComparer.Ordinal);
    }

    private static string Base64Url(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed record TokenEnvelope(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
}

/// <summary>Extracts non-secret routing claims from a Codex access token.</summary>
internal static class OpenAiCodexTokenClaims
{
    /// <summary>Returns the authenticated ChatGPT account identifier when present.</summary>
    public static string? TryGetAccountId(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            if (root.TryGetProperty("https://api.openai.com/auth", out var auth)
                && auth.TryGetProperty("chatgpt_account_id", out var account))
            {
                return account.GetString();
            }

            return root.TryGetProperty("chatgpt_account_id", out var direct) ? direct.GetString() : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }
}
