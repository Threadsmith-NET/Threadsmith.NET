namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using Xunit;

/// <summary>Checks fixed-row health projection and live, non-scrolling health updates.</summary>
[Collection("TUIKit terminal")]
public static class ToolStatisticsHealthIndicatorTests
{
    /// <summary>Rendering changes only the warning cells, even on narrow surfaces.</summary>
    [Theory]
    [InlineData(120, 35, false)]
    [InlineData(40, 12, false)]
    [InlineData(10, 3, false)]
    [InlineData(2, 1, false)]
    [InlineData(1, 1, false)]
    [InlineData(120, 35, true)]
    public static void Indicator_only_paints_the_existing_bottom_row_and_preserves_final_cell(int width, int height, bool suppress)
    {
        var cells = new CellBuffer(width, height);
        cells.Fill(new Rect(0, 0, width, height), Cell.Glyph("X", CellStyle.Default, 1));
        var indicator = new ToolStatisticsHealthIndicator();

        indicator.Render(new BufferSurface(cells), suppress);

        Assert.Equal("X", cells.Get(width - 1, height - 1).Grapheme);
        Assert.Equal("X", cells.Get(0, height - 1).Grapheme);
        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.Equal("X", cells.Get(x, y).Grapheme);
            }
        }

        if (width > 2)
        {
            var expectedStyle = suppress ? CellStyle.Default : CellStyle.Default.WithForeground(Color.FromPalette(1));
            Assert.Equal(expectedStyle, cells.Get(width - 2, height - 1).Style);
            Assert.Equal("d", cells.Get(width - 2, height - 1).Grapheme);
        }

        if (width >= ToolStatisticsHealthIndicator.Message.Length + 2)
        {
            Assert.Contains(ToolStatisticsHealthIndicator.Message, TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
            Assert.Equal("T", cells.Get(width - 1 - ToolStatisticsHealthIndicator.Message.Length, height - 1).Grapheme);
        }
    }

    /// <summary>The normal idle render loop observes health without a transcript event or draft submission.</summary>
    [Fact]
    public static async Task Live_health_transition_appears_without_submitting_or_altering_the_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        using var backend = new ObservedBackend(120, 35);
        var health = new StubHealth();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend);
        await surface.RunAsync(
            async token =>
        {
            await surface.AttachStartupServicesAsync(null, health, token);
            var read = surface.ReadComposerAsync(new ComposerRequest("Threadsmith > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("draft");
            var draftFrame = await backend.WaitForOutputAsync("draft", token);
            Assert.DoesNotContain(ToolStatisticsHealthIndicator.Message, draftFrame, StringComparison.Ordinal);

            health.Disable();
            var output = await backend.WaitForOutputAsync(ToolStatisticsHealthIndicator.Message, token);
            Assert.Contains(ToolStatisticsHealthIndicator.Message, output, StringComparison.Ordinal);
            Assert.False(read.IsCompleted);
            backend.FeedInput("\r");
            Assert.Equal("draft", (await read).Text);
        },
            ct);
    }

    private sealed class StubHealth : IToolStatisticsHealthSource
    {
        private int _disabled;

        public bool PersistenceDisabled => Volatile.Read(ref _disabled) != 0;

        public void Disable() => Volatile.Write(ref _disabled, 1);
    }
}
