namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Verifies the trusted startup role schema and the selected provider boundary.</summary>
public static class AgentRoleModelConfigurationTests
{
    private const string ProfileId = "11111111-1111-1111-1111-111111111111";
    private const string RolePath = "agents:roleModels:explorer:";

    /// <summary>All six roles may share one trusted profile and inherit its default reasoning.</summary>
    [Fact]
    public static void Load_AllRolesMayShareOneProfileAndOmittedReasoningUsesDefault()
    {
        var entries = new Dictionary<string, string?>();
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var path = "agents:roleModels:" + AgentRoleNames.GetName(role) + ":";
            entries.Add(path + "providerId", "trusted-provider");
            entries.Add(path + "profileId", ProfileId);
        }

        var policy = AgentRoleModelConfiguration.Load(Build(entries), CreateCatalog());

        Assert.Equal(6, policy.RoleModels.Count);
        Assert.All(policy.RoleModels.Values, route =>
        {
            Assert.Equal(ReasoningLevel.Medium, route.ReasoningLevel);
            Assert.Equal("trusted-provider", route.ProviderId);
            Assert.Equal(Guid.Parse(ProfileId), route.ProfileId.Value);
        });
    }

    /// <summary>Malformed and unknown routing fields fail startup without exposing configured values.</summary>
    [Theory]
    [InlineData("providerId", null)]
    [InlineData("providerId", "")]
    [InlineData("providerId", "unknown-provider")]
    [InlineData("profileId", null)]
    [InlineData("profileId", "bad-profile")]
    [InlineData("profileId", "{11111111-1111-1111-1111-111111111111}")]
    [InlineData("profileId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("profileId", "22222222-2222-2222-2222-222222222222")]
    [InlineData("reasoningLevel", "")]
    [InlineData("reasoningLevel", null)]
    [InlineData("reasoningLevel", "minimal")]
    [InlineData("reasoningLevel", "unknown")]
    [InlineData("reasoningLevel", "3")]
    [InlineData("reasoningLevel", "Low, High")]
    [InlineData("reasoningLevel", " medium ")]
    [InlineData("endpoint", "https://private-value.example/credential")]
    [InlineData("providerId:0", "private-value")]
    [InlineData("profileId:extra", "private-value")]
    [InlineData("reasoningLevel:0", "medium")]
    public static void Load_InvalidFieldsFailWithSanitizedError(string field, string? value)
    {
        var entries = CreateEntries();
        entries[RolePath + field] = value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentRoleModelConfiguration.Load(Build(entries), CreateCatalog()));

        Assert.Contains("agents:roleModels", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Only exact host-owned role names are accepted.</summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("Explorer")]
    [InlineData("0")]
    [InlineData("explorer,implementer")]
    public static void Load_UnknownRoleNamesFail(string role)
    {
        var entries = new Dictionary<string, string?>
        {
            ["agents:roleModels:" + role + ":providerId"] = "trusted-provider",
            ["agents:roleModels:" + role + ":profileId"] = ProfileId,
        };

        Assert.Throws<InvalidOperationException>(() => AgentRoleModelConfiguration.Load(Build(entries), CreateCatalog()));
    }

    /// <summary>Legacy role profile configuration is rejected even when empty.</summary>
    [Theory]
    [InlineData("agents:roleProfiles", null)]
    [InlineData("agents:roleProfiles", "legacy")]
    [InlineData("agents:roleProfiles:explorer", ProfileId)]
    public static void Load_LegacyRoleProfilesAlwaysRejected(string key, string? value)
    {
        var entries = new Dictionary<string, string?> { [key] = value };

        var exception = Assert.Throws<InvalidOperationException>(() => AgentRoleModelConfiguration.Load(Build(entries), CreateCatalog()));

        Assert.Contains("use agents:roleModels", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Role collections and entries must be objects rather than scalar values.</summary>
    [Theory]
    [InlineData("agents:roleModels")]
    [InlineData("agents:roleModels:explorer")]
    public static void Load_ScalarObjectFails(string path)
    {
        var entries = CreateEntries();
        entries[path] = "invalid-scalar";

        Assert.Throws<InvalidOperationException>(() => AgentRoleModelConfiguration.Load(Build(entries), CreateCatalog()));
    }

    /// <summary>Disabled providers and disabled profiles cannot satisfy trusted role bindings.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public static void Load_DisabledProviderOrProfileFails(bool providerEnabled, bool modelEnabled)
    {
        Assert.Throws<InvalidOperationException>(() => AgentRoleModelConfiguration.Load(
            Build(CreateEntries()), CreateCatalog(providerEnabled, modelEnabled)));
    }

    /// <summary>Repository-only catalog entries cannot be promoted into trusted role configuration.</summary>
    [Fact]
    public static void Load_RepositoryOnlyProfileCannotSatisfyTrustedRole()
    {
        var effective = CreateCatalog();

        Assert.Throws<InvalidOperationException>(() => AgentRoleModelConfiguration.Load(
            Build(CreateEntries()), trustedCatalog: null, providerCatalog: effective));
    }

    /// <summary>Absent role configuration preserves the ordinary inherited selection behavior.</summary>
    [Fact]
    public static void Load_EmptyConfigurationPreservesNoRoleOverride()
    {
        var policy = AgentRoleModelConfiguration.Load(Build([]), trustedCatalog: null);

        Assert.Empty(policy.RoleModels);
        Assert.Null(policy.Get(AgentRole.Explorer));
    }

    /// <summary>Bound role preferences are immutable snapshots with canonical trusted provider identities.</summary>
    [Fact]
    public static void Load_SnapshotsConfigurationAndNormalizesTrustedProviderIdentity()
    {
        var entries = CreateEntries();
        entries[RolePath + "providerId"] = "TRUSTED-PROVIDER";
        entries[RolePath + "reasoningLevel"] = "hIgH";
        var configuration = Build(entries);
        var policy = AgentRoleModelConfiguration.Load(configuration, CreateCatalog());

        configuration[RolePath + "reasoningLevel"] = "none";

        var route = Assert.IsType<AgentRoleModelPreference>(policy.Get(AgentRole.Explorer));
        Assert.Equal(ReasoningLevel.High, route.ReasoningLevel);
        Assert.Equal("trusted-provider", route.ProviderId);
    }

    /// <summary>Selections marked trusted dispatch only through the repository-excluding provider.</summary>
    [Fact]
    public static void ModelServices_TrustedSelectionUsesTrustedDispatcher()
    {
        var ordinary = new FakeModelProvider(new ScriptedSession());
        var trusted = new FakeModelProvider(new ScriptedSession());
        using var services = new ModelServices(
            new HttpClient(),
            new ConfiguredModelCatalog([]),
            ordinary,
            startupProfile: null,
            preferredProfileId: null,
            "test",
            new SessionModelPreferences(),
            activeModels: null,
            trustedProvider: trusted);
        var selection = new AgentModelSelection(new ModelProfileId(Guid.Parse(ProfileId)), ReasoningLevel.High, [])
        {
            ContextWindowTokens = 8_192,
            MaximumOutputTokens = 1_024,
            OutputReserveTokens = 1_024,
            Provenance = new AgentModelProvenance
            {
                Source = AgentModelSelectionSource.RoleConfiguration,
                EffectiveProviderId = "trusted-provider",
                EffectiveProfileId = new ModelProfileId(Guid.Parse(ProfileId)),
                EffectiveReasoningLevel = nameof(ReasoningLevel.High),
                UsesTrustedCatalog = true,
            },
        };

        Assert.Same(trusted, services.ResolveAgentProvider(selection));
        Assert.Same(ordinary, services.ResolveAgentProvider(selection with { Provenance = null }));
    }

    private static IConfigurationRoot Build(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    private static Dictionary<string, string?> CreateEntries()
    {
        return new Dictionary<string, string?>
        {
            [RolePath + "providerId"] = "trusted-provider",
            [RolePath + "profileId"] = ProfileId,
        };
    }

    private static EffectiveModelProviderCatalog CreateCatalog(bool providerEnabled = true, bool modelEnabled = true)
    {
        return new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration
            {
                Providers =
                [
                    new OpenAiCompatibleProviderConfiguration
                    {
                        Id = "trusted-provider",
                        Name = "Trusted Provider",
                        Enabled = providerEnabled,
                        BaseUri = new Uri("https://trusted.example/v1"),
                        Models =
                        [
                            new OpenAiCompatibleModelConfiguration
                            {
                                Id = new ModelProfileId(Guid.Parse(ProfileId)),
                                Name = "Test Model",
                                ModelId = "model",
                                Enabled = modelEnabled,
                                ContextWindow = 8_192,
                                MaximumOutputTokens = 1_024,
                                DefaultReasoningLevel = ReasoningLevel.Medium,
                                SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Medium, ReasoningLevel.High],
                            },
                        ],
                    },
                ],
            },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]));
    }
}
