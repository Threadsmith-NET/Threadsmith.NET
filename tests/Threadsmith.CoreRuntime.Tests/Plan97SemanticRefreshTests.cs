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

    /// <summary>Lifecycle output identifies admitted inputs and safely encodes terminal controls.</summary>
    [Fact]
    public static void ConversationTranscript_RefreshNamesTriggerFiles()
    {
        var started = new SemanticRefreshStarted(SessionId.New(), DateTimeOffset.UtcNow, SemanticRefreshId.New(), WorkspaceId.New(), SemanticRefreshReason.ExternalChange, SemanticRefreshMode.Full, 20, 1)
        {
            TriggerPaths = ["src/Example.cs", "Directory.Build.props", "src/escape\u001b.cs"],
        };
        var json = DomainEventJson.Serialize(started);
        var restored = Assert.IsType<SemanticRefreshStarted>(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(started), 1, json));
        Assert.Equal(started.TriggerPaths, restored.TriggerPaths);
        var historicalJson = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var triggerKey = Assert.Single(historicalJson.Select(pair => pair.Key), key => key.Equals("TriggerPaths", StringComparison.OrdinalIgnoreCase));
        historicalJson.Remove(triggerKey);
        var historical = Assert.IsType<SemanticRefreshStarted>(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(started), 1, historicalJson.ToJsonString()));
        Assert.Empty(historical.TriggerPaths);
        var transcript = new ConversationTranscript(string.Empty);
        Assert.True(transcript.Apply(started));
        Assert.Contains("Triggered by:", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("src/Example.cs", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.props", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("17 other files", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", transcript.Text, StringComparison.Ordinal);
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
