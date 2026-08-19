namespace Threadsmith.Milestone10.Tests;

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
        Assert.Equal("code-value", ParseQuery(received.Query)["code"]);
    }

    /// <summary>Token fields use the documented per-profile namespace and survive cache reconstruction.</summary>
    [Fact]
    public async Task Token_cache_round_trips_access_refresh_and_expiry_fields()
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
            AuthorizationServer = "https://identity.example",
        };

        await first.TokenCache!.StoreTokensAsync(stored, TestContext.Current.CancellationToken);
        var second = await flow.CreateOptionsAsync(CreateProfile(), TestContext.Current.CancellationToken);
        var restored = await second.TokenCache!.GetTokensAsync(TestContext.Current.CancellationToken);

        Assert.Equal("access-secret", restored?.AccessToken);
        Assert.Equal("refresh-secret", restored?.RefreshToken);
        Assert.Contains("mcp:oauth:remote:accessToken", tokens.Values.Keys);
        Assert.Contains("mcp:oauth:remote:refreshToken", tokens.Values.Keys);
        Assert.Contains("mcp:oauth:remote:expiresAt", tokens.Values.Keys);
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

    /// <summary>OAuth rejects static authorization headers, stdio transports, and missing pre-registered clients.</summary>
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

    private sealed class DictionaryTokenStore : IMcpOAuthTokenStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Values.GetValueOrDefault(secretReference));
        }

        public Task SetAsync(string secretReference, string value, CancellationToken cancellationToken = default)
        {
            Values[secretReference] = value;
            return Task.CompletedTask;
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
        private readonly string _reference;
        private readonly string _value;

        public DictionarySecretStore(string reference, string value)
        {
            _reference = reference;
            _value = value;
        }

        public int ReadCount { get; private set; }

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult<string?>(
                string.Equals(secretReference, _reference, StringComparison.Ordinal) ? _value : null);
        }
    }
}
