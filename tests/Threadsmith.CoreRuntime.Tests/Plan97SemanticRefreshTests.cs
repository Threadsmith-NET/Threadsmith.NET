namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Verifies semantic-refresh command parity and serialized TUI lifecycle projection.</summary>
public static class Plan97SemanticRefreshTests
{
    /// <summary>Admission progress is presented before an awaited request obtains its run identity.</summary>
    [Fact]
    public static async Task SubmitDisplaysProgressBeforeAdmissionCompletes()
    {
        var dispatcher = new RecordingDispatcher(SessionId.New(), CreateResult(SemanticRefreshReason.Manual));
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new InteractionController(
            new InteractionPresenter(dispatcher, new InMemoryProjectionStore()),
            async (label, operation, token) =>
        {
            Assert.Contains("Preparing request", label, StringComparison.Ordinal);
            presented.TrySetResult();
            await operation(token).WaitAsync(token);
        });
        await controller.OpenAsync("refresh-test", TestContext.Current.CancellationToken);
        var submission = controller.SubmitAsync("review", TestContext.Current.CancellationToken);
        await presented.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(submission.IsCompleted);
        Assert.Null(controller.ActiveRunId);
        var run = RunId.New();
        dispatcher.Submission.SetResult(run);
        Assert.Equal(run, await submission);
        Assert.Equal(run, controller.ActiveRunId);
    }

    /// <summary>The TUI controller forces refresh through the host command boundary without submitting a request.</summary>
    [Fact]
    public static async Task InteractionController_ForceSemanticRefresh_DispatchesLocalCommand()
    {
        var sessionId = SessionId.New();
        var expected = CreateResult(SemanticRefreshReason.Manual);
        var dispatcher = new RecordingDispatcher(sessionId, expected);
        var controller = new InteractionController(
            new InteractionPresenter(dispatcher, new InMemoryProjectionStore()));
        _ = await controller.OpenAsync("refresh-test");

        var actual = await controller.ForceSemanticRefreshAsync();

        Assert.Equal(expected, actual);
        var command = Assert.IsType<ForceSemanticRefreshCommand>(dispatcher.Commands[^1]);
        Assert.Equal(sessionId, command.SessionId);
        Assert.DoesNotContain(dispatcher.Commands, item => item is SubmitRequestCommand);
    }

    /// <summary>The headless surface exposes the same force-refresh command and structured result.</summary>
    [Fact]
    public static async Task HeadlessShell_ForceSemanticRefresh_DispatchesSharedCommand()
    {
        var sessionId = SessionId.New();
        var expected = CreateResult(SemanticRefreshReason.Manual);
        var dispatcher = new RecordingDispatcher(sessionId, expected);
        var shell = new HeadlessShell(
            dispatcher,
            new InMemoryProjectionStore(),
            TextWriter.Null);

        var actual = await shell.ForceSemanticRefreshAsync(sessionId);

        Assert.Equal(expected, actual);
        var command = Assert.IsType<ForceSemanticRefreshCommand>(Assert.Single(dispatcher.Commands));
        Assert.Equal(sessionId, command.SessionId);
    }

    private static SemanticRefreshResult CreateResult(SemanticRefreshReason reason)
    {
        return new SemanticRefreshResult(
            SemanticRefreshId.New(),
            WorkspaceId.New(),
            reason,
            SemanticRefreshMode.Full,
            ChangedFileCount: 0,
            DirtyVersion: 1,
            AppliedVersion: 1,
            SemanticConfidenceLevel.FullSemantic,
            TimeSpan.FromMilliseconds(12),
            WasRefreshed: true);
    }

    private sealed class RecordingDispatcher : ICommandDispatcher
    {
        private readonly SemanticRefreshResult _result;
        private readonly SessionId _sessionId;

        public RecordingDispatcher(SessionId sessionId, SemanticRefreshResult result)
        {
            _sessionId = sessionId;
            _result = result;
        }

        public List<object> Commands { get; } = [];

        public TaskCompletionSource<RunId> Submission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (command is SubmitRequestCommand)
            {
                return (TResponse)(object)await Submission.Task.WaitAsync(cancellationToken);
            }

            object response = command switch
            {
                CreateSessionCommand => _sessionId,
                ForceSemanticRefreshCommand => _result,
                _ => throw new InvalidOperationException($"Unexpected command {command.GetType().Name}."),
            };
            return (TResponse)response;
        }
    }
}
