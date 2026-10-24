namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Long approval targets remain inspectable without changing approval semantics.</summary>
[Collection("TUIKit terminal")]
public static class ChoicePromptTests
{
    /// <summary>Mutation review evidence is available in the existing details view before selecting an outcome.</summary>
    [Fact]
    public static void ReviewDetailsRemainVisibleBeforeSelection()
    {
        var modal = new ChoiceModal(
            "Mutation review",
            [new("apply", "Apply approved set"), new("discard", "Discard")],
            reviewDetails: "--- Example.cs\n+++ Example.cs\n- class Example { }\n+ class Changed { }\u001b[31m");
        var cells = new CellBuffer(100, 30);
        modal.Render(new BufferSurface(cells));
        Assert.Contains("Apply approved set", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.Render(new BufferSurface(cells));
        var details = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("class Example", details, StringComparison.Ordinal);
        Assert.Contains("class Changed", details, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', details);
        Assert.False(modal.IsClosed);
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        Assert.True(modal.IsClosed);
    }

    /// <summary>Details wrap unbroken targets, survive resize, and never approve through scrolling.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(80, 24)]
    [InlineData(160, 40)]
    public static void FullPromptRemainsReachable(int width, int height)
    {
        var prompt = "Origin: https://example.test/" + new string('x', 700) + "/TARGETTAIL\u001b[31m";
        var modal = new ChoiceModal(prompt, [new("deny", "Deny"), new("approve", "Approve once")]);
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        var initial = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("Origin:", initial, StringComparison.Ordinal);
        Assert.Contains("Deny", initial, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.Render(new BufferSurface(cells));
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        modal.Render(new BufferSurface(cells));
        var last = TUIKit.Testing.Snapshot.ToText(cells);
        for (var row = 0; row < 100 && !new string([.. last.Where(char.IsLetterOrDigit)]).Contains("TARGETTAIL", StringComparison.Ordinal); row++)
        {
            modal.HandleKey(KeyEvent.Special(KeyCode.Up));
            modal.Render(new BufferSurface(cells));
            last = TUIKit.Testing.Snapshot.ToText(cells);
        }

        Assert.Contains("TARGETTAIL", new string([.. last.Where(char.IsLetterOrDigit)]), StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', last);
        Assert.False(modal.IsClosed);
        modal.Render(new BufferSurface(new CellBuffer(40, 12)));
        modal.Render(new BufferSurface(cells));
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        Assert.True(modal.IsClosed);
        if (width > 120)
        {
            Assert.Contains(initial.Split('\n'), line => line.Count(character => character == '─') > 90);
        }
    }
}
