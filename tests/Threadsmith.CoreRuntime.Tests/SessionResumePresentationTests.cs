namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Exercises resume commands through retained semantic rendering.</summary>
public static class SessionResumePresentationTests
{
    /// <summary>Direct, picker, and current-session resume replace the old output and follow the last exchange.</summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public static async Task Resume_replaces_output_with_chronological_messages_and_follows_tail(bool picker, bool current, bool renderMarkdown)
    {
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var dispatcher = new HistoryDispatcher(current);
        var surface = new HistorySurface(picker ? "/resume" : $"/resume {dispatcher.Target.Value:D}");
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(dispatcher, new EmptyProjectionStore()),
            events,
            surface,
            displayOptions: new InteractionDisplayOptions(RenderMarkdown: renderMarkdown),
            sessionLifecycleAvailable: true);

        await coordinator.RunAsync(cancellationToken: token);

        Assert.Equal(1, surface.Replacements);
        Assert.DoesNotContain("previous output", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Threadsmith.NET", surface.Output, StringComparison.Ordinal);
        Assert.Contains("You > first question", surface.Output, StringComparison.Ordinal);
        Assert.Contains("first answer", surface.Output, StringComparison.Ordinal);
        Assert.Contains("You > last question", surface.Output, StringComparison.Ordinal);
        Assert.Contains("last answer", surface.Output, StringComparison.Ordinal);
        Assert.Equal(!renderMarkdown, surface.Output.Contains("**last answer**", StringComparison.Ordinal));
        Assert.True(surface.Output.IndexOf("first question", StringComparison.Ordinal) < surface.Output.IndexOf("first answer", StringComparison.Ordinal));
        Assert.True(surface.Output.IndexOf("first answer", StringComparison.Ordinal) < surface.Output.IndexOf("last question", StringComparison.Ordinal));
        Assert.True(surface.View.AtBottom);
        Assert.Equal(0, surface.View.NewCount);
        Assert.Empty(surface.View.SelectedText());
        var query = Assert.IsType<GetConversationStateCommand>(dispatcher.HistoryQuery);
        Assert.True(query.IncludeBodies);
        Assert.NotNull(query.HistoryWindow);
        Assert.Equal(dispatcher.Target, query.SessionId);
        foreach (var width in new[] { 40, 100 })
        {
            var cells = new CellBuffer(width, 8);
            surface.View.Render(new BufferSurface(cells));
            Assert.Contains("last answer", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
            surface.View.HandleKey(KeyEvent.Special(KeyCode.Home));
            surface.View.Render(new BufferSurface(cells));
            Assert.DoesNotContain("previous output", surface.Output, StringComparison.Ordinal);
            surface.View.HandleKey(KeyEvent.Special(KeyCode.End));
        }
    }

    /// <summary>Empty archives and expired bodies never leave previous-session output visible.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Empty_or_expired_history_still_replaces_output(bool expired)
    {
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var dispatcher = new HistoryDispatcher(false) { Empty = !expired, Expired = expired };
        var surface = new HistorySurface($"/resume {dispatcher.Target.Value:D}");
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(dispatcher, new EmptyProjectionStore()),
            events,
            surface,
            sessionLifecycleAvailable: true);

        await coordinator.RunAsync(cancellationToken: token);

        Assert.Equal(1, surface.Replacements);
        Assert.DoesNotContain("previous output", surface.Output, StringComparison.Ordinal);
        Assert.Equal(expired, surface.Output.Contains("Saved message content is no longer available", StringComparison.Ordinal));
        Assert.Contains("Resumed session", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Rejected transitions leave the visible conversation available.</summary>
    [Fact]
    public static async Task Failed_resume_preserves_output()
    {
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var dispatcher = new HistoryDispatcher(false) { Fail = true };
        var surface = new HistorySurface($"/resume {dispatcher.Target.Value:D}");
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(dispatcher, new EmptyProjectionStore()),
            events,
            surface,
            sessionLifecycleAvailable: true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(cancellationToken: token));

        Assert.Equal(0, surface.Replacements);
        Assert.Contains("previous output", surface.Output, StringComparison.Ordinal);
        Assert.Equal("Session unavailable", failure.Message);
        Assert.Null(dispatcher.HistoryQuery);
    }

    private sealed class HistoryDispatcher : ICommandDispatcher
    {
        private readonly SessionId _source = SessionId.New();

        internal HistoryDispatcher(bool current)
        {
            Target = current ? _source : SessionId.New();
        }

        internal SessionId Target { get; }

        internal bool Empty { get; init; }

        internal bool Expired { get; init; }

        internal bool Fail { get; init; }

        internal GetConversationStateCommand? HistoryQuery { get; private set; }

        public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object result;
            switch (command)
            {
                case CreateNewSessionCommand:
                    result = new SessionTransitionResult { Kind = SessionTransitionKind.New, ActiveSession = Entry(_source) };
                    break;
                case ListResumableSessionsCommand:
                    result = new[] { Entry(Target) };
                    break;
                case ResumeSessionCommand resume:
                    Assert.Equal(Target, resume.SessionId);
                    if (Fail)
                    {
                        throw new InvalidOperationException("Session unavailable");
                    }

                    result = new SessionTransitionResult { Kind = SessionTransitionKind.Resume, ActiveSession = Entry(Target) };
                    break;
                case GetConversationStateCommand history:
                    HistoryQuery = history;
                    result = new ConversationStateSnapshot
                    {
                        SessionId = Target,
                        Messages = Empty ? [] :
                        [
                            Message(1, ConversationRole.User, "first question"),
                            Message(2, ConversationRole.Assistant, "## first answer\n" + string.Join('\n', Enumerable.Repeat("older line", 20))),
                            Message(3, ConversationRole.User, "last question"),
                            Message(4, ConversationRole.Assistant, Expired ? null : "**last answer**\n"),
                        ],
                    };
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected command: {command.GetType().Name}");
            }

            return Task.FromResult((TResponse)result);
        }

        private static SessionCatalogEntry Entry(SessionId sessionId)
        {
            return new SessionCatalogEntry
            {
                SessionId = sessionId, RepositoryIdentity = "repo", RepositoryDisplayName = "repo",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, State = SessionLifecycleState.Idle,
            };
        }

        private ConversationMessage Message(long sequence, ConversationRole role, string? content)
        {
            return new ConversationMessage
            {
                Id = ConversationMessageId.New(), SessionId = Target, RunId = RunId.New(), Sequence = sequence,
                Role = role, Content = content, ContentHash = "hash", EstimatedTokens = 1, OccurredAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private sealed class EmptyProjectionStore : IProjectionStore
    {
        public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TProjection?> GetAsync<TProjection>(ProjectionKey key, CancellationToken cancellationToken = default)
            where TProjection : class, IProjection => Task.FromResult<TProjection?>(null);
    }

    private sealed class HistorySurface : IInteractionSurface
    {
        private readonly Queue<string> _inputs;

        internal HistorySurface(string command) => _inputs = new([command, "/quit"]);

        public InteractionSurfaceCapabilities Capabilities { get; } = new();

        internal TranscriptView View { get; } = new();

        internal int Replacements { get; private set; }

        internal string Output => string.Join('\n', View.Lines);

        public Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default)
        {
            if (_inputs.Count == 2)
            {
                View.EchoInput("old > ", "previous output");
                View.Render(new BufferSurface(new CellBuffer(60, 8)));
                View.HandleKey(KeyEvent.Special(KeyCode.Home));
                View.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
            }

            return Task.FromResult(new InteractionInput(true, _inputs.Dequeue(), cancellationToken));
        }

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new InteractionSelectionResult(request.Options[0].Id, false));

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (batch.ReplaceOutput)
            {
                Replacements++;
            }

            View.Present(batch);
            return Task.CompletedTask;
        }

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default)
            => operation.WaitAsync(cancellationToken);
    }
}
