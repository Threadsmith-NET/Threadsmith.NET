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
    /// <summary>Wide graphemes cannot shift or hide the status column.</summary>
    [Theory]
    [InlineData(34)]
    [InlineData(86)]
    public static void TruncationPreservesGraphemesAndStatusColumn(int width)
    {
        var row = StartupModal.FormatRow(TimeSpan.Zero, string.Concat(Enumerable.Repeat("界", 30)), SemanticStartupPhaseState.Failed, string.Empty, width);
        var itemWidth = Math.Min(42, width - 17);

        Assert.Equal(17 + itemWidth, UnicodeWidth.GetWidth(row));
        Assert.Contains("...", row, StringComparison.Ordinal);
        Assert.EndsWith(" Failed   ", row, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', row);
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
        Assert.Contains("Startup", text, StringComparison.Ordinal);
        Assert.Contains("Failed", text, StringComparison.Ordinal);
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
            || line.Contains("Failed", StringComparison.Ordinal) || line.Contains("Total startup", StringComparison.Ordinal));
    }
}
