namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using System.Reflection;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Matching;
using Xunit;

public static class PolicyEvaluationPlanTests
{
    [Fact]
    public static void CompilationSharesOneSourceRevisionAcrossProfilesAndRefreshesChangedInputs()
    {
        var baseline = ShippedCommandPolicy.CreateDefaults();
        var machine = new PolicyDocument();
        var user = new PolicyDocument
        {
            Common = new PolicyProfile
            {
                AlwaysAllow = [.. Enumerable.Range(0, 1000).Select(index =>
                    new PolicyGrant("grant-" + index, new string('a', 64), new string('b', 64)))],
            },
        };

        var profiles = PolicyCompiler.CompileAll(baseline, machine, user);

        Assert.Equal(14, profiles.Count);
        var revision = profiles.Values.First().ConfigurationRevision;
        Assert.Equal(PolicyJson.Fingerprint(new { machine, user }), revision);
        Assert.All(profiles.Values, snapshot => Assert.Same(revision, snapshot.ConfigurationRevision));
        var fallback = PolicyCompiler.Compile(baseline, machine, user, "linux", "unsupported");
        Assert.Equal(revision, fallback.ConfigurationRevision);
        Assert.Empty(fallback.Rules);
        var changed = user with { Common = user.Common with { AlwaysAllow = [] } };
        var recompiled = PolicyCompiler.CompileAll(baseline, machine, changed);
        Assert.All(recompiled.Values, snapshot => Assert.NotEqual(revision, snapshot.ConfigurationRevision));
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("enforced")]
    [InlineData("grants")]
    [InlineData("matcher")]
    public static void NullPolicyEntriesAreValidationFailures(string location)
    {
        var profile = location switch
        {
            "rules" => new PolicyProfile { Rules = [null!] },
            "enforced" => new PolicyProfile { Enforced = [null!] },
            "grants" => new PolicyProfile { AlwaysAllow = [null!] },
            _ => new PolicyProfile { Rules = [Rule("invalid", "git") with { Matchers = [null!] }] },
        };
        var document = new PolicyDocument { Common = profile };
        Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), location == "enforced" ? document : null, location == "enforced" ? null : document, "linux", "bash"));
    }

    [Theory]
    [InlineData("(?=unsupported)")]
    [InlineData("[")]
    [InlineData("(a)\\1")]
    public static void InvalidInactivePatternsAreNormalized(string pattern)
    {
        var rule = Rule("invalid", "git") with { Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Regex, Fact = PolicyFact.ExecutableName, Value = pattern }] };
        var user = new PolicyDocument { Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add("linux", new PolicyProfile { Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("zsh", new PolicyProfile { Rules = [rule] }) }) };
        var error = Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, user, "linux", "bash"));
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public static void ValidatedPatternsAreReusedByIdentityAndCopiesDoNotReuseChangedPatterns()
    {
        var matcher = new PolicyMatcher { Type = PolicyMatcherType.Regex, Fact = PolicyFact.ExecutableName, Value = "git" };
        var catalog = new PolicyMatcherCatalog();
        catalog.Validate(matcher);
        var patternMethod = typeof(PolicyMatcherCatalog).GetMethod("Pattern", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pattern = patternMethod.Invoke(null, [matcher]);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            Assert.True(catalog.Matches([matcher], Facts("git") with { Arguments = ["--value", "safe=value", "target"] }));
            Assert.Same(pattern, patternMethod.Invoke(null, [matcher]));
        }

        Assert.NotSame(pattern, patternMethod.Invoke(null, [matcher with { Value = "dotnet" }]));
    }

    [Theory]
    [InlineData("linux", "git", "external")]
    [InlineData("windows", "GIT", "external")]
    [InlineData("linux", "git", "builtin")]
    public static void IndexSelectsOnlyRelevantRulesAndPreservesFullScanDecisions(string platform, string name, string kind)
    {
        var basis = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, platform, "bash");
        var rules = Enumerable.Range(0, 4094).Select(index => Rule("rule-" + index, "unrelated-" + index)).ToImmutableArray();
        var selected = Rule("selected", "git");
        var fallback = selected with { Id = "fallback", Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.Shell, Value = "bash" }] };
        var snapshot = basis with { Rules = [.. rules, selected, fallback], Enforced = [Rule("constraint", "git") with { Policy = PolicyOutcome.ApprovalRequired }] };
        var facts = Facts(name) with { Platform = platform, CommandKind = kind };
        var planType = typeof(PolicyEvaluator).Assembly.GetType("Threadsmith.Policies.Evaluation.PolicyEvaluationPlan")!;
        var getPlan = planType.GetMethod("For", BindingFlags.Static | BindingFlags.NonPublic)!;
        var plan = getPlan.Invoke(null, [snapshot]);
        var candidates = (IEnumerable<PolicyRule>)planType.GetMethod("Ordinary", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plan, [facts])!;
        Assert.Equal(kind == "external" ? 2 : 1, candidates.Count());
        Assert.Same(plan, getPlan.Invoke(null, [snapshot]));

        // A singleton AnyOf preserves semantics but deliberately forces full-scan fallback.
        var unindexed = snapshot with { Rules = [.. snapshot.Rules.Select(Wrap)], Enforced = [.. snapshot.Enforced.Select(Wrap)] };
        var indexedDecision = PolicyEvaluator.Evaluate(snapshot, facts);
        var scannedDecision = PolicyEvaluator.Evaluate(unindexed, facts);
        Assert.Equal(scannedDecision.Outcome, indexedDecision.Outcome);
        Assert.Equal(scannedDecision.ReasonCode, indexedDecision.ReasonCode);
        Assert.Equal(scannedDecision.RuleIds, indexedDecision.RuleIds);
        Assert.Equal(scannedDecision.RuleFingerprint, indexedDecision.RuleFingerprint);
        Assert.NotSame(plan, getPlan.Invoke(null, [unindexed]));
        Assert.Equal(PolicyReasons.AnalysisError, PolicyEvaluator.Evaluate(snapshot, facts with { WorkingDirectory = "relative" }).ReasonCode);
    }

    [Theory]
    [InlineData(PolicyMatcherType.Regex, "^external$")]
    [InlineData(PolicyMatcherType.Glob, "ext*")]
    public static void CompositePatternSiblingsAreRetainedInSelectedCandidates(PolicyMatcherType type, string pattern)
    {
        // Arrange: exercise pattern validation and composite indexing, not regex execution deadlines.
        var basis = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        var sibling = new PolicyMatcher { Type = type, Fact = PolicyFact.CommandKind, Value = pattern };
        var rule = Rule("selected", "git") with
        {
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.AllOf, Matchers = [sibling, Rule("unused", "git").Matchers[0]] }],
        };
        var snapshot = basis with { Rules = [rule], Enforced = [rule] };

        // Act / Assert: both partitions retain the original rule, including its pattern predicate.
        Assert.Same(rule, Assert.Single(Candidates(snapshot, Facts("git"), "Ordinary")));
        Assert.Same(rule, Assert.Single(Candidates(snapshot, Facts("git"), "Enforced")));
        Assert.Empty(Candidates(snapshot, Facts("other"), "Ordinary"));
        Assert.Empty(Candidates(snapshot, Facts("other"), "Enforced"));
    }

    [Fact]
    public static void AnyOfRemainsFallbackUnlessItsSiblingRequiresAnExecutable()
    {
        var basis = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        var alternative = new PolicyMatcher
        {
            Type = PolicyMatcherType.AnyOf,
            Matchers = [Rule("unused", "other").Matchers[0], new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.Shell, Value = "bash" }],
        };
        var ambiguous = Rule("ambiguous", "unused") with { Matchers = [alternative] };
        var mandatory = Rule("mandatory", "other") with { Matchers = [alternative, Rule("unused", "other").Matchers[0]] };
        var snapshot = basis with { Rules = [ambiguous, mandatory] };
        var facts = Facts("git");

        var candidates = Candidates(snapshot, facts, "Ordinary").ToArray();

        Assert.Equal(["ambiguous"], candidates.Select(rule => rule.Id));
        AssertEquivalent(PolicyEvaluator.Evaluate(snapshot with { Rules = [.. snapshot.Rules.Select(Wrap)] }, facts), PolicyEvaluator.Evaluate(snapshot, facts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void UnrelatedExecutableCannotHideInvalidSibling(bool enforced)
    {
        var basis = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        var invalid = Rule("invalid", "other") with
        {
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.AllOf, Matchers = [Rule("unused", "other").Matchers[0], new PolicyMatcher { Type = (PolicyMatcherType)int.MaxValue }] }],
        };
        var snapshot = enforced ? basis with { Enforced = [invalid] } : basis with { Rules = [invalid] };

        var decision = PolicyEvaluator.Evaluate(snapshot, Facts("git"));

        Assert.Equal(PolicyReasons.AnalysisError, decision.ReasonCode);
        Assert.False(decision.IsAllowed);
    }

    private static IEnumerable<PolicyRule> Candidates(PolicySnapshot snapshot, CommandFacts facts, string partition)
    {
        var type = typeof(PolicyEvaluator).Assembly.GetType("Threadsmith.Policies.Evaluation.PolicyEvaluationPlan")!;
        var plan = type.GetMethod("For", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [snapshot]);
        return (IEnumerable<PolicyRule>)type.GetMethod(partition, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plan, [facts])!;
    }

    private static void AssertEquivalent(PolicyDecision expected, PolicyDecision actual)
    {
        Assert.Equal(expected.ReasonCode, actual.ReasonCode);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.IsAllowed, actual.IsAllowed);
        Assert.Equal(expected.RequiresApproval, actual.RequiresApproval);
        Assert.Equal(expected.RuleIds, actual.RuleIds);
        Assert.Equal(expected.RuleFingerprint, actual.RuleFingerprint);
        Assert.Equal(expected.Choices, actual.Choices);
    }

    private static PolicyRule Wrap(PolicyRule rule) => rule with
    {
        Matchers =
        [
            new PolicyMatcher
            {
                Type = PolicyMatcherType.AnyOf,
                Matchers = [new PolicyMatcher { Type = PolicyMatcherType.AllOf, Matchers = rule.Matchers }],
            },
        ],
    };

    private static PolicyRule Rule(string id, string executable) => new() { Id = id, Policy = PolicyOutcome.Allow, Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = executable }] };

    private static CommandFacts Facts(string executable) => new("linux", "bash", executable, Path.GetFullPath("resolved-fixture"), "external", [], Path.GetFullPath("."));
}
