namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible.OpenRouter;
using Threadsmith.Telemetry;
using Xunit;

public static partial class OpenRouterProviderTests
{
    /// <summary>Routing stays restricted and response identities follow the configured list.</summary>
    [Theory]
    [InlineData("Azure", true)]
    [InlineData("OpenAI", true)]
    [InlineData(null, true)]
    [InlineData("Other", false)]
    public static async Task Fallbacks_ProjectOrderedListAndValidateIdentity(string? identity, bool accepted)
    {
        var model = Model() with { Upstreams = Routes() };
        using var handler = new Handler(Terminal("response.completed", "completed", [], Usage(), identity));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        if (accepted)
        {
            await CollectAsync(Provider(client, model), Request(), []);
        }
        else
        {
            await Assert.ThrowsAsync<MalformedModelOutputException>(() => CollectAsync(Provider(client, model), Request(), []));
        }

        using var body = JsonDocument.Parse(handler.Body!);
        var routing = body.RootElement.GetProperty("provider");
        Assert.Collection(
            routing.GetProperty("order").EnumerateArray(),
            item => Assert.Equal("azure", item.GetString()),
            item => Assert.Equal("openai", item.GetString()));
        Assert.Equal(routing.GetProperty("order").GetRawText(), routing.GetProperty("only").GetRawText());
        Assert.True(routing.GetProperty("allow_fallbacks").GetBoolean());
        Assert.True(routing.GetProperty("require_parameters").GetBoolean());
    }

    /// <summary>Configuration rejects ambiguous and unusable routes while allowing shared display names.</summary>
    [Fact]
    public static void Fallbacks_ValidateConfiguration()
    {
        var registration = new OpenRouterProviderRegistration();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OpenRouterModelConfiguration>("{\"modelId\":\"vendor/model\",\"upstream\":\"azure\"}"));
        OpenRouterUpstreamConfiguration[][] invalid =
        [
            [],
            [null!],
            [new() { Provider = " ", ExpectedProviderName = "Azure" }],
            [new() { Provider = "azure", ExpectedProviderName = " " }],
            [new() { Provider = "azure", ExpectedProviderName = "Azure" }, new() { Provider = "AZURE", ExpectedProviderName = "Azure" }],
        ];
        foreach (var routes in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => registration.Validate(Configuration(Model() with { Upstreams = routes })));
        }

        registration.Validate(Configuration(Model() with
        {
            ReasoningControl = ReasoningControllability.AlwaysOn,
            SupportsReasoningReplay = true,
            Upstreams = [new() { Provider = "azure/east", ExpectedProviderName = "Azure" }, new() { Provider = "azure/west", ExpectedProviderName = "Azure" }],
        }));
    }

    /// <summary>Availability errors preserve usage and text through the existing bounded retry policy.</summary>
    [Theory]
    [InlineData("provider_unavailable", false)]
    [InlineData("provider_overloaded", false)]
    [InlineData("provider_unavailable", true)]
    [InlineData("invalid_request", false)]
    public static async Task ProviderFailure_PreservesDetailsAndCommitBoundary(string errorType, bool afterText)
    {
        var failure = Frame(new JsonObject
        {
            ["type"] = "response.failed",
            ["response"] = new JsonObject
            {
                ["id"] = "generation-1",
                ["error_type"] = errorType,
                ["provider"] = "Azure",
                ["error"] = new JsonObject { ["code"] = "server_error", ["message"] = "Capacity detail for test-key" },
                ["usage"] = Usage(),
            },
        }.ToJsonString());
        using var handler = new Handler((afterText ? Frame("{\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"visible\"}") : string.Empty) + failure);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var model = Model() with { RetryMaxAttempts = 2, Upstreams = Routes() };
        var chunks = new List<ModelChunk>();
        var exception = await Record.ExceptionAsync(() => CollectAsync(Provider(client, model), Request(), chunks));
        Assert.NotNull(exception);
        Assert.Contains("Capacity detail for [REDACTED]", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            "Capacity detail for [REDACTED]" + Environment.NewLine
                + "Code: server_error · Type: " + errorType + " · Provider: Azure · Generation ID: generation-1",
            exception.Message);
        var transient = errorType != "invalid_request";
        Assert.Equal(transient, exception is TransientModelException);
        Assert.Equal(transient && !afterText ? 2 : 1, handler.Calls);
        Assert.All(chunks.Where(chunk => chunk.Usage is not null), chunk => Assert.Equal(transient, chunk.IsInterruptedUsage));
    }

    /// <summary>HTTP failures remain bounded and are sanitized before the raw exchange logger.</summary>
    [Fact]
    public static async Task ProviderFailure_HttpAvailabilityUsesExistingRetries()
    {
        using var handler = new Handler("{\"error_type\":\"provider_unavailable\",\"error\":{\"message\":\"Capacity detail\"}}") { StatusCode = HttpStatusCode.BadGateway };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var error = await Assert.ThrowsAsync<TransientModelException>(() => CollectAsync(Provider(client, Model() with { RetryMaxAttempts = 2 }), Request(), []));
        Assert.Equal("Capacity detail" + Environment.NewLine + "HTTP 502 · Type: provider_unavailable", error.Message);
        Assert.Contains("Capacity detail", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>HTTP failures remain bounded and are sanitized before the raw exchange logger.</summary>
    [Theory]
    [InlineData("valid", "Capacity detail")]
    [InlineData("json", "Capacity detail")]
    [InlineData("malformed", "OpenRouter rejected the request with HTTP 400.")]
    [InlineData("oversized", "OpenRouter rejected the request with HTTP 400.")]
    public static async Task ProviderFailure_HttpDetailsAreSafeBeforeLogging(string form, string expected)
    {
        var body = form switch
        {
            "json" => new JsonObject { ["error"] = new JsonObject { ["message"] = "Capacity detail for test-key; {\"password\":\"SECOND_CANARY\"}" } }.ToJsonString(),
            "valid" => "{\"request_id\":\"req-1\",\"error\":{\"code\":\"capacity\",\"message\":\"Capacity detail for test-key; password=SECOND_CANARY\"}}",
            "oversized" => "{\"message\":\"" + new string('x', 5000) + "\"}",
            _ => "not JSON",
        };
        using var handler = new Handler(body) { StatusCode = HttpStatusCode.BadRequest };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var catalog = new EffectiveModelProviderCatalog(new() { Providers = [Configuration(Model())] }, new([new OpenRouterProviderRegistration()]));
        var configured = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>("test-key"), sanitizer: new SecretOutputSanitizer());
        var path = Path.Combine(Path.GetTempPath(), "threadsmith-provider-error-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var provider = new LoggingModelProvider(configured, new JsonlModelExchangeLog(path));
            var error = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(provider, Request(), []));
            Assert.DoesNotContain("\"message\":", error.Message, StringComparison.Ordinal);
            Assert.Contains(expected, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SECOND_CANARY", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("test-key", error.Message, StringComparison.Ordinal);
            var log = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(expected, log, StringComparison.Ordinal);
            Assert.DoesNotContain("test-key", log, StringComparison.Ordinal);
            Assert.DoesNotContain("SECOND_CANARY", log, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Large failed responses expose only bounded explanations, never private output.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ProviderFailure_LargeResponseRetainsOnlySafeDetails(bool longMessage)
    {
        // Arrange: unrelated output can be large but must not be formatted or copied into diagnostics.
        var response = new JsonObject
        {
            ["id"] = "generation-large",
            ["provider"] = "Azure",
            ["error"] = new JsonObject { ["code"] = "invalid_request", ["message"] = longMessage ? new string('x', 5000) + "test-key" : "Rejected test-key" },
            ["output"] = new JsonArray(new JsonObject { ["encrypted_content"] = "PRIVATE_REASONING_CANARY" + new string('z', 2 * 1024 * 1024) }),
        };
        using var handler = new Handler(Frame(new JsonObject { ["type"] = "response.failed", ["response"] = response }.ToJsonString()));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // Act
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(Provider(client, Model()), Request(), []));

        // Assert
        Assert.Contains("Code: invalid_request · Provider: Azure · Generation ID: generation-large", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_REASONING_CANARY", error.Message, StringComparison.Ordinal);
        if (longMessage)
        {
            var message = error.Message.Split(Environment.NewLine)[0];
            Assert.Equal(4096, message.Length);
            Assert.EndsWith(" [message truncated]", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("Rejected [REDACTED]" + Environment.NewLine, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Metadata and omission markers survive configured dispatch and raw failure logging.</summary>
    [Theory]
    [InlineData("message", "Rejected [REDACTED]")]
    [InlineData("code-only", "OpenRouter rejected the request with HTTP 403.")]
    [InlineData("malformed", "Error body: unreadable")]
    [InlineData("oversized", "Error body: truncated (omitted)")]
    [InlineData("empty", "OpenRouter rejected the request with HTTP 403.")]
    public static async Task ProviderFailure_MetadataAndOmissionsReachConfiguredLogging(string form, string expected)
    {
        var body = form switch
        {
            "message" => "{\"request_id\":\"req-body\",\"error\":{\"code\":\"invalid_request\",\"message\":\"Rejected test-key\"}}",
            "code-only" => "{\"error\":{\"code\":\"invalid_request\"}}",
            "malformed" => "not JSON PRIVATE_BODY_CANARY",
            "oversized" => new string('x', 4097) + "PRIVATE_BODY_CANARY",
            _ => string.Empty,
        };
        using var handler = new Handler(body) { StatusCode = HttpStatusCode.Forbidden, RequestId = "req-header" };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var catalog = new EffectiveModelProviderCatalog(new() { Providers = [Configuration(Model())] }, new([new OpenRouterProviderRegistration()]));
        var configured = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>("test-key"), sanitizer: new SecretOutputSanitizer());
        var path = Path.Combine(Path.GetTempPath(), "threadsmith-metadata-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var provider = new LoggingModelProvider(configured, new JsonlModelExchangeLog(path));
            var error = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(provider, Request(), []));
            Assert.Contains(expected, error.Message, StringComparison.Ordinal);
            Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
            Assert.Contains("Request ID: " + (form == "message" ? "req-body" : "req-header"), error.Message, StringComparison.Ordinal);
            if (form is "message" or "code-only")
            {
                Assert.Contains("Code: invalid_request", error.Message, StringComparison.Ordinal);
            }

            var log = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains(expected, log, StringComparison.Ordinal);
            Assert.Contains("Request ID:", log, StringComparison.Ordinal);
            Assert.DoesNotContain("test-key", log, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_BODY_CANARY", log, StringComparison.Ordinal);
            Assert.DoesNotContain("{\"error\":", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static OpenRouterUpstreamConfiguration[] Routes() =>
    [
        new() { Provider = "azure", ExpectedProviderName = "Azure" },
        new() { Provider = "openai", ExpectedProviderName = "OpenAI" },
    ];
}
