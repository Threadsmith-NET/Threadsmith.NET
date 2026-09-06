namespace Threadsmith.McpTransports.Tests;

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Threadsmith.Core;
using Threadsmith.Mcp;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies HTTP/SSE profile binding, transport mapping, authentication guards, and live opt-in connectivity.</summary>
public sealed class SdkHttpTransportTests
{
    /// <summary>SSE and streamable-HTTP profiles map to their explicit SDK modes.</summary>
    [Theory]
    [InlineData(McpTransport.Sse, HttpTransportMode.Sse)]
    [InlineData(McpTransport.Http, HttpTransportMode.StreamableHttp)]
    public void Profile_maps_to_http_transport_options(McpTransport transport, HttpTransportMode expectedMode)
    {
        var profile = CreateProfile("https://mcp.example.test/endpoint", transport) with
        {
            Headers = new Dictionary<string, string> { ["X-Tenant"] = "example" },
        };

        var options = SdkHttpTransport.CreateOptions(profile);

        Assert.Equal(new Uri(profile.Command), options.Endpoint);
        Assert.Equal(expectedMode, options.TransportMode);
        Assert.Equal(profile.StartupTimeout, options.ConnectionTimeout);
        Assert.Equal("example", options.AdditionalHeaders?["X-Tenant"]);
    }

    /// <summary>HTTP headers and OAuth stubs bind from configuration without resolving secrets.</summary>
    [Fact]
    public void Profile_loader_binds_headers_and_oauth_stub()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "remote",
                ["mcp:profiles:0:name"] = "Remote",
                ["mcp:profiles:0:command"] = "https://mcp.example.test/mcp",
                ["mcp:profiles:0:transport"] = "http",
                ["mcp:profiles:0:headers:Authorization"] = "secrets:MCP_TOKEN",
                ["mcp:profiles:0:secretScope:0"] = "secrets:MCP_TOKEN",
                ["mcp:profiles:0:secretScope:1"] = "secrets:MCP_CLIENT_SECRET",
                ["mcp:profiles:0:oauth:enabled"] = "false",
                ["mcp:profiles:0:oauth:scopes:0"] = "tools.read",
                ["mcp:profiles:0:oauth:clientId"] = "threadsmith",
                ["mcp:profiles:0:oauth:clientSecret"] = "secrets:MCP_CLIENT_SECRET",
                ["mcp:profiles:0:oauth:redirectPort"] = "8400",
            }).Build();

        var profile = Assert.Single(McpProfileConfigurationLoader.Load(configuration));

        Assert.Equal("secrets:MCP_TOKEN", profile.Headers["Authorization"]);
        Assert.NotNull(profile.OAuth);
        Assert.False(profile.OAuth.Enabled);
        Assert.Equal(["tools.read"], profile.OAuth.Scopes);
        Assert.Equal(8400, profile.OAuth.RedirectPort);
    }

    /// <summary>Only explicitly scoped logical secrets are resolved into HTTP header values.</summary>
    [Fact]
    public async Task Header_secret_resolution_requires_profile_scope()
    {
        var secretStore = new DictionarySecretStore
        {
            Values = { ["secrets:MCP_TOKEN"] = "token-value" },
        };
        var transport = new SdkHttpTransport(secretStore, NullLoggerFactory.Instance);
        var allowed = CreateProfile("https://mcp.example.test/mcp", McpTransport.Http) with
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "secrets:MCP_TOKEN" },
            SecretScope = ["secrets:MCP_TOKEN"],
        };

        var resolved = await transport.ResolveHeadersAsync(allowed);

        Assert.Equal("token-value", resolved["Authorization"]);
        var denied = allowed with { SecretScope = [] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ResolveHeadersAsync(denied));
        await transport.DisposeAsync();
    }

    /// <summary>OAuth-enabled profiles now reach the transport boundary for Plan 23 handling.</summary>
    [Fact]
    public async Task OAuth_enabled_profile_routes_to_transport()
    {
        var factoryCalled = false;
        var adapter = new McpAdapter(
            _ =>
            {
                factoryCalled = true;
                return new NoOpTransport();
            },
            new DictionarySecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance,
            TestPromptLoader.Instance);
        var profile = CreateProfile("https://mcp.example.test/mcp", McpTransport.Http) with
        {
            OAuth = new McpOAuthOptions { Enabled = true, ClientId = "threadsmith", RedirectPort = 8400 },
        };

        var result = await adapter.ConnectAsync(profile);

        Assert.True(factoryCalled);
        Assert.True(result.Succeeded);
    }

    /// <summary>An imported HTTP tool exposes its host to the standard allow-list policy.</summary>
    [Fact]
    public void Http_imported_tool_network_host_is_policy_evaluated()
    {
        var profile = CreateProfile("https://mcp.example.test/mcp", McpTransport.Http);
        var capability = new McpImportedCapability
        {
            Id = "remote:echo",
            Kind = McpCapabilityKind.Tool,
            ServerName = "echo",
            InputSchemaJson = "{}",
        };
        var tool = new McpImportedTool(
            new NoOpTransport(),
            profile,
            capability,
            new SecretOutputSanitizer(),
            TestPromptLoader.Instance);
        var input = tool.DeserializeInput("{}");
        var policy = new DefaultPolicyEngine();

        Assert.Equal(ToolCategory.CodeExecution, tool.Definition.Category);
        Assert.Equal(ToolSideEffect.ExecutesCode, tool.Definition.SideEffect);
        Assert.Equal(ApprovalLevel.HostPolicy, tool.Definition.RequiredApproval);
        Assert.Equal(RepositoryTrustLevel.TrustedBuild, tool.Definition.RequiredTrust);
        Assert.True(tool.Definition.ConversationAvailable);

        var denied = policy.Evaluate(tool, input, CreateContext([]));
        var allowed = policy.Evaluate(tool, input, CreateContext(["mcp.example.test"]));

        Assert.False(denied.IsAllowed);
        Assert.Contains("network host", denied.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(allowed.IsAllowed);
    }

    /// <summary>Unknown-length HTTP bodies are rejected at the transport stream before full materialization.</summary>
    [Fact]
    public async Task Http_response_stream_enforces_pre_materialization_wire_bound()
    {
        using var client = new HttpClient(new McpBoundedHttpResponseHandler(
            new DelegateHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(
                    new byte[McpBoundedHttpResponseHandler.MaximumResponseBytes + 1])),
            }))));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetByteArrayAsync("https://mcp.example.test/result"));
    }

    /// <summary>Long-lived SSE responses reset the wire ceiling at each bounded event boundary.</summary>
    [Fact]
    public async Task Http_response_stream_bounds_each_sse_event_not_connection_lifetime()
    {
        var eventData = "data:" + new string('x', 600 * 1024) + "\n\n";
        using var client = new HttpClient(new McpBoundedHttpResponseHandler(
            new DelegateHttpHandler((_, _) =>
            {
                var content = new StringContent(eventData + eventData);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "text/event-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            })));

        var result = await client.GetByteArrayAsync("https://mcp.example.test/events");

        Assert.True(result.Length > McpBoundedHttpResponseHandler.MaximumResponseBytes);
    }

    /// <summary>A failed OAuth connection releases the redirect reservation owned by that attempt.</summary>
    [Fact]
    public async Task OAuth_connection_failure_releases_callback_reservation()
    {
        var callback = new TrackingCallbackListener();
        var secrets = new DictionarySecretStore();
        var flow = new McpOAuthFlow(
            new NoOpBrowserLauncher(),
            callback,
            new DictionaryTokenStore(),
            secrets,
            NullLogger<McpOAuthFlow>.Instance);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))));
        await using var transport = new SdkHttpTransport(
            secrets,
            NullLoggerFactory.Instance,
            flow,
            httpClient);
        var profile = CreateProfile("https://mcp.example.test/mcp", McpTransport.Http) with
        {
            OAuth = new McpOAuthOptions
            {
                Enabled = true,
                ClientId = "fixture-client",
                RedirectPort = 8400,
            },
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => transport.StartAsync(profile, new Dictionary<string, string>()));

        Assert.Equal(1, callback.ReleaseCount);
    }

    /// <summary>Every HTTP cleanup starts before any resource can consume the shared shutdown deadline.</summary>
    [Fact]
    public async Task Bounded_shutdown_starts_every_owned_async_resource_before_awaiting_deadline()
    {
        var transport = new SdkHttpTransport(new DictionarySecretStore(), NullLoggerFactory.Instance);
        var blocked = new RecordingAsyncDisposable(complete: false);
        var completed = new RecordingAsyncDisposable(complete: true);
        var failed = new RecordingAsyncDisposable(complete: true, fail: true);
        using var cancellation = new CancellationTokenSource();

        var shutdown = transport.DisposeAllWithinDeadlineAsync(
            [blocked, completed, failed],
            cancellation.Token);

        Assert.Equal(1, blocked.DisposeCount);
        Assert.Equal(1, completed.DisposeCount);
        Assert.Equal(1, failed.DisposeCount);
        await cancellation.CancelAsync();
        Assert.False(await shutdown);
        blocked.Complete();
        await transport.DisposeAsync();
    }

    /// <summary>An explicitly configured live endpoint imports and invokes a real HTTP MCP tool.</summary>
    [Fact]
    public async Task Live_http_endpoint_connects_imports_invokes_and_disconnects()
    {
        var endpoint = Environment.GetEnvironmentVariable("THREADSMITH_MCP_HTTP_ENDPOINT");
        var toolName = Environment.GetEnvironmentVariable("THREADSMITH_MCP_HTTP_TOOL");
        var arguments = Environment.GetEnvironmentVariable("THREADSMITH_MCP_HTTP_ARGUMENTS") ?? "{}";
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(toolName))
        {
            Assert.Skip("Set THREADSMITH_MCP_HTTP_ENDPOINT and THREADSMITH_MCP_HTTP_TOOL for live HTTP MCP verification.");
        }

        var mode = string.Equals(
            Environment.GetEnvironmentVariable("THREADSMITH_MCP_HTTP_MODE"),
            "sse",
            StringComparison.OrdinalIgnoreCase)
            ? McpTransport.Sse
            : McpTransport.Http;
        var secretStore = new DictionarySecretStore();
        var sanitizer = new SecretOutputSanitizer();
        var adapter = new McpAdapter(
            _ => new SdkHttpTransport(secretStore, NullLoggerFactory.Instance),
            secretStore,
            sanitizer,
            NullLogger<McpAdapter>.Instance,
            TestPromptLoader.Instance);
        var profile = CreateProfile(endpoint, mode);

        var connection = await adapter.ConnectAsync(profile);

        Assert.True(connection.Succeeded, connection.Status.Error);
        var tool = Assert.Single(
            connection.Tools,
            candidate => string.Equals(candidate.Capability.ServerName, toolName, StringComparison.Ordinal));
        var input = tool.DeserializeInput(arguments);
        var result = await tool.ExecuteAsync(
            input,
            new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateContext([new Uri(endpoint).Host])),
            CancellationToken.None);
        Assert.NotNull(result.Value);
        await adapter.DisconnectAsync(profile.Id);
        await adapter.DisposeAsync();
    }

    private static McpConnectionProfile CreateProfile(string endpoint, McpTransport transport)
    {
        return new()
        {
            Id = "remote",
            DisplayName = "Remote MCP",
            Command = endpoint,
            Transport = transport,
            Trust = McpTrustLevel.TrustedExecution,
            StartupTimeout = TimeSpan.FromSeconds(10),
            RequestTimeout = TimeSpan.FromSeconds(10),
            AllowedCapabilities = [McpCapabilityKind.Tool],
        };
    }

    private static ToolInvocationContext CreateContext(IReadOnlyList<string> networkHosts)
    {
        return new()
        {
            RepositoryPath = Directory.GetCurrentDirectory(),
            TrustLevel = RepositoryTrustLevel.TrustedBuild,
            ApprovedRoots = [Directory.GetCurrentDirectory()],
            AllowedNetworkHosts = networkHosts,
            RequestedBy = "milestone9-test",
        };
    }

    private sealed class DictionarySecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Values.TryGetValue(secretReference, out var value) ? value : null);
        }
    }

    private sealed class DelegateHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        internal DelegateHttpHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private sealed class DictionaryTokenStore : IMcpOAuthTokenStore
    {
        public Task ApplyAsync(
            McpOAuthTokenStoreMutation mutation,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyDictionary<string, string>> GetSnapshotAsync(
            string secretReferencePrefix,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>());
        }

        public Task SetAsync(
            string secretReference,
            string value,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpBrowserLauncher : IBrowserLauncher
    {
        public Task LaunchAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingCallbackListener : IOAuthCallbackListener
    {
        public int ReleaseCount { get; private set; }

        public void ReleaseRedirectUri(Uri redirectUri)
        {
            ReleaseCount++;
        }

        public Uri ReserveRedirectUri(int requestedPort)
        {
            return new Uri($"http://localhost:{requestedPort}/callback", UriKind.Absolute);
        }

        public Task<Uri> WaitForCallbackAsync(
            Uri redirectUri,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<Uri>(new InvalidOperationException("Callback was not expected."));
        }
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        private readonly bool _complete;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _fail;

        internal RecordingAsyncDisposable(bool complete, bool fail = false)
        {
            _complete = complete;
            _fail = fail;
        }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (_fail)
            {
                return ValueTask.FromException(new InvalidOperationException("fixture cleanup failure"));
            }

            if (_complete)
            {
                _completion.TrySetResult();
            }

            return new ValueTask(_completion.Task);
        }

        internal void Complete()
        {
            _completion.TrySetResult();
        }
    }

    private sealed class NoOpTransport : IMcpTransport
    {
        public int? ProcessId => null;

        public Task<IReadOnlyList<McpImportedCapability>> StartAsync(
            McpConnectionProfile profile,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<McpImportedCapability>>([]);
        }

        public Task<McpTransportInvocation> InvokeAsync(
            string capabilityId,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new McpTransportInvocation { Succeeded = true, ResultJson = "[]" });
        }

        public Task<bool> StopAsync(TimeSpan drainKillTimeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
