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
    /// <summary>Long usage and descriptions remain reachable in aligned columns at every supported width.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(80, 24)]
    [InlineData(120, 35)]
    public static void ColumnsWrapScrollAndKeepAllHelpReachable(int width, int height)
    {
        var modal = new CommandHelpModal(
            [
                new("/a", "/a", "Alpha"),
                new("/longer", "/longer", "Beta"),
                new("/long", "/long " + string.Concat(Enumerable.Repeat("[argument] ", 20)) + "USAGETAIL", string.Concat(Enumerable.Repeat("Long description 界e\u0301 ", 20)) + "HELPTAIL"),
                new("/last", "/last", "Last help"),
            ],
            _ => CellStyle.Default,
            () => { },
            () => { });
        var cells = new CellBuffer(width, height);
        var surface = new BufferSurface(cells);
        modal.Render(surface);
        var first = TUIKit.Testing.Snapshot.ToText(cells);
        var lines = first.Split('\n');
        var alpha = Assert.Single(lines, line => line.Contains("Alpha", StringComparison.Ordinal));
        var beta = Assert.Single(lines, line => line.Contains("Beta", StringComparison.Ordinal));
        Assert.Equal(alpha.IndexOf("Alpha", StringComparison.Ordinal), beta.IndexOf("Beta", StringComparison.Ordinal));
        Assert.Contains("/a", alpha, StringComparison.Ordinal);
        Assert.Contains("/longer", beta, StringComparison.Ordinal);

        var snapshots = new List<string> { first };
        for (var page = 0; page < 100; page++)
        {
            modal.HandleKey(KeyEvent.Special(KeyCode.PageDown));
            modal.Render(surface);
            snapshots.Add(TUIKit.Testing.Snapshot.ToText(cells));
        }

        var all = string.Join('\n', snapshots);
        Assert.Contains("USAGETAIL", all, StringComparison.Ordinal);
        Assert.Contains("HELPTAIL", all, StringComparison.Ordinal);
        Assert.Contains("Last help", all, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Home));
        modal.Render(surface);
        Assert.Equal(first, TUIKit.Testing.Snapshot.ToText(cells));

        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        modal.Render(new BufferSurface(new CellBuffer(40, 12)));
        modal.Render(new BufferSurface(new CellBuffer(120, 35)));
        Assert.True(modal.HandlePaste("/quit\r"));
        Assert.False(modal.IsClosed);
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        Assert.True(modal.IsClosed);
    }

    /// <summary>Cancelling a help request releases its modal so the next composer can accept input.</summary>
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "The assertion observes the modal task started and cancelled within this test.")]
    public static async Task CancellingHelpRestoresComposer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
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
