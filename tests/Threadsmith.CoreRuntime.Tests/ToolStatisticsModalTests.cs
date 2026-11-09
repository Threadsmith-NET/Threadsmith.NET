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

    /// <summary>Measurements round averages and distinguish unknown from zero.</summary>
    [Fact]
    public static void Formatting_rounds_average_and_preserves_unknown_zero_and_coverage()
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
        Assert.Contains("Average duration: 201 ms", text, StringComparison.Ordinal);
        Assert.Equal("200 ms", ToolStatisticsFormatter.AverageDuration(measured with { TotalDurationMilliseconds = 400 }));
        Assert.Equal("200 ms", ToolStatisticsFormatter.AverageDuration(measured with { TotalDurationMilliseconds = 601, MeasuredInvocationCount = 3 }));
        Assert.Equal("201 ms", ToolStatisticsFormatter.AverageDuration(measured with { TotalDurationMilliseconds = 602, MeasuredInvocationCount = 3 }));
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
        CompleteRequest(modal, new([.. Enumerable.Range(0, 80).Select(index => Row($"stored-tool-{index:00}"))]));
        Render(modal);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        Assert.Contains("stored-tool-79", Render(modal), StringComparison.Ordinal);
        Assert.DoesNotContain("stored-tool-00", Render(modal), StringComparison.Ordinal);
        Assert.Contains("stored-tool-79", Render(modal, 50, 16), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Home));
        Assert.Contains("stored-tool-00", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.PageDown));
        Assert.DoesNotContain("stored-tool-00", Render(modal, 50, 16), StringComparison.Ordinal);
    }

    /// <summary>Every value occupies a table column, with a blank line below the introduction and no details pane.</summary>
    [Fact]
    public static void Table_columns_show_measurements_without_a_details_pane()
    {
        var modal = Create();
        modal.Requests.TryRead(out _);
        var row = Row("stored-tool") with
        {
            TotalCount = 3,
            SuccessCount = 2,
            FailureCount = 1,
            TotalDurationMilliseconds = 401,
            MeasuredInvocationCount = 2,
            LastDurationMilliseconds = null,
        };
        CompleteRequest(modal, new([row]));

        var text = Render(modal, 240, 24);
        var lines = text.Split('\n');
        var introduction = Array.FindIndex(lines, line => line.Contains("Stored tools, including unavailable", StringComparison.Ordinal));
        Assert.True(introduction >= 0);
        Assert.DoesNotContain("Tool ID", lines[introduction + 1], StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(lines[introduction + 1].Trim(' ', '│')));
        var columns = ReadColumns(modal, 240, 24);
        Assert.Contains("Tool ID", columns, StringComparison.Ordinal);
        Assert.Contains("Total", columns, StringComparison.Ordinal);
        Assert.Contains("Success", columns, StringComparison.Ordinal);
        Assert.Contains("Failure", columns, StringComparison.Ordinal);
        Assert.Contains("Last invocation (UTC)", columns, StringComparison.Ordinal);
        Assert.Contains("1970-01-01T00:00:00+00:00", columns, StringComparison.Ordinal);
        var header = Assert.Single(lines, line => line.Contains("Last invocation (UTC)", StringComparison.Ordinal));
        Assert.EndsWith("Last invocation (UTC)", header.TrimEnd(' ', '│'), StringComparison.Ordinal);
        Assert.Contains("Total duration", columns, StringComparison.Ordinal);
        Assert.Contains("401 ms", columns, StringComparison.Ordinal);
        Assert.Contains("Average duration", columns, StringComparison.Ordinal);
        Assert.Contains("201 ms", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("200.5 ms", columns, StringComparison.Ordinal);
        Assert.Contains("Last-run duration", columns, StringComparison.Ordinal);
        Assert.Contains("unavailable", columns, StringComparison.Ordinal);
        Assert.Contains("Measured invocations", columns, StringComparison.Ordinal);
        Assert.Contains("2/3", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("Tab list/details", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("Tool ID:", columns, StringComparison.Ordinal);
    }

    /// <summary>The final column displays UTC timestamps without fractional seconds.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1_234_567)]
    [InlineData(9_999_999)]
    public static void Timestamp_is_last_and_displays_whole_seconds(int fractionalTicks)
    {
        var row = Row("timestamp-tool") with
        {
            LastInvokedAtUtc = new DateTimeOffset(2026, 7, 10, 12, 34, 56, TimeSpan.FromHours(2)).AddTicks(fractionalTicks),
        };

        var prepared = ToolStatisticsTableSnapshot.Create(new([row]), TestContext.Current.CancellationToken);

        Assert.Equal("Last invocation (UTC)", ToolStatisticsTableSnapshot.ColumnNames[^1]);
        Assert.Equal("2026-07-10T10:34:56+00:00", Assert.Single(prepared.Rows).Cells[^1]);
        Assert.Equal(row.LastInvokedAtUtc.Ticks % TimeSpan.TicksPerSecond, fractionalTicks);
    }

    /// <summary>Header labels use the output-pane background, not the background inherited by selected rows.</summary>
    [Theory]
    [InlineData(false, "black")]
    [InlineData(false, "blue")]
    [InlineData(true, "black")]
    public static void Table_uses_host_selection_and_row_styles(bool suppress, string background)
    {
        var styles = CreateStyles(suppress, background);
        var modal = new ToolStatisticsModal(styles.Resolve, () => { }, () => { }, () => true);
        modal.Requests.TryRead(out _);
        CompleteRequest(modal, new([Row("selected-tool"), Row("ordinary-tool")]));
        var cells = RenderCells(modal, 240, 24);
        var expectedHeader = styles.Resolve(PresentationTextRole.Status)
            .WithBackground(styles.Resolve(PresentationTextRole.OutputStreamPaneRole).Background);
        var lines = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
        var headerY = Array.FindIndex(lines, line => line.Contains("Tool ID", StringComparison.Ordinal));
        Assert.True(headerY >= 0);
        foreach (var label in ToolStatisticsTableSnapshot.ColumnNames)
        {
            var left = lines[headerY].IndexOf(label, StringComparison.Ordinal);
            Assert.True(left >= 0, label);
            for (var x = left; x < left + label.Length; x++)
            {
                Assert.Equal(expectedHeader, cells.Get(x, headerY).Style);
            }
        }

        Assert.Equal(styles.Resolve(PresentationTextRole.SelectionPrompt), TextCell(cells, "selected-tool").Style);
        Assert.Equal(styles.Resolve(PresentationTextRole.Default), TextCell(cells, "ordinary-tool").Style);
        if (suppress)
        {
            Assert.Equal(CellStyle.Default, TextCell(cells, "Tool ID").Style);
            Assert.Equal(CellStyle.Default, TextCell(cells, "selected-tool").Style);
            Assert.Equal(CellStyle.Default, TextCell(cells, "ordinary-tool").Style);
        }
    }

    /// <summary>Native table rows retain the supplied order and selected identity across refresh.</summary>
    [Fact]
    public static void Table_preserves_snapshot_order_and_selection_on_refresh()
    {
        var styles = CreateStyles(false);
        var modal = new ToolStatisticsModal(styles.Resolve, () => { }, () => { }, () => true);
        modal.Requests.TryRead(out _);
        var rows = new[] { Row("z-first"), Row("a-second"), Row("m-third") };
        CompleteRequest(modal, new([.. rows]));
        var text = Render(modal);
        Assert.True(text.IndexOf("z-first", StringComparison.Ordinal) < text.IndexOf("a-second", StringComparison.Ordinal), text);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        modal.HandleKey(KeyEvent.Special(KeyCode.F5));
        Assert.True(modal.Requests.TryRead(out var reset));
        Assert.False(reset);

        // Move the selected ID to a new index: retaining the old index or resetting to zero must fail.
        CompleteRequest(modal, new([Row("new-first"), rows[2], rows[0], rows[1], .. Enumerable.Range(0, 70).Select(index => Row($"new-{index:00}"))]));
        var cells = RenderCells(modal, 40, 12);
        Assert.Equal(styles.Resolve(PresentationTextRole.SelectionPrompt), TextCell(cells, "m-third").Style);
        Assert.Equal(styles.Resolve(PresentationTextRole.Default), TextCell(cells, "new-first").Style);
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        cells = RenderCells(modal, 40, 12);
        Assert.Equal(styles.Resolve(PresentationTextRole.SelectionPrompt), TextCell(cells, "z-first").Style);
        Assert.Equal(styles.Resolve(PresentationTextRole.Default), TextCell(cells, "m-third").Style);
    }

    /// <summary>Large projections retain exact, sanitized values and reuse their measured widths.</summary>
    [Fact]
    public static void Preparation_caches_large_unicode_snapshot_and_honors_cancellation()
    {
        var id = new string('界', 256) + "e\u0301-tail\u001b[31m\n";
        var rows = Enumerable.Range(0, 10_000).Select(index => Row(id + index)).ToArray();
        var prepared = ToolStatisticsTableSnapshot.Create(new([.. rows]), TestContext.Current.CancellationToken);
        Assert.Equal(rows.Length, prepared.Rows.Length);
        Assert.Equal(9, prepared.ColumnWidths.Length);
        Assert.Equal(9_999, prepared.RowIndices[rows[9_999].ToolId]);
        Assert.Equal(TranscriptView.Safe(rows[0].ToolId).Replace('\n', ' '), prepared.Rows[0].Cells[0]);
        Assert.DoesNotContain('\u001b', prepared.Rows[0].Cells[0]);
        Assert.Equal("100 ms", prepared.Rows[0].Cells[5]);
        Assert.Equal("1/1", prepared.Rows[0].Cells[7]);
        var measuredWidth = TUIKit.Unicode.Graphemes.Split(prepared.Rows[^1].Cells[0]).Sum(grapheme => grapheme.Width) + 1;
        Assert.Equal(measuredWidth, prepared.ColumnWidths[0]);
        var modal = Create();
        modal.Requests.TryRead(out _);
        modal.CompleteRequest(prepared, null);
        Assert.Equal(prepared.ColumnWidths.Sum(), modal.TableWidth);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ToolStatisticsTableSnapshot.Create(new([.. rows]), cancellation.Token));
    }

    /// <summary>Mouse wheels scroll rows and columns only while over the table and capture is enabled.</summary>
    [Fact]
    public static void Mouse_scrolls_table_with_local_coordinates_and_respects_capture()
    {
        var capture = true;
        var modal = new ToolStatisticsModal(_ => CellStyle.Default, () => { }, () => { }, () => capture);
        modal.Requests.TryRead(out _);
        CompleteRequest(modal, new([.. Enumerable.Range(0, 80).Select(index => Row($"stored-tool-{index:00}"))]));
        Render(modal, 40, 12);
        modal.HandleMouse(new MouseEvent(MouseEventKind.Wheel, MouseButton.WheelDown, 0, 0, KeyModifiers.None, 0));
        Assert.Contains("stored-tool-00", Render(modal, 40, 12), StringComparison.Ordinal);
        capture = false;
        modal.HandleMouse(new MouseEvent(MouseEventKind.Wheel, MouseButton.WheelDown, 5, 5, KeyModifiers.None, 0));
        Assert.Contains("stored-tool-00", Render(modal, 40, 12), StringComparison.Ordinal);
        capture = true;
        modal.HandleMouse(new MouseEvent(MouseEventKind.Wheel, MouseButton.WheelDown, 5, 5, KeyModifiers.None, 0));
        Assert.DoesNotContain("stored-tool-00", Render(modal, 40, 12), StringComparison.Ordinal);
        modal.HandleMouse(new MouseEvent(MouseEventKind.Wheel, MouseButton.WheelRight, 5, 5, KeyModifiers.None, 0));
        var scrolled = Render(modal, 40, 12);
        Assert.DoesNotContain("Tool ID", scrolled, StringComparison.Ordinal);
    }

    /// <summary>Pending work serializes actions, errors discard stale data, and Escape stays available.</summary>
    [Fact]
    public static void Requests_are_serialized_errors_remove_old_snapshot_and_escape_works_while_busy()
    {
        var modal = Create();
        modal.Requests.TryRead(out _);
        CompleteRequest(modal, new([Row("previously-recorded")]));
        Render(modal);
        modal.HandleKey(new KeyEvent(KeyCode.Character, 'r', KeyModifiers.None));
        Assert.True(modal.Requests.TryRead(out var reset));
        Assert.True(reset);
        modal.HandleKey(new KeyEvent(KeyCode.Character, 'r', KeyModifiers.None));
        Assert.False(modal.Requests.TryRead(out _));
        CompleteRequest(modal, null, "storage failed");
        Assert.Contains("storage failed", Render(modal), StringComparison.Ordinal);
        Assert.DoesNotContain("previously-recorded", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F5));
        Assert.True(modal.Requests.TryRead(out var retry));
        Assert.False(retry);
        CompleteRequest(modal, new([]));
        Assert.Contains("No completed tool invocations", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F5));
        modal.Requests.TryRead(out _);
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        CompleteRequest(modal, new([Row("late-result")]));
        Assert.DoesNotContain("late-result", Render(modal), StringComparison.Ordinal);
    }

    /// <summary>Minimum-size layouts keep all columns reachable after resize and sanitize long Unicode IDs.</summary>
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public static void Minimum_size_keeps_scrolled_columns_accessible(int height)
    {
        var modal = Create();
        modal.Requests.TryRead(out _);
        CompleteRequest(modal, new([Row("stored-tool-" + new string('界', 80) + "-tail\u001b[31m")]));
        Render(modal);
        Assert.Contains("stored-tool-", Render(modal, 40, height), StringComparison.Ordinal);

        var columns = ReadColumns(modal, 40, height);
        Assert.DoesNotContain("\u001b", columns, StringComparison.Ordinal);
        Assert.Contains("-tail", columns, StringComparison.Ordinal);
        Assert.Contains("Last invocation (UTC)", columns, StringComparison.Ordinal);
        Assert.Contains("Total duration", columns, StringComparison.Ordinal);
        Assert.Contains("Average duration", columns, StringComparison.Ordinal);
        Assert.Contains("Last-run duration", columns, StringComparison.Ordinal);
        Assert.Contains("Measured invocations", columns, StringComparison.Ordinal);
        Assert.Contains("100 ms", columns, StringComparison.Ordinal);
        Assert.Contains("1/1", columns, StringComparison.Ordinal);
    }

    private static string ReadColumns(ToolStatisticsModal modal, int width, int height)
    {
        var frames = new System.Text.StringBuilder();

        // At most one distinct integer offset per content cell, plus a final stationary frame.
        var seen = new HashSet<int>();
        var previous = -1;
        for (var step = 0; step <= modal.TableWidth + 1; step++)
        {
            var frame = Render(modal, width, height);
            var offset = modal.HorizontalOffset;
            Assert.InRange(offset, 0, modal.TableWidth);
            if (offset == previous)
            {
                return frames.ToString();
            }

            Assert.True(seen.Add(offset), $"Horizontal scrolling cycled to offset {offset}; extent {modal.TableWidth}.");
            Assert.True(offset > previous, $"Right moved backwards from {previous} to {offset}.");
            frames.Append(frame);
            previous = offset;
            modal.HandleKey(KeyEvent.Special(KeyCode.Right));
        }

        Assert.Fail($"Horizontal scrolling did not stop within the {modal.TableWidth}-cell extent.");
        return string.Empty;
    }

    private static ToolStatisticsRow Row(string id) => new(id, 1, 1, 0, DateTimeOffset.UnixEpoch, 100, 1, 100);

    private static ToolStatisticsModal Create() => new(_ => CellStyle.Default, () => { }, () => { }, () => true);

    private static void CompleteRequest(ToolStatisticsModal modal, ToolStatisticsSnapshot? snapshot, string? error = null)
    {
        modal.CompleteRequest(snapshot is null ? null : ToolStatisticsTableSnapshot.Create(snapshot, TestContext.Current.CancellationToken), error);
    }

    private static TuiKitStyles CreateStyles(bool suppress, string background = "black")
    {
        var theme = new TuiTheme(
            "statistics-test",
            [
                new(PresentationTextRole.Default, new(TuiColor.Parse("green"), TuiColor.Parse("grey"))),
                new(PresentationTextRole.OutputStreamPaneRole, new(Background: TuiColor.Parse(background))),
                new(PresentationTextRole.Status, new(TuiColor.Parse("red"), TuiColor.Parse("cyan"), TuiTextDecoration.Underline)),
                new(PresentationTextRole.SelectionPrompt, new(TuiColor.Parse("yellow"), TuiColor.Parse("magenta"), TuiTextDecoration.Bold)),
            ]);
        return new(new ConfiguredTheme("Statistics test", theme, TuiThemeUi.Default, false), suppress);
    }

    private static Cell TextCell(CellBuffer cells, string text)
    {
        var lines = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
        for (var y = 0; y < lines.Length; y++)
        {
            var x = lines[y].IndexOf(text, StringComparison.Ordinal);
            if (x >= 0)
            {
                return cells.Get(x, y);
            }
        }

        Assert.Fail($"Text '{text}' was not rendered.\n{string.Join('\n', lines)}");
        return default;
    }

    private static string Render(ToolStatisticsModal modal, int width = 120, int height = 35)
    {
        return TUIKit.Testing.Snapshot.ToText(RenderCells(modal, width, height));
    }

    private static CellBuffer RenderCells(ToolStatisticsModal modal, int width = 120, int height = 35)
    {
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        return cells;
    }
}
