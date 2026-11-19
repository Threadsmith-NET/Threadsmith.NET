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
