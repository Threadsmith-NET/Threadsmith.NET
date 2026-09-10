namespace Threadsmith.ContextCaching.Tests;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

/// <summary>Verifies private protocol lifetime, correlation, and admission contracts.</summary>
public static class TransientModelResponseTests
{
    /// <summary>Serialization and formatting cannot export private replay or credential generation.</summary>
    [Fact]
    public static void PrivateReplay_IsExcludedFromEveryGenericProjection()
    {
        var request = CreateRequest();
        using var state = new ModelRequestTransientState();
        var envelope = CreateEnvelope(request, ["wire-one"]);
        state.Accept(request, envelope);
        var projections = new[]
        {
            JsonSerializer.Serialize(envelope),
            JsonSerializer.Serialize(state),
            JsonSerializer.Serialize(request with { TransientState = state, IncludeReasoningText = true }),
            JsonSerializer.Serialize(new ModelChunk { ResponseEnvelope = envelope }),
            envelope.ToString(),
            state.ToString(),
            (request with { TransientState = state }).ToString(),
            new ModelChunk { ResponseEnvelope = envelope }.ToString(),
        };

        foreach (var projection in projections)
        {
            Assert.DoesNotContain("PRIVATE_CANARY", projection, StringComparison.Ordinal);
            Assert.DoesNotContain("CREDENTIAL_CANARY", projection, StringComparison.Ordinal);
        }

        state.Dispose();
        Assert.Equal(0, envelope.ByteCount);
        Assert.False(state.HasResponses);
    }

    /// <summary>Identical tool names bind by ordinal and independent round, then reject changed visible history.</summary>
    [Fact]
    public static void OrdinalCorrelation_RejectsRewrittenOrUnsealedHistory()
    {
        var request = CreateRequest();
        using var state = new ModelRequestTransientState();
        state.Accept(request, CreateEnvelope(request, ["wire-one", "wire-two"]));
        state.BindToolCall(0, 0, "host-a");
        state.BindToolCall(0, 1, "host-b");
        var messages = CreateMessages("host-a", "host-b");
        state.SealRound(0, messages);
        var continuation = request with { Messages = messages, ToolContinuationRound = 1, TransientState = state };
        state.ValidateHistory(continuation);
        Assert.Equal("wire-one", state.GetWireToolCallId(0, "host-a"));
        Assert.Equal("wire-two", state.GetWireToolCallId(0, "host-b"));
        Assert.Throws<ModelProviderException>(() => state.GetWireToolCallId(1, "host-a"));
        Assert.Throws<ModelProviderException>(() => state.BindToolCall(0, 0, "another-host"));
        Assert.Throws<ModelProviderException>(() => state.ValidateHistory(continuation with { HistoryRewriteGeneration = 1 }));
        Assert.Throws<ModelProviderException>(() => state.ValidateHistory(continuation with
        {
            Messages = [messages[0] with { Content = [new ModelContentPart { Content = "changed" }] }, messages[1]],
        }));
    }

    /// <summary>Rejected completion metadata releases its payload without enlarging retained state.</summary>
    [Fact]
    public static void RetainedByteBound_RejectsAndClearsOverflow()
    {
        var request = CreateRequest();
        using var state = new ModelRequestTransientState(5);
        var envelope = CreateEnvelope(request, []);
        Assert.Throws<ModelProviderException>(() => state.Accept(request, envelope));
        Assert.Equal(0, envelope.ByteCount);
        Assert.Equal(0, state.RetainedBytes);
    }

    /// <summary>Cache categories are priced once and admission allows the cold-write rate.</summary>
    [Fact]
    public static void CachePricing_DistinguishesActualUsageFromColdAdmission()
    {
        var cost = new ModelCostMetadata
        {
            InputPerMillionTokens = 3,
            OutputPerMillionTokens = 15,
            CachePricing = new ModelCachePricing
            {
                WritePerMillionTokens = 3.75m,
                ReadPerMillionTokens = 0.3m,
                SourceDate = "2026-09-10",
            },
        };
        var usage = new ModelUsage(600, 25, Cache: new ModelCacheUsage
        {
            CacheReadTokens = 300,
            CacheWriteTokens = 200,
            ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
        });

        Assert.Equal(0.001515m, cost.CalculateUsage(usage));
        Assert.Equal(0.002625m, cost.CalculateAdmission(600, 25));
        Assert.Equal(cost.CalculateAdmission(600, 25), cost.CalculateUsage(usage with { Cache = null }));
        Assert.Throws<ModelProviderException>(() => (cost with { PricesAvailable = false }).CalculateAdmission(600, 25));
        Assert.Throws<ModelProviderException>(() => (cost with { PricesAvailable = false }).CalculateUsage(usage with { Cache = null }));
        var profile = new ModelProfile
        {
            Id = ModelProfileId.New(),
            Name = "cache-priced",
            Provider = "test",
            ModelId = "cache-priced",
            Endpoint = new Uri("https://example.invalid"),
            ContextWindow = 10000,
            MaximumOutputTokens = 1000,
            Cost = cost,
        };
        var selection = new ModelSelectionRequest
        {
            Constraints = new ModelSelectionConstraints { MaximumCombinedCostPerMillionTokens = 18.5m },
        };
        Assert.False(ModelCapabilityNegotiator.Negotiate(profile, selection).IsCompatible);
        Assert.True(ModelCapabilityNegotiator.Negotiate(profile with { Cost = cost with { CachePricing = null } }, selection).IsCompatible);
        Assert.False(ModelCapabilityNegotiator.Negotiate(profile with { Cost = cost with { PricesAvailable = false } }, selection).IsCompatible);
    }

    private static ModelStreamRequest CreateRequest() => new()
    {
        RunId = new RunId(Guid.NewGuid()),
        ResolvedProfileId = new ModelProfileId(Guid.NewGuid()),
        Input = "hello",
    };

    private static ModelResponseReplayEnvelope CreateEnvelope(ModelStreamRequest request, IReadOnlyList<string> calls)
    {
        var profileId = request.ResolvedProfileId ?? throw new InvalidOperationException();
        return new ModelResponseReplayEnvelope(
            new ModelReplayBinding
            {
                ProviderId = "provider",
                ModelId = "model",
                ProfileId = profileId,
                RunId = request.RunId,
                ModelRound = request.ToolContinuationRound,
                CredentialGeneration = "CREDENTIAL_CANARY",
                ToolInventoryDigest = "tools",
                InstructionDigest = "policy",
                NormalizedRoundDigest = "round",
            },
            Encoding.UTF8.GetBytes("PRIVATE_CANARY"),
            calls,
            20);
    }

    private static ModelMessage[] CreateMessages(string first, string second) =>
    [
        new()
        {
            Role = ModelMessageRole.Assistant,
            SectionId = "call",
            ModelRound = 0,
            ToolCallId = first,
            ToolName = "read",
            Content = [new ModelContentPart { Content = "{}" }],
        },
        new()
        {
            Role = ModelMessageRole.Assistant,
            SectionId = "call",
            ModelRound = 0,
            ToolCallId = second,
            ToolName = "read",
            Content = [new ModelContentPart { Content = "{}" }],
        },
    ];
}
