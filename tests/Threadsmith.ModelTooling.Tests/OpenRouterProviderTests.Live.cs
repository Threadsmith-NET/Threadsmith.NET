namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible.OpenRouter;
using Threadsmith.Tools;
using Xunit;

public static partial class OpenRouterProviderTests
{
    /// <summary>Opt-in remote verification uses the existing catalog and user-owned credential boundary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Live_ConfiguredResponsesReasoningAndTools(bool reasoning)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("THREADSMITH_OPENROUTER_LIVE") == "1", "Explicit live-test opt-in required; incurs provider charges.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var userDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".threadsmith");
        var catalogPath = Path.Combine(userDirectory, "providers.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath, cancellationToken))!.AsObject();
        var registration = new OpenRouterProviderRegistration();
        var registry = new ModelProviderRegistry([registration]);
        var providerNode = root["providers"]!.AsArray().OfType<JsonObject>().First(node => node["type"]?.GetValue<string>() == "openrouter");
        var configuration = Assert.IsType<OpenRouterProviderConfiguration>(providerNode.Deserialize<ModelProviderConfiguration>(registry.CreateSerializerOptions()));
        var resolver = new SecretResolver([new EnvironmentSecretProvider(), new UserFileSecretProvider(Path.Combine(userDirectory, "secrets", "config.json"))], timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var resolution = await resolver.ResolveAsync(
            new SecretResolutionRequest
            {
                Reference = SecretReference.Parse(configuration.SecretKeyReference!),
                ComponentId = "openrouter-live-test",
                Purpose = "Authorized opt-in Responses verification",
            },
            cancellationToken);
        Assert.True(resolution.Value is not null, "Configured credential could not be resolved.");
        using var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var configuredModel in configuration.Models.OfType<OpenRouterModelConfiguration>().Where(model => model.Enabled))
        {
            // Functional checks use runner cancellation, not production wall-clock defaults.
            var model = configuredModel with { TimeoutSeconds = 0 };
            var isolated = configuration with { Models = [model] };
            var profile = Assert.Single(registration.CreateProfiles(isolated));
            // Separate release-blocker reproduction; ordinary configured checks must not imply seed compatibility.
            if (Environment.GetEnvironmentVariable("THREADSMITH_OPENROUTER_SEED_PROBE") == "1")
            {
                using var probe = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);
                probe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolution.Value.Reveal());
                var probeBody = new JsonObject
                {
                    ["model"] = model.ModelId,
                    ["input"] = "Reply OK.",
                    ["max_output_tokens"] = 512,
                    ["stream"] = true,
                    ["seed"] = 42,
                    ["store"] = false,
                    ["debug"] = new JsonObject { ["echo_upstream_body"] = true },
                };
                probe.Content = new StringContent(probeBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                using var probeResponse = await client.SendAsync(probe, cancellationToken);
                var data = await probeResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!probeResponse.IsSuccessStatusCode)
                {
                    var error = JsonNode.Parse(data)?["error"]?["message"]?.GetValue<string>() ?? string.Empty;
                    TestContext.Current.TestOutputHelper!.WriteLine($"Debug probe status={(int)probeResponse.StatusCode}, mentionsDebug={error.Contains("debug", StringComparison.OrdinalIgnoreCase)}, mentionsSeed={error.Contains("seed", StringComparison.OrdinalIgnoreCase)}");
                    probeBody.Remove("debug");
                    using var seedProbe = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);
                    seedProbe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolution.Value.Reveal());
                    seedProbe.Content = new StringContent(probeBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    using var seedResponse = await client.SendAsync(seedProbe, cancellationToken);
                    TestContext.Current.TestOutputHelper.WriteLine($"Seed-only acceptance status={(int)seedResponse.StatusCode} (acceptance does not prove forwarding).");
                    probeBody.Remove("seed");
                    probeBody["max_output_tokens"] = model.EffectiveRequestOutputTokenReserve;
                    using var baselineProbe = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);
                    baselineProbe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolution.Value.Reveal());
                    baselineProbe.Content = new StringContent(probeBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    using var baselineResponse = await client.SendAsync(baselineProbe, cancellationToken);
                    TestContext.Current.TestOutputHelper.WriteLine($"No-seed baseline with configured output reserve status={(int)baselineResponse.StatusCode}.");
                    probeBody["seed"] = 42;
                    using var matchedProbe = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);
                    matchedProbe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolution.Value.Reveal());
                    matchedProbe.Content = new StringContent(probeBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    using var matchedResponse = await client.SendAsync(matchedProbe, cancellationToken);
                    TestContext.Current.TestOutputHelper.WriteLine($"Seed with configured output reserve status={(int)matchedResponse.StatusCode}.");
                    Assert.Equal(System.Net.HttpStatusCode.OK, matchedResponse.StatusCode);
                }

                foreach (var line in data.Split('\n').Where(line => line.StartsWith("data: {", StringComparison.Ordinal)))
                {
                    var node = JsonNode.Parse(line[6..])!.AsObject();
                    if (node.ContainsKey("debug") || node["type"]?.GetValue<string>()?.Contains("debug", StringComparison.Ordinal) == true)
                    {
                        TestContext.Current.TestOutputHelper!.WriteLine("Remote debug schema: " + string.Join(",", node.Select(pair => pair.Key)));
                        TestContext.Current.TestOutputHelper.WriteLine("Remote debug payload: " + DescribeSeed(node));
                    }
                }
            }

            var provider = registration.CreateProvider(new ModelProviderActivationContext
            {
                HttpClient = client,
                Profile = profile,
                ProviderConfiguration = isolated,
                ModelConfiguration = model,
                ResolvedSecret = resolution.Value.Reveal(),
            });
            using var state = new ModelRequestTransientState();
            var request = new ModelStreamRequest
            {
                RunId = RunId.New(),
                ResolvedProfileId = model.Id,
                ReasoningLevel = reasoning && model.SupportedReasoningLevels.Contains(ReasoningLevel.High) ? ReasoningLevel.High : model.DefaultReasoningLevel,
                Input = "Work out 137 times 249 step by step, then use inspect once with empty arguments. After receiving its result answer exactly verified.",
                Messages = [Message(ModelMessageRole.User, "Work out 137 times 249 step by step, then use inspect once with empty arguments. After receiving its result answer exactly verified.")],
                Tools = [new ModelToolDefinition { Name = "inspect", Description = "Returns a verification value", ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" }],
                IncludeReasoningText = false,
                TransientState = state,
            };
            var chunks = new List<ModelChunk>();
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
            {
                chunks.Add(chunk);
                if (chunk.ResponseEnvelope is { } envelope)
                {
                    state.Accept(request, envelope);
                }
            }

            Assert.Single(chunks, chunk => chunk.Output is ToolRequestModelOutput);
            Assert.Single(chunks, chunk => chunk.FinishReason == ModelFinishReason.ToolCalls);
            Assert.Contains(chunks, chunk => chunk.Usage is { InputTokens: > 0, OutputTokens: > 0, IsEstimate: false });
            var usage = chunks.Last(chunk => chunk.Usage is not null).Usage!;
            Assert.True(!reasoning || request.ReasoningLevel == ReasoningLevel.None || usage.ReasoningTokens > 0, "Configured reasoning was requested but no reasoning tokens were reported.");
            TestContext.Current.TestOutputHelper!.WriteLine($"{model.ModelId} ({request.ReasoningLevel}): replay={model.SupportsReasoningReplay}, seed={model.SeedSupported}, input={usage.InputTokens}, output={usage.OutputTokens}, reasoning={usage.ReasoningTokens}, reads={usage.Cache?.CacheReadTokens}, writes={usage.Cache?.CacheWriteTokens}, charge={usage.ReportedCost}");
            if (state.HasResponses)
            {
                state.BindToolCall(0, 0, "host-call");
            }

            ModelMessage[] history = [.. request.Messages,
                Message(ModelMessageRole.Assistant, "{}") with { ToolCallId = "host-call", ToolName = "inspect", ModelRound = 0 },
                Message(ModelMessageRole.Tool, "verified") with { ToolCallId = "host-call", ModelRound = 0 }];
            if (state.HasResponses)
            {
                state.SealRound(0, history);
            }

            chunks.Clear();
            await CollectAsync(provider, request with { Messages = history, ToolContinuationRound = 1 }, chunks);
            Assert.Contains("verified", string.Concat(chunks.Select(chunk => chunk.Text)), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(chunks, chunk => chunk.Output is not null);
        }
    }

    private static string DescribeSeed(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            return string.Join(";", obj.Select(pair => pair.Key == "seed" ? "seed=" + pair.Value?.ToJsonString()
                : pair.Value is JsonObject or JsonArray ? pair.Key + "{" + DescribeSeed(pair.Value) + "}" : pair.Key));
        }

        return node is JsonArray array ? string.Join(";", array.Select(DescribeSeed)) : string.Empty;
    }
}
