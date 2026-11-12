namespace Threadsmith.Policies.Evaluation;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Matching;

/// <summary>Retains conservative executable indexes for the lifetime of each immutable snapshot.</summary>
internal sealed class PolicyEvaluationPlan
{
    private static readonly ConditionalWeakTable<PolicySnapshot, PolicyEvaluationPlan> Plans = [];
    private readonly RuleIndex _ordinary;
    private readonly RuleIndex _enforced;

    private PolicyEvaluationPlan(PolicySnapshot snapshot)
    {
        // Conservatively include PowerShell names; final matching retains host path case semantics.
        var comparer = snapshot.Platform == CommandPolicyNames.Windows
            || snapshot.Shell is CommandPolicyNames.WindowsPowerShell or CommandPolicyNames.PowerShell
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _ordinary = new RuleIndex(snapshot.Rules, comparer);
        _enforced = new RuleIndex(snapshot.Enforced, comparer);
    }

    /// <summary>Reuses an identity-bound plan without retaining expired snapshots or copying stale indexes.</summary>
    internal static PolicyEvaluationPlan For(PolicySnapshot snapshot) => Plans.GetValue(snapshot, static value => new PolicyEvaluationPlan(value));

    /// <summary>Returns possible ordinary matches and all rules requiring full evaluation.</summary>
    internal IEnumerable<PolicyRule> Ordinary(CommandFacts facts) => _ordinary.Candidates(facts);

    /// <summary>Returns possible machine constraints without granting ordinary permission.</summary>
    internal IEnumerable<PolicyRule> Enforced(CommandFacts facts) => _enforced.Candidates(facts);

    private sealed class RuleIndex
    {
        private readonly Dictionary<string, List<PolicyRule>> _executables;
        private readonly List<PolicyRule> _fallback = [];

        internal RuleIndex(ImmutableArray<PolicyRule> rules, StringComparer comparer)
        {
            _executables = new Dictionary<string, List<PolicyRule>>(comparer);
            foreach (var rule in rules)
            {
                // Only a validated mandatory constraint may exclude a rule. Selected candidates still
                // evaluate every matcher, including regex/path failures, through the existing catalog.
                var executable = CanIndex(rule.Matchers) ? MandatoryExecutable(rule.Matchers) : null;
                if (executable is null)
                {
                    _fallback.Add(rule);
                    continue;
                }

                if (!_executables.TryGetValue(executable, out var bucket))
                {
                    bucket = [];
                    _executables.Add(executable, bucket);
                }

                bucket.Add(rule);
            }
        }

        internal IEnumerable<PolicyRule> Candidates(CommandFacts facts)
        {
            foreach (var rule in _fallback)
            {
                yield return rule;
            }

            if (facts.ResolvedExecutable.Length > 0 && _executables.TryGetValue(facts.ResolvedExecutable, out var resolved))
            {
                foreach (var rule in resolved)
                {
                    yield return rule;
                }
            }

            if (facts.CommandKind is CommandPolicyNames.External or CommandPolicyNames.ShellCommand or CommandPolicyNames.Script
                && !_executables.Comparer.Equals(facts.ExecutableName, facts.ResolvedExecutable)
                && _executables.TryGetValue(facts.ExecutableName, out var named))
            {
                foreach (var rule in named)
                {
                    yield return rule;
                }
            }
        }

        private static bool CanIndex(ImmutableArray<PolicyMatcher> matchers)
        {
            try
            {
                foreach (var matcher in matchers)
                {
                    PolicyMatcherCatalog.Validate(matcher, null);
                }

                return true;
            }
            catch (InvalidDataException)
            {
                // Source compilation owns declared-platform root validation. Foreign-platform roots
                // and malformed copied nodes retain full evaluation, including fail-closed errors.
                return false;
            }
        }

        private static string? MandatoryExecutable(ImmutableArray<PolicyMatcher> matchers)
        {
            foreach (var matcher in matchers)
            {
                if (matcher.Type == PolicyMatcherType.Executable)
                {
                    return matcher.Value;
                }

                if (matcher.Type == PolicyMatcherType.AllOf
                    && MandatoryExecutable(matcher.Matchers) is { } executable)
                {
                    return executable;
                }
            }

            // An executable within AnyOf need not match a successful alternative.
            return null;
        }
    }
}
