namespace Threadsmith.Policies.Tests;

using System.Text.RegularExpressions;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Matching;

internal static class TestPolicyEvaluation
{
    internal static PolicyDecision Evaluate(PolicySnapshot snapshot, CommandFacts facts)
    {
        // Exercise real matching semantics without making correctness depend on scheduling.
        // Each evaluation owns its execution cache; production pattern bounds remain unchanged.
        return PolicyEvaluator.Evaluate(snapshot, facts, new PolicyMatcherCatalog(Regex.InfiniteMatchTimeout));
    }
}
