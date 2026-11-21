namespace Threadsmith.Architecture.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Persistence;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Xunit;

/// <summary>Checks command authority at its real source-admission and serialization boundaries.</summary>
public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Machine and user policy subtrees are admitted without a repository authority fallback.</summary>
    [Fact]
    public void CommandPolicyTrustedSourcesCompileAndRemainOutsideSerializedSessionPolicy()
    {
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"runProcess":{"commandPolicy":{"schemaVersion":1,"platforms":{}}}}}""");
        var policy = Load();
        Assert.NotEmpty(policy.CommandPolicies);
        var serialized = JsonSerializer.Serialize(policy with { AllowedTools = [], NetworkHostRestriction = [] });
        Assert.DoesNotContain("git-standard", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandPolicies", serialized, StringComparison.Ordinal);
    }

    /// <summary>Repository and session files cannot define even an otherwise valid command-policy document.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandPolicyRejectsUntrustedSource(bool session)
    {
        File.WriteAllText(session ? _paths.SessionConfiguration : _paths.RepositoryConfiguration, """{"tools":{"runProcess":{"commandPolicy":{"schemaVersion":1,"platforms":{}}}}}""");
        Assert.Throws<InvalidDataException>(Load);
    }

    /// <summary>The outer machine enforcement envelope cannot silently ignore command policy.</summary>
    [Fact]
    public void CommandPolicyRejectsOuterEnforcementEnvelope()
    {
        File.WriteAllText(_paths.MachineConfiguration, """{"enforced":{"tools":{"runProcess":{"commandPolicy":{"schemaVersion":1,"platforms":{}}}}}}""");
        Assert.Throws<InvalidDataException>(Load);
    }

    /// <summary>The command-policy dialect rejects obsolete list shortcuts.</summary>
    [Fact]
    public void CommandPolicyRejectsObsoleteModes()
    {
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"runProcess":{"commandPolicy":{"denyList":[]}}}}""");
        Assert.ThrowsAny<Exception>(Load);
    }
}
