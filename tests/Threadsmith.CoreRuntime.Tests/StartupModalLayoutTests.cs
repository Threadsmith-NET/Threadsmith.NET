namespace Threadsmith.CoreRuntime.Tests;

using System.Diagnostics;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using Xunit;

/// <summary>Verifies automatic overflow and textual status at supported terminal sizes.</summary>
public static class StartupModalLayoutTests
{
    /// <summary>Optional decoration and old diagnostics cannot squeeze out a full banner that fits.</summary>
    [Theory]
    [InlineData(17, 0)]
    [InlineData(24, 8)]
    public static void FullLogoTakesPriorityOverDecorationAndDiagnosticHistory(int height, int detailCount)
    {
        var modal = new StartupModal(StartupBanner.Text, "Loading solution", [], () => { }, _ => CellStyle.Default)
        {
            Progress = () => [new(default, SemanticStartupPhaseState.Running, TimeSpan.FromSeconds(2)) { Label = "Active work" }],
            TotalElapsed = () => TimeSpan.FromSeconds(5),
            Tips = new StartupTips(["Tips remain visible."]),
            Details = Enumerable.Range(0, detailCount).Select(index => $"Diagnostic {index}").ToArray(),
        };
        var cells = new CellBuffer(100, height);
        modal.Render(new BufferSurface(cells));
        var text = TUIKit.Testing.Snapshot.ToText(cells);

        foreach (var line in StartupBanner.Text.Split('\n'))
        {
            Assert.Contains(line, text, StringComparison.Ordinal);
        }

        Assert.Contains("Tip: Tips remain visible.", text, StringComparison.Ordinal);
        Assert.Contains("Active work", text, StringComparison.Ordinal);
        Assert.Contains("5.0s Total startup", text, StringComparison.Ordinal);
    }

    /// <summary>Completed history cannot displace the original banner or the tip footer.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(100, 40)]
    public static void LogoAndTipsStayVisibleAsCompletedHistoryOverflows(int width, int height)
    {
        var rows = new List<SemanticStartupPhaseSnapshot>
        {
            new(default, SemanticStartupPhaseState.Running, TimeSpan.FromSeconds(2)) { Label = "Active work" },
        };
        var modal = new StartupModal(StartupBanner.Text, null, [], () => { }, _ => CellStyle.Default)
        {
            Progress = () => rows,
            TotalElapsed = () => TimeSpan.FromSeconds(5),
            Tips = new StartupTips(["Tips remain visible."]),
        };
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        var initialText = TUIKit.Testing.Snapshot.ToText(cells);
        var initialLines = initialText.Split('\n');
        var tipRow = Array.FindIndex(initialLines, line => line.Contains("Tip: Tips remain visible.", StringComparison.Ordinal));
        Assert.True(tipRow >= 0);

        rows.AddRange(Enumerable.Range(0, 60).Select(index => new SemanticStartupPhaseSnapshot(default, SemanticStartupPhaseState.Completed, TimeSpan.FromSeconds(1)) { Label = $"History {index}" }));
        modal.Render(new BufferSurface(cells));
        var text = TUIKit.Testing.Snapshot.ToText(cells);

        Assert.Contains("Tip: Tips remain visible.", text.Split('\n')[tipRow], StringComparison.Ordinal);
        Assert.Contains("Active work", text, StringComparison.Ordinal);
        Assert.Contains("5.0s Total startup", text, StringComparison.Ordinal);
        Assert.Contains("History 59", text, StringComparison.Ordinal);
        Assert.DoesNotContain("History 0", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n'), line => line.Contains("Running", StringComparison.Ordinal));
        if (width >= 80)
        {
            foreach (var line in StartupBanner.Text.Split('\n'))
            {
                Assert.Contains(line, initialText, StringComparison.Ordinal);
                Assert.Contains(line, text, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Contains("Threadsmith.NET", text, StringComparison.Ordinal);
        }
    }

    /// <summary>Old completed history cannot displace concurrent active work or the total timer.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(100, 30)]
    public static void ActiveRowsAndTotalSurviveOverflow(int width, int height)
    {
        var rows = Enumerable.Range(0, 60).Select(index => new SemanticStartupPhaseSnapshot(default, SemanticStartupPhaseState.Completed, TimeSpan.FromSeconds(1)) { Label = $"History {index}" }).ToList();
        rows.Insert(0, new(default, SemanticStartupPhaseState.Running, TimeSpan.FromSeconds(7)) { Label = "Native skills" });
        rows.Insert(5, new(default, SemanticStartupPhaseState.Running, TimeSpan.FromSeconds(3)) { Label = "Claude skills" });
        var roles = new List<PresentationTextRole>();
        var modal = new StartupModal(StartupBanner.Text, "Loading solution", [], () => { }, role =>
        {
            roles.Add(role);
            return CellStyle.Default;
        })
        {
            Progress = () => rows,
            TotalElapsed = () => TimeSpan.FromSeconds(12),
            Tips = new StartupTips(["A long tip that must not displace active operations."]),
        };
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        var text = TUIKit.Testing.Snapshot.ToText(cells);

        Assert.Contains("Native skills", text, StringComparison.Ordinal);
        Assert.Contains("Claude skills", text, StringComparison.Ordinal);
        Assert.Contains("Running", text, StringComparison.Ordinal);
        Assert.Contains("12.0s Total startup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("History 0", text, StringComparison.Ordinal);
        Assert.Contains(PresentationTextRole.Status, roles);
        Assert.Contains(PresentationTextRole.Brand, roles);
        Assert.Contains("Tip:", text, StringComparison.Ordinal);
    }

    /// <summary>Retained rows refresh on replacement and resize while both progress sources retain display order.</summary>
    [Fact]
    public static void CachedRowsMatchFreshRenderingAfterProgressAndWidthChanges()
    {
        var rows = new List<SemanticStartupPhaseSnapshot>
        {
            new(default, SemanticStartupPhaseState.Completed, TimeSpan.FromSeconds(1)) { Label = "Host phase" },
        };
        SemanticStartupPhaseSnapshot[] completed = [new(default, SemanticStartupPhaseState.Completed, TimeSpan.FromSeconds(2)) { Label = "Outer phase" }];
        SemanticStartupPhaseSnapshot[] semantic = [new(SemanticStartupPhase.OpenWorkspace, SemanticStartupPhaseState.Running, TimeSpan.FromSeconds(3))];
        var modal = new StartupModal(StartupBanner.Text, null, completed, () => { }, _ => CellStyle.Default)
        { Progress = () => rows, SemanticProgress = () => semantic };
        var initial = new CellBuffer(100, 30);
        modal.Render(new BufferSurface(initial));
        var initialText = TUIKit.Testing.Snapshot.ToText(initial);
        Assert.True(initialText.IndexOf("Host phase", StringComparison.Ordinal) < initialText.IndexOf("Outer phase", StringComparison.Ordinal));
        Assert.True(initialText.IndexOf("Outer phase", StringComparison.Ordinal) < initialText.IndexOf("Open/evaluate solution", StringComparison.Ordinal));

        rows[0] = rows[0] with { State = SemanticStartupPhaseState.Failed, Detail = "Fallback available" };
        semantic[0] = semantic[0] with { State = SemanticStartupPhaseState.Completed, Elapsed = TimeSpan.FromSeconds(5) };
        foreach (var width in new[] { 40, 100 })
        {
            var retained = new CellBuffer(width, 30);
            modal.Render(new BufferSurface(retained));
            var fresh = new StartupModal(StartupBanner.Text, null, completed, () => { }, _ => CellStyle.Default)
            { Progress = () => rows, SemanticProgress = () => semantic };
            var expected = new CellBuffer(width, 30);
            fresh.Render(new BufferSurface(expected));
            Assert.Equal(TUIKit.Testing.Snapshot.ToText(expected), TUIKit.Testing.Snapshot.ToText(retained));
        }
    }

    /// <summary>Stable history is repainted without reformatting and segmenting every retained row.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public static void WarmRenderAllocationsStayBoundedWithCompletedHistory(int historyCount)
    {
        var rows = Enumerable.Range(0, historyCount).Select(index => new SemanticStartupPhaseSnapshot(default, SemanticStartupPhaseState.Completed, TimeSpan.FromSeconds(1)) { Label = $"History {index}" }).ToList();
        rows.Add(new(default, SemanticStartupPhaseState.Running, TimeSpan.FromSeconds(2)) { Label = "Active work" });
        var modal = new StartupModal(StartupBanner.Text, null, [], () => { }, _ => CellStyle.Default)
        { Progress = () => rows, TotalElapsed = () => TimeSpan.FromSeconds(3), Tips = new StartupTips(["Stable tip"]) };
        var surface = new BufferSurface(new CellBuffer(100, 30));
        for (var index = 0; index < 10; index++)
        {
            modal.Render(surface);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < 100; index++)
        {
            modal.Render(surface);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper!.WriteLine($"{historyCount} history rows: {allocated / 100} bytes/frame; 100 frames in {elapsed.TotalMilliseconds:0.0}ms");
        Assert.InRange(allocated, 0, 100 * 8192);
    }

    /// <summary>Small terminals explicitly disclose clipped diagnostics and keep acknowledgment separate.</summary>
    [Fact]
    public static void LongFailureAtMinimumSizeDisclosesFullErrorOnExit()
    {
        var modal = new StartupModal(StartupBanner.Text, "Startup", [], () => { }, _ => CellStyle.Default)
        {
            Acknowledge = () => { },
            TotalElapsed = () => TimeSpan.FromSeconds(8),
            Details = ["Invalid configuration in C:/Users/developer/.threadsmith/config.json: Failed to convert configuration value at tools:config:memories:Recall:MaximumResults to type System.Int32. many is not a valid value for Int32."],
        };
        modal.Finish(SemanticStartupPhaseState.Failed);
        var cells = new CellBuffer(40, 12);
        modal.Render(new BufferSurface(cells));
        var lines = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
        var overflow = Array.FindIndex(lines, line => line.Contains("Full error shown on exit.", StringComparison.Ordinal));
        var confirmation = Array.FindIndex(lines, line => line.Contains("press Enter to exit", StringComparison.Ordinal));
        Assert.True(overflow >= 0);
        Assert.Equal(overflow + 2, confirmation);
        Assert.All(lines[confirmation - 1], character => Assert.True(char.IsWhiteSpace(character) || character == '│'));
    }

    /// <summary>Fatal acknowledgment remains readable without color at the smallest supported size.</summary>
    [Theory]
    [InlineData(40, 12, 0)]
    [InlineData(40, 12, 60)]
    [InlineData(100, 30, 60)]
    public static void FailureConfirmationFollowsStatusesWithBlankSeparator(int width, int height, int historyCount)
    {
        var roles = new List<PresentationTextRole>();
        var modal = new StartupModal(StartupBanner.Text, "Startup", [], () => { }, role =>
        {
            roles.Add(role);
            return CellStyle.Default;
        })
        {
            Acknowledge = () => { },
            TotalElapsed = () => TimeSpan.FromSeconds(8),
            Details = ["Cannot initialize database: access denied to the configured location."],
            Progress = () => Enumerable.Range(0, historyCount)
                .Select(index => new SemanticStartupPhaseSnapshot(default, SemanticStartupPhaseState.Completed, TimeSpan.FromSeconds(1)) { Label = $"History {index}" }).ToArray(),
        };
        modal.Finish(SemanticStartupPhaseState.Failed);
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        var text = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("Cannot initialize database:", text, StringComparison.Ordinal);
        Assert.Contains("access denied", text, StringComparison.Ordinal);
        Assert.Contains("Startup Failed", text, StringComparison.Ordinal);
        Assert.Contains("press Enter to exit", text, StringComparison.Ordinal);
        Assert.Contains("Total startup", text, StringComparison.Ordinal);
        Assert.Contains(PresentationTextRole.Error, roles);
        var lines = text.Split('\n');
        var confirmationRow = Array.FindIndex(lines, line => line.Contains("press Enter to exit", StringComparison.Ordinal));
        var totalRow = Array.FindIndex(lines, line => line.Contains("Total startup", StringComparison.Ordinal));
        var errorRow = Array.FindIndex(lines, line => line.Contains("Cannot initialize database:", StringComparison.Ordinal));
        Assert.Equal(totalRow + 1, errorRow);
        Assert.True(confirmationRow > errorRow + 1);
        Assert.All(lines[confirmationRow - 1], character => Assert.True(char.IsWhiteSpace(character) || character == '│'));
        Assert.DoesNotContain(lines.Skip(confirmationRow + 1), line => line.Contains("History", StringComparison.Ordinal)
            || line.Contains("Startup Failed", StringComparison.Ordinal) || line.Contains("Total startup", StringComparison.Ordinal));
    }
}
