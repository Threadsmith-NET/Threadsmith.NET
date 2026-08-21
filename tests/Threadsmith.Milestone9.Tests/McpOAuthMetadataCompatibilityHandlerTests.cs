namespace Threadsmith.Milestone9.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Threadsmith.Mcp;
using Xunit;

/// <summary>Verifies bounded, server-neutral OAuth metadata compatibility behavior.</summary>
public sealed class McpOAuthMetadataCompatibilityHandlerTests
{
    /// <summary>A metadata proxy is rebound to its HTTPS canonical issuer without losing proxy-owned endpoints.</summary>
    [Fact]
    public async Task Delegated_metadata_preserves_proxy_document_at_canonical_issuer()
    {
        var inner = new RoutingHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            "https://mcp.example/.well-known/oauth-protected-resource" => JsonResponse(
                """
                {"resource":"https://mcp.example/mcp","authorization_servers":["https://mcp.example/"]}
                """),
            "https://mcp.example/.well-known/oauth-authorization-server" => JsonResponse(
                """
                {
                  "issuer":"https://login.example/",
                  "authorization_endpoint":"https://login.example/authorize",
                  "token_endpoint":"https://login.example/token",
                  "registration_endpoint":"https://mcp.example/register"
                }
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        using var client = new HttpClient(new McpOAuthMetadataCompatibilityHandler(inner));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Compatibility-Test/1.0");

        using var protectedResourceResponse = await client.GetAsync(
            "https://mcp.example/.well-known/oauth-protected-resource",
            TestContext.Current.CancellationToken);
        var protectedResource = JsonNode.Parse(
            await protectedResourceResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var canonicalResponse = await client.GetAsync(
            "https://login.example/.well-known/oauth-authorization-server",
            TestContext.Current.CancellationToken);
        var canonical = JsonNode.Parse(
            await canonicalResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("https://login.example/", protectedResource?["authorization_servers"]?[0]?.GetValue<string>());
        Assert.Equal("https://login.example/", canonical?["issuer"]?.GetValue<string>());
        Assert.Equal("https://mcp.example/register", canonical?["registration_endpoint"]?.GetValue<string>());
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal("Compatibility-Test/1.0", inner.Requests[1].UserAgent);
    }

    /// <summary>Compliant metadata remains unchanged and its prefetched document is reused once by the SDK.</summary>
    [Fact]
    public async Task Compliant_metadata_remains_unchanged()
    {
        var inner = new RoutingHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            "https://identity.example/.well-known/oauth-protected-resource" => JsonResponse(
                """
                {"resource":"https://identity.example/mcp","authorization_servers":["https://identity.example/"]}
                """),
            "https://identity.example/.well-known/oauth-authorization-server" => JsonResponse(
                """
                {"issuer":"https://identity.example/","authorization_endpoint":"https://identity.example/authorize","token_endpoint":"https://identity.example/token"}
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        using var client = new HttpClient(new McpOAuthMetadataCompatibilityHandler(inner));

        using var protectedResourceResponse = await client.GetAsync(
            "https://identity.example/.well-known/oauth-protected-resource",
            TestContext.Current.CancellationToken);
        var protectedResource = JsonNode.Parse(
            await protectedResourceResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var metadataResponse = await client.GetAsync(
            "https://identity.example/.well-known/oauth-authorization-server",
            TestContext.Current.CancellationToken);
        var metadata = JsonNode.Parse(
            await metadataResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "https://identity.example/",
            protectedResource?["authorization_servers"]?[0]?.GetValue<string>());
        Assert.Equal("https://identity.example/", metadata?["issuer"]?.GetValue<string>());
        Assert.Equal(2, inner.Requests.Count);
    }

    /// <summary>OpenID discovery is used only after the RFC 8414 metadata location rejects the request.</summary>
    [Fact]
    public async Task OpenId_metadata_fallback_is_bounded_and_cached()
    {
        var inner = new RoutingHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            "https://mcp.example/.well-known/oauth-protected-resource" => JsonResponse(
                """
                {"resource":"https://mcp.example/mcp","authorization_servers":["https://proxy.example/"]}
                """),
            "https://proxy.example/.well-known/oauth-authorization-server" =>
                new HttpResponseMessage(HttpStatusCode.NotFound),
            "https://proxy.example/.well-known/openid-configuration" => JsonResponse(
                """
                {"issuer":"https://issuer.example/","authorization_endpoint":"https://issuer.example/authorize","token_endpoint":"https://issuer.example/token"}
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        using var client = new HttpClient(new McpOAuthMetadataCompatibilityHandler(inner));

        using var protectedResourceResponse = await client.GetAsync(
            "https://mcp.example/.well-known/oauth-protected-resource",
            TestContext.Current.CancellationToken);
        using var canonicalResponse = await client.GetAsync(
            "https://issuer.example/.well-known/oauth-authorization-server",
            TestContext.Current.CancellationToken);

        Assert.True(canonicalResponse.IsSuccessStatusCode);
        Assert.Equal(3, inner.Requests.Count);
    }

    /// <summary>An insecure canonical issuer is rejected rather than normalized.</summary>
    [Fact]
    public async Task Insecure_canonical_issuer_fails_closed()
    {
        var inner = new RoutingHandler(request => request.RequestUri?.AbsoluteUri switch
        {
            "https://mcp.example/.well-known/oauth-protected-resource" => JsonResponse(
                """
                {"resource":"https://mcp.example/mcp","authorization_servers":["https://proxy.example/"]}
                """),
            "https://proxy.example/.well-known/oauth-authorization-server" => JsonResponse(
                """
                {"issuer":"http://issuer.example/","authorization_endpoint":"https://issuer.example/authorize","token_endpoint":"https://issuer.example/token"}
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });
        using var client = new HttpClient(new McpOAuthMetadataCompatibilityHandler(inner));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetAsync(
                "https://mcp.example/.well-known/oauth-protected-resource",
                TestContext.Current.CancellationToken));
    }

    /// <summary>The owned MCP HTTP client identifies Threadsmith without exposing configuration or credentials.</summary>
    [Fact]
    public async Task Owned_http_client_sends_product_user_agent()
    {
        var inner = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = SdkHttpTransport.CreateHttpClient(inner);

        using var response = await client.GetAsync(
            "https://mcp.example/status",
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Threadsmith.NET/", Assert.Single(inner.Requests).UserAgent, StringComparison.Ordinal);
    }

    /// <summary>The owned metadata handler returns redirects rather than following unvalidated locations.</summary>
    [Fact]
    public async Task Owned_metadata_handler_does_not_follow_redirects()
    {
        await using var server = new LoopbackRedirectServer();
        using var client = SdkHttpTransport.CreateHttpClient(SdkHttpTransport.CreateMetadataCompatibilityHandler());

        using var response = await client.GetAsync(
            server.RedirectUri,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, server.RequestCount);
    }

    /// <summary>SDK clients use the established protocol instead of the newer discovery draft by default.</summary>
    [Fact]
    public void Sdk_clients_pin_the_established_protocol_version()
    {
        var options = McpProtocolCompatibility.CreateClientOptions(TimeSpan.FromSeconds(30));

        Assert.Equal("2025-06-18", options.ProtocolVersion);
        Assert.Equal(TimeSpan.FromSeconds(30), options.InitializationTimeout);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        {
            _route = route;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedRequest(
                request.RequestUri,
                request.Headers.UserAgent.ToString()));
            var response = _route(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed record RecordedRequest(Uri? RequestUri, string UserAgent);

    private sealed class LoopbackRedirectServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TcpListener _listener;
        private readonly Task _serverTask;
        private int _requestCount;

        public LoopbackRedirectServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            RedirectUri = new Uri($"http://127.0.0.1:{endpoint.Port}/redirect", UriKind.Absolute);
            _serverTask = ServeAsync();
        }

        public Uri RedirectUri { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            ObserveBackgroundFailure(_serverTask);
            _cancellation.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                await HandleAsync(client, _cancellation.Token);
            }
        }

        private static void ObserveBackgroundFailure(Task task)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            await using NetworkStream stream = client.GetStream();
            var buffer = new byte[1024];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    break;
                }
            }

            string request = Encoding.ASCII.GetString(buffer, 0, total);
            string response = request.StartsWith("GET /redirect ", StringComparison.Ordinal)
                ? "HTTP/1.1 302 Found\r\nLocation: /target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            byte[] payload = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(payload, cancellationToken);
        }
    }
}
