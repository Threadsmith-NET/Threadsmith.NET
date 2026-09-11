namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Interaction.Agents;
using Xunit;

/// <summary>Regression checks for agent name configuration tests.</summary>
public static class AgentNameConfigurationTests
{
    /// <summary>Verifies higher provider replaces whole list and shared names do not mask explicit roles.</summary>
    [Fact]
    public static void HigherProviderReplacesWholeListAndSharedNamesDoNotMaskExplicitRoles()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tui:agentNames:defaultNames:0"] = "Shared",
                ["tui:agentNames:byRole:explorer:0"] = "Old",
                ["tui:agentNames:byRole:explorer:1"] = "Unwanted tail",
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tui:agentNames:byRole:explorer:0"] = "New",
            }).Build();
        var result = AgentNameConfiguration.Load(config);
        Assert.Equal(["New"], result.Catalog.GetNames(AgentRole.Explorer));
        Assert.Equal(["Shared"], result.Catalog.GetNames(AgentRole.TestReviewer));
    }

    /// <summary>Verifies invalid role list falls back to shared without echoing values.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("\u001b[31msecret")]
    [InlineData("line\nbreak")]
    public static void InvalidRoleListFallsBackToSharedWithoutEchoingValues(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:agentNames:defaultNames:0"] = "Shared",
            ["tui:agentNames:byRole:explorer:0"] = value,
            ["tui:agentNames:byRole:unknown:0"] = "secret-name",
        }).Build();
        var result = AgentNameConfiguration.Load(config);
        Assert.Equal(["Shared"], result.Catalog.GetNames(AgentRole.Explorer));
        Assert.NotEmpty(result.Warnings);
        Assert.DoesNotContain("secret", string.Join(" ", result.Warnings), StringComparison.Ordinal);
    }

    /// <summary>Constructs malformed UTF-16 at runtime so attribute metadata does not replace it.</summary>
    [Fact]
    public static void MalformedUnicodeFallsBack() => InvalidRoleListFallsBackToSharedWithoutEchoingValues(new string((char)0xd800, 1));

    /// <summary>The shipped commented JSON contains exactly the compiled defaults for every public role.</summary>
    [Fact]
    public static void ShippedExampleMatchesCompiledDefaults()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".threadsmith", "config.example")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var stream = File.OpenRead(Path.Combine(directory.FullName, ".threadsmith", "config.example"));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var loaded = AgentNameConfiguration.Load(configuration);
        var defaults = new AgentNameCatalog();
        Assert.Empty(loaded.Warnings);
        Assert.All(Enum.GetValues<AgentRole>(), role => Assert.Equal(defaults.GetNames(role), loaded.Catalog.GetNames(role)));
    }

    /// <summary>Verifies all roles have distinct themed defaults and excess names are bounded.</summary>
    [Fact]
    public static void AllRolesHaveDistinctThemedDefaultsAndExcessNamesAreBounded()
    {
        var catalog = new AgentNameCatalog();
        Assert.All(Enum.GetValues<AgentRole>(), role => Assert.Equal(6, catalog.GetNames(role).Count));
        Assert.Contains("Shackleton", catalog.GetNames(AgentRole.Explorer));
        Assert.Contains("Hoare", catalog.GetNames(AgentRole.TestReviewer));
        var configured = new AgentNameCatalog(Enumerable.Range(0, 129).Select(index => "Person" + index).ToArray());
        Assert.Equal(128, configured.GetNames(AgentRole.Explorer).Count);
    }
}
