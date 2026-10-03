namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Verifies effective routing assertions without provider completion or persistence.</summary>
public static class EffectiveConfigurationPreflightTests
{
    private const string ProfileId = "11111111-1111-1111-1111-111111111111";

    /// <summary>Checks effective routing and rejects incorrect or malformed expectations.</summary>
    [Theory]
    [InlineData("reasoningLevel", "High", 0)]
    [InlineData("reasoningLevel", "Medium", 2)]
    [InlineData("providerId", "trusted-provider", 0)]
    [InlineData("providerId", "other", 2)]
    [InlineData("profileId", ProfileId, 0)]
    [InlineData("profileId", "bad", 2)]
    [InlineData("source", "RoleConfiguration", 0)]
    [InlineData("source", "Inherited", 2)]
    [InlineData("reasoning", "High", 2)]
    [InlineData("REASONINGLEVEL", "High", 0, "ROLES", "EXPECT")]
    [InlineData("ReasoningLevel", "High", 0, "Roles", "Expect")]
    [InlineData("REASONINGLEVEL", "Medium", 2, "ROLES", "EXPECT")]
    public static async Task RunAsync_ReportsEffectiveAuthorityAndChecksExpectations(
        string field,
        string expected,
        int exitCode,
        string rolesKey = "roles",
        string expectKey = "expect")
    {
        var catalog = CreateCatalog();
        var id = new ModelProfileId(Guid.Parse(ProfileId));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json");
        var parent = new ActiveModelSelectionService(catalog, new SessionModelPreferences(id, ReasoningLevel.Medium), path);
        var roles = new AgentRoleModelPolicy(
            catalog,
            [new AgentRoleModelPreference(AgentRole.Explorer, "trusted-provider", id, ReasoningLevel.High)],
            catalog);
        var selector = new AgentModelSelector(catalog.ModelCatalog, new DefaultModelSelectionPolicy(catalog.ModelCatalog), roleModels: roles);
        var preflight = new EffectiveConfigurationPreflight(parent, selector, AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"headless:preflight:{rolesKey}"] = "explorer,bugReviewer",
            [$"headless:preflight:{expectKey}:explorer:{field}"] = expected,
        }).Build();
        await using var output = new StringWriter();

        var exit = await preflight.RunAsync(configuration, output, TestContext.Current.CancellationToken);

        Assert.Equal(exitCode, exit);
        var report = JsonSerializer.Deserialize<EffectiveConfigurationReport>(output.ToString())!;
        Assert.Equal(exitCode == 0, report.Passed);
        var parentRow = Assert.Single(report.Selections, item => item.Target == "parent");
        Assert.Equal("Medium", parentRow.ReasoningLevel);
        Assert.Equal("UserDefault", parentRow.Source);
        var child = Assert.Single(report.Selections, item => item.Target == "explorer");
        Assert.Equal("High", child.ReasoningLevel);
        Assert.Equal("RoleConfiguration", child.Source);
        Assert.True(child.UsesTrustedCatalog);
        Assert.Equal("Inherited", Assert.Single(report.Selections, item => item.Target == "bugReviewer").Source);
        Assert.False(File.Exists(path));
        Assert.Equal(ReasoningLevel.Medium, parent.Current.ReasoningLevel);
    }

    /// <summary>Checks effective routing and rejects incorrect or malformed expectations.</summary>
    [Theory]
    [InlineData("headless:preflight:roles", "typo")]
    [InlineData("headless:preflight:expect:typo:source", "Inherited")]
    [InlineData("headless:preflight:expect:parent:reasoning", "Medium")]
    [InlineData("headless:preflight:expect", "invalid")]
    [InlineData("headless:preflight:roles:0", "explorer")]
    [InlineData("headless:preflight:unknown", "explorer")]
    [InlineData("headless:preflight:UNKNOWN", "explorer")]
    public static async Task RunAsync_MalformedConfigurationFailsClosed(string key, string value)
    {
        var catalog = CreateCatalog();
        var id = new ModelProfileId(Guid.Parse(ProfileId));
        var parent = new ActiveModelSelectionService(
            catalog,
            new SessionModelPreferences(id, ReasoningLevel.Medium),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"));
        var selector = new AgentModelSelector(catalog.ModelCatalog, new DefaultModelSelectionPolicy(catalog.ModelCatalog));
        var preflight = new EffectiveConfigurationPreflight(parent, selector, AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        await using var output = new StringWriter();

        Assert.True(EffectiveConfigurationPreflight.IsRequested(configuration));
        Assert.Equal(2, await preflight.RunAsync(configuration, output, TestContext.Current.CancellationToken));
    }

    /// <summary>Preserves active selection and terminal failure semantics.</summary>
    [Fact]
    public static async Task RunAsync_NoActiveModelAndCancellationDoNotDispatch()
    {
        var catalog = new ConfiguredModelCatalog([]);
        var preflight = new EffectiveConfigurationPreflight(
            null,
            new AgentModelSelector(catalog, new DefaultModelSelectionPolicy(catalog)),
            new AgentResourceBudget());
        var configuration = new ConfigurationBuilder().Build();
        await using var output = new StringWriter();
        Assert.Equal(2, await preflight.RunAsync(configuration, output, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preflight.RunAsync(configuration, output, cancellation.Token));
    }

    /// <summary>Preserves active selection and terminal failure semantics.</summary>
    [Fact]
    public static async Task RunAsync_ReportsRepositoryPreferenceInsteadOfGenericOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json");
            const string original = "{\"model\":{\"providerId\":\"trusted-provider\",\"profileId\":\"" + ProfileId + "\",\"reasoningLevel\":\"High\"}}";
            await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
            var catalog = CreateCatalog();
            var parent = new ActiveModelSelectionService(
                catalog,
                new SessionModelPreferences(new ModelProfileId(Guid.Parse(ProfileId)), ReasoningLevel.Medium),
                path);
            var selector = new AgentModelSelector(catalog.ModelCatalog, new DefaultModelSelectionPolicy(catalog.ModelCatalog));
            var preflight = new EffectiveConfigurationPreflight(parent, selector, new AgentResourceBudget());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["model:reasoningLevel"] = "Medium",
                ["headless:preflight:expect:parent:reasoningLevel"] = "Medium",
            }).Build();
            await using var output = new StringWriter();

            Assert.Equal(2, await preflight.RunAsync(configuration, output, TestContext.Current.CancellationToken));
            var report = JsonSerializer.Deserialize<EffectiveConfigurationReport>(output.ToString())!;
            Assert.Equal("Repository", Assert.Single(report.Selections).Source);
            Assert.Equal("High", Assert.Single(report.Selections).ReasoningLevel);
            Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
                                Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
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
