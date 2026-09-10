namespace Threadsmith.Models.Anthropic;

using System.Text;
using System.Text.Json;
using Threadsmith.Models;

/// <summary>Binds private content to the precise active request authority, excluding summary visibility.</summary>
internal static class AnthropicReplayIdentity
{
    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static ModelReplayBinding Create(ModelStreamRequest request, ModelProfile profile, AnthropicModelCompatibility compatibility, string providerId, string apiKey, ReadOnlySpan<byte> payload)
    {
        return new ModelReplayBinding
        {
            ProviderId = providerId,
            ProfileId = profile.Id,
            ModelId = profile.ModelId,
            RunId = request.RunId,
            ModelRound = request.ToolContinuationRound,
            HistoryRewriteGeneration = request.HistoryRewriteGeneration,
            CredentialGeneration = Credential(apiKey, providerId, profile.SecretKeyReference),
            ToolInventoryDigest = ModelToolCanonicalizer.ComputeDigest(ModelToolCanonicalizer.Canonicalize(request.Tools)),
            InstructionDigest = Instructions(request, compatibility),
            NormalizedRoundDigest = AnthropicRequestMapper.Hash(payload),
        };
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static void Validate(ModelStreamRequest request, ModelProfile profile, AnthropicModelCompatibility compatibility, string providerId, string? apiKey = null)
    {
        if (request.TransientState is not { HasResponses: true } state)
        {
            return;
        }

        state.ValidateHistory(request);
        var tools = ModelToolCanonicalizer.ComputeDigest(ModelToolCanonicalizer.Canonicalize(request.Tools));
        var instructions = Instructions(request, compatibility);
        foreach (var envelope in state.Responses)
        {
            var binding = envelope.Binding;
            if (binding.Version != 1 || binding.ProviderId != providerId || binding.ModelId != profile.ModelId
                || binding.ProfileId != profile.Id || binding.ToolInventoryDigest != tools || binding.InstructionDigest != instructions
                || (apiKey is not null && binding.CredentialGeneration != Credential(apiKey, providerId, profile.SecretKeyReference)))
            {
                throw new ModelProviderException("Anthropic continuation authority changed; complete the active tool boundary and start a fresh turn.");
            }
        }
    }

    /// <summary>Anchors each completed response to its exact preceding canonical history, including transient round ordering.</summary>
    internal static string HistoryDigest(IEnumerable<ModelMessage> messages) => AnthropicRequestMapper.Hash(
        JsonSerializer.SerializeToUtf8Bytes(messages.Select(message => new { Message = message, message.ModelRound })));

    private static string Credential(string apiKey, string providerId, string? reference) => AnthropicRequestMapper.Hash(
        JsonSerializer.SerializeToUtf8Bytes(new { providerId, reference, apiKey }));

    private static string Instructions(ModelStreamRequest request, AnthropicModelCompatibility compatibility) => AnthropicRequestMapper.Hash(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Prefix = request.Messages.Where(message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer)
                .Select(message => new { message.Role, message.SectionId, Content = message.GetModelVisibleContent() }),
            request.ProviderInstructions,
            request.ReasoningLevel,
            compatibility.ThinkingMode,
            Budget = compatibility.ManualThinkingBudgets.GetValueOrDefault(request.ReasoningLevel),
            compatibility.PromptCachingEnabled,
            compatibility.SupportsStrictSchemas,
            request.AllowMultipleToolCalls,
            request.ToolTransportMode,
            request.Layout?.Version,
            request.ContinuationBinding?.TrustPolicyGeneration,
            request.ContinuationBinding?.Phase,
            request.ContinuationBinding?.InstructionBundleDigest,
        }));
}
