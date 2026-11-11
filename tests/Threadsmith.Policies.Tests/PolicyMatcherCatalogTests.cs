namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Matching;
using Xunit;

public static class PolicyMatcherCatalogTests
{
    [Theory]
    [InlineData(PolicyFact.WorkingDirectory)]
    [InlineData(PolicyFact.ResolvedExecutable)]
    public static void PathContainmentNormalizesBothPathsAndPreservesDirectoryBoundaries(PolicyFact fact)
    {
        // Arrange: configured roots do not depend on a request's working directory.
        var root = Path.GetFullPath(Path.Combine("policy-fixture", "private"));
        var outside = Path.GetFullPath(Path.Combine("policy-fixture", "outside"));
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var facts = new CommandFacts(platform, "bash", "git", outside, "external", [], outside);
        var matcher = new PolicyMatcher { Type = PolicyMatcherType.Path, Fact = fact, Values = [root] };
        var catalog = new PolicyMatcherCatalog();
        catalog.Validate(matcher);
        (string Value, bool Expected)[] cases =
        [
            (root, true),
            (root + Path.DirectorySeparatorChar, true),
            (Path.Combine(root, "child", "nested"), true),
            (root + "-sibling", false),
            (Path.Combine(root, "child", "..", "inside"), true),
            (Path.Combine(root, "..", "outside"), false),
            (root.ToUpperInvariant(), OperatingSystem.IsWindows()),
        ];
        foreach (var (value, expected) in cases)
        {
            var selected = fact == PolicyFact.WorkingDirectory ? facts with { WorkingDirectory = value } : facts with { ResolvedExecutable = value };

            // Act / Assert.
            Assert.Equal(expected, catalog.Matches([matcher], selected));
        }

        var descendant = fact == PolicyFact.WorkingDirectory ? facts with { WorkingDirectory = Path.Combine(root, "child") } : facts with { ResolvedExecutable = Path.Combine(root, "child") };
        Assert.True(catalog.Matches([matcher with { Values = [Path.Combine(root, "child", "..")] }], descendant));
        Assert.True(catalog.Matches([matcher with { Values = [Path.GetPathRoot(root)!] }], descendant));
    }

    [Fact]
    public static void EnforcedSubtreeDenialMatchesTheRootAndEveryChildButNotSiblingPrefixes()
    {
        // Arrange.
        var root = Path.GetFullPath(Path.Combine("policy-fixture", "private"));
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var machine = new PolicyDocument
        {
            Platforms = ImmutableDictionary<string, PolicyProfile>.Empty.Add(platform, new PolicyProfile
            {
                Enforced =
                [
                    new PolicyRule
                    {
                        Id = "private-subtree",
                        Policy = PolicyOutcome.AlwaysDeny,
                        Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Path, Fact = PolicyFact.WorkingDirectory, Values = [root] }],
                    },
                ],
            }),
        };
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), machine, null, platform, "bash");
        foreach (var directory in new[] { root, Path.Combine(root, "child"), root + "-sibling" })
        {
            var facts = new CommandFacts(platform, "bash", "git", Path.GetFullPath("git"), "external", ["status"], directory);

            // Act.
            var decision = PolicyEvaluator.Evaluate(snapshot, facts);

            // Assert.
            Assert.Equal(directory == root + "-sibling", decision.IsAllowed);
            Assert.False(decision.RequiresApproval);
        }
    }
}
