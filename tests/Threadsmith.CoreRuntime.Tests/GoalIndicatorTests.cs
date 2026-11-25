namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Themes;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Testing;
using Xunit;

/// <summary>Checks the transient indicator in rendered output cells.</summary>
[Collection("TUIKit terminal")]
public static class GoalIndicatorTests
{
    /// <summary>The workspace owns both the spinner and its removal before the next output.</summary>
    [Fact]
    public static async Task WorkspaceShowsAssessmentOnlyUntilTheFirstOutcome()
    {
        const string answer = "The parser passed.";
        const string result = "Met: test verified.";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend);
        await surface.RunAsync(InspectFramesAsync, lifetime.Token);

        async Task InspectFramesAsync(CancellationToken token)
        {
            var size = new Size(80, 16);
            await surface.PresentAsync(new PresentationBatch([new PresentationSourceItem(answer, answer, false)]), token);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activity = new InteractionActivity("Checking continuation", 0, false, TimeProvider.System) { GoalAssessmentId = Guid.NewGuid() };
            var presentation = surface.PresentActivityUntilAsync(activity, release.Task, token);
            try
            {
                var rows = Snapshot.ToText(await surface.CaptureOutputAsync(size, token)).Split('\n');
                var answerRow = Array.FindIndex(rows, line => line.Contains(answer, StringComparison.Ordinal));
                Assert.True(answerRow >= 0);
                Assert.Equal(answerRow + 2, Array.IndexOf(rows, Assert.Single(rows, line => line.Contains(activity.Label, StringComparison.Ordinal))));
                Assert.Empty(rows[answerRow + 1].Trim(' ', '│', '\r'));
            }
            finally
            {
                release.TrySetResult();
                await presentation;
            }

            await surface.PresentAsync(new PresentationBatch([new PresentationSourceItem(result, result, false)]), token);
            var finished = Snapshot.ToText(await surface.CaptureOutputAsync(size, token));
            Assert.Contains(answer, finished, StringComparison.Ordinal);
            Assert.Contains(result, finished, StringComparison.Ordinal);
            Assert.DoesNotContain(activity.Label, finished, StringComparison.Ordinal);
        }
    }

    /// <summary>A two-row output pane gives the live assessment priority over the eviction notice.</summary>
    [Fact]
    public static void SmallPaneRestoresEvictionNoticeAfterAssessment()
    {
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptLines = 2 });
        foreach (var text in new[] { "First\n", "Second\n", "Third\n", "Answer" })
        {
            view.Present(new PresentationBatch([new PresentationSourceItem(text, text, false)]));
        }

        var cells = new CellBuffer(72, 2);
        var surface = new BufferSurface(cells);
        view.GoalActivityFrame = "| Checking continuation";
        view.Render(surface);
        var rows = Snapshot.ToText(cells).Split('\n');
        Assert.Equal("Answer", rows[0].Trim());
        Assert.Equal("| Checking continuation", rows[1].Trim());
        view.GoalActivityFrame = null;
        view.Render(surface);
        Assert.Contains("Older visible output omitted", Snapshot.ToText(cells), StringComparison.Ordinal);
    }

    /// <summary>Hides the spacer on small panes without hiding the indicator.</summary>
    [Theory]
    [InlineData(5, 2)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    public static void OutputShowsOneTransientGapWhenSpacePermits(int height, int indicatorRow)
    {
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationSourceItem("Answer\n\n", "Answer\n\n", false)]));
        var cells = new CellBuffer(40, height);
        var surface = new BufferSurface(cells);
        view.Render(surface);
        var original = Snapshot.ToText(cells);
        view.GoalActivityFrame = "| Checking continuation";
        view.Render(surface);
        var rows = Snapshot.ToText(cells).Split('\n');
        if (height > 1)
        {
            Assert.Equal("Answer", rows[0].Trim());
        }

        Assert.Equal("| Checking continuation", rows[indicatorRow].Trim());
        if (indicatorRow == 2)
        {
            Assert.Empty(rows[1].Trim());
        }

        view.GoalActivityFrame = null;
        view.Render(surface);
        Assert.Equal(original, Snapshot.ToText(cells));
    }
}
