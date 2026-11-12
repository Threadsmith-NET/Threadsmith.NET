namespace Threadsmith.Policies.Evaluation;

using System.Collections.Immutable;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Matching;

/// <summary>Compiles all source branches before selecting effective platform and shell authority.</summary>
public static class PolicyCompiler
{
    private enum PolicySource
    {
        Default,
        Machine,
        User,
    }

    /// <summary>Compiles host defaults, machine rules, then user rules without array-index merging.</summary>
    public static PolicySnapshot Compile(PolicyDocument baseline, PolicyDocument? machine, PolicyDocument? user, string platform, string shell)
    {
        var profiles = CompileAll(baseline, machine, user, out var configurationRevision);
        return profiles.GetValueOrDefault(platform + "/" + shell) ?? CompileProfile(baseline, machine, user, platform, shell, configurationRevision);
    }

    /// <summary>Validates every effective graph and compiles all declared profiles once.</summary>
    public static ImmutableDictionary<string, PolicySnapshot> CompileAll(PolicyDocument baseline, PolicyDocument? machine, PolicyDocument? user) =>
        CompileAll(baseline, machine, user, out _);

    private static ImmutableDictionary<string, PolicySnapshot> CompileAll(PolicyDocument baseline, PolicyDocument? machine, PolicyDocument? user, out string configurationRevision)
    {
        Validate(baseline, PolicySource.Default);
        if (machine is not null)
        {
            Validate(machine, PolicySource.Machine);
        }

        if (user is not null)
        {
            Validate(user, PolicySource.User);
        }

        configurationRevision = PolicyJson.Fingerprint(new { machine, user });
        PolicyDocument?[] documents = [baseline, machine, user];
        var profiles = ImmutableDictionary.CreateBuilder<string, PolicySnapshot>(StringComparer.Ordinal);
        foreach (var platform in documents.Where(document => document is not null).SelectMany(document => document?.Platforms.Keys ?? []).Distinct(StringComparer.Ordinal))
        {
            var shells = documents.SelectMany(document => document?.Platforms.GetValueOrDefault(platform)?.Shells.Keys ?? []).Distinct(StringComparer.Ordinal).DefaultIfEmpty(CommandPolicyNames.UnsupportedShell);
            foreach (var shell in shells)
            {
                profiles[platform + "/" + shell] = CompileProfile(baseline, machine, user, platform, shell, configurationRevision);
            }
        }

        return profiles.ToImmutable();
    }

    private static PolicySnapshot CompileProfile(PolicyDocument baseline, PolicyDocument? machine, PolicyDocument? user, string platform, string shell, string configurationRevision)
    {
        var rules = new Dictionary<string, PolicyRule>(StringComparer.Ordinal);
        var sources = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var enforced = new List<PolicyRule>();
        var grants = new Dictionary<string, PolicyGrant>(StringComparer.Ordinal);
        var closed = false;
        var foundShell = false;
        foreach (var (document, source) in new[] { (baseline, PolicySource.Default), (machine, PolicySource.Machine), (user, PolicySource.User) })
        {
            if (document is null)
            {
                continue;
            }

            Apply(document.Common, source);
            if (!document.Platforms.TryGetValue(platform, out var common))
            {
                continue;
            }

            Apply(common, source);
            if (common.Shells.TryGetValue(shell, out var selected))
            {
                foundShell = true;
                Apply(selected, source);
            }
        }

        ValidateOverrides(rules);
        if (!foundShell)
        {
            rules.Clear();
        }

        if (enforced.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != enforced.Count)
        {
            throw new InvalidDataException("PolicyDuplicateEnforcedId");
        }

        var ordinary = rules.Values.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToImmutableArray();
        var constraints = enforced.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToImmutableArray();
        var fingerprint = PolicyJson.Fingerprint(new { platform, shell, ordinary, constraints, closed });
        var snapshot = new PolicySnapshot(platform, shell, fingerprint, ordinary, constraints, closed, [.. grants.Values.OrderBy(grant => grant.Id, StringComparer.Ordinal)], sources.ToImmutable())
        {
            ConfigurationRevision = configurationRevision,
        };
        _ = PolicyEvaluationPlan.For(snapshot);
        return snapshot;

        void Apply(PolicyProfile profile, PolicySource source)
        {
            foreach (var id in profile.DisabledRuleIds)
            {
                rules.Remove(id);
                sources.Remove(id);
            }

            foreach (var rule in profile.Rules)
            {
                rules[rule.Id] = rule;
                sources[rule.Id] = source.ToString();
            }

            enforced.AddRange(profile.Enforced);
            closed |= profile.RequireEnforcedMatch;
            if (source == PolicySource.User)
            {
                foreach (var grant in profile.AlwaysAllow)
                {
                    grants[grant.Id] = grant;
                }
            }
        }
    }

    /// <summary>Validates every platform, dialect and unused matcher branch.</summary>
    private static void Validate(PolicyDocument document, PolicySource source)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1 || document.Platforms is null || document.Platforms.Count > 3)
        {
            throw new InvalidDataException("PolicyUnsupportedSchema");
        }

        var total = 0;
        ValidateProfile(document.Common, true, null);
        foreach (var (platform, profile) in document.Platforms)
        {
            if (platform is not (CommandPolicyNames.Windows or CommandPolicyNames.MacOS or CommandPolicyNames.Linux))
            {
                throw new InvalidDataException("PolicyUnknownPlatform");
            }

            ValidateProfile(profile, false, platform);
            foreach (var (shell, selected) in profile.Shells)
            {
                if (shell is not (CommandPolicyNames.WindowsPowerShell or CommandPolicyNames.PowerShell or CommandPolicyNames.Cmd or CommandPolicyNames.Bash or CommandPolicyNames.Sh or CommandPolicyNames.Zsh)
                    || (platform != CommandPolicyNames.Windows && shell is CommandPolicyNames.WindowsPowerShell or CommandPolicyNames.Cmd))
                {
                    throw new InvalidDataException("PolicyUnknownShell");
                }

                ValidateProfile(selected, true, platform);
            }
        }

        void ValidateProfile(PolicyProfile profile, bool nested, string? platform)
        {
            if (profile?.Rules.IsDefault != false || profile.Enforced.IsDefault || profile.DisabledRuleIds.IsDefault
                || profile.AlwaysAllow.IsDefault || profile.Shells is null || (nested && profile.Shells.Count > 0)
                || (source != PolicySource.Machine && (profile.Enforced.Length > 0 || profile.RequireEnforcedMatch))
                || (source != PolicySource.User && profile.AlwaysAllow.Length > 0)
                || profile.AlwaysAllow.Length > 1024 || profile.DisabledRuleIds.Length > 1024)
            {
                throw new InvalidDataException("PolicyInvalidSource");
            }

            total += profile.Rules.Length + profile.Enforced.Length;
            if (total > 4096 || profile.Rules.Any(rule => rule is null) || profile.Enforced.Any(rule => rule is null)
                || profile.Rules.Concat(profile.Enforced).Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count()
                != profile.Rules.Length + profile.Enforced.Length || profile.DisabledRuleIds.Any(string.IsNullOrWhiteSpace)
                || profile.DisabledRuleIds.Distinct(StringComparer.Ordinal).Count() != profile.DisabledRuleIds.Length)
            {
                throw new InvalidDataException("PolicyRuleLimitOrDuplicate");
            }

            foreach (var rule in profile.Rules)
            {
                ValidateRule(rule, false, platform);
            }

            foreach (var rule in profile.Enforced)
            {
                ValidateRule(rule, true, platform);
            }

            if (profile.AlwaysAllow.Any(grant => grant is null)
                || profile.AlwaysAllow.Select(grant => grant.Id).Distinct(StringComparer.Ordinal).Count() != profile.AlwaysAllow.Length
                || profile.AlwaysAllow.Any(grant => string.IsNullOrWhiteSpace(grant.Id) || grant.Id.Length > 128
                    || !IsFingerprint(grant.ContextFingerprint) || !IsFingerprint(grant.RuleFingerprint)))
            {
                throw new InvalidDataException("PolicyInvalidGrant");
            }
        }
    }

    private static void ValidateRule(PolicyRule rule, bool isEnforced, string? platform)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 128 || rule.Matchers.IsDefaultOrEmpty || rule.Matchers.Length > 64
            || !Enum.IsDefined(rule.Policy) || rule.Overrides.IsDefault || rule.Overrides.Length > 64
            || rule.Overrides.Any(string.IsNullOrWhiteSpace) || rule.Overrides.Distinct(StringComparer.Ordinal).Count() != rule.Overrides.Length
            || (isEnforced && rule.Overrides.Length > 0))
        {
            throw new InvalidDataException("PolicyInvalidRule");
        }

        _ = PolicyMatcherCatalog.GetArgumentPatterns(rule);
        foreach (var matcher in rule.Matchers)
        {
            PolicyMatcherCatalog.Validate(matcher, platform);
        }
    }

    private static bool IsFingerprint(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static void ValidateOverrides(Dictionary<string, PolicyRule> rules)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in rules.Keys)
        {
            Visit(id);
        }

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!rules.TryGetValue(id, out var rule) || !visiting.Add(id))
            {
                throw new InvalidDataException("PolicyInvalidOverride");
            }

            foreach (var target in rule.Overrides)
            {
                Visit(target);
            }

            visiting.Remove(id);
            visited.Add(id);
        }
    }
}
