namespace Threadsmith.Skills;

using Threadsmith.Core;

/// <summary>Formats exact package identities used by external skill policy.</summary>
internal static class SkillPolicyIdentity
{
    /// <summary>Formats the scope, skill, and version prefix shared by exact selectors.</summary>
    internal static string FormatSelectorPrefix(SkillCatalogCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}"
            + $"@{candidate.Metadata.Version}+";
    }

    /// <summary>Formats an exact scope, skill, version, and digest selector.</summary>
    internal static string FormatSelector(SkillCatalogCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return FormatSelectorPrefix(candidate) + candidate.Identity.Digest.Value;
    }

    /// <summary>Formats the exact digest, publisher, and source allowlist tuple.</summary>
    internal static string FormatAllowlistEntry(SkillCatalogCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return string.Join(
            '|',
            candidate.Identity.Digest.Value,
            candidate.Metadata.Publisher,
            candidate.Provenance.Source);
    }
}
