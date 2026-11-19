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
    /// <summary>Expanded tool text counts encoded controls and its truncation notice within the display budget.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(100)]
    public static void InspectionOutputHonorsEncodedCharacterBudget(int maximum)
    {
        var started = new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), "code_explore");
        var completed = new ToolInvocationCompleted(
            started.SessionId,
            started.OccurredAt,
            started.ToolInvocationId,
            true,
            ModelResultContent: new string('\u001b', 200));
        var text = InteractionPresentationFormatter.FormatToolCompletion(
            started,
            completed,
            false,
            inspectCodeExploreOutput: true,
            maximumInspectionCharacters: maximum);
        var output = string.Join("\n", text.ReplaceLineEndings("\n").Split('\n')
            .SkipWhile(line => !line.EndsWith("Output:", StringComparison.Ordinal))
            .Skip(1)
            .Where(line => line.Length > 0)
            .Select(line => line[(line.IndexOfAny(['│', '└']) + 1)..])
            .Select(line => line.StartsWith(' ') ? line[1..] : line));

        Assert.Contains("Output:", text, StringComparison.Ordinal);
        if (maximum > 1)
        {
            Assert.NotEmpty(output);
            Assert.Contains("[code_explore", output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(output);
        }

        Assert.InRange(output.Length, 0, maximum);
        Assert.DoesNotContain('\u001b', output);
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

    private static async Task<string> WaitForTextAsync(ObservedBackend backend, string text, CancellationToken cancellationToken)
    {
        var output = string.Empty;
        while (!output.Contains(text, StringComparison.Ordinal))
        {
            output += await backend.ReadOutputAsync(cancellationToken);
        }

        return output;
    }

    private sealed class ActivityClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Interlocked.Read(ref _ticks);
        }

        internal void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _ticks, elapsed.Ticks);
        }
    }
}
