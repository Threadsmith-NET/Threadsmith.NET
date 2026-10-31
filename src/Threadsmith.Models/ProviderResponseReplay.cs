namespace Threadsmith.Models;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Binds detached provider responses to the existing active-loop replay owner.</summary>
internal static class ProviderResponseReplay
{
    /// <summary>Includes retained output in admission without accumulating allowances on repeated preparation.</summary>
    internal static ModelRequestPreparationResult Prepare(ModelRequestPreparationContext context)
    {
        var request = context.Request;
        var estimate = Estimate(request, context.Profile);
        return new ModelRequestPreparationResult
        {
            WireEstimate = estimate,
            WireDigest = Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                request.Input,
                History = ModelRequestTransientState.ComputePrefixDigest(request.Messages),
                Tools = ModelToolCanonicalizer.ComputeDigest(ModelToolCanonicalizer.Canonicalize(request.Tools)),
                Instructions = Instructions(request),
                estimate,
                Replay = request.TransientState?.Responses.Select(item => item.Binding.NormalizedRoundDigest).ToArray(),
            })),
            CacheCapabilities = request.CacheCapabilities,
            CachePlan = request.CachePlan,
        };
    }

    /// <summary>Recomputes capacity from normalized input and the current retained state.</summary>
    internal static ModelWireEstimate Estimate(ModelStreamRequest request, ModelProfile profile)
    {
        var messages = request.Messages.Count > 0 ? request.Messages
            : [new ModelMessage { Role = ModelMessageRole.User, SectionId = "legacy-input", Content = [new() { Content = request.Input }] }];
        var tools = request.ToolTransportMode == ToolTransportMode.Text
            ? new ModelWireToolEstimate { TextToolTokens = request.WireEstimate?.TextToolTokens ?? throw new ModelProviderException("Text tool requests require a host capacity estimate.") }
            : ModelWireEstimator.EstimateTools(ModelToolCanonicalizer.Canonicalize(request.Tools), request.ToolTransportMode);
        return IncludeAllowance(request, ModelWireEstimator.Estimate(
            messages,
            tools,
            request.Layout?.StablePrefixMessageCount ?? 0,
            request.MaximumOutputTokens ?? profile.EffectiveRequestOutputTokenReserve,
            request.ProviderInstructions));
    }

    /// <summary>Adds size-only replay attribution to a fresh neutral estimate.</summary>
    internal static ModelWireEstimate IncludeAllowance(ModelStreamRequest request, ModelWireEstimate estimate)
    {
        var retained = request.TransientState?.RetainedOutputTokens ?? 0;
        if (retained == 0)
        {
            return estimate;
        }

        var fits = retained <= int.MaxValue - estimate.WireInputTokens;
        var total = fits ? estimate.WireInputTokens + retained : int.MaxValue;
        return estimate with
        {
            WireInputTokens = (int)total,
            Components = fits
                ? [.. estimate.Components, new("retained-allowance", "Provider overhead/replay", "Retained output allowance", "Capacity allowances", retained)] : [],
            StablePrefixComponentCount = fits ? estimate.StablePrefixComponentCount : 0,
            EstimationBasis = estimate.EstimationBasis + " Includes retained-output capacity allowance.",
        };
    }

    /// <summary>Retains a completed protocol response together with its exact preceding history.</summary>
    internal static ModelResponseReplayEnvelope Create(
        ModelStreamRequest request,
        ModelProfile profile,
        string credential,
        string protocol,
        JsonNode response,
        IReadOnlyList<string> callIds,
        long outputTokens)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["history_count"] = request.Messages.Count,
            ["history_digest"] = ModelRequestTransientState.ComputePrefixDigest(request.Messages),
            ["input_digest"] = Hash(JsonSerializer.SerializeToUtf8Bytes(request.Input)),
            ["response"] = response,
        });
        try
        {
            var binding = new ModelReplayBinding
            {
                ProviderId = protocol,
                ModelId = profile.ModelId,
                ProfileId = profile.Id,
                RunId = request.RunId,
                ModelRound = request.ToolContinuationRound,
                HistoryRewriteGeneration = request.HistoryRewriteGeneration,
                CredentialGeneration = Hash(JsonSerializer.SerializeToUtf8Bytes(new { credential, profile.Endpoint, profile.SecretKeyReference })),
                ToolInventoryDigest = ModelToolCanonicalizer.ComputeDigest(ModelToolCanonicalizer.Canonicalize(request.Tools)),
                InstructionDigest = Instructions(request),
                NormalizedRoundDigest = Hash(payload),
            };
            return new ModelResponseReplayEnvelope(binding, payload, callIds, Math.Max(outputTokens, (payload.LongLength + 3) / 4));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Rejects incompatible authority before any private data is sent to an endpoint.</summary>
    internal static void Validate(ModelStreamRequest request, ModelProfile profile, string credential, string protocol)
    {
        if (request.TransientState is not { HasResponses: true } state)
        {
            return;
        }

        state.ValidateHistory(request);
        var credentialDigest = Hash(JsonSerializer.SerializeToUtf8Bytes(new { credential, profile.Endpoint, profile.SecretKeyReference }));
        var tools = ModelToolCanonicalizer.ComputeDigest(ModelToolCanonicalizer.Canonicalize(request.Tools));
        var instructions = Instructions(request);
        foreach (var envelope in state.Responses)
        {
            var binding = envelope.Binding;
            if (binding.ProviderId != protocol || binding.ModelId != profile.ModelId || binding.ProfileId != profile.Id
                || binding.CredentialGeneration != credentialDigest || binding.ToolInventoryDigest != tools
                || binding.InstructionDigest != instructions)
            {
                throw new ModelProviderException("Private response authority changed; start a fresh turn.");
            }
        }
    }

    /// <summary>Projects detached protocol data only at its original chronological history position.</summary>
    internal static JsonNode Read(ModelResponseReplayEnvelope envelope, ModelStreamRequest request, int messageIndex)
    {
        var bytes = envelope.CopyProtocolPayload();
        try
        {
            if (Hash(bytes) != envelope.Binding.NormalizedRoundDigest)
            {
                throw new ModelProviderException("Private response content changed.");
            }

            var payload = JsonNode.Parse(bytes)?.AsObject() ?? throw new ModelProviderException("Private response is missing.");
            var count = payload["history_count"]?.GetValue<int>() ?? -1;
            var originalInput = request.Messages.Count > 0 ? request.Messages[0].GetModelVisibleContent() : request.Input;
            var prefixMatches = request.TransientState?.MatchesRewrittenPrefix(envelope.Binding.ModelRound, request.Messages, messageIndex);
            if (prefixMatches == false || (prefixMatches is null && (count < 0 || count > request.Messages.Count || (count > 0 && count != messageIndex)
                || payload["history_digest"]?.GetValue<string>() != ModelRequestTransientState.ComputePrefixDigest(request.Messages.Take(count))
                || (count == 0 && payload["input_digest"]?.GetValue<string>() != Hash(JsonSerializer.SerializeToUtf8Bytes(originalInput))))))
            {
                throw new ModelProviderException("Private response preceding history changed; start a fresh turn.");
            }

            return payload["response"]?.DeepClone() ?? throw new ModelProviderException("Private response is missing.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Instructions(ModelStreamRequest request)
        => Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            // Read validates every message preceding each retained response, including instructions.
            // Appended chronological corrections must not change the stable authority digest.
            request.ProviderInstructions,
            request.ReasoningLevel,
            request.AllowMultipleToolCalls,
            request.ToolTransportMode,
            request.ContinuationBinding?.ProviderId,
            request.ContinuationBinding?.RequestGeneration,
            request.ContinuationBinding?.TrustPolicyGeneration,
            request.ContinuationBinding?.Phase,
            request.ContinuationBinding?.InstructionBundleDigest,
            request.Layout?.Version,
        }));
}
