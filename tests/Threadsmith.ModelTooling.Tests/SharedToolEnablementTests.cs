namespace Threadsmith.ModelTooling.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Every registered source shares preference persistence independently of readiness and authority.</summary>
public static class SharedToolEnablementTests
{
    /// <summary>Registered sources share toggles without changing service readiness or policy.</summary>
    [Theory]
    [InlineData("Built-in", false)]
    [InlineData("Extension:fixture", false)]
    [InlineData("MCP:fixture", false)]
    [InlineData("Built-in", true)]
    public static async Task TogglePersistsAndRefreshesEveryRegistryEntryPoint(string source, bool feature)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "shared-tool-toggle-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, ".threadsmith", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var tool = new FixtureTool(source);
            var binding = new SessionPolicyBinding();
            binding.Publish(new EffectiveSessionPolicy { BindingId = Guid.NewGuid(), Generation = 1, RepositoryPath = root, Fingerprint = "test", AllowedTools = [] });
            var approvals = Path.Combine(root, "approvals.json");
            var state = new ToolStateManager([], new ConfigurationBuilder().Build(), path, mcpApprovalPath: approvals, policyBinding: binding);
            var registry = new ToolRegistry([], state);
            registry.RegisterOrReplace(tool, new ToolActivitySource(ToolActivitySourceKind.Extension, "fixture"));
            if (feature)
            {
                state.RegisterFeature(tool.Definition.Id, tool);
            }

            var session = SessionId.New();
            var run = RunId.New();
            var context = new ToolInvocationContext { RepositoryPath = root, RequestedBy = "fixture", TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation };
            Assert.Empty(registry.GetRegistrations(session, run, context));
            await state.EnableAsync(tool.Definition.Id, token);
            Assert.True(state.IsEnabled(tool.Definition.Id));
            Assert.Single(registry.Definitions);
            Assert.Single(registry.GetDefinitions(session, run));
            Assert.Single(registry.GetRegistrations(session, run, context));
            Assert.Equal(tool.Definition.Id, registry.GetRegistration(tool.Definition.Id, context).Tool.Definition.Id);
            Assert.Equal("policy_denied", Assert.Single(state.GetAllStates()).AvailabilityReasonCode);
            Assert.False(binding.Current.AllowsTool(tool.Definition.Id));
            Assert.False(tool.Ready); // A tool checkbox must not start or enable a feature service.
            var reload = new ToolStateManager([tool.Definition], new ConfigurationBuilder().AddJsonFile(path).Build(), path, mcpApprovalPath: approvals);
            Assert.True(reload.IsEnabled(tool.Definition.Id));
            await state.DisableAsync(tool.Definition.Id, token);
            Assert.Empty(registry.Definitions);
            Assert.Empty(registry.GetDefinitions(session, run));
            Assert.Empty(registry.GetRegistrations(session, run, context));
            Assert.Throws<KeyNotFoundException>(() => registry.Get(tool.Definition.Id));
            Assert.Throws<KeyNotFoundException>(() => registry.GetRegistration(tool.Definition.Id, context));
            await state.BindRepositoryAsync(root, token);
            Assert.False(state.IsEnabled(tool.Definition.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixtureTool : Tool<Dictionary<string, string>, string>, IRepositoryFeatureTool
    {
        public FixtureTool(string source)
        {
            Definition = ToolDefinitionFactory.Create<Dictionary<string, string>, string>("fixture", "Synthetic fixture", ToolCategory.Workflow, RepositoryTrustLevel.UntrustedInspection, ApprovalLevel.None, ToolSideEffect.ReadOnly, TimeSpan.FromSeconds(10), 1024) with { EnabledByDefault = false, Source = source };
        }

        public bool Ready { get; init; }

        public override ToolDefinition Definition { get; }

        public RepositoryIntelligenceAvailability CaptureAvailability(string repositoryRoot) => new(Ready, "not_ready", "Synthetic feature is not ready.");

        public override Task<ToolExecution<string>> ExecuteAsync(Dictionary<string, string> input, ToolExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(new ToolExecution<string>("done", []));

        protected override void ValidateInput(Dictionary<string, string> input)
        {
        }
    }
}
