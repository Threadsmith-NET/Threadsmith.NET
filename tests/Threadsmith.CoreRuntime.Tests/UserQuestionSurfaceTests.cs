namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Runs;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Terminal;
using Xunit;

/// <summary>Exercises clarification through the real terminal input owner.</summary>
[Collection("TUIKit terminal")]
public static class UserQuestionSurfaceTests
{
    /// <summary>Every user-decline route resolves the same result and releases the modal owner for another request.</summary>
    [Theory(Timeout = 30000)]
    [InlineData("escape")]
    [InlineData("other")]
    [InlineData("details")]
    [InlineData("cancel")]
    [InlineData("f5")]
    public static async Task DeclineResolvesAndReleasesInput(string route)
    {
        var token = TestContext.Current.CancellationToken;
        using var backend = new ObservedBackend();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend);
        await surface.RunAsync(
            async lifetime =>
        {
            var request = new UserQuestionRequest(Guid.NewGuid(), SessionId.New(), RunId.New(), ToolInvocationId.New(), "call", "repo", 0, [new("q", "Pick", [new("a", "First"), new("b", "Second")])], DateTimeOffset.UtcNow);
            var shown = backend.ExpectOutputAsync("The Assistant Has a Question");
            var pending = surface.AskAsync(request, lifetime);
            await shown.WaitAsync(lifetime);
            if (route == "other")
            {
                var editor = backend.ExpectOutputAsync("Your answer:");
                backend.Input.FeedInput("3");
                await editor.WaitAsync(lifetime);
                backend.Input.FeedInput("draft");
            }
            else if (route == "details")
            {
                var details = backend.ExpectOutputAsync("Scroll; F2 back; Esc cancels");
                backend.Input.FeedInput("\u001bOQ");
                await details.WaitAsync(lifetime);
            }

            backend.Input.FeedInput(route == "cancel" ? "\t\t\r" : route == "f5" ? "\u001b[15~" : "\u001b[27u");
            var result = await pending;
            Assert.Equal(UserQuestionStatus.Cancelled, result.Status);
            Assert.Equal("The user declined to answer", result.Message);
            Assert.Empty(result.Answers);
            shown = backend.ExpectOutputAsync("The Assistant Has a Question");
            var next = surface.AskAsync(request with { RequestId = Guid.NewGuid() }, lifetime);
            await shown.WaitAsync(lifetime);
            backend.Input.FeedInput("\u001b[B\r");
            Assert.Equal("b", Assert.Single((await next).Answers).OptionId);
        },
            token);
    }

    /// <summary>Real terminal bytes navigate from initial focus and retain choices through resize.</summary>
    [Fact(Timeout = 30000)]
    public static async Task InitialArrowsSelectThroughTerminalInputAndSurviveResize()
    {
        var token = TestContext.Current.CancellationToken;
        using var backend = new ObservedBackend();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend);
        await surface.RunAsync(
            async lifetime =>
        {
            var request = new UserQuestionRequest(Guid.NewGuid(), SessionId.New(), RunId.New(), ToolInvocationId.New(), "call", "repo", 0, [new("q", "How are you today?", [new("great", "Great!"), new("lousy", "Lousy")])], DateTimeOffset.UtcNow);
            var shown = backend.ExpectOutputAsync("> [ ] 1. Great!");
            var pending = surface.AskAsync(request, lifetime);
            await shown.WaitAsync(lifetime);
            var moved = backend.ExpectOutputAsync("> [ ] 2. Lousy");
            backend.Input.FeedInput("\u001b[B");
            await moved.WaitAsync(lifetime);
            Assert.False(pending.IsCompleted);
            var selected = backend.ExpectOutputAsync("> [x] 2. Lousy");
            backend.Input.FeedInput(" ");
            await selected.WaitAsync(lifetime);
            var resized = backend.ExpectOutputAsync("> [x] 2. Lousy");
            backend.Input.Resize(40, 15);
            await resized.WaitAsync(lifetime);
            backend.Input.FeedInput("\r");
            var answer = await pending;
            Assert.Equal(UserQuestionStatus.Answered, answer.Status);
            Assert.Equal("lousy", Assert.Single(answer.Answers).OptionId);
        },
            token);
    }

    /// <summary>Cancellation completes the request; mute survives into the next request.</summary>
    [Fact(Timeout = 30000)]
    public static async Task BellMuteAndCancellationUseOneInputOwner()
    {
        var token = TestContext.Current.CancellationToken;
        using var backend = new ObservedBackend();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend) { QuestionBellEnabled = true };
        await surface.RunAsync(
            async lifetime =>
        {
            var request = new UserQuestionRequest(Guid.NewGuid(), SessionId.New(), RunId.New(), ToolInvocationId.New(), "call", "repo", 0, [new("q", "Choose a destination", [])], DateTimeOffset.UtcNow);
            var shown = backend.ExpectOutputAsync("The Assistant Has a Question");
            var pending = surface.AskAsync(request, lifetime);
            await shown.WaitAsync(lifetime);
            Assert.Equal(1, backend.Bells);
            Assert.False(pending.IsCompleted);
            backend.Input.FeedInput("\u001b[21~\u001b[27u");
            Assert.Equal(UserQuestionStatus.Cancelled, (await pending).Status);
            Assert.False(surface.QuestionBellEnabled);
            shown = backend.ExpectOutputAsync("The Assistant Has a Question");
            pending = surface.AskAsync(request with { RequestId = Guid.NewGuid() }, lifetime);
            await shown.WaitAsync(lifetime);
            Assert.Equal(1, backend.Bells);
            backend.Input.FeedInput("\u001b[15~");
            Assert.Equal(UserQuestionStatus.Cancelled, (await pending).Status);
        },
            token);
    }

    /// <summary>Selection and question share input ownership; Ctrl+C cancels the active run.</summary>
    [Fact(Timeout = 30000)]
    public static async Task QuestionQueuesBehindSelectionAndCtrlCCancelsRun()
    {
        var token = TestContext.Current.CancellationToken;
        using var backend = new ObservedBackend();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend);
        await surface.RunAsync(
            async lifetime =>
        {
            await using var lease = Assert.IsAssignableFrom<IActiveRunInputLease>(surface.BeginActiveRunInput(TimeProvider.System));
            var selectionShown = backend.ExpectOutputAsync("Existing selection");
            var selection = surface.SelectAsync(new InteractionSelectionRequest("Existing selection", [new("one", "Choice")]), lifetime);
            await selectionShown.WaitAsync(lifetime);
            using var questionLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            var request = new UserQuestionRequest(Guid.NewGuid(), SessionId.New(), RunId.New(), ToolInvocationId.New(), "call", "repo", 0, [new("q", "Choose a destination", [])], DateTimeOffset.UtcNow);
            var pending = surface.AskAsync(request, questionLifetime.Token);
            Assert.False(pending.IsCompleted);
            var shown = backend.ExpectOutputAsync("The Assistant Has a Question");
            backend.Input.FeedInput("\u001b[27u");
            await selection;
            await shown.WaitAsync(lifetime);
            Assert.Equal(0, backend.Bells);
            backend.Input.FeedInput("\u001b[99;5u");
            Assert.Equal(ActiveRunInputSignal.CancellationRequested, await lease.ReadAsync(lifetime));
            await questionLifetime.CancelAsync();
            try
            {
                await pending;
                Assert.Fail("The cancelled question completed normally.");
            }
            catch (OperationCanceledException) when (questionLifetime.IsCancellationRequested)
            {
                Assert.True(pending.IsCanceled);
            }
        },
            token);
    }

    private sealed class ObservedBackend : ITerminalBackend
    {
        private Expectation? _expectation;
        private int _bells;

        public TerminalCapabilities Capabilities => Input.Capabilities;

        public Size Size => Input.Size;

        public bool IsInteractive => true;

        internal int Bells => Volatile.Read(ref _bells);

        internal HeadlessBackend Input { get; } = new(80, 24);

        public void Start()
        {
            Input.Start();
        }

        public void Stop()
        {
            Input.Stop();
        }

        public void Dispose()
        {
            Input.Dispose();
        }

        public void Flush()
        {
            Input.Flush();
        }

        public int ReadInput(byte[] buffer, int offset, int count)
        {
            return Input.ReadInput(buffer, offset, count);
        }

        public void Write(string data)
        {
            Interlocked.Add(ref _bells, data.Count(character => character == '\a'));
            Input.Write(data);
            if (Volatile.Read(ref _expectation) is { } expectation
                && Input.PeekOutput().Contains(expectation.Text, StringComparison.Ordinal))
            {
                expectation.Completion.TrySetResult();
            }
        }

        internal Task ExpectOutputAsync(string text)
        {
            _ = Input.TakeOutput();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expectation = new Expectation(text, completion);
            Volatile.Write(ref _expectation, expectation);
            return completion.Task;
        }

        private sealed record Expectation(string Text, TaskCompletionSource Completion);
    }
}
