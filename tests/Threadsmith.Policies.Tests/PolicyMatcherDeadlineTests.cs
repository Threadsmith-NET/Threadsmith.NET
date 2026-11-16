namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Matching;
using Xunit;

public static class PolicyMatcherDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void MatchingTimeoutFailsClosedWithoutWaitingForARealDeadline(bool argumentSearch)
    {
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        var facts = new CommandFacts("linux", "bash", "git", Path.GetFullPath("git"), "external", ["status"], Path.GetFullPath("."));

        var decision = PolicyEvaluator.Evaluate(snapshot, facts, new TimeoutMatcher(argumentSearch));

        Assert.False(decision.IsAllowed);
        Assert.False(decision.RequiresApproval);
        Assert.Equal(PolicyReasons.AnalysisError, decision.ReasonCode);
        Assert.Null(decision.Outcome);
        Assert.Empty(decision.Choices);
    }

    [Fact]
    public static void FunctionalMatchingDoesNotChangeTheSharedProductionDeadline()
    {
        var node = new PolicyMatcher { Type = PolicyMatcherType.Regex, Fact = PolicyFact.ExecutableName, Value = "git" };
        var method = typeof(PolicyMatcherCatalog).GetMethod("Pattern", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pattern = Assert.IsType<Regex>(method.Invoke(null, [node]));
        var facts = new CommandFacts("linux", "bash", "git", Path.GetFullPath("git"), "external", [], Path.GetFullPath("."));

        Assert.True(new PolicyMatcherCatalog(Regex.InfiniteMatchTimeout).Matches([node], facts));

        Assert.Equal(TimeSpan.FromMilliseconds(100), pattern.MatchTimeout);
        Assert.Same(pattern, method.Invoke(null, [node]));
    }

    private sealed class TimeoutMatcher : IPolicyMatcher
    {
        private readonly bool _argumentSearch;

        internal TimeoutMatcher(bool argumentSearch)
        {
            _argumentSearch = argumentSearch;
        }

        public void Validate(PolicyMatcher matcher) => throw new NotSupportedException();

        public bool Matches(ImmutableArray<PolicyMatcher> matchers, ActionFacts facts) =>
            _argumentSearch ? true : throw new RegexMatchTimeoutException();

        public bool DeniesArguments(PolicyRule rule, string arguments) => throw new RegexMatchTimeoutException();
    }
}
