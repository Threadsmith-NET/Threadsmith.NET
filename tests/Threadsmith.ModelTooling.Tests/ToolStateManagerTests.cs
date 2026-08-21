namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies repository-scoped plan-27 tool availability state.</summary>
public static class ToolStateManagerTests
{
    /// <summary>An explicitly empty enabled array remains a fail-closed allowlist.</summary>
    [Fact]
    public static async Task EmptyEnabledAllowList_DisablesNonEssentialTools()
    {
        var repository = CreateTemporaryDirectory();
        var configPath = Path.Combine(repository, ".threadsmith", "config.json");
        try
        {
            var configDirectory = Path.GetDirectoryName(configPath)
                ?? throw new InvalidOperationException("Test configuration path has no parent directory.");
            Directory.CreateDirectory(configDirectory);
            await File.WriteAllTextAsync(configPath, "{\"Tools\":{\"Enabled\":[]}}");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddJsonFile(configPath)
                .Build();
            ITool[] tools = [new TestTool("optional"), new TestTool("essential", essential: true)];

            var state = new ToolStateManager(tools.Select(tool => tool.Definition), configuration, configPath);

            Assert.False(state.IsEnabled("optional"));
            Assert.True(state.IsEnabled("essential"));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Repository switching reloads state and redirects subsequent persistence.</summary>
    [Fact]
    public static async Task BindRepositoryAsync_ReloadsStateAndPersistenceTarget()
    {
        var firstRepository = CreateTemporaryDirectory();
        var secondRepository = CreateTemporaryDirectory();
        var firstConfig = Path.Combine(firstRepository, ".threadsmith", "config.json");
        var secondConfig = Path.Combine(secondRepository, ".threadsmith", "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(firstConfig)
                ?? throw new InvalidOperationException("Test configuration path has no parent directory."));
            Directory.CreateDirectory(Path.GetDirectoryName(secondConfig)
                ?? throw new InvalidOperationException("Test configuration path has no parent directory."));
            await File.WriteAllTextAsync(firstConfig, "{\"tools\":{\"disabled\":[\"optional\"]}}");
            await File.WriteAllTextAsync(secondConfig, "{}");
            IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(firstConfig).Build();
            ITool[] tools = [new TestTool("optional")];
            var state = new ToolStateManager(tools.Select(tool => tool.Definition), configuration, firstConfig);
            Assert.False(state.IsEnabled("optional"));

            await state.BindRepositoryAsync(secondRepository);
            Assert.True(state.IsEnabled("optional"));
            await state.DisableAsync("optional");

            using var first = JsonDocument.Parse(await File.ReadAllTextAsync(firstConfig));
            using var second = JsonDocument.Parse(await File.ReadAllTextAsync(secondConfig));
            Assert.Equal(
                "optional",
                Assert.Single(first.RootElement.GetProperty("tools").GetProperty("disabled").EnumerateArray()).GetString());
            Assert.Equal(
                "optional",
                Assert.Single(second.RootElement.GetProperty("tools").GetProperty("disabled").EnumerateArray()).GetString());
        }
        finally
        {
            Directory.Delete(firstRepository, recursive: true);
            Directory.Delete(secondRepository, recursive: true);
        }
    }

    /// <summary>Disabled entries override enabled entries while essential tools remain available.</summary>
    [Fact]
    public static void ConfigurationPrecedence_IsFailClosedAndProtectsEssentialTools()
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tools:enabled:0"] = "optional",
                    ["tools:enabled:1"] = "essential",
                    ["tools:disabled:0"] = "optional",
                    ["tools:disabled:1"] = "essential",
                })
                .Build();
            ITool[] tools = [new TestTool("optional"), new TestTool("essential", essential: true)];
            var state = new ToolStateManager(
                tools.Select(tool => tool.Definition),
                configuration,
                Path.Combine(repository, ".threadsmith", "config.json"));
            var registry = new ToolRegistry(tools, state);

            Assert.False(state.IsEnabled("optional"));
            Assert.True(state.IsEnabled("essential"));
            Assert.Single(registry.Definitions);
            Assert.Equal(2, registry.AllDefinitions.Count);
            Assert.Throws<KeyNotFoundException>(() => registry.Get("optional"));
            Assert.Equal("essential", registry.Get("essential").Definition.Id);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>State changes persist without removing unrelated repository configuration.</summary>
    [Fact]
    public static async Task ToggleAsync_PersistsAndRoundTripsRepositoryState()
    {
        var repository = CreateTemporaryDirectory();
        var configPath = Path.Combine(repository, ".threadsmith", "config.json");
        try
        {
            var configDirectory = Path.GetDirectoryName(configPath)
                ?? throw new InvalidOperationException("Test configuration path has no parent directory.");
            Directory.CreateDirectory(configDirectory);
            await File.WriteAllTextAsync(configPath, "{\"unrelated\":{\"value\":true}}");
            IConfiguration configuration = new ConfigurationBuilder().Build();
            ITool[] tools = [new TestTool("optional"), new TestTool("essential", essential: true)];
            var state = new ToolStateManager(tools.Select(tool => tool.Definition), configuration, configPath);

            await state.DisableAsync("optional");
            await state.DisableAsync("essential");

            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            Assert.True(persisted.RootElement.GetProperty("unrelated").GetProperty("value").GetBoolean());
            Assert.Equal(
                "optional",
                Assert.Single(persisted.RootElement.GetProperty("tools").GetProperty("disabled").EnumerateArray()).GetString());
            Assert.False(persisted.RootElement.GetProperty("tools").TryGetProperty("enabled", out _));

            IConfiguration reloaded = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tools:disabled:0"] = "optional",
                })
                .Build();
            var restored = new ToolStateManager(tools.Select(tool => tool.Definition), reloaded, configPath);
            Assert.False(restored.IsEnabled("optional"));
            Assert.True(restored.IsEnabled("essential"));

            await restored.EnableAsync("optional");
            Assert.True(restored.IsEnabled("optional"));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Persistence accepts the same comments and trailing commas as repository configuration loading.</summary>
    [Fact]
    public static async Task ToggleAsync_PersistsJsonWithCommentsAndTrailingCommas()
    {
        var repository = CreateTemporaryDirectory();
        var configPath = Path.Combine(repository, ".threadsmith", "config.json");
        try
        {
            var configDirectory = Path.GetDirectoryName(configPath)
                ?? throw new InvalidOperationException("Test configuration path has no parent directory.");
            Directory.CreateDirectory(configDirectory);
            await File.WriteAllTextAsync(
                configPath,
                """
                {
                  // Repository configuration permits comments.
                  "unrelated": true,
                  "tools": {
                    "enabled": ["optional",],
                  },
                }
                """);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddJsonFile(configPath)
                .Build();
            ITool[] tools = [new TestTool("optional")];
            var state = new ToolStateManager(tools.Select(tool => tool.Definition), configuration, configPath);

            await state.DisableAsync("optional");

            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            Assert.True(persisted.RootElement.GetProperty("unrelated").GetBoolean());
            Assert.Empty(persisted.RootElement.GetProperty("tools").GetProperty("enabled").EnumerateArray());
            Assert.Equal(
                "optional",
                Assert.Single(persisted.RootElement.GetProperty("tools").GetProperty("disabled").EnumerateArray()).GetString());
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>A failed durable write does not change the effective in-memory state.</summary>
    [Fact]
    public static async Task DisableAsync_WhenPersistenceFails_PreservesEnabledState()
    {
        var repository = CreateTemporaryDirectory();
        var configPath = Path.Combine(repository, ".threadsmith", "config.json");
        try
        {
            var configDirectory = Path.GetDirectoryName(configPath)
                ?? throw new InvalidOperationException("Test configuration path has no parent directory.");
            Directory.CreateDirectory(configDirectory);
            await File.WriteAllTextAsync(configPath, "{}");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddJsonFile(configPath)
                .Build();
            ITool[] tools = [new TestTool("optional")];
            var state = new ToolStateManager(tools.Select(tool => tool.Definition), configuration, configPath);
            await File.WriteAllTextAsync(configPath, "{ malformed");

            await Assert.ThrowsAnyAsync<JsonException>(() => state.DisableAsync("optional"));

            Assert.True(state.IsEnabled("optional"));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "threadsmith-tool-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestTool : Tool<TestInput, TestOutput>
    {
        private readonly ToolDefinition _definition;

        internal TestTool(string id, bool essential = false)
        {
            _definition = new ToolDefinition
            {
                Id = id,
                DisplayName = id,
                Source = "Built-in",
                Essential = essential,
                Version = "1.0",
                Description = id,
                InputSchema = new ToolSchema(nameof(TestInput), 1, "{\"type\":\"object\"}"),
                OutputSchema = new ToolSchema(nameof(TestOutput), 1, "{\"type\":\"object\"}"),
                Timeout = TimeSpan.FromSeconds(1),
                MaximumOutputBytes = 1024,
            };
        }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<TestOutput>> ExecuteAsync(
            TestInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ToolExecution<TestOutput>(new TestOutput(), [], false));
        }

        protected override void ValidateInput(TestInput input)
        {
        }
    }

    private sealed record TestInput;

    private sealed record TestOutput;
}
