namespace Threadsmith.Architecture.Tests;

using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Mcp;
using Threadsmith.Persistence;
using Threadsmith.Skills;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    private (RepositoryScopedBindingCoordinator Coordinator, MutationApprovalPolicyService Approval, SessionPolicyBinding Binding) CreateStartupBinding(SqliteRepositoryFactsStore facts, StartupMcpManager manager)
    {
        var options = CommandLineParser.Parse(["--tui", "--trust", "FullyTrustedAutomation"]).Options!;
        var configuration = ConfigurationBootstrap.Build([], _paths);
        var binding = new SessionPolicyBinding();
        binding.PrepareRepositoryTrust(options.RequestedTrust!.Value);
        binding.Publish(Load());
        var approval = new MutationApprovalPolicyService(configuration, _paths.RepositoryConfiguration, binding, facts);
        var tools = new ToolStateManager([], configuration, _paths.RepositoryConfiguration, userConsentPath: Path.Combine(_root, "outbound.json"), mcpApprovalPath: Path.Combine(_root, "mcp.json"), policyBinding: binding);
        var coordinator = new RepositoryScopedBindingCoordinator(_paths.RepositoryRoot, tools, null, approval, new RepositorySecretProvider(_paths.RepositoryRoot, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), manager, policyBinding: binding, configurationPaths: root => root == _paths.RepositoryRoot ? _paths : throw new InvalidOperationException("Unexpected fixture root"));
        return (coordinator, approval, binding);
    }

    private sealed class StartupMcpManager : IMcpManager
    {
        public Exception? Failure { get; set; }

        public Exception? DeliveredFailure { get; private set; }

        public int BindCount { get; private set; }

        public Task AutoConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RebindRepositoryAsync(CancellationToken cancellationToken = default)
        {
            BindCount++;
            if (Failure is { } failure)
            {
                Failure = null;
                DeliveredFailure = failure;
                return Task.FromException(failure);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<McpManagementResult> ExecuteAsync(McpManagementRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
