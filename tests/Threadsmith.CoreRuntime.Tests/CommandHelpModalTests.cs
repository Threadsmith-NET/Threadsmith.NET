namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using TUIKit.Terminal;
using Xunit;

/// <summary>Verifies readable command columns and help-dialog input ownership.</summary>
[Collection("TUIKit terminal")]
public static class CommandHelpModalTests
{
    /// <summary>Cancelling a help request releases its modal so the next composer can accept input.</summary>
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "The assertion observes the modal task started and cancelled within this test.")]
    public static async Task CancellingHelpRestoresComposer()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            using var helpCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var help = surface.ShowCommandHelpAsync(InteractiveCommandCatalog.All, helpCancellation.Token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            await helpCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => help);
            var read = surface.ReadComposerAsync(new ComposerRequest("Threadsmith > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("ready\r");
            Assert.Equal("ready", (await read).Text);
        },
            timeout.Token);
    }
}
