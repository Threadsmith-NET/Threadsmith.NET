namespace Threadsmith.Models;

/// <summary>Plans bounded provider cache breakpoints without changing semantic request content.</summary>
public static class ModelCachePlanner
{
    /// <summary>Creates a deterministic breakpoint plan from eligible stable message boundaries.</summary>
    public static ModelCachePlan CreatePlan(
        ModelRequestLayout layout,
        ModelCacheCapabilities capabilities,
        int stablePrefixTokens)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentOutOfRangeException.ThrowIfNegative(stablePrefixTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(capabilities.MaximumBreakpoints);
        ArgumentOutOfRangeException.ThrowIfNegative(capabilities.MinimumCacheablePrefixTokens);
        if (!capabilities.ExplicitCacheControl
            || capabilities.MaximumBreakpoints == 0
            || stablePrefixTokens < capabilities.MinimumCacheablePrefixTokens)
        {
            return new ModelCachePlan([]);
        }

        ModelCacheBreakpoint[] candidates =
        [
            new(ModelCacheBreakpointClass.HostPolicy, 0),
            new(ModelCacheBreakpointClass.RepositoryInstructions, 1),
            new(ModelCacheBreakpointClass.ToolInventory, 1),
            new(ModelCacheBreakpointClass.PhasePolicy, 2),
        ];
        return new ModelCachePlan(
            [.. candidates
                .Where(candidate => candidate.AfterMessageIndex < layout.StablePrefixMessageCount)
                .Take(capabilities.MaximumBreakpoints)]);
    }
}

/// <summary>Validates whether a frozen continuation binding remains safe to reuse.</summary>
public static class ModelContinuationValidator
{
    /// <summary>Compares current authoritative generations and identifies mandatory reassembly.</summary>
    public static ModelContinuationReassemblyReason GetReassemblyReason(
        ModelContinuationBinding frozen,
        ModelContinuationBinding current)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(current);
        if (!string.Equals(frozen.ProviderId, current.ProviderId, StringComparison.Ordinal)
            || frozen.ProfileId != current.ProfileId
            || frozen.LayoutVersion != current.LayoutVersion)
        {
            return ModelContinuationReassemblyReason.ModelOrLayoutChanged;
        }

        if (frozen.Phase != current.Phase)
        {
            return ModelContinuationReassemblyReason.PhaseChanged;
        }

        if (!string.Equals(
            frozen.TrustPolicyGeneration,
            current.TrustPolicyGeneration,
            StringComparison.Ordinal))
        {
            return ModelContinuationReassemblyReason.TrustOrPolicyChanged;
        }

        if (!string.Equals(
            frozen.InstructionBundleDigest,
            current.InstructionBundleDigest,
            StringComparison.Ordinal))
        {
            return ModelContinuationReassemblyReason.InstructionBundleChanged;
        }

        if (!string.Equals(
            frozen.ToolInventoryDigest,
            current.ToolInventoryDigest,
            StringComparison.Ordinal))
        {
            return ModelContinuationReassemblyReason.ToolInventoryChanged;
        }

        if (frozen.CompactionGeneration != current.CompactionGeneration)
        {
            return ModelContinuationReassemblyReason.CompactionChanged;
        }

        if (!string.Equals(
            frozen.RequestGeneration,
            current.RequestGeneration,
            StringComparison.Ordinal))
        {
            return ModelContinuationReassemblyReason.TrustOrPolicyChanged;
        }

        if (!string.Equals(
            frozen.StatelessRequestDigest,
            current.StatelessRequestDigest,
            StringComparison.Ordinal))
        {
            return ModelContinuationReassemblyReason.ModelOrLayoutChanged;
        }

        return ModelContinuationReassemblyReason.None;
    }
}
