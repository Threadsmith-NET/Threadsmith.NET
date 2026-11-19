namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Grants;
using Xunit;

public static class PolicyEvaluatorTests
{
    [Fact]
    public static void SameOutcomeOverlapsAreHarmlessButMixedOutcomesConflict()
    {
        var first = Rule("first", PolicyOutcome.Allow);
        var second = Rule("second", PolicyOutcome.Allow);
        Assert.True(TestPolicyEvaluation.Evaluate(Compile(first, second), Facts("git")).IsAllowed);
        var conflict = second with { Policy = PolicyOutcome.AlwaysDeny };
        Assert.Equal("PolicyConflict", TestPolicyEvaluation.Evaluate(Compile(first, conflict), Facts("git")).ReasonCode);
        var exception = conflict with { Overrides = [first.Id] };
        Assert.Equal(PolicyOutcome.AlwaysDeny, TestPolicyEvaluation.Evaluate(Compile(first, exception), Facts("git")).Outcome);
    }

    [Theory]
    [InlineData(PolicyOutcome.Allow)]
    [InlineData(PolicyOutcome.ApprovalRequired)]
    public static void ArgumentDenialVetoesPermissionAndApprovalEvenWhenRuleIsOverridden(PolicyOutcome outcome)
    {
        // Arrange.
        var basis = Rule("basis", outcome) with { DenyArgumentsMatching = ["never", "blocked"] };
        var replacement = Rule("replacement", PolicyOutcome.Allow) with { Overrides = [basis.Id] };
        var snapshot = Compile(basis, replacement);

        // Act.
        var decision = TestPolicyEvaluation.Evaluate(snapshot, Facts("git", "prefix-blocked-suffix"));

        // Assert.
        Assert.False(decision.IsAllowed);
        Assert.False(decision.RequiresApproval);
        Assert.Empty(decision.Choices);
        Assert.Equal(PolicyOutcome.AlwaysDeny, decision.Outcome);
        Assert.Contains(basis.Id, decision.RuleIds);
        Assert.True(TestPolicyEvaluation.Evaluate(snapshot, Facts("git", "other", "--unknown")).IsAllowed);
    }

    [Fact]
    public static void ArgumentsAreUnrestrictedWithoutDenyExpressions()
    {
        var snapshot = Compile(Rule("all-arguments", PolicyOutcome.Allow));
        Assert.True(TestPolicyEvaluation.Evaluate(snapshot, Facts("git", "unknown", "--flag", "--flag", "path with spaces")).IsAllowed);
    }

    [Fact]
    public static void EnforcedConstraintsCannotGrantOrdinaryPermission()
    {
        var machine = Document(new PolicyProfile { Enforced = [Rule("constraint", PolicyOutcome.Allow)], Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("bash", new PolicyProfile()) });
        var snapshot = PolicyCompiler.Compile(new PolicyDocument(), machine, null, "linux", "bash");
        Assert.False(TestPolicyEvaluation.Evaluate(snapshot, Facts("git")).IsAllowed);
        Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(new PolicyDocument(), null, machine, "linux", "bash"));
    }

    [Fact]
    public static void EnforcedArgumentExpressionsCannotBeRelaxedByUserRules()
    {
        var constraint = Rule("constraint", PolicyOutcome.Allow) with { DenyArgumentsMatching = ["private/path"] };
        var machine = Document(new PolicyProfile { RequireEnforcedMatch = true, Enforced = [constraint] });
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), machine, null, "linux", "bash");
        Assert.True(TestPolicyEvaluation.Evaluate(snapshot, Facts("git", "status", "--unknown")).IsAllowed);
        Assert.False(TestPolicyEvaluation.Evaluate(snapshot, Facts("git", "add", "private/path/file")).IsAllowed);
    }

    [Fact]
    public static void InactiveProfileOverridesAreValidated()
    {
        var broken = Document(new PolicyProfile { Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("zsh", new PolicyProfile { Rules = [Rule("bad", PolicyOutcome.Allow) with { Overrides = ["missing"] }] }) });
        Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, broken, "linux", "bash"));
    }

    [Fact]
    public static void InvalidUnusedMatcherAndOverrideCycleRejectCompilation()
    {
        var unknown = Rule("bad", PolicyOutcome.Allow) with { Matchers = [new PolicyMatcher { Type = (PolicyMatcherType)(-1) }] };
        Assert.Throws<InvalidDataException>(() => Compile(unknown));
        var first = Rule("first", PolicyOutcome.Allow) with { Overrides = ["second"] };
        var second = Rule("second", PolicyOutcome.Allow) with { Overrides = ["first"] };
        Assert.Throws<InvalidDataException>(() => Compile(first, second));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"unknown\":[]}")]
    public static void JsonRejectsUnknownAndDuplicateProperties(string json)
    {
        Assert.ThrowsAny<Exception>(() => PolicyJson.Parse(json));
    }

    [Fact]
    public static void OnceIsAtomicAndCannotBeReplayedAfterConsumptionOrSessionReset()
    {
        var authority = new PolicyGrantAuthority();
        var request = new PolicyApprovalRequest(Guid.NewGuid(), "child", "exact", "identity", "authority", authority.Epoch, ["rule"], [PolicyApprovalChoice.Once, PolicyApprovalChoice.Deny]);
        var response = new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Once);
        Assert.True(authority.Approve(request, response, "rule-fingerprint"));
        Assert.True(authority.Consume(request.RequestId, request.ContextFingerprint, request.SessionEpoch));
        Assert.False(authority.Consume(request.RequestId, request.ContextFingerprint, request.SessionEpoch));
        Assert.False(authority.Approve(request, response, "rule-fingerprint"));
        authority.Reset();
        Assert.False(authority.Approve(request, response, "rule-fingerprint"));
    }

    private static PolicySnapshot Compile(params PolicyRule[] rules) => PolicyCompiler.Compile(Document(new PolicyProfile { Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("bash", new PolicyProfile { Rules = [.. rules] }) }), null, null, "linux", "bash");

    private static PolicyDocument Document(PolicyProfile profile) => new() { Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add("linux", profile) };

    private static PolicyRule Rule(string id, PolicyOutcome outcome) => new() { Id = id, Policy = outcome, Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = "git" }] };

    private static CommandFacts Facts(string executable, params string[] arguments) => new("linux", "bash", executable, Path.GetFullPath(executable), "external", [.. arguments], Path.GetFullPath("."));
}
