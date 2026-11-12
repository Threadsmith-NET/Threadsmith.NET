namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Xunit;

public static class PolicyReviewRegressionTests
{
    [Fact]
    public static void MixedPlatformRootsCompileIncludingNestedShellMatchers()
    {
        var profiles = ImmutableDictionary.CreateBuilder<string, PolicyProfile>();
        foreach (var (platform, root) in new[] { ("windows", @"C:\Tools"), ("linux", "/usr/bin"), ("macos", "/Applications") })
        {
            var rule = Rule(platform) with
            {
                Matchers = [new PolicyMatcher { Type = PolicyMatcherType.AllOf, Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Path, Fact = PolicyFact.WorkingDirectory, Values = [root] }] }],
            };
            profiles[platform] = new PolicyProfile
            {
                Rules = [rule],
                Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("pwsh", new PolicyProfile { Rules = [rule with { Id = platform + "-shell" }] }),
            };
        }

        var compiled = PolicyCompiler.CompileAll(ShippedCommandPolicy.CreateDefaults(), null, new PolicyDocument { Platforms = profiles.ToImmutable() });

        Assert.Contains("windows/pwsh", compiled.Keys);
        Assert.Contains("linux/pwsh", compiled.Keys);
        Assert.Contains("macos/pwsh", compiled.Keys);
    }

    [Theory]
    [InlineData("windows", "C:Tools", false)]
    [InlineData("windows", @"\Tools", false)]
    [InlineData("windows", "/usr/bin", false)]
    [InlineData("windows", @"\\server", false)]
    [InlineData("windows", @"\\server\share", true)]
    [InlineData("windows", "C:/Tools", true)]
    [InlineData("linux", @"C:\Tools", false)]
    [InlineData("macos", "relative", false)]
    public static void DeclaredPlatformControlsRootValidation(string platform, string root, bool valid)
    {
        var rule = Rule("path") with { Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Path, Fact = PolicyFact.WorkingDirectory, Values = [root] }] };
        var user = new PolicyDocument { Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add(platform, new PolicyProfile { Rules = [rule] }) };

        var error = Record.Exception(() => PolicyCompiler.CompileAll(ShippedCommandPolicy.CreateDefaults(), null, user));

        if (valid)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.IsType<InvalidDataException>(error);
        }
    }

    [Theory]
    [InlineData("top", false)]
    [InlineData("all", false)]
    [InlineData("any", false)]
    [InlineData("top", true)]
    [InlineData("all", true)]
    [InlineData("any", true)]
    public static void LaterMatcherErrorsCannotBeHidden(string shape, bool enforced)
    {
        var first = new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.ExecutableName, Value = shape == "any" ? "git" : "other" };
        var invalid = new PolicyMatcher { Type = (PolicyMatcherType)int.MaxValue };
        ImmutableArray<PolicyMatcher> matchers = shape == "top" ? [first, invalid]
            : [new PolicyMatcher { Type = shape == "all" ? PolicyMatcherType.AllOf : PolicyMatcherType.AnyOf, Matchers = [first, invalid] }];
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        var broken = Rule("broken") with { Matchers = matchers };
        snapshot = enforced ? snapshot with { Enforced = [broken] } : snapshot with { Rules = [broken] };

        var decision = PolicyEvaluator.Evaluate(snapshot, Facts());

        Assert.False(decision.IsAllowed);
        Assert.Equal(PolicyReasons.AnalysisError, decision.ReasonCode);
    }

    [Fact]
    public static void FallbackMatcherAllocationsDoNotScaleWithRuleCountAcross64Actions()
    {
        var basis = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        var small = basis with { Rules = [Rule("one")] };
        var large = basis with { Rules = [.. Enumerable.Range(0, 4096).Select(index => Rule("rule-" + index))] };
        var facts = Facts() with { Arguments = [.. Enumerable.Repeat("literal", 1024)] };
        _ = Measure(small, facts);
        _ = Measure(large, facts);

        var smallBytes = Measure(small, facts);
        var largeBytes = Measure(large, facts);

        // Generous fixed allocation allowance, not a runner-speed or wall-clock assertion.
        Assert.True(largeBytes <= smallBytes + 65536, $"Small={smallBytes}, large={largeBytes}");
    }

    [Theory]
    [InlineData("windows", "powershell", "shell-command")]
    [InlineData("windows", "pwsh", "shell-command")]
    [InlineData("linux", "pwsh", "shell-command")]
    [InlineData("macos", "pwsh", "shell-command")]
    [InlineData("windows", "cmd", "external")]
    [InlineData("windows", "cmd", "explicit-external")]
    public static void RetainedHttpDenialCannotBeBypassedByCaseWithAdditionalPermission(string platform, string shell, string kind)
    {
        // Arrange: broad ordinary permission retains the shipped denial, without overrides.
        var permission = new PolicyRule
        {
            Id = "fixture-broad",
            Policy = PolicyOutcome.Allow,
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.Platform, Value = platform }],
        };
        var user = new PolicyDocument { Common = new PolicyProfile { Rules = [permission] } };
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, user, platform, shell);
        var names = kind == "shell-command" ? new[] { "Invoke-WebRequest", "Invoke-RestMethod", "iwr", "irm" } : ["curl", "wget"];
        foreach (var name in names)
        {
            foreach (var spelling in new[] { name, name.ToUpperInvariant(), char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant() })
            {
                var facts = new CommandFacts(platform, shell, spelling, kind == "shell-command" ? string.Empty : Path.GetFullPath(spelling), kind, [], Path.GetFullPath("."));

                // Act / Assert.
                var decision = PolicyEvaluator.Evaluate(snapshot, facts);
                Assert.False(decision.IsAllowed);
                Assert.False(decision.RequiresApproval);
                Assert.Empty(decision.Choices);
                Assert.Equal(PolicyReasons.Conflict, decision.ReasonCode);
                Assert.True(PolicyEvaluator.Evaluate(snapshot, facts with { ExecutableName = "fixture-safe" }).IsAllowed);
            }
        }
    }

    [Theory]
    [InlineData(0, 4096)]
    [InlineData(2048, 2048)]
    [InlineData(4096, 0)]
    public static void LargePolicyValidatesBothPartitionsAndLateInvalidRules(int ordinaryCount, int enforcedCount)
    {
        // Arrange.
        var ordinary = Enumerable.Range(0, ordinaryCount).Select(index => Rule("ordinary-" + index)).ToImmutableArray();
        var enforced = Enumerable.Range(0, enforcedCount).Select(index => Rule("enforced-" + index)).ToImmutableArray();
        var machine = new PolicyDocument { Common = new PolicyProfile { Rules = ordinary, Enforced = enforced } };
        var baseline = ShippedCommandPolicy.CreateDefaults();

        // Act / Assert: all declared profiles compile at the source rule limit.
        var snapshots = PolicyCompiler.CompileAll(baseline, machine, null);
        Assert.NotEmpty(snapshots);
        Assert.All(snapshots.Values, snapshot =>
        {
            Assert.Equal(enforcedCount, snapshot.Enforced.Length);
            Assert.Equal(ordinaryCount + baseline.Common.Rules.Length + 1, snapshot.Rules.Length);
        });
        Assert.True(PolicyEvaluator.Evaluate(snapshots["linux/bash"], Facts()).IsAllowed);

        var last = ordinaryCount > 0 ? ordinary[^1] : enforced[^1];
        var broken = last with { Matchers = [new PolicyMatcher { Type = (PolicyMatcherType)int.MaxValue }] };
        var invalid = ordinaryCount > 0 ? machine with { Common = machine.Common with { Rules = ordinary.SetItem(ordinary.Length - 1, broken) } }
            : machine with { Common = machine.Common with { Enforced = enforced.SetItem(enforced.Length - 1, broken) } };
        Assert.Throws<InvalidDataException>(() => PolicyCompiler.CompileAll(baseline, invalid, null));
        var overLimit = machine with { Common = machine.Common with { Rules = ordinary.Add(Rule("extra")) } };
        Assert.Equal("PolicyRuleLimitOrDuplicate", Assert.Throws<InvalidDataException>(() => PolicyCompiler.CompileAll(baseline, overLimit, null)).Message);
        if (enforcedCount > 0)
        {
            var overrides = machine with { Common = machine.Common with { Enforced = enforced.SetItem(enforced.Length - 1, enforced[^1] with { Overrides = ["git-standard"] }) } };
            Assert.Equal("PolicyInvalidRule", Assert.Throws<InvalidDataException>(() => PolicyCompiler.CompileAll(baseline, overrides, null)).Message);
        }
    }

    private static long Measure(PolicySnapshot snapshot, CommandFacts facts)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var action = 0; action < 64; action++)
        {
            var decision = PolicyEvaluator.Evaluate(snapshot, facts);
            Assert.Equal(PolicyReasons.Unmatched, decision.ReasonCode);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static PolicyRule Rule(string id) => new()
    {
        Id = id,
        Policy = PolicyOutcome.Allow,
        Matchers = [new PolicyMatcher { Type = PolicyMatcherType.AllOf, Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.ExecutableName, Value = "other" }] }],
    };

    private static CommandFacts Facts() => new("linux", "bash", "git", Path.GetFullPath("git"), "external", [], Path.GetFullPath("."));
}
