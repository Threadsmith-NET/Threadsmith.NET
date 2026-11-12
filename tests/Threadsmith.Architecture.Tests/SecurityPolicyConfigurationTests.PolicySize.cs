namespace Threadsmith.Architecture.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Rejected final serialized candidates leave valid authority and settings intact.</summary>
    [Fact]
    public async Task OversizedPersistedPolicyUpdateLeavesDiskAndAuthorityUnchanged()
    {
        // Compact input is valid; indentation at the final persistence boundary crosses the limit.
        var ct = TestContext.Current.CancellationToken;
        var document = new PolicyDocument
        {
            Common = new PolicyProfile
            {
                Rules =
                [
                    new PolicyRule
                    {
                        Id = "large-values",
                        Policy = PolicyOutcome.Allow,
                        Matchers = [new PolicyMatcher { Type = PolicyMatcherType.ValueSet, Fact = PolicyFact.ExecutableName, Values = [.. Enumerable.Repeat(new string('x', 490), 1024)] }],
                    },
                ],
            },
        };
        var compact = PolicyJson.Serialize(document);
        Assert.True(compact.Length < 512 * 1024);
        var root = new JsonObject { ["tools"] = new JsonObject { ["runProcess"] = new JsonObject { ["commandPolicy"] = JsonNode.Parse(compact) } } };
        var indented = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var persisted = ConfigurationDocument.Parse(Encoding.UTF8.GetBytes(indented))["tools:runProcess:commandPolicy"].GetRawText();
        Assert.True(persisted.Length > 512 * 1024);
        await File.WriteAllTextAsync(_paths.UserConfiguration, root.ToJsonString(), ct);
        var before = await File.ReadAllBytesAsync(_paths.UserConfiguration, ct);
        var facts = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "policy-size.db")};Pooling=False", _paths.UserConfiguration);
        await facts.InitializeAsync(ct);
        var (coordinator, _, binding) = CreateStartupBinding(facts, new StartupMcpManager());
        await coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        var captured = binding.Current;
        var snapshot = captured.CommandPolicies.Values.First();
        var grant = new PolicyGrant("size-grant", PolicyJson.Fingerprint("context"), PolicyJson.Fingerprint("rules"));
        var request = new PolicyUpdateRequest(PolicyDestination.User, snapshot.Platform, snapshot.Shell, snapshot.ConfigurationRevision, PolicyUpdateOperation.Grant, grant.Id, Grant: grant);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new PolicyConfigurationService(coordinator).UpdateAsync(request, ct));

        Assert.Equal("PolicyDocumentTooLarge", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(_paths.UserConfiguration, ct));
        Assert.Same(captured, binding.Current);
        binding.Validate(captured);
    }
}
