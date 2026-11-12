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
    [Theory]
    [InlineData(PolicyOutcome.Allow, true, false, 0)]
    [InlineData(PolicyOutcome.AlwaysDeny, false, false, 0)]
    [InlineData(PolicyOutcome.ApprovalAlwaysRequired, true, true, 2)]
    [InlineData(PolicyOutcome.ApprovalRequiredSession, true, true, 3)]
    [InlineData(PolicyOutcome.ApprovalRequired, true, true, 4)]
    public static void OutcomesHaveExactChoices(PolicyOutcome outcome, bool allowed, bool approval, int choices)
    {
        var snapshot = Compile(Rule("only", outcome));
        var decision = PolicyEvaluator.Evaluate(snapshot, Facts("git"));
        Assert.Equal(allowed, decision.IsAllowed);
        Assert.Equal(approval, decision.RequiresApproval);
        Assert.Equal(choices, decision.Choices.Length);
    }

    [Theory]
    [InlineData("git", "commit", "-m", "hello world")]
    [InlineData("git", "unknown-plugin", "--unknown-option")]
    [InlineData("dotnet", "unknown-tool", "--unknown-option")]
    [InlineData("git", "fetch")]
    [InlineData("git", "pull")]
    [InlineData("git", "push")]
    [InlineData("dotnet", "restore")]
    [InlineData("dotnet", "package", "add", "Example")]
    [InlineData("dotnet", "tool", "install", "example")]
    [InlineData("dotnet", "workload", "restore")]
    public static void NormalWorkflowsAreAllowed(string executable, params string[] arguments)
    {
        var defaults = ShippedCommandPolicy.CreateDefaults();
        foreach (var (platform, profile) in defaults.Platforms)
        {
            foreach (var shell in profile.Shells.Keys)
            {
                var snapshot = PolicyCompiler.Compile(defaults, null, null, platform, shell);
                var decision = PolicyEvaluator.Evaluate(snapshot, Facts(executable, arguments) with { Platform = platform, Shell = shell });
                Assert.True(decision.IsAllowed, decision.ReasonCode);
                Assert.False(decision.RequiresApproval);
            }
        }
    }

    [Theory]
    [InlineData("git", "-c", "core.sshCommand=unsafe", "fetch")]
    [InlineData("git", "fetch", "--upload-pack=unsafe")]
    [InlineData("git", "--exec-path=unsafe", "status")]
    [InlineData("git", "clone", "-c", "safe=value", "--upload-pack=custom", "repo")]
    [InlineData("git", "format-patch", "--ext-diff", "HEAD~1")]
    [InlineData("git", "format-patch", "-n", "--ext-diff", "HEAD~1")]
    [InlineData("git", "format-patch", "--from", "--ext-diff", "HEAD~1")]
    [InlineData("git", "rebase", "-x", "custom", "HEAD~1")]
    [InlineData("git", "grep", "-Ocustom", "pattern")]
    [InlineData("git", "clone", "--config=core.sshCommand=custom", "ssh://host/repo")]
    [InlineData("git", "clone", "-qc", "core.sshCommand=custom", "ssh://host/repo")]
    [InlineData("git", "submodule", "foreach", "custom")]
    [InlineData("git", "bisect", "run", "custom")]
    [InlineData("git", "clone", "-u", "custom", "ssh://host/repo")]
    [InlineData("git", "clone", "-ucustom", "ssh://host/repo")]
    [InlineData("curl", "https://example.com")]
    public static void SelectedExecutionTextAndHttpCommandsDeny(string executable, params string[] arguments)
    {
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        Assert.False(PolicyEvaluator.Evaluate(snapshot, Facts(executable, arguments)).IsAllowed);
    }

    [Fact]
    public static void SameOutcomeOverlapsAreHarmlessButMixedOutcomesConflict()
    {
        var first = Rule("first", PolicyOutcome.Allow);
        var second = Rule("second", PolicyOutcome.Allow);
        Assert.True(PolicyEvaluator.Evaluate(Compile(first, second), Facts("git")).IsAllowed);
        var conflict = second with { Policy = PolicyOutcome.AlwaysDeny };
        Assert.Equal("PolicyConflict", PolicyEvaluator.Evaluate(Compile(first, conflict), Facts("git")).ReasonCode);
        var exception = conflict with { Overrides = [first.Id] };
        Assert.Equal(PolicyOutcome.AlwaysDeny, PolicyEvaluator.Evaluate(Compile(first, exception), Facts("git")).Outcome);
    }

    [Fact]
    public static void OverrideOrderDoesNotChangeEffectiveRequirement()
    {
        var basis = Rule("basis", PolicyOutcome.AlwaysDeny);
        var exception = Rule("exception", PolicyOutcome.Allow) with { Overrides = [basis.Id] };
        Assert.Equal(PolicyEvaluator.Evaluate(Compile(basis, exception), Facts("git")).RuleFingerprint, PolicyEvaluator.Evaluate(Compile(exception, basis), Facts("git")).RuleFingerprint);
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
        var decision = PolicyEvaluator.Evaluate(snapshot, Facts("git", "prefix-blocked-suffix"));

        // Assert.
        Assert.False(decision.IsAllowed);
        Assert.False(decision.RequiresApproval);
        Assert.Empty(decision.Choices);
        Assert.Equal(PolicyOutcome.AlwaysDeny, decision.Outcome);
        Assert.Contains(basis.Id, decision.RuleIds);
        Assert.True(PolicyEvaluator.Evaluate(snapshot, Facts("git", "other", "--unknown")).IsAllowed);
    }

    [Theory]
    [InlineData("^commit -m hello world$", false, "commit", "-m", "hello world")]
    [InlineData("^commit -m hello world$", false, "commit", "-m", "hello", "world")]
    [InlineData("git", true, "commit")]
    [InlineData("HELLO", true, "hello")]
    [InlineData("(?i)HELLO", false, "hello")]
    [InlineData("hello world", true, "hello  world")]
    [InlineData("line\\nnext", false, "line\nnext")]
    [InlineData("^ $", false, "", "")]
    [InlineData("^$", false)]
    public static void ArgumentSearchUsesOnlyJoinedLiteralValues(string pattern, bool allowed, params string[] arguments)
    {
        // Arrange.
        var snapshot = Compile(Rule("search", PolicyOutcome.Allow) with { DenyArgumentsMatching = [pattern] });

        // Act / Assert.
        Assert.Equal(allowed, PolicyEvaluator.Evaluate(snapshot, Facts("git", arguments)).IsAllowed);
        Assert.False(PolicyEvaluator.Evaluate(snapshot, Facts("unmatched", arguments)).IsAllowed);
    }

    [Fact]
    public static void ArgumentsAreUnrestrictedWithoutDenyExpressions()
    {
        var snapshot = Compile(Rule("all-arguments", PolicyOutcome.Allow));
        Assert.True(PolicyEvaluator.Evaluate(snapshot, Facts("git", "unknown", "--flag", "--flag", "path with spaces")).IsAllowed);
    }

    [Fact]
    public static void EnforcedConstraintsCannotGrantOrdinaryPermission()
    {
        var machine = Document(new PolicyProfile { Enforced = [Rule("constraint", PolicyOutcome.Allow)], Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("bash", new PolicyProfile()) });
        var snapshot = PolicyCompiler.Compile(new PolicyDocument(), machine, null, "linux", "bash");
        Assert.False(PolicyEvaluator.Evaluate(snapshot, Facts("git")).IsAllowed);
        Assert.Throws<InvalidDataException>(() => PolicyCompiler.Compile(new PolicyDocument(), null, machine, "linux", "bash"));
    }

    [Fact]
    public static void EnforcedArgumentExpressionsCannotBeRelaxedByUserRules()
    {
        var constraint = Rule("constraint", PolicyOutcome.Allow) with { DenyArgumentsMatching = ["private/path"] };
        var machine = Document(new PolicyProfile { RequireEnforcedMatch = true, Enforced = [constraint] });
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), machine, null, "linux", "bash");
        Assert.True(PolicyEvaluator.Evaluate(snapshot, Facts("git", "status", "--unknown")).IsAllowed);
        Assert.False(PolicyEvaluator.Evaluate(snapshot, Facts("git", "add", "private/path/file")).IsAllowed);
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

    [Fact]
    public static void PolicyJsonRoundTripsImmutableDocuments()
    {
        var document = ShippedCommandPolicy.CreateDefaults();
        var parsed = PolicyJson.Parse(PolicyJson.Serialize(document));
        Assert.Equal(PolicyCompiler.Compile(document, null, null, "linux", "bash").Fingerprint, PolicyCompiler.Compile(parsed, null, null, "linux", "bash").Fingerprint);
    }

    private static PolicySnapshot Compile(params PolicyRule[] rules) => PolicyCompiler.Compile(Document(new PolicyProfile { Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("bash", new PolicyProfile { Rules = [.. rules] }) }), null, null, "linux", "bash");

    private static PolicyDocument Document(PolicyProfile profile) => new() { Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add("linux", profile) };

    private static PolicyRule Rule(string id, PolicyOutcome outcome) => new() { Id = id, Policy = outcome, Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = "git" }] };

    private static CommandFacts Facts(string executable, params string[] arguments) => new("linux", "bash", executable, Path.GetFullPath(executable), "external", [.. arguments], Path.GetFullPath("."));
}
