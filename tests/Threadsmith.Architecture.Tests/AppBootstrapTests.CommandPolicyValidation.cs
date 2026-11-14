namespace Threadsmith.Architecture.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks isolated validator production registration and default availability.</summary>
public static partial class AppBootstrapTests
{
    /// <summary>Uses the actual host composition and state manager, not a standalone registry.</summary>
    [Fact]
    public static async Task CommandPolicyValidatorIsRegisteredButNormallyDisabled()
    {
        using var temporary = new TemporaryDirectory("policy-validator-composition");
        var paths = CreatePaths(temporary.Root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["persistence:path"] = temporary.GetPath("host.db"),
        }).Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        try
        {
            await using var foundation = await HostFoundation.CreateAsync(configuration, configuration, paths, loggerFactory, TestPromptLoader.Instance, cancellationToken: TestContext.Current.CancellationToken);

            var definition = Assert.Single(foundation.ToolRegistry.AllDefinitions, item => item.Id == "validate_command_policy");
            Assert.Equal(ToolCategory.NativeSkillTools, definition.Category);
            Assert.False(definition.EnabledByDefault);
            Assert.False(definition.Essential);
            Assert.Equal(ToolSideEffect.ReadOnly, definition.SideEffect);
            Assert.Equal(TestPromptLoader.Instance.Get(PromptFileNames.ToolValidateCommandPolicyDescription), definition.Description);
            Assert.False(foundation.ToolStateManager.IsEnabled(definition.Id));
            Assert.DoesNotContain(foundation.ToolRegistry.Definitions, item => item.Id == definition.Id);
            Assert.Throws<KeyNotFoundException>(() => foundation.ToolRegistry.GetRegistration(definition.Id));
            Assert.Equal("Threadsmith.Tools", PromptAssetCatalog.Get(PromptFileNames.ToolValidateCommandPolicyDescription).Owner);
        }
        finally
        {
            // The production stores use pooling. Release only pools for this owned fixture,
            // after host disposal has joined background owners; never clear other tests' pools.
            foreach (var database in Directory.EnumerateFiles(temporary.Root, "*.db", SearchOption.AllDirectories))
            {
                await using var connection = new SqliteConnection($"Data Source={database}");
                SqliteConnection.ClearPool(connection);
            }
        }
    }
}
