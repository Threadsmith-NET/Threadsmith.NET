namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Grants;
using Threadsmith.Policies.Matching;
using Xunit;

public static class ArgumentDenialTests
{
    [Theory]
    [InlineData("[")]
    [InlineData("(?=unsupported)")]
    [InlineData("(a)\\1")]
    public static void InvalidPatternsInInactiveOrReplacedRulesRejectCompilation(string expression)
    {
        // Arrange: even a rule replaced by user configuration must validate.
        var baseline = ShippedCommandPolicy.CreateDefaults();
        var invalid = baseline.Common.Rules[0] with { DenyArgumentsMatching = [expression] };
        var broken = baseline with { Common = baseline.Common with { Rules = [invalid] } };
        var user = new PolicyDocument { Common = new PolicyProfile { Rules = [invalid with { DenyArgumentsMatching = [] }] } };
        var inactive = new PolicyDocument
        {
            Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add("linux", new PolicyProfile
            {
                Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("zsh", new PolicyProfile { Rules = [invalid] }),
            }),
        };
        var machine = new PolicyDocument { Common = new PolicyProfile { Enforced = [invalid] } };

        // Act / Assert.
        Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(broken, null, user, "linux", "bash"));
        Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(baseline, null, inactive, "linux", "bash"));
        Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(baseline, machine, null, "linux", "bash"));
    }

    [Fact]
    public static void InvalidPatternArraysAndExceededBoundsRejectCompilation()
    {
        var baseline = ShippedCommandPolicy.CreateDefaults();
        ImmutableArray<string>[] invalid = [default, [null!], [new string('a', 4097)], [.. Enumerable.Repeat("x", 65)]];
        foreach (var patterns in invalid)
        {
            var rule = baseline.Common.Rules[0] with { DenyArgumentsMatching = patterns };
            var user = new PolicyDocument { Common = new PolicyProfile { Rules = [rule] } };
            Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(baseline, null, user, "linux", "bash"));
        }
    }

    [Fact]
    public static void UserCanReplaceExpressionsWithoutChangingCommandMatchers()
    {
        // Arrange / Act: round-trip the actual user contract.
        var baseline = ShippedCommandPolicy.CreateDefaults();
        var rule = baseline.Common.Rules.Single(rule => rule.Id == "git-standard") with { DenyArgumentsMatching = ["private/path"] };
        var user = PolicyJson.Parse(PolicyJson.Serialize(new PolicyDocument { Common = new PolicyProfile { Rules = [rule] } }));
        var snapshot = PolicyCompiler.Compile(baseline, null, user, "linux", "bash");

        // Assert.
        Assert.Equal(rule.DenyArgumentsMatching, Assert.Single(user.Common.Rules).DenyArgumentsMatching);
        Assert.False(PolicyEvaluator.Evaluate(snapshot, Facts("add", "private/path/file")).IsAllowed);
        Assert.True(PolicyEvaluator.Evaluate(snapshot, Facts("fetch", "--upload-pack=chosen")).IsAllowed);
        Assert.NotEqual(PolicyCompiler.Compile(baseline, null, null, "linux", "bash").Fingerprint, snapshot.Fingerprint);
    }

    [Fact]
    public static void CompiledSearchesAreBoundedAndReusedOnlyForTheSameRuleIdentity()
    {
        var rule = ShippedCommandPolicy.CreateDefaults().Common.Rules[0] with { DenyArgumentsMatching = ["blocked"] };
        var method = typeof(PolicyMatcherCatalog).GetMethod("GetArgumentPatterns", BindingFlags.Static | BindingFlags.NonPublic)!;
        var patterns = (Regex[])method.Invoke(null, [rule])!;
        var pattern = Assert.Single(patterns);
        Assert.Equal(RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, pattern.Options);
        Assert.Equal(TimeSpan.FromMilliseconds(100), pattern.MatchTimeout);
        Assert.Matches(pattern, "prefix blocked suffix");
        Assert.Same(patterns, method.Invoke(null, [rule]));
        Assert.NotSame(patterns, method.Invoke(null, [rule with { DenyArgumentsMatching = ["other"] }]));
    }

    [Fact]
    public static void RememberedGrantCannotAuthorizeAnArgumentDenial()
    {
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        var decision = PolicyEvaluator.Evaluate(snapshot, Facts("fetch", "--upload-pack=chosen"));
        var authority = new PolicyGrantAuthority();
        snapshot = snapshot with { AlwaysAllow = [new PolicyGrant("saved", "identity", decision.RuleFingerprint)] };
        Assert.False(authority.HasRemembered(decision, snapshot, "identity", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static void CommonOnlyMachineConstraintsApplyWithoutAPlatformMarker(bool closedSet)
    {
        var rule = ShippedCommandPolicy.CreateDefaults().Common.Rules[0] with { Id = "machine-global", Policy = PolicyOutcome.AlwaysDeny };
        var machine = new PolicyDocument
        {
            Common = closedSet ? new PolicyProfile { RequireEnforcedMatch = true } : new PolicyProfile { Enforced = [rule] },
        };
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), machine, null, "linux", "bash");
        Assert.False(PolicyEvaluator.Evaluate(snapshot, Facts("status")).IsAllowed);
    }

    [Fact]
    public static void RemovedSchemaFieldsAreRejected()
    {
        Assert.Throws<JsonException>(() => PolicyJson.Parse("""
            {"common":{"rules":[{"id":"old","policy":"Allow","matchers":[{"type":"executable","value":"git","schema":{}}]}]}}
            """));
    }

    private static CommandFacts Facts(params string[] arguments) => new("linux", "bash", "git", Path.GetFullPath("git"), "external", [.. arguments], Path.GetFullPath("."));
}
