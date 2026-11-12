namespace Threadsmith.Policies.Evaluation;

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Matching;

/// <summary>Resolves one action through ordinary rules, explicit exceptions and machine constraints.</summary>
public static class PolicyEvaluator
{
    /// <summary>Evaluates a compiled snapshot. Matching errors deny rather than selecting a broader rule.</summary>
    public static PolicyDecision Evaluate(PolicySnapshot snapshot, CommandFacts facts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(facts);
        try
        {
            if (facts.Platform != snapshot.Platform || facts.Shell != snapshot.Shell)
            {
                return Denied(PolicyReasons.ProfileMismatch);
            }

            _ = PolicyMatcherCatalog.ValidateFacts(facts);
            var plan = PolicyEvaluationPlan.For(snapshot);
            var matching = plan.Ordinary(facts).Where(rule => PolicyMatcherCatalog.MatchesValidated(rule.Matchers, facts)).ToArray();
            var constraints = plan.Enforced(facts).Where(rule => PolicyMatcherCatalog.MatchesValidated(rule.Matchers, facts)).ToArray();
            var arguments = string.Join(' ', facts.Arguments);
            var argumentDenials = matching.Concat(constraints).Where(rule => PolicyMatcherCatalog.DeniesArguments(rule, arguments)).ToArray();
            if (argumentDenials.Length > 0)
            {
                return new PolicyDecision(
                    false,
                    false,
                    PolicyReasons.ExplicitDenial,
                    PolicyOutcome.AlwaysDeny,
                    [.. argumentDenials.Select(rule => rule.Id).Order(StringComparer.Ordinal)],
                    string.Empty,
                    []);
            }

            var remaining = matching;
            if (matching.Any(rule => !rule.Overrides.IsEmpty))
            {
                var replaced = matching.SelectMany(rule => rule.Overrides).ToHashSet(StringComparer.Ordinal);
                remaining = [.. matching.Where(rule => !replaced.Contains(rule.Id))];
            }

            if (remaining.Length == 0)
            {
                return Denied(PolicyReasons.Unmatched);
            }

            if (remaining.Any(rule => rule.Policy != remaining[0].Policy))
            {
                return Denied(PolicyReasons.Conflict);
            }

            if (snapshot.RequireEnforcedMatch && constraints.Length == 0)
            {
                return Denied(PolicyReasons.ClosedSet);
            }

            var outcome = remaining[0].Policy;
            foreach (var constraint in constraints)
            {
                if (Rank(constraint.Policy) > Rank(outcome))
                {
                    outcome = constraint.Policy;
                }
            }

            ImmutableArray<string> ids = [.. remaining.Concat(constraints).Select(rule => rule.Id).Order(StringComparer.Ordinal)];
            var fingerprint = PolicyJson.Fingerprint(new { snapshot.Fingerprint, ids, outcome });
            var choices = Choices(outcome);
            return new PolicyDecision(
                outcome != PolicyOutcome.AlwaysDeny,
                outcome is not (PolicyOutcome.Allow or PolicyOutcome.AlwaysDeny),
                outcome == PolicyOutcome.AlwaysDeny ? PolicyReasons.ExplicitDenial : PolicyReasons.Matched,
                outcome,
                ids,
                fingerprint,
                choices);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or RegexMatchTimeoutException or NotSupportedException)
        {
            return Denied(PolicyReasons.AnalysisError);
        }
    }

    /// <summary>Returns the exact host-permitted choices for an outcome.</summary>
    public static ImmutableArray<PolicyApprovalChoice> Choices(PolicyOutcome outcome) => outcome switch
    {
        PolicyOutcome.ApprovalAlwaysRequired => [PolicyApprovalChoice.Once, PolicyApprovalChoice.Deny],
        PolicyOutcome.ApprovalRequiredSession => [PolicyApprovalChoice.Once, PolicyApprovalChoice.Session, PolicyApprovalChoice.Deny],
        PolicyOutcome.ApprovalRequired => [PolicyApprovalChoice.Once, PolicyApprovalChoice.Session, PolicyApprovalChoice.Always, PolicyApprovalChoice.Deny],
        _ => [],
    };

    private static int Rank(PolicyOutcome outcome) => outcome switch
    {
        PolicyOutcome.AlwaysDeny => 4,
        PolicyOutcome.ApprovalAlwaysRequired => 3,
        PolicyOutcome.ApprovalRequiredSession => 2,
        PolicyOutcome.ApprovalRequired => 1,
        _ => 0,
    };

    private static PolicyDecision Denied(string reason) => new(false, false, reason, null, [], string.Empty, []);
}
