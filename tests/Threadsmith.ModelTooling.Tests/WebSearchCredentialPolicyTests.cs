namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies credential use through the actual search invocation pipeline.</summary>
public sealed class WebSearchCredentialPolicyTests
{
    /// <summary>Default and custom credentials resolve without a second permission setting.</summary>
    [Theory]
    [InlineData("secrets:BRAVE_SEARCH_API_KEY")]
    [InlineData("secrets:search:brave")]
    public async Task Pipeline_ConfiguredCredential_AuthenticatesWithoutSeparatePermission(string reference)
    {
        // Act
        var (result, resolver, handler) = await InvokeAsync(reference);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(reference, resolver.LastRequest?.Reference.CanonicalName);
        Assert.Equal(SecretProviderTrust.UserOwned, resolver.LastRequest?.MinimumTrust);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("test-credential-value", handler.SeenCredential);
        Assert.DoesNotContain("test-credential-value", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>Removing a stored credential still stops search before transport.</summary>
    [Fact]
    public async Task Pipeline_MissingCredential_FailsBeforeHttp()
    {
        // Act
        var (result, resolver, handler) = await InvokeAsync("secrets:BRAVE_SEARCH_API_KEY", credentialPresent: false);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>Existing tool and network denials stop credential resolution and transport.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Pipeline_ToolOrNetworkDenied_DoesNotResolveCredential(bool networkAllowed, bool toolDenied)
    {
        // Act
        var (result, resolver, handler) = await InvokeAsync(
            "secrets:BRAVE_SEARCH_API_KEY", networkAllowed: networkAllowed, toolDenied: toolDenied);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>Malformed declared references remain invalid without an allowlist.</summary>
    [Theory]
    [InlineData("token")]
    [InlineData("secrets:")]
    [InlineData("secrets:bad/name")]
    public async Task Pipeline_MalformedCredentialReference_DoesNotResolveCredential(string reference)
    {
        // Act
        var (result, resolver, handler) = await InvokeAsync(reference);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, handler.CallCount);
    }

    private static async Task<(ToolInvocationResult Result, RecordingResolver Resolver, RecordingHandler Handler)> InvokeAsync(
        string reference,
        bool credentialPresent = true,
        bool networkAllowed = true,
        bool toolDenied = false)
    {
        // Arrange: all secret and HTTP activity is in memory; no live credentials or network are used.
        var options = new WebSearchOptions { SecretReference = reference, MinimumRequestInterval = TimeSpan.Zero };
        var resolver = new RecordingResolver { CredentialPresent = credentialPresent };
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var client = new BraveWebSearchClient(http, resolver, options, TestPromptLoader.Instance);
        var sanitizer = new SecretOutputSanitizer();
        var tool = new WebSearchTool(client, options, sanitizer, TestPromptLoader.Instance);
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([tool]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance);

        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "web_search",
            ArgumentsJson = "{\"query\":\"threadsmith\"}",
            Context = new ToolInvocationContext
            {
                RepositoryPath = Path.GetTempPath(),
                AllowedNetworkHosts = networkAllowed ? [options.Endpoint.Host] : [],
                DeniedToolIds = toolDenied ? ["web_search"] : [],
                RequestedBy = "test",
            },
        });
        return (result, resolver, handler);
    }

    private sealed class RecordingResolver : ISecretResolver
    {
        public bool CredentialPresent { get; init; }

        public int CallCount { get; private set; }

        public SecretResolutionRequest? LastRequest { get; private set; }

        public Task<SecretResolutionResult> ResolveAsync(SecretResolutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(CredentialPresent
                ? new SecretResolutionResult { Value = new SecretValue("test-credential-value"), ProviderId = "test-user", Failure = SecretResolutionFailure.None }
                : new SecretResolutionResult { Failure = SecretResolutionFailure.NotFound, Diagnostics = ["test-user:not-found"] });
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? SeenCredential { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            SeenCredential = request.Headers.GetValues("X-Subscription-Token").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{\"web\":{\"results\":[]}}"),
            });
        }
    }
}
