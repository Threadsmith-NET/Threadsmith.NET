namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Themes;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Terminal;
using Xunit;

/// <summary>Checks live tool rendering before the pending operation can complete.</summary>
[Collection("TUIKit terminal")]
public static class ToolActivityRenderingTests
{
    /// <summary>The native render loop paints running activity and updated elapsed time before any result.</summary>
    [Theory]
    [InlineData(ToolActivitySourceKind.Mcp, "MCP: Green Street/search_sectors")]
    [InlineData(ToolActivitySourceKind.BuiltIn, "TOOLS: search_sectors")]
    public static async Task PendingToolIsPaintedAndTimerAdvancesBeforeCompletion(ToolActivitySourceKind kind, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(120, 28);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        var clock = new ActivityClock();
        var started = new ToolInvocationStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ToolInvocationId.New(),
            "search_sectors",
            Source: new ToolActivitySource(kind, "Green Street"));
        var activity = InteractionPresentationFormatter.CreateToolActivity(started, clock, true);
        await surface.RunAsync(
            async token =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await surface.PresentToolActivitiesAsync([activity], token);
            var output = await WaitForTextAsync(backend, label + " - running", token);
            Assert.DoesNotContain("completed", output, StringComparison.Ordinal);
            Assert.False(completion.Task.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(2));
            await WaitForTextAsync(backend, "2.0s", token);
            Assert.False(completion.Task.IsCompleted);
            completion.SetResult();
            await surface.PresentToolActivitiesAsync([], token);
        },
            timeout.Token);
    }

    /// <summary>Live blocks consume content rows temporarily and never accumulate timer frames in history.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public static void LiveBlockOccupiesTranscriptContentWithoutRetainingTimerFrames(int height)
    {
        var clock = new ActivityClock();
        var started = new ToolInvocationStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ToolInvocationId.New(),
            "search_sectors",
            Source: new ToolActivitySource(ToolActivitySourceKind.Mcp, "Green Street"));
        var transcript = new TranscriptView
        {
            ToolActivities = [InteractionPresentationFormatter.CreateToolActivity(started, clock, true)],
        };
        transcript.Present(new PresentationBatch([new PresentationTextItem([new("Previous output\n", PresentationTextRole.Default)])]));
        var cells = new CellBuffer(120, height);
        var buffer = new BufferSurface(cells);
        transcript.Render(buffer);
        Assert.Contains("MCP: Green Street/search_sectors - running", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(3));
        transcript.Render(buffer);
        Assert.Contains("3.0s", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.DoesNotContain(transcript.Lines, line => line.Contains("MCP:", StringComparison.Ordinal));
        transcript.ToolActivities = [];
        var completed = new ToolInvocationCompleted(started.SessionId, DateTimeOffset.UtcNow, started.ToolInvocationId, true, ElapsedMilliseconds: 3000);
        var text = InteractionPresentationFormatter.FormatToolCompletion(started, completed, true);
        transcript.Present(new PresentationBatch([new PresentationTextItem([new(text, PresentationTextRole.ToolSuccess)])]));
        transcript.Render(buffer);
        Assert.DoesNotContain("running", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Single(transcript.Lines, line => line.Contains("MCP:", StringComparison.Ordinal));
    }

    /// <summary>Each concurrent widget keeps its original timer while neighboring invocations finish.</summary>
    [Fact]
    public static void ConcurrentWidgetsRetainIndependentTimersAndClearOnlyCompletedRows()
    {
        var clock = new ActivityClock();
        var start = new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), "first", Source: new ToolActivitySource(ToolActivitySourceKind.Mcp, "Server"));
        var first = InteractionPresentationFormatter.CreateToolActivity(start, clock, true);
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = InteractionPresentationFormatter.CreateToolActivity(start with { ToolInvocationId = ToolInvocationId.New(), ToolName = "second" }, clock, true);
        var transcript = new TranscriptView { ToolActivities = [first, second] };
        var cells = new CellBuffer(100, 8);
        var surface = new BufferSurface(cells);
        clock.Advance(TimeSpan.FromSeconds(3));
        transcript.Render(surface);
        var output = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("MCP: Server/first - running · 5.0s", output, StringComparison.Ordinal);
        Assert.Contains("MCP: Server/second - running · 3.0s", output, StringComparison.Ordinal);
        transcript.ToolActivities = [second];
        transcript.Render(surface);
        output = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.DoesNotContain("Server/first", output, StringComparison.Ordinal);
        Assert.Contains("MCP: Server/second - running · 3.0s", output, StringComparison.Ordinal);
        transcript.ToolActivities = [];
        transcript.Render(surface);
        Assert.DoesNotContain("running", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
    }

    /// <summary>Narrow tool rows preserve elapsed time and report overflow without replacing transcript selection.</summary>
    [Fact]
    public static void NarrowActivityRowsKeepTimersAndReportConcurrentOverflow()
    {
        var clock = new ActivityClock();
        var start = new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), new string('界', 60), Source: new ToolActivitySource(ToolActivitySourceKind.Mcp, "Green Street"));
        var activity = InteractionPresentationFormatter.CreateToolActivity(start, clock, true);
        clock.Advance(TimeSpan.FromSeconds(2));
        var transcript = new TranscriptView { ToolActivities = [activity] };
        var cells = new CellBuffer(36, 3);
        transcript.Render(new BufferSurface(cells));
        Assert.Contains("… · 2.0s", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        transcript.ToolActivities = [activity, activity, activity];
        transcript.Render(new BufferSurface(cells));
        var text = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("2.0s", text, StringComparison.Ordinal);
        Assert.Contains("2 more tools running", text, StringComparison.Ordinal);
    }

    /// <summary>Progress shares the live bracket, updates without retained duplicates, and handles small panes.</summary>
    [Fact]
    public static void ProgressRowsRenderWithinLiveToolBracket()
    {
        var clock = new ActivityClock();
        var activity = InteractionPresentationFormatter.CreateToolActivity(new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), "delegate_agents"), clock, true) with
        {
            ToolProgress = [new("Avery · Explorer: Running", PresentationTextRole.Status), new("Robin · Test Reviewer: Queued", PresentationTextRole.Status)],
        };
        var view = new TranscriptView { ToolActivities = [activity] };
        var cells = new CellBuffer(100, 10);
        view.Render(new BufferSurface(cells));
        var text = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("│ Avery · Explorer: Running", text, StringComparison.Ordinal);
        Assert.Contains("└ Robin · Test Reviewer: Queued", text, StringComparison.Ordinal);
        Assert.Empty(view.Lines);
        clock.Advance(TimeSpan.FromSeconds(3));
        view.ToolActivities = [activity with { ToolProgress = [new("Avery · Explorer: Completed", PresentationTextRole.Status), activity.ToolProgress[1]] }];
        view.Render(new BufferSurface(cells));
        text = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.DoesNotContain("Explorer: Running", text, StringComparison.Ordinal);
        Assert.Contains("Explorer: Completed", text, StringComparison.Ordinal);
        Assert.Contains("3.0s", text, StringComparison.Ordinal);
        var small = new CellBuffer(60, 4);
        view.Render(new BufferSurface(small));
        Assert.Contains("more status lines", TUIKit.Testing.Snapshot.ToText(small), StringComparison.Ordinal);
        view.ToolActivities = [];
        view.Render(new BufferSurface(cells));
        Assert.DoesNotContain("Explorer", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
    }

    private static async Task<string> WaitForTextAsync(HeadlessBackend backend, string text, CancellationToken cancellationToken)
    {
        var output = string.Empty;
        while (!output.Contains(text, StringComparison.Ordinal))
        {
            await Task.Delay(10, cancellationToken);
            output += backend.TakeOutput();
        }

        return output;
    }

    private sealed class ActivityClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
