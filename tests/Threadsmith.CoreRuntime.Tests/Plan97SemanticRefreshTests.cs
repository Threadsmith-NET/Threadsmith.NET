namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies semantic-refresh command parity and serialized TUI lifecycle projection.</summary>
public static class Plan97SemanticRefreshTests
{
    /// <summary>The TUI controller forces refresh through the host command boundary without submitting a request.</summary>
    [Fact]
    public static async Task TuiController_ForceSemanticRefresh_DispatchesLocalCommand()
    {
        var sessionId = SessionId.New();
        var expected = CreateResult(SemanticRefreshReason.Manual);
        var dispatcher = new RecordingDispatcher(sessionId, expected);
        var controller = new TuiController(
            new TuiPresenter(dispatcher, new InMemoryProjectionStore()));
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

    /// <summary>External refresh renders one exact start/completion pair with shared duration formatting.</summary>
    [Fact]
    public static void ConversationTranscript_ExternalSemanticRefresh_RendersOneLifecyclePair()
    {
        var sessionId = SessionId.New();
        var workspaceId = WorkspaceId.New();
        var refreshId = SemanticRefreshId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);

        Assert.True(transcript.Apply(new SemanticRefreshStarted(
            sessionId,
            occurredAt,
            refreshId,
            workspaceId,
            SemanticRefreshReason.ExternalChange,
            SemanticRefreshMode.Incremental,
            ChangedFileCount: 3,
            DirtyVersion: 2)));
        Assert.True(transcript.Apply(new SemanticRefreshCompleted(
            sessionId,
            occurredAt,
            refreshId,
            workspaceId,
            SemanticRefreshReason.ExternalChange,
            SemanticRefreshMode.Incremental,
            ChangedFileCount: 3,
            DirtyVersion: 2,
            AppliedVersion: 2,
            SemanticConfidenceLevel.FullSemantic,
            ElapsedMilliseconds: 240)));

        Assert.Equal(
            "External changes detected; updating semantic model...\n\n"
                + "Semantic model updated (3 files, 240ms).\n",
            transcript.Text.ReplaceLineEndings("\n"));
    }

    /// <summary>Watcher recovery renders one bounded start paired with its single completion.</summary>
    [Fact]
    public static void ConversationTranscript_RecoverySemanticRefresh_RendersOneLifecyclePair()
    {
        var sessionId = SessionId.New();
        var workspaceId = WorkspaceId.New();
        var refreshId = SemanticRefreshId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);

        Assert.True(transcript.Apply(new SemanticRefreshStarted(
            sessionId,
            occurredAt,
            refreshId,
            workspaceId,
            SemanticRefreshReason.Recovery,
            SemanticRefreshMode.Full,
            ChangedFileCount: 0,
            DirtyVersion: 4)));
        Assert.True(transcript.Apply(new SemanticRefreshCompleted(
            sessionId,
            occurredAt,
            refreshId,
            workspaceId,
            SemanticRefreshReason.Recovery,
            SemanticRefreshMode.Full,
            ChangedFileCount: 0,
            DirtyVersion: 4,
            AppliedVersion: 4,
            SemanticConfidenceLevel.FullSemantic,
            ElapsedMilliseconds: 500)));

        Assert.Equal(
            "External changes require semantic recovery; updating semantic model...\n\n"
                + "Semantic model updated (0 files, 500ms).\n",
            transcript.Text.ReplaceLineEndings("\n"));
    }

    /// <summary>A host-attributed start upgraded by an external follow-up still renders a complete visible pair.</summary>
    [Fact]
    public static void ConversationTranscript_HostThenExternalRefresh_SynthesizesVisibleStartBeforeCompletion()
    {
        var sessionId = SessionId.New();
        var workspaceId = WorkspaceId.New();
        var refreshId = SemanticRefreshId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);

        Assert.False(transcript.Apply(new SemanticRefreshStarted(
            sessionId,
            occurredAt,
            refreshId,
            workspaceId,
            SemanticRefreshReason.HostMutation,
            SemanticRefreshMode.Incremental,
            ChangedFileCount: 1,
            DirtyVersion: 2)));
        Assert.True(transcript.Apply(new SemanticRefreshCompleted(
            sessionId,
            occurredAt,
            refreshId,
            workspaceId,
            SemanticRefreshReason.ExternalChange,
            SemanticRefreshMode.Full,
            ChangedFileCount: 2,
            DirtyVersion: 3,
            AppliedVersion: 3,
            SemanticConfidenceLevel.FullSemantic,
            ElapsedMilliseconds: 300)));

        Assert.Equal(
            "External changes detected; updating semantic model...\n\n"
                + "Semantic model updated (2 files, 300ms).\n",
            transcript.Text.ReplaceLineEndings("\n"));
    }

    /// <summary>An externally upgraded failure also receives the visible start hidden at sequence creation.</summary>
    [Fact]
    public static void ConversationTranscript_HostThenExternalRefresh_SynthesizesVisibleStartBeforeFailure()
    {
        var sessionId = SessionId.New();
        var workspaceId = WorkspaceId.New();
        var refreshId = SemanticRefreshId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);

        Assert.False(transcript.Apply(new SemanticRefreshStarted(
            sessionId,
            occurredAt,
            refreshId,
            workspaceId,
            SemanticRefreshReason.HostMutation,
            SemanticRefreshMode.Incremental,
            ChangedFileCount: 1,
            DirtyVersion: 2)));
        Assert.True(transcript.Apply(new SemanticRefreshFailed(
            sessionId,
            occurredAt,
            refreshId,
            workspaceId,
            SemanticRefreshReason.ExternalChange,
            SemanticRefreshMode.Full,
            ChangedFileCount: 2,
            DirtyVersion: 3,
            AppliedVersion: 2,
            SemanticRefreshFailureKind.Infrastructure,
            "safe reason",
            ElapsedMilliseconds: 300)));

        Assert.Equal(
            "External changes detected; updating semantic model...\n\n"
                + "Semantic model refresh failed (Infrastructure) after 300ms: safe reason\n",
            transcript.Text.ReplaceLineEndings("\n"));
    }

    /// <summary>Manual completion reports resulting confidence while mutation echoes stay silent.</summary>
    [Fact]
    public static void ConversationTranscript_ManualAndHostMutationRefresh_ProjectsByAttribution()
    {
        var sessionId = SessionId.New();
        var workspaceId = WorkspaceId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);
        var hostRefreshId = SemanticRefreshId.New();

        Assert.False(transcript.Apply(new SemanticRefreshStarted(
            sessionId,
            occurredAt,
            hostRefreshId,
            workspaceId,
            SemanticRefreshReason.HostMutation,
            SemanticRefreshMode.Incremental,
            ChangedFileCount: 1,
            DirtyVersion: 2)));
        Assert.False(transcript.Apply(new SemanticRefreshCompleted(
            sessionId,
            occurredAt,
            hostRefreshId,
            workspaceId,
            SemanticRefreshReason.HostMutation,
            SemanticRefreshMode.Incremental,
            ChangedFileCount: 1,
            DirtyVersion: 2,
            AppliedVersion: 2,
            SemanticConfidenceLevel.FullSemantic,
            ElapsedMilliseconds: 12)));

        Assert.True(transcript.Apply(new SemanticRefreshCompleted(
            sessionId,
            occurredAt,
            SemanticRefreshId.New(),
            workspaceId,
            SemanticRefreshReason.Manual,
            SemanticRefreshMode.Full,
            ChangedFileCount: 0,
            DirtyVersion: 2,
            AppliedVersion: 2,
            SemanticConfidenceLevel.PartialCompilation,
            ElapsedMilliseconds: 1250)));

        Assert.Equal(
            "Semantic model updated (0 files, 1.2s; confidence PartialCompilation).\n",
            transcript.Text.ReplaceLineEndings("\n"));
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

        public Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            object response = command switch
            {
                CreateSessionCommand => _sessionId,
                ForceSemanticRefreshCommand => _result,
                _ => throw new InvalidOperationException($"Unexpected command {command.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
        }
    }
}
