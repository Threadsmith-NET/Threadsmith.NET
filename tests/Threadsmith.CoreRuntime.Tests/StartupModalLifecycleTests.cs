namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit.Terminal;
using Xunit;

/// <summary>Exercises startup handoffs through the terminal loop and emitted frames.</summary>
[Collection("TUIKit terminal")]
public static class StartupModalLifecycleTests
{
    /// <summary>No workspace frame appears between successful stages or before input readiness.</summary>
    [Fact]
    public static async Task CompletedStagesStayVisibleUntilComposerTakesOver()
    {
        using var backend = new HeadlessBackend(100, 30);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend);
        await surface.RunAsync(
            async token =>
            {
                await surface.ShowStartupAsync("Startup logo", "Loading solution", Task.CompletedTask, token);

                // Force a complete frame while the host is between startup operations.
                var betweenStages = await RepaintAsync(backend, 101, token);
                Assert.Contains("Startup logo", betweenStages, StringComparison.Ordinal);
                Assert.Contains("Loading solution Completed", betweenStages, StringComparison.Ordinal);

                await surface.ShowStartupAsync("Startup logo", "Semantic Loading ...", Task.CompletedTask, token);
                var beforeComposer = await RepaintAsync(backend, 102, token);
                Assert.Contains("Startup logo", beforeComposer, StringComparison.Ordinal);
                Assert.Contains("Semantic Loading ... Completed", beforeComposer, StringComparison.Ordinal);

                var read = surface.ReadComposerAsync(new ComposerRequest("Ready prompt > "), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                var ready = await RepaintAsync(backend, 103, token);
                Assert.DoesNotContain("Startup logo", ready, StringComparison.Ordinal);
                Assert.Contains("Ready prompt >", ready, StringComparison.Ordinal);
                backend.FeedInput("kept\r");
                Assert.Equal("kept", (await read).Text);
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>Failure and cancellation remove both the current splash and the preceding stage.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task UnsuccessfulStageDismissesRetainedStartup(bool cancel)
    {
        using var backend = new HeadlessBackend(100, 30);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend);
        await surface.RunAsync(
            async token =>
            {
                await surface.ShowStartupAsync("Startup logo", "Loading solution", Task.CompletedTask, token);
                await Assert.ThrowsAnyAsync<Exception>(() => surface.ShowStartupAsync(
                    "Startup logo",
                    "Semantic Loading ...",
                    _ => cancel
                        ? Task.FromCanceled(new CancellationToken(true))
                        : Task.FromException(new InvalidOperationException("Load failed")),
                    token));

                var frame = await RepaintAsync(backend, 101, token);
                Assert.DoesNotContain("Startup logo", frame, StringComparison.Ordinal);
                Assert.Contains(cancel ? "Cancelled" : "Failed", frame, StringComparison.Ordinal);
            },
            TestContext.Current.CancellationToken);
    }

    private static async Task<string> RepaintAsync(HeadlessBackend backend, int width, CancellationToken cancellationToken)
    {
        _ = backend.TakeOutput();
        backend.Resize(width, 30);
        var output = new StringBuilder();
        while (true)
        {
            output.Append(backend.TakeOutput());
            var frame = output.ToString();
            var main = frame.IndexOf("MAIN", StringComparison.Ordinal);
            if (main >= 0 && frame.LastIndexOf(Ansi.EndSynchronizedUpdate, StringComparison.Ordinal) > main)
            {
                return frame;
            }

            await Task.Delay(10, cancellationToken);
        }
    }
}
