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

    /// <summary>Discovery Escape neither cancels the draft nor counts toward active-run cancellation.</summary>
    [Theory]
    [InlineData("/rea" + Escape)]
    [InlineData(F3 + Escape)]
    public static async Task DiscoveryDismissalDoesNotArmActiveCancellation(string keys)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            await using var lease = Assert.IsAssignableFrom<IActiveRunInputLease>(surface.BeginActiveRunInput(new FakeTimeProvider()));
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var read = surface.ReadComposerAsync(new ComposerRequest("ordinary"), readLifetime.Token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            var opened = backend.ExpectOutputAsync("Commands");
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

    /// <summary>One active-run Enter submits the exact text at the steering boundary.</summary>
    [Fact]
    public static async Task ActiveRunSubmissionDeliversExactSteeringWithoutSecondEnter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], static () => { }, backend);
        await surface.RunAsync(
            async token =>
        {
            await using (var lease = Assert.IsAssignableFrom<IActiveRunInputLease>(surface.BeginActiveRunInput(new FakeTimeProvider())))
            {
                backend.FeedInput("  guide main\nonly  \r");
                Assert.Equal(ActiveRunInputSignal.SteeringRequested, await lease.ReadAsync(token));
            }

            var input = await surface.ReadComposerAsync(new ComposerRequest("steer > ", ComposerPurpose.Steering), token);
            Assert.True(input.IsSubmitted);
            Assert.Equal("  guide main\nonly  ", input.Text);
            var ordinary = surface.ReadComposerAsync(new ComposerRequest("repo > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("next\r");
            Assert.Equal("next", (await ordinary).Text);
        },
            cancellationToken);
    }

    /// <summary>Finishing before admission preserves the submitted draft for the next turn.</summary>
    [Fact]
    public static async Task ActiveRunFinishingBeforeSteeringRestoresSubmittedText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], static () => { }, backend);
        await surface.RunAsync(
            async token =>
        {
            await using (var lease = Assert.IsAssignableFrom<IActiveRunInputLease>(surface.BeginActiveRunInput(new FakeTimeProvider())))
            {
                backend.FeedInput("keep this\r");
                Assert.Equal(ActiveRunInputSignal.SteeringRequested, await lease.ReadAsync(token));
            }

            var ordinary = surface.ReadComposerAsync(new ComposerRequest("repo > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\r");
            Assert.Equal("keep this", (await ordinary).Text);
        },
            cancellationToken);
    }
}
