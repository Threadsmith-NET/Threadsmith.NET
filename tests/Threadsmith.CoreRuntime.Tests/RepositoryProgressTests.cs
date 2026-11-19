namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Progress owns the wait before repository and solution operations complete.</summary>
public static class RepositoryProgressTests
{
    private sealed class GatedRepositoryHandler :
        ICommandHandler<CreateSessionCommand, SessionId>,
        ICommandHandler<GetRepositoryTrustCommand, RepositoryTrustState?>,
        ICommandHandler<OpenRepositoryCommand, RepositoryOpenResult>,
        ICommandHandler<SelectSolutionCommand, SolutionSelectionResult>,
        ICommandHandler<RecordBaselineCommand, WorkspaceBaseline>
    {
        private readonly WorkspaceId _workspace = WorkspaceId.New();
        private readonly string _candidate;

        internal GatedRepositoryHandler(string candidate)
        {
            _candidate = candidate;
        }

        internal TaskCompletionSource OpenGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SolutionGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool OpenStarted { get; private set; }

        internal bool SolutionStarted { get; private set; }

        public Task<SessionId> HandleAsync(CreateSessionCommand command, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SessionId.New());
        }

        public Task<RepositoryTrustState?> HandleAsync(GetRepositoryTrustCommand command, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<RepositoryTrustState?>(null);
        }

        public async Task<RepositoryOpenResult> HandleAsync(OpenRepositoryCommand command, CancellationToken cancellationToken = default)
        {
            OpenStarted = true;
            await OpenGate.Task.WaitAsync(cancellationToken);
            return new RepositoryOpenResult(
                _workspace,
                command.RepositoryPath,
                new RepositoryTrustState(command.RepositoryPath, command.RequestedTrust, DateTimeOffset.UtcNow),
                new RepositoryConfigurationSnapshot(null, [], []),
                null,
                [_candidate]);
        }

        public async Task<SolutionSelectionResult> HandleAsync(SelectSolutionCommand command, CancellationToken cancellationToken = default)
        {
            SolutionStarted = true;
            await SolutionGate.Task.WaitAsync(cancellationToken);
            return new SolutionSelectionResult(_workspace, command.SolutionPath, ["net10.0"]);
        }

        public Task<WorkspaceBaseline> HandleAsync(RecordBaselineCommand command, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkspaceBaseline(_workspace, "repo", DateTimeOffset.UtcNow, []));
        }
    }
}
