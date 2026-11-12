namespace Threadsmith.Policies.Tests;

using System.Text.Json;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Matching;
using Xunit;

public static class PolicyJsonTests
{
    [Theory]
    [InlineData(PolicyMatcherType.Exact, "exact")]
    [InlineData(PolicyMatcherType.ValueSet, "valueSet")]
    [InlineData(PolicyMatcherType.Executable, "executable")]
    [InlineData(PolicyMatcherType.Path, "path")]
    [InlineData(PolicyMatcherType.Glob, "glob")]
    [InlineData(PolicyMatcherType.Regex, "regex")]
    [InlineData(PolicyMatcherType.AllOf, "allOf")]
    [InlineData(PolicyMatcherType.AnyOf, "anyOf")]
    public static void MatcherKindsRetainTheirSerializedNames(PolicyMatcherType type, string name)
    {
        // Arrange.
        var document = Document(new PolicyMatcher { Type = type });

        // Act.
        var json = PolicyJson.Serialize(document);
        var parsed = PolicyJson.Parse(json);

        // Assert: matcher naming must not change outcome naming.
        Assert.Contains("\"type\":\"" + name + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"policy\":\"Allow\"", json, StringComparison.Ordinal);
        Assert.Equal(type, Assert.Single(Assert.Single(parsed.Common.Rules).Matchers).Type);
    }

    [Theory]
    [InlineData("\"argumentPrefix\"")]
    [InlineData("\"argumentSchema\"")]
    [InlineData("\"arbitrary-code\"")]
    [InlineData("\"standardCli\"")]
    [InlineData("\"Exact\"")]
    [InlineData("\"0\"")]
    [InlineData("\"\"")]
    [InlineData("0")]
    [InlineData("null")]
    public static void UnknownOrUntypedMatcherKindsRejectTheWholeDocument(string token)
    {
        // Arrange: place the invalid discriminator after a valid alternative.
        var json = "{\"common\":{\"rules\":[{\"id\":\"fixture\",\"policy\":\"Allow\",\"matchers\":[{\"type\":\"anyOf\",\"matchers\":[{\"type\":\"exact\",\"fact\":\"ExecutableName\",\"value\":\"fixture\"},{\"type\":" + token + "}]}]}]}}";

        // Act / Assert.
        Assert.Throws<JsonException>(() => PolicyJson.Parse(json));
    }

    [Fact]
    public static void UndefinedInMemoryKindsCannotBeSerializedOrValidated()
    {
        // Arrange.
        var matcher = new PolicyMatcher { Type = (PolicyMatcherType)(-1) };
        var unusedBranch = new PolicyMatcher
        {
            Type = PolicyMatcherType.AnyOf,
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.ExecutableName, Value = "fixture" }, matcher],
        };

        // Act / Assert.
        Assert.Throws<JsonException>(() => PolicyJson.Serialize(Document(matcher)));
        Assert.Throws<InvalidDataException>(() => new PolicyMatcherCatalog().Validate(unusedBranch));
    }

    private static PolicyDocument Document(PolicyMatcher matcher) => new()
    {
        Common = new PolicyProfile
        {
            Rules = [new PolicyRule { Id = "fixture", Policy = PolicyOutcome.Allow, Matchers = [matcher] }],
        },
    };
}
