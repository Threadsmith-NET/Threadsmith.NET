namespace Threadsmith.Models.Anthropic;

using System.Text.Json;

/// <summary>Validates ordinary model-only policy without allowing repository discovery or credential authority.</summary>
public static class AnthropicRepositoryModelOverrides
{
    /// <summary>Retains trusted provider evidence while permitting validated model request policy overrides.</summary>
    public static AnthropicProviderConfiguration Apply(
        AnthropicProviderConfiguration trusted, AnthropicProviderConfiguration candidate)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.Id, trusted.Id, StringComparison.OrdinalIgnoreCase)
            || candidate.Name != trusted.Name || candidate.Enabled != trusted.Enabled
            || candidate.SecretKeyReference != trusted.SecretKeyReference
            || !Equivalent(candidate.Defaults, trusted.Defaults)
            || !Equivalent(candidate.ModelOverrides, trusted.ModelOverrides))
        {
            throw new InvalidOperationException("Repository configuration cannot change Anthropic provider credentials, identity, or discovery policy.");
        }

        var known = trusted.Models.ToDictionary(model => model.Id);
        foreach (var model in candidate.Models)
        {
            if (model is not AnthropicModelConfiguration requested
                || !known.TryGetValue(model.Id, out var found) || found is not AnthropicModelConfiguration original
                || requested.ModelId != original.ModelId
                || (!original.Enabled && requested.Enabled)
                || requested.ContextWindow > original.ContextWindow
                || requested.MaximumOutputTokens != original.MaximumOutputTokens
                || requested.Temperature != original.Temperature
                || !Equivalent(requested.Cost, original.Cost)
                || !Equivalent(requested.Capabilities, original.Capabilities)
                || !Equivalent(requested.Compatibility, original.Compatibility)
                || !Equivalent(requested.SupportedReasoningLevels, original.SupportedReasoningLevels)
                || (original.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed
                    && requested.SensitiveDataPolicy == ModelSensitiveDataPolicy.Allowed))
            {
                throw new InvalidOperationException("Repository Anthropic models may change request policy but cannot add models, enlarge hard limits, prices, or capabilities.");
            }
        }

        return trusted with { Models = Array.AsReadOnly(candidate.Models.ToArray()) };
    }

    private static bool Equivalent<T>(T first, T second)
    {
        return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first), JsonSerializer.SerializeToElement(second));
    }
}
