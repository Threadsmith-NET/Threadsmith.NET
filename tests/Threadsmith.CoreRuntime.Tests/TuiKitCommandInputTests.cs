namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Content;
using TUIKit.Terminal;
using Xunit;

/// <summary>Exercises command discovery through the real input parser and UI owner.</summary>
[Collection("TUIKit terminal")]
public static class TuiKitCommandInputTests
{
    private const string F3 = "\u001b[13~";
    private const string Escape = "\u001b[27u";
    private const string CopyDraft = "\u001b[99;6u";

    /// <summary>Completion updates the draft before subsequent buffered input, without implicit execution.</summary>
    [Theory]
    [InlineData("/rea\t high\r", "/reasoning high")]
    [InlineData("/rea\r high\r", "/reasoning high")]
    [InlineData(F3 + "reasoning effort\r high\r", "/reasoning high")]
    [InlineData("/rea" + F3 + "reasoning effort\r high\r", "/reasoning high")]
    [InlineData("\u001b[200~/rea\u001b[201~\t high\r", "/reasoning high")]
    [InlineData("/rea\u001b[13;5ucontinued\r", "/rea\ncontinued")]
    [InlineData("/rea\ncontinued\r", "/rea\ncontinued")]
    [InlineData("/rea" + Escape + " suffix\r", "/rea suffix")]
    [InlineData("/rea\t\u001a" + Escape + " suffix\r", "/rea suffix")]
    [InlineData("/rea\u001b[1;2D\t\r", "/re    ")]
    [InlineData("ordinary" + F3 + " prose\r", "ordinary prose")]
    [InlineData("/reasoning" + F3 + " high\r", "/reasoning high")]
    [InlineData("/unknown" + F3 + Escape + " arg\r", "/unknown arg")]
    [InlineData(F3 + "discard" + F3 + "ordinary\r", "ordinary")]
    public static async Task BufferedInputPreservesCompletionAndEditorSemantics(string keys, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(keys);
            Assert.Equal(expected, (await read).Text);
        },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Accepting from either view leaves the coordinator read pending until a separate submission.</summary>
    [Theory]
    [InlineData("/rea\t")]
    [InlineData(F3 + "reasoning effort\r")]
    public static async Task AcceptanceDoesNotSubmit(string keys)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new ObservedBackend();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            var copied = backend.ExpectOutput(ClipboardWriter.BuildSequence("/reasoning"));
            backend.Input.FeedInput(keys + CopyDraft);
            await copied.WaitAsync(token);
            Assert.False(read.IsCompleted);
            backend.Input.FeedInput(" high\r");
            Assert.Equal("/reasoning high", (await read).Text);
        },
            timeout.Token);
    }

    /// <summary>Host-owned prompts never inherit ordinary command discovery or ordinary drafts.</summary>
    [Theory]
    [InlineData(ComposerPurpose.Secondary)]
    [InlineData(ComposerPurpose.Steering)]
    public static async Task OtherPurposesDoNotComplete(ComposerPurpose purpose)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("/rea" + Escape + Escape);
            Assert.False((await read).IsSubmitted);

            var secondary = surface.ReadComposerAsync(new ComposerRequest("answer", purpose), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("/rea" + F3 + "\t\r");
            Assert.Equal("/rea    ", (await secondary).Text);

            var resumed = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\t high\r");
            Assert.Equal("/reasoning high", (await resumed).Text);
        },
            timeout.Token);
    }

    /// <summary>Discovery Escape neither cancels the draft nor counts toward active-run cancellation.</summary>
    [Theory]
    [InlineData("/rea" + Escape)]
    [InlineData(F3 + Escape)]
    public static async Task DiscoveryDismissalDoesNotArmActiveCancellation(string keys)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            await using var lease = Assert.IsAssignableFrom<IActiveRunInputLease>(surface.BeginActiveRunInput(TimeProvider.System));
            backend.FeedInput(keys + "\r" + Escape + Escape);
            Assert.Equal(ActiveRunInputSignal.SteeringRequested, await lease.ReadAsync(token));
            Assert.Equal(ActiveRunInputSignal.CancellationArmed, await lease.ReadAsync(token));
            Assert.Equal(ActiveRunInputSignal.CancellationRequested, await lease.ReadAsync(token));
        },
            timeout.Token);
    }

    /// <summary>Input-lifetime transitions close the palette before a new host-owned prompt can receive input.</summary>
    [Fact]
    public static async Task CancelledReadClosesPaletteAndPreservesDraft()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new ObservedBackend();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var read = surface.ReadComposerAsync(new ComposerRequest("ordinary"), readLifetime.Token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            var opened = backend.ExpectOutput("Commands");
            backend.Input.FeedInput("/rea" + F3);
            await opened.WaitAsync(token);
            await readLifetime.CancelAsync();
            try
            {
                await read;
                Assert.Fail("Cancelled read completed normally.");
            }
            catch (OperationCanceledException) when (readLifetime.IsCancellationRequested)
            {
                Assert.True(read.IsCanceled);
            }

            var secondary = surface.ReadComposerAsync(new ComposerRequest("answer", ComposerPurpose.Secondary), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.Input.FeedInput("answer\r");
            Assert.Equal("answer", (await secondary).Text);

            var resumed = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.Input.FeedInput("\t high\r");
            Assert.Equal("/reasoning high", (await resumed).Text);
        },
            timeout.Token);
    }

    private sealed class ObservedBackend : ITerminalBackend
    {
        private Expectation? _expectation;

        public TerminalCapabilities Capabilities => Input.Capabilities;

        public Size Size => Input.Size;

        public bool IsInteractive => true;

        internal HeadlessBackend Input { get; } = new(80, 24);

        public void Start() => Input.Start();

        public void Stop() => Input.Stop();

        public void Dispose() => Input.Dispose();

        public void Flush() => Input.Flush();

        public int ReadInput(byte[] buffer, int offset, int count) => Input.ReadInput(buffer, offset, count);

        public void Write(string data)
        {
            Input.Write(data);
            if (Volatile.Read(ref _expectation) is { } expectation
                && Input.PeekOutput().Contains(expectation.Text, StringComparison.Ordinal))
            {
                expectation.Completion.TrySetResult();
            }
        }

        internal Task ExpectOutput(string text)
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
