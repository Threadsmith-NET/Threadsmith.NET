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

    /// <summary>The real settings adapter publishes first-time grants without dropping unrelated user settings.</summary>
    [Theory]
    [InlineData(false, false, "unknown")]
    [InlineData(true, false, "unknown")]
    [InlineData(true, true, "unknown")]
    [InlineData(false, true, "null")]
    [InlineData(false, true, "parent")]
    public async Task CommandPolicyPublicationRevocationAndInvalidSourcesShareBindingOwnership(bool bom, bool corruptOnGrant, string invalidShape)
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_paths.UserConfiguration, """{"fixture":{"keep":"unchanged"}}""", new UTF8Encoding(bom), ct);
        var facts = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "command-policy.db")};Pooling=False", _paths.UserConfiguration);
        await facts.InitializeAsync(ct);
        var (coordinator, _, binding) = CreateStartupBinding(facts, new StartupMcpManager());
        await coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        var service = new PolicyConfigurationService(coordinator);
        var captured = binding.Current;
        var snapshot = captured.CommandPolicies.Values.First();
        var grant = new PolicyGrant("fixture-grant", PolicyJson.Fingerprint("context"), PolicyJson.Fingerprint("rules"));
        var request = new PolicyUpdateRequest(PolicyDestination.User, snapshot.Platform, snapshot.Shell, snapshot.ConfigurationRevision, PolicyUpdateOperation.Grant, grant.Id, Grant: grant);

        var receipt = await service.UpdateAsync(request, ct);

        Assert.True(receipt.DiskPublished);
        Assert.True(receipt.Activated, receipt.ReasonCode);
        Assert.Throws<UnauthorizedAccessException>(() => binding.Validate(captured));
        var current = binding.Current.CommandPolicies[snapshot.Shell];
        Assert.Single(current.AlwaysAllow);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(_paths.UserConfiguration, ct))!.AsObject();
        Assert.Equal("unchanged", root["fixture"]?["keep"]?.GetValue<string>());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdateAsync(request, ct));
        var revoked = await service.UpdateAsync(request with { ExpectedRevision = current.ConfigurationRevision, Operation = PolicyUpdateOperation.Revoke, Grant = null }, ct);
        Assert.True(revoked.Activated, revoked.ReasonCode);
        Assert.Empty(binding.Current.CommandPolicies[snapshot.Shell].AlwaysAllow);

        root = JsonNode.Parse(await File.ReadAllTextAsync(_paths.UserConfiguration, ct))!.AsObject();
        if (invalidShape == "parent")
        {
            root["tools"] = "invalid";
        }
        else
        {
            root["tools"]!["runProcess"]!["commandPolicy"] = invalidShape == "null" ? null : new JsonObject { ["unknown"] = true };
        }

        await File.WriteAllTextAsync(_paths.UserConfiguration, root.ToJsonString(), ct);
        if (corruptOnGrant)
        {
            var failure = await Record.ExceptionAsync(() => service.UpdateAsync(request, ct));
            Assert.True(failure is JsonException or InvalidDataException);
        }
        else
        {
            var failed = await service.UpdateAsync(request with { Operation = PolicyUpdateOperation.Reload, Grant = null }, ct);
            Assert.False(failed.DiskPublished);
            Assert.False(failed.Activated);
        }

        Assert.Throws<UnauthorizedAccessException>(() => binding.Current);

        // Correcting the source and retrying manual reload uses the existing full rebind,
        // not stale executable authority or a retained comparison snapshot.
        await File.WriteAllTextAsync(_paths.UserConfiguration, "{}", ct);
        var recovered = await service.UpdateAsync(request with { Operation = PolicyUpdateOperation.Reload, Grant = null }, ct);
        Assert.False(recovered.DiskPublished);
        Assert.True(recovered.Activated, recovered.ReasonCode);
        Assert.NotEmpty(binding.Current.CommandPolicies);
        Assert.Throws<UnauthorizedAccessException>(() => binding.Validate(captured));
    }

    /// <summary>Unrelated trusted settings above the repository byte limit do not block grant updates.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeTrustedSettingsSupportCommandGrantPublicationAndRevocation(bool machine)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = machine ? _paths.MachineConfiguration : _paths.UserConfiguration;
        var unrelated = new string('x', (1024 * 1024) + 1);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { fixture = unrelated }), new UTF8Encoding(true), ct);
        var facts = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "large-command-policy.db")};Pooling=False", _paths.UserConfiguration);
        await facts.InitializeAsync(ct);
        var (coordinator, _, binding) = CreateStartupBinding(facts, new StartupMcpManager());
        await coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        var service = new PolicyConfigurationService(coordinator);
        var snapshot = binding.Current.CommandPolicies.Values.First();
        var grant = new PolicyGrant("large-settings-grant", PolicyJson.Fingerprint("context"), PolicyJson.Fingerprint("rules"));
        var request = new PolicyUpdateRequest(PolicyDestination.User, snapshot.Platform, snapshot.Shell, snapshot.ConfigurationRevision, PolicyUpdateOperation.Grant, grant.Id, Grant: grant);

        var saved = await service.UpdateAsync(request, ct);

        Assert.True(saved.DiskPublished);
        Assert.True(saved.Activated, saved.ReasonCode);
        var current = binding.Current.CommandPolicies[snapshot.Shell];
        Assert.Equal(grant, Assert.Single(current.AlwaysAllow));
        Assert.Equal(unrelated, JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!["fixture"]!.GetValue<string>());

        var revoked = await service.UpdateAsync(request with { ExpectedRevision = current.ConfigurationRevision, Operation = PolicyUpdateOperation.Revoke, Grant = null }, ct);

        Assert.True(revoked.DiskPublished);
        Assert.True(revoked.Activated, revoked.ReasonCode);
        Assert.Empty(binding.Current.CommandPolicies[snapshot.Shell].AlwaysAllow);
        Assert.Equal(unrelated, JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!["fixture"]!.GetValue<string>());
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
