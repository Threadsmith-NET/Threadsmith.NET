namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Checks statistics projection, navigation, measurements and pending actions.</summary>
public static class ToolStatisticsModalTests
{
    /// <summary>Command discovery uses the shared catalog and parser.</summary>
    [Theory]
    [InlineData("/toolstats", "")]
    [InlineData("/TOOLSTATS\tRESET", "RESET")]
    [InlineData("/toolstats\r\n reset ", "reset")]
    public static void Catalog_and_router_discover_statistics(string text, string argument)
    {
        Assert.True(InteractiveCommandRouter.TryParse(text, out var invocation));
        Assert.NotNull(invocation);
        Assert.Equal("/toolstats", invocation.Descriptor.Name);
        Assert.Equal("/toolstats [reset]", invocation.Descriptor.Usage);
        Assert.False(invocation.Descriptor.IsFrontendLocal);
        Assert.Equal(argument, invocation.Argument);
    }

    /// <summary>Measurements retain fractional averages and distinguish unknown from zero.</summary>
    [Fact]
    public static void Formatting_preserves_unknown_zero_fractional_average_and_coverage()
    {
        var measured = Row("removed-tool") with
        {
            TotalCount = 3,
            SuccessCount = 2,
            FailureCount = 1,
            TotalDurationMilliseconds = 401,
            MeasuredInvocationCount = 2,
            LastDurationMilliseconds = null,
        };
        var text = ToolStatisticsFormatter.Format(new([measured]));
        Assert.Contains("Average duration: 200.5 ms", text, StringComparison.Ordinal);
        Assert.Contains("Last-run duration: unavailable", text, StringComparison.Ordinal);
        Assert.Contains("Measured invocations: 2/3", text, StringComparison.Ordinal);
        Assert.Contains("Last invocation (UTC):", text, StringComparison.Ordinal);
        var zero = measured with { TotalDurationMilliseconds = 0, MeasuredInvocationCount = 1, LastDurationMilliseconds = 0 };
        text = ToolStatisticsFormatter.Format(new([zero]));
        Assert.Contains("Average duration: 0 ms", text, StringComparison.Ordinal);
        Assert.Contains("Last-run duration: 0 ms", text, StringComparison.Ordinal);
        Assert.Contains("Average duration: unavailable", ToolStatisticsFormatter.Format(new([zero with { MeasuredInvocationCount = 0 }])), StringComparison.Ordinal);
    }

    /// <summary>List navigation exposes rows beyond the viewport after resizing.</summary>
    [Fact]
    public static void Navigation_reaches_rows_beyond_viewport_and_survives_resize()
    {
        var modal = Create();
        Assert.True(modal.Requests.TryRead(out var reset));
        Assert.False(reset);
        modal.CompleteRequest(new([.. Enumerable.Range(0, 80).Select(index => Row($"stored-tool-{index:00}"))]), null);
        Render(modal);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        Assert.Contains("> stored-tool-79", Render(modal), StringComparison.Ordinal);
        Assert.Contains("> stored-tool-79", Render(modal, 50, 16), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Home));
        Assert.Contains("> stored-tool-00", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.PageDown));
        Assert.DoesNotContain("> stored-tool-00", Render(modal), StringComparison.Ordinal);
    }

    /// <summary>Selected details wrap safely and remain scrollable.</summary>
    [Fact]
    public static void Details_wrap_safely_and_scroll_to_duration_fields()
    {
        var modal = Create();
        modal.Requests.TryRead(out _);
        var row = Row(new string('x', 300) + "\u001b[31m") with
        {
            TotalCount = 3,
            SuccessCount = 2,
            FailureCount = 1,
            TotalDurationMilliseconds = 400,
            MeasuredInvocationCount = 2,
            LastDurationMilliseconds = null,
        };
        modal.CompleteRequest(new([row]), null);
        Render(modal, 50, 18);
        modal.HandleKey(KeyEvent.Special(KeyCode.Tab));
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        var text = Render(modal, 50, 18);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.Contains("Measured invocations: 2/3", text, StringComparison.Ordinal);
        Assert.Contains("Last-run duration: unavailable", text, StringComparison.Ordinal);
    }

    /// <summary>Pending work serializes actions, errors discard stale data, and Escape stays available.</summary>
    [Fact]
    public static void Requests_are_serialized_errors_remove_old_snapshot_and_escape_works_while_busy()
    {
        var modal = Create();
        modal.Requests.TryRead(out _);
        modal.CompleteRequest(new([Row("previously-recorded")]), null);
        Render(modal);
        modal.HandleKey(new KeyEvent(KeyCode.Character, 'r', KeyModifiers.None));
        Assert.True(modal.Requests.TryRead(out var reset));
        Assert.True(reset);
        modal.HandleKey(new KeyEvent(KeyCode.Character, 'r', KeyModifiers.None));
        Assert.False(modal.Requests.TryRead(out _));
        modal.CompleteRequest(null, "storage failed");
        Assert.Contains("storage failed", Render(modal), StringComparison.Ordinal);
        Assert.DoesNotContain("previously-recorded", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F5));
        Assert.True(modal.Requests.TryRead(out var retry));
        Assert.False(retry);
        modal.CompleteRequest(new([]), null);
        Assert.Contains("No completed tool invocations", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F5));
        modal.Requests.TryRead(out _);
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        modal.CompleteRequest(new([Row("late-result")]), null);
        Assert.DoesNotContain("late-result", Render(modal), StringComparison.Ordinal);
    }

    /// <summary>Minimum-size layouts keep all selected-row fields reachable after resize.</summary>
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public static void Minimum_size_keeps_list_and_scrolled_details_accessible(int height)
    {
        var modal = Create();
        modal.Requests.TryRead(out _);
        modal.CompleteRequest(new([Row("stored-tool")]), null);
        Render(modal);
        Assert.Contains("> stored-tool", Render(modal, 40, height), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Tab));
        var fields = new System.Text.StringBuilder();
        // Navigate the known seven detail fields, not a timed readiness poll.
        for (var index = 0; index < 16; index++)
        {
            fields.Append(Render(modal, 40, height));
            modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        }

        Assert.Contains("Total:", fields.ToString(), StringComparison.Ordinal);
        Assert.Contains("Last invocation (UTC):", fields.ToString(), StringComparison.Ordinal);
        Assert.Contains("Total duration:", fields.ToString(), StringComparison.Ordinal);
        Assert.Contains("Average duration:", fields.ToString(), StringComparison.Ordinal);
        Assert.Contains("Last-run duration:", fields.ToString(), StringComparison.Ordinal);
        Assert.Contains("Measured invocations:", fields.ToString(), StringComparison.Ordinal);
    }

    private static ToolStatisticsRow Row(string id) => new(id, 1, 1, 0, DateTimeOffset.UnixEpoch, 100, 1, 100);

    private static ToolStatisticsModal Create() => new(_ => CellStyle.Default, () => { }, () => { }, () => true);

    private static string Render(ToolStatisticsModal modal, int width = 120, int height = 35)
    {
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        return TUIKit.Testing.Snapshot.ToText(cells);
    }
}
