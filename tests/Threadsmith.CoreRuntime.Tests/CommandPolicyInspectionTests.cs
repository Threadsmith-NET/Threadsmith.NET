namespace Threadsmith.CoreRuntime.Tests;

using System.Collections.Immutable;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Policies.Contracts;
using Xunit;

/// <summary>Checks bounded user-only command-policy presentation.</summary>
public static class CommandPolicyInspectionTests
{
    /// <summary>Large catalogs and identities cannot expand the manual preview beyond its bound.</summary>
    [Fact]
    public static void LargePolicyInspectionBoundsIdentitiesWithoutSerializingPayloads()
    {
        var huge = new string('x', 4096);
        var rules = Enumerable.Range(0, 4096).Select(index => new PolicyRule
        {
            Id = "rule-" + index + huge,
            Policy = PolicyOutcome.Allow,
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.ExecutableName, Value = "payload-not-for-presentation" }],
        }).ToImmutableArray();
        var grants = Enumerable.Range(0, 1024).Select(index => new PolicyGrant("grant-" + index + huge, huge, huge)).ToImmutableArray();
        var snapshot = new PolicySnapshot("linux", "bash", "fixture-fingerprint", rules, rules, true, grants, ImmutableDictionary<string, string>.Empty.Add("source\u001b" + huge, huge))
        {
            ConfigurationRevision = "fixture-revision",
        };
        string[] live = [.. Enumerable.Range(0, 1024).Select(index => "live-" + index + huge)];

        var text = InteractionPresentationFormatter.FormatCommandPolicyInspection(snapshot, 42, live);

        Assert.True(text.Length < 16384);
        Assert.Contains("Rules: 4096", text, StringComparison.Ordinal);
        Assert.Contains("Persistent grants: 1024", text, StringComparison.Ordinal);
        Assert.Contains("Live grants: 1024", text, StringComparison.Ordinal);
        Assert.Contains("4088 omitted", text, StringComparison.Ordinal);
        Assert.Contains("fixture-fingerprint", text, StringComparison.Ordinal);
        Assert.Contains("fixture-revision", text, StringComparison.Ordinal);
        Assert.Contains("Live session epoch: 42", text, StringComparison.Ordinal);
        Assert.DoesNotContain("payload-not-for-presentation", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain(huge, text, StringComparison.Ordinal);
    }
}
