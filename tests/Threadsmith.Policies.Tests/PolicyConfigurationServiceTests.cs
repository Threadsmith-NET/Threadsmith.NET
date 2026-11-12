namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Grants;
using Xunit;

public static class PolicyConfigurationServiceTests
{
    [Fact]
    public static async Task EveryListUpdateUsesOneHostPortAndDeduplicatesExactGrant()
    {
        var host = new ConfigurationHost();
        var service = new PolicyConfigurationService(host);
        var grant = new PolicyGrant("grant", PolicyJson.Fingerprint("identity"), PolicyJson.Fingerprint("rules"));
        var request = new PolicyUpdateRequest(PolicyDestination.User, "linux", "bash", "revision", PolicyUpdateOperation.Grant, grant.Id, Grant: grant);
        Assert.True((await service.UpdateAsync(request, TestContext.Current.CancellationToken)).Activated);
        Assert.True((await service.UpdateAsync(request, TestContext.Current.CancellationToken)).Activated);
        Assert.Single(host.User?.Platforms["linux"].Shells["bash"].AlwaysAllow ?? []);
        Assert.True((await service.UpdateAsync(request with { Operation = PolicyUpdateOperation.Revoke, Grant = null }, TestContext.Current.CancellationToken)).Activated);
        Assert.Empty(host.User?.Platforms["linux"].Shells["bash"].AlwaysAllow ?? []);
        Assert.Equal(3, host.Calls);
    }

    [Fact]
    public static async Task RevisionConflictsAndInvalidUnusedProfilesNeverPublish()
    {
        var host = new ConfigurationHost();
        var service = new PolicyConfigurationService(host);
        var request = new PolicyUpdateRequest(PolicyDestination.User, "linux", "bash", "stale", PolicyUpdateOperation.Remove, "rule");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdateAsync(request, TestContext.Current.CancellationToken));
        Assert.Null(host.User);
        host.User = new PolicyDocument { Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add("linux", new PolicyProfile { Shells = ImmutableDictionary<string, PolicyProfile>.Empty.Add("zsh", new PolicyProfile { Rules = [new PolicyRule { Id = "bad", Policy = PolicyOutcome.Allow, Overrides = ["missing"], Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = "git" }] }] }) }) };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdateAsync(request with { Operation = PolicyUpdateOperation.Reload }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public static void CanonicalFingerprintsIgnoreObjectInsertionOrderButNotArrayOrder()
    {
        Assert.Equal(PolicyJson.Fingerprint(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }), PolicyJson.Fingerprint(new Dictionary<string, int> { ["b"] = 2, ["a"] = 1 }));
        int[] ascending = [1, 2];
        int[] descending = [2, 1];
        Assert.NotEqual(PolicyJson.Fingerprint(ascending), PolicyJson.Fingerprint(descending));
    }

    [Fact]
    public static async Task RevocationRemovesAnInheritedPlatformGrant()
    {
        var grant = new PolicyGrant("inherited", PolicyJson.Fingerprint("context"), PolicyJson.Fingerprint("rules"));
        var host = new ConfigurationHost { User = new PolicyDocument { Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add("linux", new PolicyProfile { AlwaysAllow = [grant] }) } };
        var service = new PolicyConfigurationService(host);
        var request = new PolicyUpdateRequest(PolicyDestination.User, "linux", "bash", "revision", PolicyUpdateOperation.Revoke, grant.Id);

        await service.UpdateAsync(request, TestContext.Current.CancellationToken);

        Assert.Empty(host.User!.Platforms["linux"].AlwaysAllow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task SavedShellGrantReplacesAStaleInheritedGrant(bool common)
    {
        // Arrange: a rule edit made an inherited record stale for this exact context.
        var baseline = ShippedCommandPolicy.CreateDefaults();
        var identity = PolicyJson.Fingerprint("context");
        var decision = PolicyEvaluator.Evaluate(
            PolicyCompiler.Compile(baseline, null, null, CommandPolicyNames.Linux, CommandPolicyNames.Bash),
            new CommandFacts(CommandPolicyNames.Linux, CommandPolicyNames.Bash, "git", Path.GetFullPath("git"), CommandPolicyNames.External, ["status"], Path.GetFullPath(".")));
        decision = decision with { Outcome = PolicyOutcome.ApprovalRequired, RequiresApproval = true };
        var stale = new PolicyGrant(identity, identity, PolicyJson.Fingerprint("old rules"));
        var inherited = new PolicyProfile { AlwaysAllow = [stale] };
        var host = new ConfigurationHost
        {
            User = common ? new PolicyDocument { Common = inherited }
                : new PolicyDocument { Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add(CommandPolicyNames.Linux, inherited) },
        };
        var replacement = stale with { RuleFingerprint = decision.RuleFingerprint };
        var request = new PolicyUpdateRequest(PolicyDestination.User, CommandPolicyNames.Linux, CommandPolicyNames.Bash, "revision", PolicyUpdateOperation.Grant, identity, Grant: replacement);

        // Act.
        await new PolicyConfigurationService(host).UpdateAsync(request, TestContext.Current.CancellationToken);
        var snapshot = PolicyCompiler.Compile(baseline, null, host.User, CommandPolicyNames.Linux, CommandPolicyNames.Bash);

        // Assert: publication and the next remembered-grant check agree on the replacement.
        Assert.Equal(replacement, Assert.Single(snapshot.AlwaysAllow));
        Assert.True(new PolicyGrantAuthority().HasRemembered(decision, snapshot, identity));
    }

    [Fact]
    public static async Task AtomicRememberedEpochAndConcurrentOnceConsumptionRespectReset()
    {
        var grants = new PolicyGrantAuthority();
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        var choices = ImmutableArray.Create(PolicyApprovalChoice.Once, PolicyApprovalChoice.Session, PolicyApprovalChoice.Deny);
        var decision = new PolicyDecision(true, true, "test", PolicyOutcome.ApprovalRequiredSession, ["rule"], "rules", choices);
        var request = new PolicyApprovalRequest(Guid.NewGuid(), "fixture", "fixture", "context", "authority", grants.Epoch, ["rule"], choices);
        Assert.True(grants.Approve(request, new(request.RequestId, PolicyApprovalChoice.Session), "rules"));
        Assert.True(grants.HasRemembered(decision, snapshot, "context", out var capturedEpoch));
        Assert.Single(grants.InspectSession());

        var results = await Task.WhenAll(
            Task.Run(() => grants.Consume(request.RequestId, request.ContextFingerprint, capturedEpoch), TestContext.Current.CancellationToken),
            Task.Run(() => grants.Consume(request.RequestId, request.ContextFingerprint, capturedEpoch), TestContext.Current.CancellationToken));

        Assert.Single(results, result => result);
        grants.Reset();
        Assert.NotEqual(capturedEpoch, grants.Epoch);
        Assert.Empty(grants.InspectSession());
        Assert.False(grants.HasRemembered(decision, snapshot, "context"));
        Assert.False(grants.Approve(request, new(request.RequestId, PolicyApprovalChoice.Session), "rules"));
    }

    private sealed class ConfigurationHost : IPolicyConfigurationHost
    {
        internal PolicyDocument? User { get; set; }

        internal int Calls { get; private set; }

        public Task<PolicyUpdateReceipt> UpdateAsync(PolicyUpdateRequest request, Func<PolicyConfigurationInputs, PolicyDocument> transform, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            User = transform(new PolicyConfigurationInputs(ShippedCommandPolicy.CreateDefaults(), null, User, "revision"));
            return Task.FromResult(new PolicyUpdateReceipt(true, true, "revision", "activated"));
        }
    }
}
