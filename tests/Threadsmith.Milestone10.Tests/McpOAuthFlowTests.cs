namespace Threadsmith.Milestone10.Tests;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using Threadsmith.Mcp;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies host-owned MCP OAuth UX, token caching, and configuration safety.</summary>
public sealed class McpOAuthFlowTests
{
    /// <summary>The SDK OAuth options use the host callback and preserve state and issuer validation inputs.</summary>
    [Fact]
    public async Task Authorization_callback_uses_host_owned_ux()
    {
        var browser = new RecordingBrowserLauncher();
        var listener = new CallbackListener();
        var tokens = new DictionaryTokenStore();
        var flow = CreateFlow(browser, listener, tokens);
        var profile = CreateProfile();

        var options = await flow.CreateOptionsAsync(profile, TestContext.Current.CancellationToken);
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://identity.example/authorize?state=expected"),
            RedirectUri = options.RedirectUri,
        };
        listener.Callback = new Uri(options.RedirectUri, "?code=code-value&state=expected&iss=https%3A%2F%2Fidentity.example");
        var result = await options.AuthorizationCallbackHandler!(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(context.AuthorizationUri, browser.AuthorizationUri);
        Assert.Equal("code-value", result?.Code);
        Assert.Equal("expected", result?.State);
        Assert.Equal("https://identity.example", result?.Iss);
    }

    /// <summary>Automatic connection cannot open a browser or request pasted callback input.</summary>
    [Fact]
    public async Task Authorization_callback_without_interaction_authority_fails_before_ux()
    {
        var browser = new RecordingBrowserLauncher();
        var listener = new CallbackListener();
        var flow = CreateFlow(browser, listener, new DictionaryTokenStore());
        var profile = CreateProfile() with { AllowOAuthUserInteraction = false };
        var options = await flow.CreateOptionsAsync(profile, TestContext.Current.CancellationToken);
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://identity.example/authorize?state=expected"),
            RedirectUri = options.RedirectUri,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => options.AuthorizationCallbackHandler!(context, TestContext.Current.CancellationToken));

        Assert.Contains("explicit connect or authentication", exception.Message, StringComparison.Ordinal);
        Assert.Null(browser.AuthorizationUri);
        Assert.False(listener.WaitStarted);
    }

    /// <summary>Advertised authorization scopes cannot widen the profile's configured grant.</summary>
    [Fact]
    public async Task Scope_selector_intersects_advertised_and_configured_scopes()
    {
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), new DictionaryTokenStore());

        var options = await flow.CreateOptionsAsync(
            CreateProfile(),
            TestContext.Current.CancellationToken);
        var selected = options.ScopeSelector?.Invoke(
            ["tools.admin", "tools.read", "tools.write", "tools.read"]);

        Assert.Equal(["tools.read"], selected);
    }

    /// <summary>A directly supplied profile cannot resolve an OAuth client secret outside its exact scope.</summary>
    [Theory]
    [InlineData("secrets:CLIENT_SECRET", "secrets:client_secret")]
    [InlineData("CLIENT_SECRET", "CLIENT_SECRET")]
    public async Task Client_secret_requires_logical_reference_and_exact_scope_membership(
        string clientSecret,
        string scopedSecret)
    {
        var secrets = new DictionarySecretStore(clientSecret, "resolved-secret");
        var flow = CreateFlow(
            new RecordingBrowserLauncher(),
            new CallbackListener(),
            new DictionaryTokenStore(),
            secrets);
        var profile = CreateProfile() with
        {
            SecretScope = [scopedSecret],
            OAuth = (CreateProfile().OAuth
                ?? throw new InvalidOperationException("The test OAuth profile is required.")) with
            {
                ClientSecret = clientSecret,
            },
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.CreateOptionsAsync(profile, TestContext.Current.CancellationToken));

        Assert.Contains("outside profile 'remote' secretScope", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, secrets.ReadCount);
    }

    /// <summary>The localhost callback is waiting before a browser can redirect to it.</summary>
    [Fact]
    public async Task Authorization_callback_starts_listener_before_browser_launch()
    {
        var listener = new CallbackListener();
        var browser = new RecordingBrowserLauncher(() => Assert.True(listener.WaitStarted));
        var flow = CreateFlow(browser, listener, new DictionaryTokenStore());
        var options = await flow.CreateOptionsAsync(
            CreateProfile(),
            TestContext.Current.CancellationToken);
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://identity.example/authorize?state=expected"),
            RedirectUri = options.RedirectUri,
        };
        listener.Callback = new Uri(options.RedirectUri, "?code=code-value&state=expected");

        await options.AuthorizationCallbackHandler!(context, TestContext.Current.CancellationToken);

        Assert.True(listener.WaitStarted);
    }

    /// <summary>An automatically selected loopback port remains bound until the callback is accepted.</summary>
    [Fact]
    public async Task Automatic_redirect_port_remains_reserved_until_callback_wait()
    {
        var listener = new LoopbackOAuthCallbackListener();
        var redirectUri = listener.ReserveRedirectUri(0);
        var competingListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, redirectUri.Port);

        Assert.Throws<System.Net.Sockets.SocketException>(competingListener.Start);

        var callbackTask = listener.WaitForCallbackAsync(redirectUri, TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        using var response = await client.GetAsync(
            redirectUri + "?code=code-value&state=expected",
            TestContext.Current.CancellationToken);
        var received = await callbackTask;

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("localhost", redirectUri.Host);
        Assert.Equal("code-value", ParseQuery(received.Query)["code"]);
    }

    /// <summary>An unused callback reservation is explicitly releasable by its connection-attempt owner.</summary>
    [Fact]
    public void Unused_redirect_reservation_can_be_released()
    {
        var listener = new LoopbackOAuthCallbackListener();
        var redirectUri = listener.ReserveRedirectUri(0);

        listener.ReleaseRedirectUri(redirectUri);

        var competingListener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        try
        {
            competingListener.Start();
        }
        finally
        {
            competingListener.Stop();
        }
    }

    /// <summary>A live OAuth transport can reacquire its registered callback after an earlier authorization.</summary>
    [Fact]
    public async Task Callback_listener_renews_exact_redirect_uri_for_reauthorization()
    {
        var listener = new LoopbackOAuthCallbackListener();
        var redirectUri = listener.ReserveRedirectUri(0);

        var firstCallback = listener.WaitForCallbackAsync(
            redirectUri,
            TestContext.Current.CancellationToken);
        using (var firstClient = new HttpClient())
        using (var firstResponse = await firstClient.GetAsync(
            redirectUri + "?code=first&state=expected",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }

        Assert.Equal("first", ParseQuery((await firstCallback).Query)["code"]);

        var secondCallback = listener.WaitForCallbackAsync(
            redirectUri,
            TestContext.Current.CancellationToken);
        using (var secondClient = new HttpClient())
        using (var secondResponse = await secondClient.GetAsync(
            redirectUri + "?code=second&state=expected",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        }

        Assert.Equal("second", ParseQuery((await secondCallback).Query)["code"]);
    }

    /// <summary>Loopback callback parsing rejects overlong request lines without retaining the port.</summary>
    [Fact]
    public async Task Loopback_callback_rejects_overlong_request_line_and_releases_port()
    {
        var listener = new LoopbackOAuthCallbackListener();
        var redirectUri = listener.ReserveRedirectUri(0);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, redirectUri.Port, TestContext.Current.CancellationToken);
        var rejection = Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.WaitForCallbackAsync(redirectUri, TestContext.Current.CancellationToken));
        await client.GetStream().WriteAsync(
            Encoding.ASCII.GetBytes("GET /" + new string('x', 9 * 1024)),
            TestContext.Current.CancellationToken);

        await rejection;
        var competingListener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        try
        {
            competingListener.Start();
        }
        finally
        {
            competingListener.Stop();
        }
    }

    /// <summary>A localhost callback accepts IPv6 when the browser resolves localhost to the IPv6 loopback address.</summary>
    [Fact]
    public async Task Automatic_redirect_port_accepts_ipv6_loopback_callback()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Skip("IPv6 is unavailable on this host.");
        }

        var listener = new LoopbackOAuthCallbackListener();
        var redirectUri = listener.ReserveRedirectUri(0);
        var callbackTask = listener.WaitForCallbackAsync(redirectUri, TestContext.Current.CancellationToken);
        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        await client.ConnectAsync(IPAddress.IPv6Loopback, redirectUri.Port, TestContext.Current.CancellationToken);
        await using NetworkStream stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            "GET /callback?code=ipv6-code&state=expected HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var statusLine = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        var received = await callbackTask;

        Assert.Equal("HTTP/1.1 200 OK", statusLine);
        Assert.Equal("ipv6-code", ParseQuery(received.Query)["code"]);
    }

    /// <summary>URL-only OAuth profiles opt into SDK dynamic client registration instead of requiring clientId.</summary>
    [Fact]
    public async Task Url_only_profile_uses_dynamic_client_registration()
    {
        var tokens = new DictionaryTokenStore();
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var options = await flow.CreateOptionsAsync(
            CreateProfile() with
            {
                OAuth = (CreateProfile().OAuth
                    ?? throw new InvalidOperationException("The test OAuth profile is required.")) with
                {
                    ClientId = null,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Null(options.ClientId);
        Assert.NotNull(options.DynamicClientRegistration);
        Assert.Equal("Threadsmith.NET", options.DynamicClientRegistration?.ClientName);
    }

    /// <summary>Client metadata document profiles pass the public metadata URI to the SDK before DCR fallback.</summary>
    [Fact]
    public async Task Client_metadata_document_profile_sets_sdk_metadata_uri()
    {
        var tokens = new DictionaryTokenStore();
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var options = await flow.CreateOptionsAsync(
            ClientMetadataDocumentProfile(),
            TestContext.Current.CancellationToken);

        Assert.Null(options.ClientId);
        Assert.Equal(
            new Uri("https://cdn.jsdelivr.net/gh/Threadsmith-NET/Threadsmith.NET@main/docs/oauth/threadsmith-mcp-client.json"),
            options.ClientMetadataDocumentUri);
        Assert.NotNull(options.DynamicClientRegistration);
    }

    /// <summary>Fixed callback ports do not restore a registration bound to another redirect URI.</summary>
    [Fact]
    public async Task Token_cache_withholds_cached_registration_when_redirect_uri_changes()
    {
        var tokens = new DictionaryTokenStore();
        SeedGrant(tokens, "http://localhost:8401/callback", "registered-client", "registered-secret");
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var profile = UrlOnlyProfile();

        var options = await flow.CreateOptionsAsync(profile, TestContext.Current.CancellationToken);
        var restored = await options.TokenCache!.GetTokensAsync(TestContext.Current.CancellationToken);

        Assert.Null(restored?.ClientId);
        Assert.Null(restored?.ClientSecret);
        Assert.Null(restored?.TokenEndpointAuthMethod);
    }

    /// <summary>Dynamic-registration clients are not reused when the ephemeral callback URI changes.</summary>
    [Fact]
    public async Task Token_cache_withholds_cached_registration_for_another_ephemeral_port()
    {
        var tokens = new DictionaryTokenStore();
        SeedGrant(tokens, "http://localhost:8401/callback", "registered-client", "registered-secret");
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);

        var options = await flow.CreateOptionsAsync(EphemeralUrlOnlyProfile(), TestContext.Current.CancellationToken);
        var restored = await options.TokenCache!.GetTokensAsync(TestContext.Current.CancellationToken);

        Assert.Null(restored?.ClientId);
        Assert.Null(restored?.ClientSecret);
        Assert.Null(restored?.TokenEndpointAuthMethod);
    }

    /// <summary>Automatic URL-only connections fail before the SDK can dynamically register a client.</summary>
    [Fact]
    public async Task Automatic_url_only_connection_requires_a_cached_client()
    {
        var tokens = new DictionaryTokenStore();
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var profile = EphemeralUrlOnlyProfile() with { AllowOAuthUserInteraction = false };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => flow.CreateOptionsAsync(profile, TestContext.Current.CancellationToken));

        Assert.Contains("before dynamic client registration", exception.Message, StringComparison.Ordinal);
        Assert.Empty(tokens.Values);
    }

    /// <summary>Automatic URL-only connections promote only a coherent fixed-redirect cached client and disable DCR.</summary>
    [Fact]
    public async Task Automatic_url_only_connection_uses_matching_cached_client_without_dynamic_registration()
    {
        var tokens = new DictionaryTokenStore();
        SeedGrant(tokens, "http://localhost:8400/callback", "registered-client", "registered-secret");
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var profile = UrlOnlyProfile() with { AllowOAuthUserInteraction = false };

        var options = await flow.CreateOptionsAsync(profile, TestContext.Current.CancellationToken);

        Assert.Equal("registered-client", options.ClientId);
        Assert.Equal("registered-secret", options.ClientSecret);
        Assert.Null(options.DynamicClientRegistration);
    }

    /// <summary>Dynamic-registration responses are staged until matching token storage commits.</summary>
    [Fact]
    public async Task Dynamic_registration_response_does_not_activate_before_token_storage()
    {
        var tokens = new DictionaryTokenStore();
        SeedGrant(tokens, "http://localhost:8400/callback", "old-client", "old-secret");
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var options = await flow.CreateOptionsAsync(UrlOnlyProfile(), TestContext.Current.CancellationToken);
        var response = new DynamicClientRegistrationResponse
        {
            ClientId = "new-client",
            ClientSecret = "new-secret",
            TokenEndpointAuthMethod = "client_secret_post",
        };
        var registrationOptions = options.DynamicClientRegistration
            ?? throw new InvalidOperationException("Dynamic registration options are required for this test.");
        var responseDelegate = registrationOptions.ResponseDelegate
            ?? throw new InvalidOperationException("Dynamic registration response delegate is required for this test.");

        await responseDelegate(response, TestContext.Current.CancellationToken);
        var restored = await options.TokenCache!.GetTokensAsync(TestContext.Current.CancellationToken);

        Assert.Contains("mcp:oauth:remote:pendingRegistration:clientId", tokens.Values.Keys);
        Assert.Equal("old-client", restored?.ClientId);
        Assert.Equal("old-secret", restored?.ClientSecret);
    }

    /// <summary>Dynamic-registration replacement keeps the previous coherent generation if a new write is partial.</summary>
    [Fact]
    public async Task Dynamic_registration_cache_replacement_does_not_mix_old_client_id_with_new_fields()
    {
        var tokens = new FailingTokenStore("tokenEndpointAuthMethod");
        SeedGrant(tokens, "http://localhost:8400/callback", "old-client", "old-secret");
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var options = await flow.CreateOptionsAsync(UrlOnlyProfile(), TestContext.Current.CancellationToken);
        var response = new DynamicClientRegistrationResponse
        {
            ClientId = "new-client",
            ClientSecret = "new-secret",
            TokenEndpointAuthMethod = "client_secret_post",
        };
        var registrationOptions = options.DynamicClientRegistration
            ?? throw new InvalidOperationException("Dynamic registration options are required for this test.");
        var responseDelegate = registrationOptions.ResponseDelegate
            ?? throw new InvalidOperationException("Dynamic registration response delegate is required for this test.");

        await Assert.ThrowsAsync<IOException>(
            () => responseDelegate(response, TestContext.Current.CancellationToken));
        var restored = await options.TokenCache!.GetTokensAsync(TestContext.Current.CancellationToken);

        Assert.Equal("old-client", restored?.ClientId);
        Assert.Equal("old-secret", restored?.ClientSecret);
        Assert.Equal("client_secret_post", restored?.TokenEndpointAuthMethod);
    }

    /// <summary>Token fields use the documented per-profile namespace and survive cache reconstruction.</summary>
    [Fact]
    public async Task Token_cache_round_trips_access_refresh_and_expiry_fields()
    {
        var tokens = new DictionaryTokenStore();
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var first = await flow.CreateOptionsAsync(UrlOnlyProfile(), TestContext.Current.CancellationToken);
        var stored = new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-secret",
            RefreshToken = "refresh-secret",
            ExpiresIn = 3600,
            Scope = "tools.read",
            ObtainedAt = DateTimeOffset.UtcNow,
            ClientId = "threadsmith",
            ClientSecret = "stored-client-secret",
            TokenEndpointAuthMethod = "client_secret_post",
            AuthorizationServer = "https://identity.example",
        };

        await first.TokenCache!.StoreTokensAsync(stored, TestContext.Current.CancellationToken);
        var second = await flow.CreateOptionsAsync(UrlOnlyProfile(), TestContext.Current.CancellationToken);
        var restored = await second.TokenCache!.GetTokensAsync(TestContext.Current.CancellationToken);

        Assert.Equal("access-secret", restored?.AccessToken);
        Assert.Equal("refresh-secret", restored?.RefreshToken);
        Assert.Contains("mcp:oauth:remote:grant:accessToken", tokens.Values.Keys);
        Assert.Contains("mcp:oauth:remote:grant:refreshToken", tokens.Values.Keys);
        Assert.Contains("mcp:oauth:remote:grant:expiresAt", tokens.Values.Keys);
        Assert.Equal("threadsmith", restored?.ClientId);
        Assert.Equal("stored-client-secret", restored?.ClientSecret);
        Assert.Equal("client_secret_post", restored?.TokenEndpointAuthMethod);
    }

    /// <summary>A successful grant replacement prunes legacy generations and staged client secrets atomically.</summary>
    [Fact]
    public async Task Token_cache_replacement_prunes_obsolete_credential_namespaces()
    {
        var tokens = new DictionaryTokenStore();
        tokens.Values["mcp:oauth:remote:activeGrantGeneration"] = "old";
        tokens.Values["mcp:oauth:remote:grant:old:refreshToken"] = "old-refresh";
        tokens.Values["mcp:oauth:remote:registration:old:clientSecret"] = "old-secret";
        tokens.Values["mcp:oauth:remote:pendingRegistrationGeneration"] = "old";
        tokens.Values["mcp:oauth:remote:pendingRegistration:clientSecret"] = "pending-secret";
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var options = await flow.CreateOptionsAsync(UrlOnlyProfile(), TestContext.Current.CancellationToken);
        var replacement = CreateTokenContainer("replacement-access", "replacement-refresh");

        await options.TokenCache!.StoreTokensAsync(replacement, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("mcp:oauth:remote:activeGrantGeneration", tokens.Values.Keys);
        Assert.DoesNotContain("mcp:oauth:remote:pendingRegistrationGeneration", tokens.Values.Keys);
        Assert.DoesNotContain(tokens.Values.Keys, key => key.StartsWith("mcp:oauth:remote:grant:old:", StringComparison.Ordinal));
        Assert.DoesNotContain(tokens.Values.Keys, key => key.StartsWith("mcp:oauth:remote:registration:", StringComparison.Ordinal));
        Assert.DoesNotContain(tokens.Values.Keys, key => key.StartsWith("mcp:oauth:remote:pendingRegistration:", StringComparison.Ordinal));
        Assert.Equal("replacement-refresh", tokens.Values["mcp:oauth:remote:grant:refreshToken"]);
    }

    /// <summary>A reader racing a replacement observes one complete grant generation.</summary>
    [Fact]
    public async Task Token_cache_concurrent_replacement_never_tears_a_grant_snapshot()
    {
        var tokens = new BarrierTokenStore();
        SeedGrant(tokens, "http://localhost:8400/callback", "old-client", "old-secret");
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var options = await flow.CreateOptionsAsync(UrlOnlyProfile(), TestContext.Current.CancellationToken);

        tokens.PauseNextSnapshot();
        Task<TokenContainer?> readTask = options.TokenCache!
            .GetTokensAsync(TestContext.Current.CancellationToken)
            .AsTask();
        await tokens.SnapshotCaptured.Task.WaitAsync(TestContext.Current.CancellationToken);
        await options.TokenCache.StoreTokensAsync(
            CreateTokenContainer("new-access", "new-refresh"),
            TestContext.Current.CancellationToken);
        tokens.ReleaseSnapshot();

        var oldGrant = await readTask;
        var newGrant = await options.TokenCache.GetTokensAsync(TestContext.Current.CancellationToken);

        Assert.Equal("access-secret", oldGrant?.AccessToken);
        Assert.Equal("refresh-secret", oldGrant?.RefreshToken);
        Assert.Equal("old-client", oldGrant?.ClientId);
        Assert.Equal("old-secret", oldGrant?.ClientSecret);
        Assert.Equal("new-access", newGrant?.AccessToken);
        Assert.Equal("new-refresh", newGrant?.RefreshToken);
        Assert.Equal("threadsmith", newGrant?.ClientId);
        Assert.Equal("stored-client-secret", newGrant?.ClientSecret);
    }

    /// <summary>Configured clients retain cached client fields so SDK refresh can validate credentials after restart.</summary>
    [Fact]
    public async Task Configured_client_token_cache_round_trips_client_material()
    {
        var tokens = new DictionaryTokenStore();
        var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), tokens);
        var first = await flow.CreateOptionsAsync(CreateProfile(), TestContext.Current.CancellationToken);
        var stored = new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-secret",
            RefreshToken = "refresh-secret",
            ExpiresIn = 3600,
            Scope = "tools.read",
            ObtainedAt = DateTimeOffset.UtcNow,
            ClientId = "threadsmith",
            ClientSecret = "stored-client-secret",
            TokenEndpointAuthMethod = "client_secret_post",
            AuthorizationServer = "https://identity.example",
        };

        await first.TokenCache!.StoreTokensAsync(stored, TestContext.Current.CancellationToken);
        var second = await flow.CreateOptionsAsync(CreateProfile(), TestContext.Current.CancellationToken);
        var restored = await second.TokenCache!.GetTokensAsync(TestContext.Current.CancellationToken);

        Assert.Equal("threadsmith", restored?.ClientId);
        Assert.Equal("stored-client-secret", restored?.ClientSecret);
        Assert.Equal("client_secret_post", restored?.TokenEndpointAuthMethod);
    }

    /// <summary>Headless authorization prints a URL and accepts a pasted callback URL.</summary>
    [Fact]
    public async Task Headless_ux_prints_authorization_url_and_reads_callback()
    {
        using var output = new StringWriter();
        var launcher = new ConsoleBrowserLauncher(output);
        var callback = new ConsoleOAuthCallbackListener(
            new StringReader("http://localhost:8400/callback?code=x&state=y\n"),
            output);

        await launcher.LaunchAsync(
            new Uri("https://identity.example/authorize"),
            TestContext.Current.CancellationToken);
        var result = await callback.WaitForCallbackAsync(
            new Uri("http://localhost:8400/callback"),
            TestContext.Current.CancellationToken);

        Assert.Contains("https://identity.example/authorize", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Paste the complete OAuth callback URL", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("x", ParseQuery(result.Query)["code"]);
    }

    /// <summary>A malformed optional cache is ignored and replaced on the next token write.</summary>
    [Fact]
    public async Task Malformed_token_cache_does_not_prevent_startup_or_future_writes()
    {
        var directory = Directory.CreateTempSubdirectory("threadsmith-oauth-");
        string cachePath = Path.Combine(directory.FullName, "tokens.json");
        await File.WriteAllTextAsync(cachePath, "{truncated", TestContext.Current.CancellationToken);
        try
        {
            var store = new McpOAuthSecretStore(new EmptySecretStore(), cachePath);

            string? missing = await store.GetAsync(
                "mcp:oauth:remote:accessToken",
                TestContext.Current.CancellationToken);
            await store.SetAsync(
                "mcp:oauth:remote:accessToken",
                "replacement",
                TestContext.Current.CancellationToken);

            Assert.Null(missing);
            Assert.Contains("replacement", await File.ReadAllTextAsync(cachePath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Compatibility-store legacy grants are visible through snapshots used by OAuth restoration.</summary>
    [Fact]
    public async Task Compatibility_store_snapshot_restores_legacy_grant_fields()
    {
        var directory = Directory.CreateTempSubdirectory("threadsmith-oauth-");
        string cachePath = Path.Combine(directory.FullName, "tokens.json");
        var fallback = new DictionarySecretStore(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["secrets:mcp:oauth:remote:accessToken"] = "access-secret",
            ["secrets:mcp:oauth:remote:refreshToken"] = "refresh-secret",
            ["secrets:mcp:oauth:remote:obtainedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["secrets:mcp:oauth:remote:expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            ["secrets:mcp:oauth:remote:tokenType"] = "Bearer",
            ["secrets:mcp:oauth:remote:scope"] = "tools.read",
            ["secrets:mcp:oauth:remote:authorizationServer"] = "https://identity.example",
            ["secrets:mcp:oauth:remote:clientId"] = "registered-client",
            ["secrets:mcp:oauth:remote:clientSecret"] = "registered-secret",
            ["secrets:mcp:oauth:remote:tokenEndpointAuthMethod"] = "client_secret_post",
            ["secrets:mcp:oauth:remote:redirectUri"] = "http://localhost:8400/callback",
        });
        try
        {
            var store = new McpOAuthSecretStore(fallback, cachePath);
            var flow = CreateFlow(new RecordingBrowserLauncher(), new CallbackListener(), store);
            var profile = UrlOnlyProfile() with { AllowOAuthUserInteraction = false };

            var options = await flow.CreateOptionsAsync(profile, TestContext.Current.CancellationToken);

            Assert.Equal("registered-client", options.ClientId);
            Assert.Equal("registered-secret", options.ClientSecret);
            Assert.Null(options.DynamicClientRegistration);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>An unstable field-level compatibility source is rejected rather than publishing a torn grant.</summary>
    [Fact]
    public async Task Compatibility_store_snapshot_rejects_mixed_legacy_generations()
    {
        var directory = Directory.CreateTempSubdirectory("threadsmith-oauth-");
        string cachePath = Path.Combine(directory.FullName, "tokens.json");
        try
        {
            var store = new McpOAuthSecretStore(new RotatingLegacySecretStore(), cachePath);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.GetSnapshotAsync(
                    "mcp:oauth:remote:",
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(cachePath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Logout tombstones compatibility credentials so fallback lookup cannot restore the identity.</summary>
    [Fact]
    public async Task Removed_profile_prefix_blocks_compatibility_secret_fallback()
    {
        var directory = Directory.CreateTempSubdirectory("threadsmith-oauth-");
        string cachePath = Path.Combine(directory.FullName, "tokens.json");
        var fallback = new DictionarySecretStore(
            "secrets:mcp:oauth:remote:accessToken",
            "fallback-secret");
        try
        {
            var store = new McpOAuthSecretStore(fallback, cachePath);
            Assert.Equal(
                "fallback-secret",
                await store.GetAsync("mcp:oauth:remote:accessToken", TestContext.Current.CancellationToken));

            await store.RemovePrefixAsync("mcp:oauth:remote:", TestContext.Current.CancellationToken);

            Assert.Null(await store.GetAsync(
                "mcp:oauth:remote:accessToken",
                TestContext.Current.CancellationToken));
            var reloaded = new McpOAuthSecretStore(fallback, cachePath);
            Assert.Null(await reloaded.GetAsync(
                "mcp:oauth:remote:accessToken",
                TestContext.Current.CancellationToken));
            Assert.Empty(await reloaded.GetSnapshotAsync(
                "mcp:oauth:remote:",
                TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>A failed cache replacement leaves the running process authenticated with its prior tokens.</summary>
    [Fact]
    public async Task Failed_token_cache_removal_preserves_in_memory_tokens()
    {
        var directory = Directory.CreateTempSubdirectory("threadsmith-oauth-");
        string cachePath = Path.Combine(directory.FullName, "tokens.json");
        var store = new McpOAuthSecretStore(new EmptySecretStore(), cachePath);
        try
        {
            await store.SetAsync(
                "mcp:oauth:remote:accessToken",
                "remote-secret",
                TestContext.Current.CancellationToken);
            await store.SetAsync(
                "mcp:oauth:other:accessToken",
                "other-secret",
                TestContext.Current.CancellationToken);
            File.Delete(cachePath);
            Directory.CreateDirectory(cachePath);

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => store.RemovePrefixAsync(
                    "mcp:oauth:remote:",
                    TestContext.Current.CancellationToken));

            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Equal(
                "remote-secret",
                await store.GetAsync(
                    "mcp:oauth:remote:accessToken",
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                "other-secret",
                await store.GetAsync(
                    "mcp:oauth:other:accessToken",
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Unix cache files are created owner-readable and owner-writable only.</summary>
    [Fact]
    public async Task Token_cache_file_has_private_unix_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("threadsmith-oauth-");
        string cachePath = Path.Combine(directory.FullName, "tokens.json");
        try
        {
            var store = new McpOAuthSecretStore(new EmptySecretStore(), cachePath);

            await store.SetAsync(
                "mcp:oauth:remote:accessToken",
                "secret",
                TestContext.Current.CancellationToken);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(cachePath));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, "*.tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>OAuth configuration accepts URL-only HTTP profiles for dynamic client registration.</summary>
    [Fact]
    public void OAuth_configuration_allows_missing_client_id_for_dynamic_registration()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "remote",
                ["mcp:profiles:0:name"] = "Remote",
                ["mcp:profiles:0:command"] = "https://mcp.example/mcp",
                ["mcp:profiles:0:transport"] = "http",
                ["mcp:profiles:0:oauth:enabled"] = "true",
                ["mcp:profiles:0:oauth:redirectPort"] = "8400",
            }).Build();

        var profile = Assert.Single(McpProfileConfigurationLoader.Load(configuration));

        Assert.Null(profile.OAuth?.ClientId);
        Assert.True(profile.OAuth?.Enabled);
    }

    /// <summary>OAuth configuration accepts a public client metadata document URI with a fixed callback port.</summary>
    [Fact]
    public void OAuth_configuration_binds_client_metadata_document_uri()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "remote",
                ["mcp:profiles:0:name"] = "Remote",
                ["mcp:profiles:0:command"] = "https://mcp.example/mcp",
                ["mcp:profiles:0:transport"] = "http",
                ["mcp:profiles:0:oauth:enabled"] = "true",
                ["mcp:profiles:0:oauth:clientMetadataDocumentUri"] =
                    "https://cdn.jsdelivr.net/gh/Threadsmith-NET/Threadsmith.NET@main/docs/oauth/threadsmith-mcp-client.json",
                ["mcp:profiles:0:oauth:redirectPort"] = "8484",
            }).Build();

        var profile = Assert.Single(McpProfileConfigurationLoader.Load(configuration));

        Assert.Null(profile.OAuth?.ClientId);
        Assert.Equal(
            new Uri("https://cdn.jsdelivr.net/gh/Threadsmith-NET/Threadsmith.NET@main/docs/oauth/threadsmith-mcp-client.json"),
            profile.OAuth?.ClientMetadataDocumentUri);
    }

    /// <summary>Client metadata document configuration fails closed for ambiguous or non-exact redirect usage.</summary>
    [Theory]
    [InlineData("https://cdn.jsdelivr.net/gh/Threadsmith-NET/Threadsmith.NET@main/docs/oauth/threadsmith-mcp-client.json", "threadsmith", "8484", "both clientId")]
    [InlineData("https://cdn.jsdelivr.net/gh/Threadsmith-NET/Threadsmith.NET@main/docs/oauth/threadsmith-mcp-client.json", null, "0", "fixed redirectPort")]
    [InlineData("http://threadsmith-net.github.io/Threadsmith.NET/oauth/mcp-client.json", null, "8484", "clientMetadataDocumentUri")]
    [InlineData("https://threadsmith-net.github.io", null, "8484", "clientMetadataDocumentUri")]
    [InlineData("https://example.com/a/../client.json", null, "8484", "clientMetadataDocumentUri")]
    [InlineData("https://example.com/%2e/client.json", null, "8484", "clientMetadataDocumentUri")]
    [InlineData("https://example.com/a\\..\\client.json", null, "8484", "clientMetadataDocumentUri")]
    [InlineData("https://example.com/a%2f..%2fclient.json", null, "8484", "clientMetadataDocumentUri")]
    public void OAuth_configuration_rejects_invalid_client_metadata_document_usage(
        string clientMetadataDocumentUri,
        string? clientId,
        string redirectPort,
        string expectedMessage)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "remote",
                ["mcp:profiles:0:name"] = "Remote",
                ["mcp:profiles:0:command"] = "https://mcp.example/mcp",
                ["mcp:profiles:0:transport"] = "http",
                ["mcp:profiles:0:oauth:enabled"] = "true",
                ["mcp:profiles:0:oauth:clientId"] = clientId,
                ["mcp:profiles:0:oauth:clientMetadataDocumentUri"] = clientMetadataDocumentUri,
                ["mcp:profiles:0:oauth:redirectPort"] = redirectPort,
            }).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => McpProfileConfigurationLoader.Load(configuration));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A configured client secret cannot be combined with an absent client identifier.</summary>
    [Fact]
    public void OAuth_configuration_rejects_client_secret_without_client_id()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "remote",
                ["mcp:profiles:0:name"] = "Remote",
                ["mcp:profiles:0:command"] = "https://mcp.example/mcp",
                ["mcp:profiles:0:transport"] = "http",
                ["mcp:profiles:0:oauth:enabled"] = "true",
                ["mcp:profiles:0:oauth:clientSecret"] = "secrets:MCP_CLIENT_SECRET",
                ["mcp:profiles:0:secretScope:0"] = "secrets:MCP_CLIENT_SECRET",
            }).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => McpProfileConfigurationLoader.Load(configuration));

        Assert.Contains("clientSecret without clientId", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Whitespace optional client identifiers normalize to the dynamic-registration mode.</summary>
    [Fact]
    public void OAuth_configuration_normalizes_empty_client_id()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "remote",
                ["mcp:profiles:0:name"] = "Remote",
                ["mcp:profiles:0:command"] = "https://mcp.example/mcp",
                ["mcp:profiles:0:transport"] = "http",
                ["mcp:profiles:0:oauth:enabled"] = "true",
                ["mcp:profiles:0:oauth:clientId"] = "   ",
            }).Build();

        var profile = Assert.Single(McpProfileConfigurationLoader.Load(configuration));

        Assert.Null(profile.OAuth?.ClientId);
        Assert.Equal(McpOAuthClientMode.DynamicRegistration, profile.OAuth?.ClientMode);
    }

    /// <summary>OAuth rejects static authorization headers and stdio transports while allowing URL-only dynamic clients.</summary>
    [Fact]
    public void OAuth_configuration_conflicts_fail_closed()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "remote",
                ["mcp:profiles:0:name"] = "Remote",
                ["mcp:profiles:0:command"] = "https://mcp.example/mcp",
                ["mcp:profiles:0:transport"] = "http",
                ["mcp:profiles:0:headers:Authorization"] = "Bearer static",
                ["mcp:profiles:0:oauth:enabled"] = "true",
                ["mcp:profiles:0:oauth:clientId"] = "threadsmith",
                ["mcp:profiles:0:oauth:redirectPort"] = "8400",
            }).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => McpProfileConfigurationLoader.Load(configuration));

        Assert.Contains("Authorization header", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An unsupported discovery override is rejected instead of being silently ignored.</summary>
    [Fact]
    public void OAuth_discovery_override_fails_closed()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "remote",
                ["mcp:profiles:0:name"] = "Remote",
                ["mcp:profiles:0:command"] = "https://mcp.example/mcp",
                ["mcp:profiles:0:transport"] = "http",
                ["mcp:profiles:0:oauth:enabled"] = "true",
                ["mcp:profiles:0:oauth:clientId"] = "threadsmith",
                ["mcp:profiles:0:oauth:discoveryUrl"] =
                    "https://identity.example/.well-known/oauth-authorization-server",
            }).Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => McpProfileConfigurationLoader.Load(configuration));

        Assert.Contains("discoveryUrl is not supported", exception.Message, StringComparison.Ordinal);
    }

    private static McpOAuthFlow CreateFlow(
        IBrowserLauncher browser,
        IOAuthCallbackListener listener,
        IMcpOAuthTokenStore tokens,
        ISecretStore? secrets = null)
    {
        return new(browser, listener, tokens, secrets ?? new EmptySecretStore(), NullLogger<McpOAuthFlow>.Instance);
    }

    private static McpConnectionProfile CreateProfile()
    {
        return new()
        {
            Id = "remote",
            DisplayName = "Remote",
            Command = "https://mcp.example/mcp",
            Transport = McpTransport.Http,
            Trust = McpTrustLevel.TrustedRead,
            OAuth = new McpOAuthOptions
            {
                Enabled = true,
                ClientId = "threadsmith",
                RedirectPort = 8400,
                Scopes = ["tools.read"],
            },
        };
    }

    private static McpConnectionProfile UrlOnlyProfile()
    {
        return CreateProfile() with
        {
            OAuth = (CreateProfile().OAuth
                ?? throw new InvalidOperationException("The test OAuth profile is required.")) with
            {
                ClientId = null,
            },
        };
    }

    private static McpConnectionProfile ClientMetadataDocumentProfile()
    {
        return UrlOnlyProfile() with
        {
            OAuth = (UrlOnlyProfile().OAuth
                ?? throw new InvalidOperationException("The test OAuth profile is required.")) with
            {
                ClientMetadataDocumentUri = new Uri(
                    "https://cdn.jsdelivr.net/gh/Threadsmith-NET/Threadsmith.NET@main/docs/oauth/threadsmith-mcp-client.json"),
                RedirectPort = 8484,
            },
        };
    }

    private static McpConnectionProfile EphemeralUrlOnlyProfile()
    {
        return UrlOnlyProfile() with
        {
            OAuth = (UrlOnlyProfile().OAuth
                ?? throw new InvalidOperationException("The test OAuth profile is required.")) with
            {
                RedirectPort = 0,
            },
        };
    }

    private static TokenContainer CreateTokenContainer(string accessToken, string refreshToken)
    {
        return new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresIn = 3600,
            Scope = "tools.read",
            ObtainedAt = DateTimeOffset.UtcNow,
            ClientId = "threadsmith",
            ClientSecret = "stored-client-secret",
            TokenEndpointAuthMethod = "client_secret_post",
            AuthorizationServer = "https://identity.example",
        };
    }

    private static void SeedGrant(
        DictionaryTokenStore tokens,
        string redirectUri,
        string clientId,
        string clientSecret)
    {
        var prefix = "mcp:oauth:remote:";
        var grantPrefix = $"{prefix}grant:";
        tokens.Values[grantPrefix + "accessToken"] = "access-secret";
        tokens.Values[grantPrefix + "refreshToken"] = "refresh-secret";
        tokens.Values[grantPrefix + "obtainedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        tokens.Values[grantPrefix + "expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture);
        tokens.Values[grantPrefix + "tokenType"] = "Bearer";
        tokens.Values[grantPrefix + "scope"] = "tools.read";
        tokens.Values[grantPrefix + "authorizationServer"] = "https://identity.example";
        tokens.Values[grantPrefix + "clientId"] = clientId;
        tokens.Values[grantPrefix + "clientSecret"] = clientSecret;
        tokens.Values[grantPrefix + "tokenEndpointAuthMethod"] = "client_secret_post";
        tokens.Values[grantPrefix + "redirectUri"] = redirectUri;
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
                .Split('&')
                .Select(item => item.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
    }

    private sealed class RecordingBrowserLauncher : IBrowserLauncher
    {
        private readonly Action? _onLaunch;

        public RecordingBrowserLauncher(Action? onLaunch = null)
        {
            _onLaunch = onLaunch;
        }

        public Uri? AuthorizationUri { get; private set; }

        public Task LaunchAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
        {
            _onLaunch?.Invoke();
            AuthorizationUri = authorizationUri;
            return Task.CompletedTask;
        }
    }

    private sealed class CallbackListener : IOAuthCallbackListener
    {
        public Uri Callback { get; set; } = new("http://localhost:8400/callback?code=x&state=y");

        public bool WaitStarted { get; private set; }

        public Uri ReserveRedirectUri(int requestedPort)
        {
            return new($"http://localhost:{requestedPort}/callback", UriKind.Absolute);
        }

        public Task<Uri> WaitForCallbackAsync(Uri redirectUri, CancellationToken cancellationToken = default)
        {
            WaitStarted = true;
            return Task.FromResult(Callback);
        }
    }

    private class DictionaryTokenStore : IMcpOAuthTokenStore
    {
        private readonly Lock _sync = new();

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult(Values.GetValueOrDefault(secretReference));
            }
        }

        public virtual Task<IReadOnlyDictionary<string, string>> GetSnapshotAsync(
            string secretReferencePrefix,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                IReadOnlyDictionary<string, string> snapshot = Values
                    .Where(pair => pair.Key.StartsWith(secretReferencePrefix, StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                return Task.FromResult(snapshot);
            }
        }

        public virtual Task SetAsync(string secretReference, string value, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Values[secretReference] = value;
            }

            return Task.CompletedTask;
        }

        public virtual Task ApplyAsync(
            McpOAuthTokenStoreMutation mutation,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                foreach (var reference in mutation.RemovedReferences)
                {
                    Values.Remove(reference);
                }

                foreach (var prefix in mutation.RemovedPrefixes)
                {
                    foreach (var key in Values.Keys
                        .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                        .ToArray())
                    {
                        Values.Remove(key);
                    }
                }

                foreach (var pair in mutation.Values)
                {
                    Values[pair.Key] = pair.Value;
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class BarrierTokenStore : DictionaryTokenStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pauseNextSnapshot;

        public TaskCompletionSource SnapshotCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PauseNextSnapshot()
        {
            Interlocked.Exchange(ref _pauseNextSnapshot, 1);
        }

        public void ReleaseSnapshot()
        {
            _release.TrySetResult();
        }

        public override async Task<IReadOnlyDictionary<string, string>> GetSnapshotAsync(
            string secretReferencePrefix,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, string> snapshot = await base.GetSnapshotAsync(
                secretReferencePrefix,
                cancellationToken);
            if (Interlocked.Exchange(ref _pauseNextSnapshot, 0) == 1)
            {
                SnapshotCaptured.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return snapshot;
        }
    }

    private sealed class FailingTokenStore : DictionaryTokenStore
    {
        private readonly string _failingReferenceFragment;

        public FailingTokenStore(string failingReferenceFragment)
        {
            _failingReferenceFragment = failingReferenceFragment;
        }

        public override Task SetAsync(string secretReference, string value, CancellationToken cancellationToken = default)
        {
            if (secretReference.Contains(_failingReferenceFragment, StringComparison.Ordinal))
            {
                return Task.FromException(new IOException("Simulated token-store failure."));
            }

            return base.SetAsync(secretReference, value, cancellationToken);
        }

        public override Task ApplyAsync(
            McpOAuthTokenStoreMutation mutation,
            CancellationToken cancellationToken = default)
        {
            if (mutation.Values.Keys.Any(
                key => key.Contains(_failingReferenceFragment, StringComparison.Ordinal)))
            {
                return Task.FromException(new IOException("Simulated token-store failure."));
            }

            return base.ApplyAsync(mutation, cancellationToken);
        }
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class DictionarySecretStore : ISecretStore
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public DictionarySecretStore(string reference, string value)
            : this(new Dictionary<string, string>(StringComparer.Ordinal) { [reference] = value })
        {
        }

        public DictionarySecretStore(IReadOnlyDictionary<string, string> values)
        {
            _values = values;
        }

        public int ReadCount { get; private set; }

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(
                _values.TryGetValue(secretReference, out string? value) ? value : null);
        }
    }

    private sealed class RotatingLegacySecretStore : ISecretStore
    {
        private int _readCount;

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = Interlocked.Increment(ref _readCount) <= 5 ? "old" : "new";
            return Task.FromResult<string?>(generation + ":" + secretReference);
        }
    }
}
