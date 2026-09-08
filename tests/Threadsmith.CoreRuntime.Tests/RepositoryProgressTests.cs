namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Progress owns the wait before repository and solution operations complete.</summary>
public static class RepositoryProgressTests
{
    /// <summary>Blocked opening and restore show progress; every terminal outcome releases the indicator.</summary>
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("cancel")]
    public static async Task RepositoryWorkflow_ReportsProgressWhileOperationsArePending(string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var handler = new GatedRepositoryHandler();
        var openVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var solutionVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var labels = new List<string>();
        var activeIndicators = 0;
        var controller = new InteractionController(
            new InteractionPresenter(new CommandDispatcher([handler]), new InMemoryProjectionStore()),
            async (label, operation, token) =>
            {
                labels.Add(label);
                activeIndicators++;
                (labels.Count == 1 ? openVisible : solutionVisible).TrySetResult();
                try
                {
                    await operation.WaitAsync(token);
                }
                finally
                {
                    activeIndicators--;
                }
            });
        await controller.OpenAsync("Progress test", timeout.Token);
        var workflow = controller.OpenRepositoryWorkflowAsync(
            "repo",
            _ => throw new InvalidOperationException("Trust is already explicitly supplied."),
            (_, _) => throw new InvalidOperationException("The only solution is already known."),
            requestedTrust: RepositoryTrustLevel.FullyTrustedAutomation,
            cancellationToken: operationCancellation.Token);
        try
        {
            await openVisible.Task.WaitAsync(timeout.Token);
            Assert.False(workflow.IsCompleted);
            Assert.Equal(1, activeIndicators);
            handler.OpenGate.SetResult();
            await solutionVisible.Task.WaitAsync(timeout.Token);
            Assert.False(workflow.IsCompleted);
            Assert.Equal(1, activeIndicators);
            Assert.Equal(["Opening repository...", "Loading solution and restoring packages..."], labels);
            if (outcome == "cancel")
            {
                await operationCancellation.CancelAsync();
#pragma warning disable VSTHRD003 // This test starts and owns the workflow observed by the assertion.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow);
#pragma warning restore VSTHRD003
            }
            else if (outcome == "fail")
            {
                handler.SolutionGate.SetException(new InvalidOperationException("Restore failed."));
#pragma warning disable VSTHRD003 // This test starts and owns the workflow observed by the assertion.
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow);
#pragma warning restore VSTHRD003
                Assert.Equal("Restore failed.", exception.Message);
            }
            else
            {
                handler.SolutionGate.SetResult();
                Assert.NotNull((await workflow).Solution);
            }

            Assert.Equal(0, activeIndicators);
        }
        finally
        {
            handler.OpenGate.TrySetResult();
            handler.SolutionGate.TrySetResult();
            await operationCancellation.CancelAsync();
        }
    }

    private sealed class GatedRepositoryHandler :
        ICommandHandler<CreateSessionCommand, SessionId>,
        ICommandHandler<GetRepositoryTrustCommand, RepositoryTrustState?>,
        ICommandHandler<OpenRepositoryCommand, RepositoryOpenResult>,
        ICommandHandler<SelectSolutionCommand, SolutionSelectionResult>,
        ICommandHandler<RecordBaselineCommand, WorkspaceBaseline>
    {
        private readonly WorkspaceId _workspace = WorkspaceId.New();

        internal TaskCompletionSource OpenGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SolutionGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionId> HandleAsync(CreateSessionCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(SessionId.New());

        public Task<RepositoryTrustState?> HandleAsync(GetRepositoryTrustCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult<RepositoryTrustState?>(null);

        public async Task<RepositoryOpenResult> HandleAsync(OpenRepositoryCommand command, CancellationToken cancellationToken = default)
        {
            await OpenGate.Task.WaitAsync(cancellationToken);
            return new RepositoryOpenResult(
                _workspace,
                command.RepositoryPath,
                new RepositoryTrustState(command.RepositoryPath, command.RequestedTrust, DateTimeOffset.UtcNow),
                new RepositoryConfigurationSnapshot(null, [], []),
                null,
                ["sample.sln"]);
        }

        public async Task<SolutionSelectionResult> HandleAsync(SelectSolutionCommand command, CancellationToken cancellationToken = default)
        {
            await SolutionGate.Task.WaitAsync(cancellationToken);
            return new SolutionSelectionResult(_workspace, command.SolutionPath, ["net10.0"]);
        }

        public Task<WorkspaceBaseline> HandleAsync(RecordBaselineCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceBaseline(_workspace, "repo", DateTimeOffset.UtcNow, []));
    }
}
