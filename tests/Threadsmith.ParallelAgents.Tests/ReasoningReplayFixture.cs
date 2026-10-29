namespace Threadsmith.ParallelAgents.Tests;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCodex;
using Threadsmith.Models.OpenAiCompatible;
using Threadsmith.Models.OpenAiCompatible.OpenRouter;
using Xunit;

/// <summary>Deterministic provider protocol data; no live endpoint or user configuration is used.</summary>
internal static class ReasoningReplayFixture
{
    private static readonly string[] CodexRoundTypes = ["reasoning", "function_call", "function_call_output"];

    internal static EffectiveModelProviderCatalog Catalog(bool codex, bool replay = true, int contextWindow = 200000, bool reasoningAlias = false, bool openRouter = false)
    {
        ModelConfiguration model = openRouter
            ? new OpenRouterModelConfiguration { Id = ModelProfileId.New(), Name = "fixture", ModelId = "vendor/fixture", JsonSchemaResponses = true, ReasoningControl = ReasoningControllability.Selectable, EffortMap = new Dictionary<ReasoningLevel, string> { [ReasoningLevel.Low] = "low" }, SupportsReasoningReplay = replay, Upstream = "test", ExpectedProviderName = "test" }
            : codex
            ? new OpenAiCodexModelConfiguration { Id = ModelProfileId.New(), Name = "fixture", ModelId = "fixture" }
            : new OpenAiCompatibleModelConfiguration
            {
                Id = ModelProfileId.New(),
                Name = "fixture",
                ModelId = "fixture",
                ReasoningCompatibility = new() { Mode = OpenAiReasoningControlMode.StandardEffort, SupportsReasoningReplay = replay, ResponseMode = OpenAiReasoningResponseMode.KnownFields, ReplayField = reasoningAlias ? OpenAiReasoningReplayField.Reasoning : OpenAiReasoningReplayField.ReasoningContent },
            };
        model = model with
        {
            ContextWindow = contextWindow,
            MaximumOutputTokens = 8192,
            RequestOutputTokenReserve = 4096,
            Capabilities = new() { Streaming = true, ToolCalls = true, StructuredOutput = true },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low],
            DefaultReasoningLevel = ReasoningLevel.Low,
        };
        ModelProviderConfiguration provider = openRouter
            ? new OpenRouterProviderConfiguration { Id = "fixture", Name = "fixture", SecretKeyReference = "secrets:fixture", Models = [model] }
            : codex
            ? new OpenAiCodexProviderConfiguration { Id = "fixture", Name = "fixture", SecretKeyReference = OpenAiCodexProviderRegistration.OAuthSecretReference, Models = [model] }
            : new OpenAiCompatibleProviderConfiguration { Id = "fixture", Name = "fixture", BaseUri = new Uri("https://fixture.invalid/v1/"), Models = [model] };
        return new EffectiveModelProviderCatalog(
            new() { Providers = [provider], DefaultModelId = model.Id },
            new ModelProviderRegistry([new OpenAiCodexProviderRegistration(), new OpenAiCompatibleProviderRegistration(), new OpenRouterProviderRegistration()]));
    }

    internal static string Stream(bool codex, int round, string tool, string arguments, bool reasoning = true)
    {
        if (!codex)
        {
            return (reasoning ? Data(new { choices = new[] { new { delta = new { reasoning_content = $"private-{round}-" } } } })
                + Data(new { choices = new[] { new { delta = new { reasoning_content = "state" } } } }) : string.Empty)
                + Data(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = $"wire-{round}", function = new { name = tool, arguments = arguments[..1] } } } } } } })
                + Data(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = arguments[1..] } } } }, finish_reason = "tool_calls" } } })
                + "data: [DONE]\n\n";
        }

        var reasoningItem = new { type = "reasoning", id = $"reason-{round}", summary = new[] { new { type = "summary_text", text = "visible summary" } }, encrypted_content = $"opaque-{round}-state", status = "completed", provider_extension = "retained" };
        var call = new { type = "function_call", id = $"item-{round}", call_id = $"wire-{round}", name = tool, arguments, status = "completed" };
        return (reasoning ? Data(new { type = "response.output_item.added", output_index = 0, item = new { type = "reasoning", id = $"reason-{round}", encrypted_content = "incomplete-do-not-replay" } })
            + Data(new { type = "response.reasoning_summary_text.delta", output_index = 0, delta = "visible " })
            + Data(new { type = "response.reasoning_summary_text.delta", output_index = 0, delta = "summary" }) : string.Empty)
            + Data(new { type = "response.function_call_arguments.delta", output_index = 1, delta = arguments[..1] })
            + Data(new { type = "response.function_call_arguments.delta", output_index = 1, delta = arguments[1..] })
            + Data(new { type = "response.output_item.done", output_index = 1, item = call })
            + (reasoning ? Data(new { type = "response.output_item.done", output_index = 0, item = reasoningItem }) : string.Empty)
            + (reasoning ? Data(new { type = "response.output_item.done", output_index = 0, item = reasoningItem }) : string.Empty)
            + (round == 0
                ? Data(new { type = "response.completed", response = new { usage = new { input_tokens = 100, output_tokens = 40 } } })
                : Data(new { type = "response.completed", response = new { output = reasoning ? new object[] { reasoningItem, call } : [call], usage = new { input_tokens = 100, output_tokens = 40 } } }));
    }

    internal static string Final(bool codex) => codex
        ? Data(new { type = "response.output_text.delta", delta = "Inspection complete." }) + Data(new { type = "response.completed", response = new { usage = new { input_tokens = 100, output_tokens = 20 } } })
        : Data(new { choices = new[] { new { delta = new { content = "Inspection complete." }, finish_reason = "stop" } } }) + "data: [DONE]\n\n";

    internal static void AssertReplay(JsonElement request, bool codex, int rounds, bool reasoningAlias = false)
    {
        if (codex)
        {
            var items = request.GetProperty("input").EnumerateArray().Where(item => item.TryGetProperty("type", out _)).ToArray();
            Assert.Equal(Enumerable.Range(0, rounds).SelectMany(_ => CodexRoundTypes), items.Select(item => item.GetProperty("type").GetString()));
            for (var round = 0; round < rounds; round++)
            {
                Assert.Equal($"opaque-{round}-state", items[round * 3].GetProperty("encrypted_content").GetString());
                Assert.Equal($"reason-{round}", items[round * 3].GetProperty("id").GetString());
                Assert.Equal("completed", items[round * 3].GetProperty("status").GetString());
                Assert.Equal("retained", items[round * 3].GetProperty("provider_extension").GetString());
                Assert.Equal("visible summary", items[round * 3].GetProperty("summary")[0].GetProperty("text").GetString());
                Assert.Equal($"wire-{round}", items[(round * 3) + 1].GetProperty("call_id").GetString());
                Assert.Equal($"wire-{round}", items[(round * 3) + 2].GetProperty("call_id").GetString());
            }
        }
        else
        {
            var messages = request.GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() is "assistant" or "tool").ToArray();
            Assert.Equal(rounds * 2, messages.Length);
            for (var round = 0; round < rounds; round++)
            {
                Assert.Equal($"private-{round}-state", messages[round * 2].GetProperty(reasoningAlias ? "reasoning" : "reasoning_content").GetString());
                Assert.False(messages[round * 2].TryGetProperty(reasoningAlias ? "reasoning_content" : "reasoning", out _));
                Assert.Equal($"wire-{round}", messages[round * 2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
                Assert.Equal($"wire-{round}", messages[(round * 2) + 1].GetProperty("tool_call_id").GetString());
            }
        }

        Assert.DoesNotContain("incomplete-do-not-replay", request.GetRawText(), StringComparison.Ordinal);
    }

    internal static HttpContent FragmentedContent(string stream) => new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(stream)));

    internal sealed class InstructionResolver : IModelProviderInstructionResolver
    {
        private readonly EffectiveModelProviderCatalog _catalog;

        internal InstructionResolver(EffectiveModelProviderCatalog catalog)
        {
            _catalog = catalog;
        }

        public ModelProviderInstructions? Resolve(ModelProfileId profileId)
        {
            var asset = _catalog.Get(profileId).Profile.ProviderInstructionAsset;
            return asset is null ? null : new ModelProviderInstructions
            {
                SectionId = asset.SectionId,
                PromptFileName = asset.PromptFileName,
                Content = TestPromptLoader.Instance.Get(asset.PromptFileName),
            };
        }
    }

    private static string Data(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";

    private sealed class FragmentedStream : MemoryStream
    {
        internal FragmentedStream(byte[] bytes)
            : base(bytes)
        {
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
    }
}
