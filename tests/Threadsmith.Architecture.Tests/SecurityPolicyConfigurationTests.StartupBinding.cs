namespace Threadsmith.Architecture.Tests;

using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Mcp;
using Threadsmith.Persistence;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Startup silently ignores repository permission keys and retains only trusted local choices.</summary>
    [Theory]
    [InlineData("alwaysTrustRepo", false)]
    [InlineData("reviewRisky", false)]
    [InlineData("reviewAll", false)]
    [InlineData("alwaysTrustRepo", true)]
    [InlineData("reviewAll", true)]
    public async Task StartupBinding_RepositoryPolicyIsSilentAndLocalChoiceSurvivesRestart(string requested, bool saved)
    {
        var ct = TestContext.Current.CancellationToken;
        var original = $$$"""{"mutation":{"approvalPolicy":"{{{requested}}}"}}""";
        await File.WriteAllTextAsync(_paths.RepositoryConfiguration, original, ct);
        var facts = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "startup.db")};Pooling=False", _paths.UserConfiguration);
        await facts.InitializeAsync(ct);
        if (saved)
        {
            await facts.SetMutationPolicyAsync(_paths.RepositoryRoot, MutationApprovalPolicy.AlwaysTrustRepo, ct);
        }

        var (coordinator, approval, binding) = CreateStartupBinding(facts, new StartupMcpManager());
        await coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(RepositoryTrustLevel.FullyTrustedAutomation, binding.Current.MaximumTrust);
        Assert.Equal(saved ? MutationApprovalPolicy.AlwaysTrustRepo : MutationApprovalPolicy.ReviewAll, approval.CurrentPolicy);
        Assert.Empty(coordinator.LastWarnings);
        Assert.Equal(saved, new RepositoryPermissionStore(_paths.UserConfiguration).Read(_paths.RepositoryRoot).Exists);

        // Reopening without a choice does not write or authorize anything.
        var permissions = new RepositoryPermissionStore(_paths.UserConfiguration);
        var before = permissions.Read(_paths.RepositoryRoot);
        await coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(before.Revision, permissions.Read(_paths.RepositoryRoot).Revision);
        Assert.Equal(saved ? MutationApprovalPolicy.AlwaysTrustRepo : MutationApprovalPolicy.ReviewAll, approval.CurrentPolicy);

        // The existing /policy save path makes the deliberate choice once, outside the checkout.
        await approval.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo, ct);
        await coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, approval.CurrentPolicy);
        var restarted = CreateStartupBinding(facts, new StartupMcpManager());
        await restarted.Coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, restarted.Approval.CurrentPolicy);
        Assert.Equal(original, await File.ReadAllTextAsync(_paths.RepositoryConfiguration, ct));
    }

    /// <summary>A failed or cancelled open can restore services while authority remains unavailable, then reopen.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupBinding_FailureRollsBackWithoutReadingInvalidAuthority(bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_paths.RepositoryConfiguration, """{"mutation":{"approvalPolicy":"alwaysTrustRepo"}}""", ct);
        var facts = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "rollback.db")};Pooling=False", _paths.UserConfiguration);
        await facts.InitializeAsync(ct);
        var manager = new StartupMcpManager { Failure = cancel ? new OperationCanceledException(ct) : new InvalidOperationException("Injected binding failure") };
        var (coordinator, approval, binding) = CreateStartupBinding(facts, manager);
        var captured = binding.Current;
        var error = await Record.ExceptionAsync(() => coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct));
        Assert.Same(manager.DeliveredFailure, error);
        Assert.Equal(2, manager.BindCount);
        Assert.Throws<UnauthorizedAccessException>(() => binding.Validate(captured));
        await coordinator.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(MutationApprovalPolicy.ReviewAll, approval.CurrentPolicy);
        Assert.Throws<UnauthorizedAccessException>(() => binding.Validate(captured));
    }

    private (RepositoryScopedBindingCoordinator Coordinator, MutationApprovalPolicyService Approval, SessionPolicyBinding Binding) CreateStartupBinding(SqliteRepositoryFactsStore facts, StartupMcpManager manager)
    {
        var options = CommandLineParser.Parse(["--tui", "--trust", "FullyTrustedAutomation"]).Options!;
        var configuration = ConfigurationBootstrap.Build([], _paths);
        var binding = new SessionPolicyBinding();
        binding.PrepareRepositoryTrust(options.RequestedTrust!.Value);
        binding.Publish(Load());
        var approval = new MutationApprovalPolicyService(configuration, _paths.RepositoryConfiguration, binding, facts);
        var tools = new ToolStateManager([], configuration, _paths.RepositoryConfiguration, userConsentPath: Path.Combine(_root, "outbound.json"), mcpApprovalPath: Path.Combine(_root, "mcp.json"), policyBinding: binding);
        var coordinator = new RepositoryScopedBindingCoordinator(_paths.RepositoryRoot, tools, null, approval, new RepositorySecretProvider(_paths.RepositoryRoot), manager, policyBinding: binding, configurationPaths: root => root == _paths.RepositoryRoot ? _paths : throw new InvalidOperationException("Unexpected fixture root"));
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
