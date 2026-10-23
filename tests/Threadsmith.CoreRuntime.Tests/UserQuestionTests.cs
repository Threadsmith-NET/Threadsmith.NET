namespace Threadsmith.CoreRuntime.Tests;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Persistence;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Clarification validation, persistence, cancellation, and modal regression coverage.</summary>
[Collection("TUIKit terminal")]
public static class UserQuestionTests
{
    /// <summary>Only complete bounded explicit answers are accepted.</summary>
    [Fact]
    public static void BoundsRejectUnknownDuplicateAndImplicitAnswers()
    {
        var request = CreateRequest();
        UserQuestionValidation.ValidateQuestions(request.Questions);
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateQuestions([]));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateQuestions([request.Questions[0], request.Questions[0]]));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateQuestions([new("q", new string('x', 2001), [])]));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateQuestions([new("q", "\u001b[31m", [])]));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateQuestions([new("q", "Choose", [new("a", "Only")])]));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateAnswers(request.Questions, []));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateAnswers(request.Questions, [new("other", null, "text")]));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateAnswers(request.Questions, [new("q", "unknown", null)]));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateAnswers(request.Questions, [new("q", "a", "text")]));
        Assert.Throws<ArgumentException>(() => UserQuestionValidation.ValidateAnswers(request.Questions, [new("q", null, new string('x', 8001))]));
        UserQuestionValidation.ValidateAnswers(request.Questions, [new("q", null, "Explicit\nanswer")]);
    }

    /// <summary>Admission and terminal selection occur before presentation and continuation.</summary>
    [Fact]
    public static async Task AdmissionPersistsBeforePresentationAndCancellationClosesThePresenter()
    {
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((value, _) =>
        {
            observed.Enqueue(value);
            return Task.CompletedTask;
        });
        var steering = new RunSteeringCoordinator();
        using var coordinator = new UserQuestionCoordinator(events, steering);
        var request = CreateRequest();
        steering.RegisterRun(request.SessionId, request.RunId);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        coordinator.Attach(async (_, cancellationToken) =>
        {
            Assert.Single(observed.OfType<UserQuestionStateChanged>());
            shown.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new(UserQuestionStatus.Answered, [new("q", "a", null)]);
            }
            finally
            {
                closed = true;
            }
        });
        Assert.Equal(request.RequestId, (await coordinator.AdmitAsync(request, token)).RequestId);
        Assert.Equal(request.RequestId, (await coordinator.AdmitAsync(request with { RequestId = Guid.NewGuid() }, token)).RequestId);
        Assert.False(shown.Task.IsCompleted);
        Assert.Equal(RunSteeringPauseRequestStatus.TooLate, steering.RequestPause(request.SessionId, request.RunId).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AdmitAsync(request with { RequestId = Guid.NewGuid(), InvocationKey = "other" }, token));
        var waiting = coordinator.WaitAsync(request, token);
        await shown.Task.WaitAsync(token);
        Assert.False(await coordinator.CompleteAsync(request.SessionId, Guid.NewGuid(), 0, new(UserQuestionStatus.Cancelled, []), token));
        Assert.False(await coordinator.CompleteAsync(request.SessionId, request.RequestId, 1, new(UserQuestionStatus.Cancelled, []), token));
        Assert.True(await coordinator.CompleteAsync(request.SessionId, request.RequestId, 0, new(UserQuestionStatus.Cancelled, []), token));
        Assert.Equal(UserQuestionStatus.Cancelled, (await waiting).Status);
        Assert.True(closed);
        Assert.True(await coordinator.CompleteAsync(request.SessionId, request.RequestId, 0, new(UserQuestionStatus.Cancelled, []), token));
        Assert.False(await coordinator.CompleteAsync(request.SessionId, request.RequestId, 0, new(UserQuestionStatus.Answered, [new("q", "a", null)]), token));
        Assert.Single(observed.OfType<UserQuestionStateChanged>(), value => value.Result.Status == UserQuestionStatus.Cancelled);
        await coordinator.ConsumeAsync(request, token);
        var next = request with { RequestId = Guid.NewGuid(), InvocationKey = "next" };
        await coordinator.AdmitAsync(next, token);
        Assert.Equal(request.RequestId, (await coordinator.AdmitAsync(request with { RequestId = Guid.NewGuid() }, token)).RequestId);
        Assert.Equal(UserQuestionStatus.Cancelled, (await coordinator.WaitAsync(request, token)).Status);
        Assert.True(await coordinator.CompleteAsync(request.SessionId, request.RequestId, 0, new(UserQuestionStatus.Cancelled, []), token));
        await coordinator.EndRunAsync(request.SessionId, request.RunId, token);
        Assert.Equal(RunSteeringPauseRequestStatus.Accepted, steering.RequestPause(request.SessionId, request.RunId).Status);
    }

    /// <summary>Clarification uses the configured retention cap, including zero to disable it.</summary>
    [Theory]
    [InlineData(null, 256)]
    [InlineData(2, 2)]
    [InlineData(300, 300)]
    [InlineData(0, 300)]
    public static async Task RetentionHonorsExecutionLimits(int? configuredLimit, int admittedCount)
    {
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var steering = new RunSteeringCoordinator();
        using var coordinator = new UserQuestionCoordinator(events, steering, limits: configuredLimit is { } limit ? new ExecutionLimits { MaxRetainedToolCalls = limit } : null);
        coordinator.Attach((_, _) => Task.FromResult(new UserQuestionResult(UserQuestionStatus.Cancelled, [])));
        var request = CreateRequest();
        steering.RegisterRun(request.SessionId, request.RunId);
        for (var index = 0; index < admittedCount; index++)
        {
            request = request with { RequestId = Guid.NewGuid(), ToolInvocationId = ToolInvocationId.New(), InvocationKey = $"call-{index}" };
            Assert.Equal(request.RequestId, (await coordinator.AdmitAsync(request, token)).RequestId);
            await coordinator.WaitAsync(request, token);
            await coordinator.ConsumeAsync(request, token);
        }

        // Idempotent retries still resolve the original request at the limit.
        Assert.Equal(request.RequestId, (await coordinator.AdmitAsync(request with { RequestId = Guid.NewGuid() }, token)).RequestId);
        var next = request with { RequestId = Guid.NewGuid(), InvocationKey = "next" };
        if (configuredLimit != 0)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AdmitAsync(next, token));
            Assert.Contains("retained clarification limit", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(next.RequestId, (await coordinator.AdmitAsync(next, token)).RequestId);
        }

        await coordinator.EndRunAsync(request.SessionId, request.RunId, token);
        Assert.Equal(next.RequestId, (await coordinator.AdmitAsync(next, token)).RequestId);
        await coordinator.EndRunAsync(request.SessionId, request.RunId, token);
    }

    /// <summary>Nonquiescent runs and failed durable admission cannot open a modal.</summary>
    [Fact]
    public static async Task ActiveDelegationAndPersistenceFailurePreventPresentation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var steering = new RunSteeringCoordinator();
        using var coordinator = new UserQuestionCoordinator(events, steering);
        coordinator.Attach((_, _) => throw new InvalidOperationException("Must not present"));
        var request = CreateRequest();
        steering.RegisterRun(request.SessionId, request.RunId);
        var delegation = DelegationId.New();
        steering.RegisterDelegation(request.SessionId, request.RunId, delegation, [RunId.New()]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AdmitAsync(request, token));
        steering.CompleteDelegation(request.SessionId, request.RunId, delegation);
        await using var subscription = events.Subscribe((_, _) => throw new IOException("Persistence unavailable"));
        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.AdmitAsync(request, token));
        Assert.Equal(RunSteeringPauseRequestStatus.Accepted, steering.RequestPause(request.SessionId, request.RunId).Status);
    }

    /// <summary>Restoration never resurrects a live tool continuation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task RestartInterruptsPendingAndAcceptedButUnconsumedQuestions(bool answered)
    {
        var token = TestContext.Current.CancellationToken;
        var database = Path.Combine(Path.GetTempPath(), $"threadsmith-question-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteEventStore($"Data Source={database};Pooling=False");
            await store.InitializeAsync(token);
            var request = CreateRequest();
            await store.AppendAsync(new UserQuestionStateChanged(request.SessionId, DateTimeOffset.UtcNow, request, new(UserQuestionStatus.Pending, [])), token);
            if (answered)
            {
                await store.AppendAsync(new UserQuestionStateChanged(request.SessionId, DateTimeOffset.UtcNow, request, new(UserQuestionStatus.Answered, [new("q", "a", null)])), token);
            }

            var restorer = new SessionRestorer(store, new DomainEventMigrationRegistry([], currentVersion: 1), NullLogger<SessionRestorer>.Instance);
            var restored = await restorer.RestoreAsync(request.SessionId, new InMemoryProjectionStore(), token);
            Assert.True(restored.Succeeded);
            Assert.Contains("interrupted", restored.Warnings, StringComparison.Ordinal);
            var history = await store.ReadAsync(request.SessionId, token);
            Assert.Equal(UserQuestionStatus.Interrupted, Assert.IsType<UserQuestionStateChanged>(history[^1]).Result.Status);
            Assert.Empty(history.OfType<UserQuestionReady>());
            await restorer.RestoreAsync(request.SessionId, new InMemoryProjectionStore(), token);
            Assert.Equal(history.Count, (await store.ReadAsync(request.SessionId, token)).Count);
        }
        finally
        {
            File.Delete(database);
        }
    }

    /// <summary>Other alone reveals the editor, and Enter submits pasted multiline text without inserting a newline.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public static void OtherIsAnExplicitUiChoiceWithNoInventedOptionId(int count)
    {
        var request = CreateRequest() with { Questions = [new("q", "Which option?", [.. Enumerable.Range(1, count).Select(index => new UserQuestionOption($"o{index}", $"Option {index}"))])] };
        var limits = new TuiResourceLimits();
        var draft = new QuestionDraft(request, limits);
        var modal = new QuestionModal(draft, limits);
        var cells = new CellBuffer(80, 30);
        modal.Render(new BufferSurface(cells));
        var before = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains($"{count + 1}. Other", before, StringComparison.Ordinal);
        Assert.DoesNotContain("Your answer:", before, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Char((char)('1' + count)));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("> Your answer:", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Null(draft.SelectedOptions[0]);
        Assert.True(draft.OtherSelected[0]);
        modal.HandlePaste("12\n34");
        Assert.False(modal.IsClosed);
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.True(modal.IsClosed);
        Assert.Equal("12\n34", draft.Editors[0].Text);
    }

    /// <summary>Opening focuses choices; arrows and Enter explicitly select and submit at all supported sizes.</summary>
    [Theory]
    [InlineData(40, 15)]
    [InlineData(80, 24)]
    [InlineData(160, 40)]
    public static void OptionsStartFocusedAndEnterSubmits(int width, int height)
    {
        var limits = new TuiResourceLimits();
        var draft = new QuestionDraft(CreateRequest(), limits);
        var modal = new QuestionModal(draft, limits);
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        var lines = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
        var prompt = Array.FindIndex(lines, line => line.Contains("Which option?", StringComparison.Ordinal));
        Assert.Contains("The Assistant Has a Question", lines[prompt - 2], StringComparison.Ordinal);
        Assert.DoesNotContain("Question", lines[prompt - 1], StringComparison.Ordinal);
        Assert.Contains("> [ ] 1. First", lines[prompt + 1], StringComparison.Ordinal);
        Assert.Null(draft.SelectedOptions[0]);
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("> [ ] 2. Second", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal("b", draft.SelectedOptions[0]);
        Assert.True(modal.IsClosed);
    }

    /// <summary>Missing group answers move to the required question with actionable feedback.</summary>
    [Fact]
    public static void MultipleQuestionsValidateAndFocusTheMissingAnswer()
    {
        var request = CreateRequest();
        request = request with { Questions = [request.Questions[0], new("text", "Explain why", [])] };
        var limits = new TuiResourceLimits();
        var draft = new QuestionDraft(request, limits);
        var modal = new QuestionModal(draft, limits);
        var cells = new CellBuffer(80, 24);
        modal.Render(new BufferSurface(cells));
        modal.HandleKey(KeyEvent.Char('2'));
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.False(modal.IsClosed);
        Assert.Equal(1, draft.QuestionIndex);
        modal.Render(new BufferSurface(cells));
        Assert.Contains("Answer question 2", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Char('1'));
        modal.HandleKey(KeyEvent.Char('2'));
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.True(modal.IsClosed);
        Assert.Equal("12", draft.Editors[1].Text);
        Assert.Equal("b", draft.SelectedOptions[0]);
    }

    /// <summary>Free-text questions directly focus editing; empty Enter stays open and Cancel never submits the draft.</summary>
    [Fact]
    public static void DirectTextAndCancelAreExplicit()
    {
        var request = CreateRequest() with { Questions = [new("q", "Tell me", [])] };
        var limits = new TuiResourceLimits();
        var draft = new QuestionDraft(request, limits);
        var modal = new QuestionModal(draft, limits);
        modal.Render(new BufferSurface(new CellBuffer(80, 24)));
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.False(modal.IsClosed);
        modal.HandlePaste("answer");
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        Assert.True(modal.IsClosed);
        Assert.Equal("answer", draft.Editors[0].Text);
    }

    private static UserQuestionRequest CreateRequest() => new(Guid.NewGuid(), SessionId.New(), RunId.New(), ToolInvocationId.New(), "call-1", "repository", 0, [new("q", "Which option?", [new("a", "First"), new("b", "Second")])], DateTimeOffset.UtcNow);
}
