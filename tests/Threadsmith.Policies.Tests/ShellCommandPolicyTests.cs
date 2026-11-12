namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Matching;
using Xunit;

public static class ShellCommandPolicyTests
{
    [Theory]
    [InlineData(PolicyOutcome.Allow)]
    [InlineData(PolicyOutcome.AlwaysDeny)]
    [InlineData(PolicyOutcome.ApprovalRequired)]
    [InlineData(PolicyOutcome.ApprovalRequiredSession)]
    [InlineData(PolicyOutcome.ApprovalAlwaysRequired)]
    public static void ShellCommandsUseTheSameOutcomesAndChoices(PolicyOutcome outcome)
    {
        var facts = Facts();
        var snapshot = Snapshot(facts, Rule("command", "Write-Output", outcome));

        var shell = PolicyEvaluator.Evaluate(snapshot, facts);
        var external = PolicyEvaluator.Evaluate(snapshot, facts with
        {
            CommandKind = CommandPolicyNames.External,
            ResolvedExecutable = Path.GetFullPath("Write-Output"),
        });

        Assert.Equal(outcome, shell.Outcome);
        Assert.Equal(external, shell with { RuleIds = external.RuleIds, Choices = external.Choices });
        Assert.Equal(PolicyEvaluator.Choices(outcome), shell.Choices);
    }

    [Theory]
    [InlineData("windows", "powershell", true)]
    [InlineData("windows", "pwsh", true)]
    [InlineData("linux", "pwsh", true)]
    [InlineData("macos", "pwsh", true)]
    [InlineData("windows", "cmd", true)]
    [InlineData("linux", "bash", false)]
    [InlineData("linux", "sh", false)]
    [InlineData("macos", "zsh", false)]
    public static void IndexedNamesRespectTheShellCaseSemantics(string platform, string shell, bool matches)
    {
        var facts = Facts() with { Platform = platform, Shell = shell, ExecutableName = "wRiTe-OuTpUt" };
        var deny = Rule("deny", "Write-Output", PolicyOutcome.AlwaysDeny) with { Overrides = ["broad"] };
        var broad = Rule("broad", "unused") with
        {
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.Shell, Value = shell }],
        };
        var snapshot = Snapshot(facts, broad, deny);

        Assert.Equal(!matches, PolicyEvaluator.Evaluate(snapshot, facts).IsAllowed);
        Assert.Equal(!matches, PolicyEvaluator.Evaluate(snapshot with { Rules = [broad], Enforced = [deny with { Overrides = [] }] }, facts).IsAllowed);
        var closed = snapshot with
        {
            Rules = [broad],
            Enforced = [deny with { Policy = PolicyOutcome.Allow, Overrides = [] }],
            RequireEnforcedMatch = true,
        };
        Assert.Equal(matches, PolicyEvaluator.Evaluate(closed, facts).IsAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void NormalizedArgumentDenialsApplyBeforeOverridesAndApprovals(bool enforced)
    {
        var facts = Facts() with { Arguments = ["-Message", "hello world"] };
        var restricted = Rule("restricted", "Write-Output", PolicyOutcome.ApprovalRequired) with
        {
            DenyArgumentsMatching = ["^-Message hello world$"],
        };
        var replacement = Rule("replacement", "Write-Output") with { Overrides = enforced ? [] : ["restricted"] };
        var snapshot = Snapshot(facts, restricted, replacement);
        if (enforced)
        {
            snapshot = snapshot with { Rules = [replacement], Enforced = [restricted] };
        }

        foreach (var arguments in new ImmutableArray<string>[] { facts.Arguments, ["-Message", "hello", "world"] })
        {
            var decision = PolicyEvaluator.Evaluate(snapshot, facts with { Arguments = arguments });
            Assert.False(decision.IsAllowed);
            Assert.False(decision.RequiresApproval);
            Assert.Equal(PolicyReasons.ExplicitDenial, decision.ReasonCode);
            Assert.Empty(decision.Choices);
        }
    }

    [Fact]
    public static void AbsentFileTargetDoesNotMatchPathsOrEmptyExecutableNames()
    {
        var facts = Facts();
        var catalog = new PolicyMatcherCatalog();
        var path = new PolicyMatcher { Type = PolicyMatcherType.Path, Fact = PolicyFact.ResolvedExecutable, Values = [facts.WorkingDirectory] };
        Assert.False(catalog.Matches([path], facts));
        Assert.True(catalog.Matches([path with { Fact = PolicyFact.WorkingDirectory }], facts));
        Assert.False(catalog.Matches([new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = string.Empty }], facts));
        var rule = Rule("alternative", "unused") with
        {
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.AnyOf, Matchers = [path, Rule("name", facts.ExecutableName).Matchers[0]] }],
        };
        Assert.True(PolicyEvaluator.Evaluate(Snapshot(facts, rule), facts).IsAllowed);
    }

    [Theory]
    [InlineData("shell-command", "relative", "Write-Output")]
    [InlineData("shell-command", null, "Write-Output")]
    [InlineData("shell-command", "", "")]
    [InlineData("external", "", "Write-Output")]
    [InlineData("explicit-external", "", "Write-Output")]
    [InlineData("builtin", "", "Write-Output")]
    public static void InvalidFactShapesDenyEvenWithoutCandidateRules(string kind, string? target, string name)
    {
        var facts = Facts() with { CommandKind = kind, ResolvedExecutable = target!, ExecutableName = name };
        Assert.Equal(PolicyReasons.AnalysisError, PolicyEvaluator.Evaluate(Snapshot(facts), facts).ReasonCode);
    }

    [Fact]
    public static void PowerShellIndexDoesNotChangeUnixExternalPathOrNameCaseSemantics()
    {
        var facts = Facts() with { ExecutableName = "Native", ResolvedExecutable = Path.GetFullPath("Native"), CommandKind = CommandPolicyNames.External };
        Assert.False(PolicyEvaluator.Evaluate(Snapshot(facts, Rule("name", "NATIVE")), facts).IsAllowed);
        Assert.False(PolicyEvaluator.Evaluate(Snapshot(facts, Rule("path", facts.ResolvedExecutable.ToUpperInvariant())), facts).IsAllowed);
        Assert.True(PolicyEvaluator.Evaluate(Snapshot(facts, Rule("path", facts.ResolvedExecutable)), facts).IsAllowed);
        var explicitFacts = facts with { CommandKind = CommandPolicyNames.ExplicitExternal };
        Assert.False(PolicyEvaluator.Evaluate(Snapshot(explicitFacts, Rule("name", facts.ExecutableName)), explicitFacts).IsAllowed);
    }

    private static CommandFacts Facts() => new("linux", "pwsh", "Write-Output", string.Empty, CommandPolicyNames.ShellCommand, [], Path.GetFullPath("."));

    private static PolicyRule Rule(string id, string name, PolicyOutcome outcome = PolicyOutcome.Allow) => new()
    {
        Id = id,
        Policy = outcome,
        Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = name }],
    };

    private static PolicySnapshot Snapshot(CommandFacts facts, params PolicyRule[] rules) =>
        PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, facts.Platform, facts.Shell) with { Rules = [.. rules], Enforced = [] };
}
