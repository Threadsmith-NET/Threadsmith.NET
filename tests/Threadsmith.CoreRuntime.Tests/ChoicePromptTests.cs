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

    /// <summary>Provider headings remain separate from choices and participate in filtering.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(80, 24)]
    [InlineData(160, 40)]
    public static void GroupedOptionsKeepHeadingsAndFilterByProviderOrModel(int width, int height)
    {
        var modal = new ChoiceModal(
            "Models",
            [
                new("alpha", "Shared [current]") { Group = "Alpha provider" },
                new("beta", "Shared") { Group = "Beta provider" },
            ]);
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        var initial = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("Alpha provider", initial, StringComparison.Ordinal);
        Assert.Contains("  > Shared [current]", initial, StringComparison.Ordinal);
        Assert.DoesNotContain("> Alpha provider", initial, StringComparison.Ordinal);

        modal.HandlePaste("bEtA provider");
        modal.Render(new BufferSurface(cells));
        var filtered = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("Beta provider", filtered, StringComparison.Ordinal);
        Assert.Contains("  > Shared", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha provider", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("[current]", filtered, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("Beta provider /", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        for (var index = 0; index < "bEtA provider".Length; index++)
        {
            modal.HandleKey(KeyEvent.Special(KeyCode.Backspace));
        }

        modal.HandlePaste("shared");
        var large = new CellBuffer(100, 30);
        modal.Render(new BufferSurface(large));
        var matches = TUIKit.Testing.Snapshot.ToText(large);
        Assert.Contains("Alpha provider", matches, StringComparison.Ordinal);
        Assert.Contains("Beta provider", matches, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.Render(new BufferSurface(large));
        Assert.Contains("  > Shared ", TUIKit.Testing.Snapshot.ToText(large), StringComparison.Ordinal);
        modal.HandlePaste(" missing");
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.False(modal.IsClosed);
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        Assert.True(modal.IsClosed);
    }

    /// <summary>Navigation skips headings and keeps selected models visible in long lists.</summary>
    [Fact]
    public static void GroupedNavigationScrollsToSelectedOptionsAcrossHeadings()
    {
        var options = Enumerable.Range(0, 40)
            .Select(index => new Choice(index.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Model {index:D2}")
            {
                Group = $"Provider {index / 2:D2}",
            }).ToArray();
        var modal = new ChoiceModal("Models", options);
        var cells = new CellBuffer(60, 15);
        modal.Render(new BufferSurface(cells));
        modal.HandleKey(KeyEvent.Special(KeyCode.PageDown));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("  > Model", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("  > Model 39", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.PageUp));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("  > Model", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Home));
        modal.Render(new BufferSurface(cells));
        var first = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("Provider 00", first, StringComparison.Ordinal);
        Assert.Contains("  > Model 00", first, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("  > Model 02", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
    }

    /// <summary>Repeated paging displays every option in both directions, including across provider headings.</summary>
    [Theory]
    [InlineData(60, 15, 2)]
    [InlineData(40, 12, 1)]
    [InlineData(80, 24, 3)]
    [InlineData(60, 15, 0)]
    public static void PagingDoesNotSkipUnseenOptions(int width, int height, int groupSize)
    {
        var options = Enumerable.Range(0, 40).Select(index => new Choice(index.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Model {index:D2}")
        {
            Group = groupSize == 0 ? null : $"Provider {index / groupSize:D2}",
        }).ToArray();
        foreach (var forward in new[] { true, false })
        {
            var modal = new ChoiceModal("Models", options);
            var cells = new CellBuffer(width, height);
            modal.HandleKey(KeyEvent.Special(forward ? KeyCode.Home : KeyCode.End));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var snapshot = string.Empty;
            for (var page = 0; page <= options.Length; page++)
            {
                modal.Render(new BufferSurface(cells));
                snapshot = TUIKit.Testing.Snapshot.ToText(cells);
                foreach (var option in options.Where(option => snapshot.Contains(option.Label, StringComparison.Ordinal)))
                {
                    seen.Add(option.Id);
                }

                modal.HandleKey(KeyEvent.Special(forward ? KeyCode.PageDown : KeyCode.PageUp));
            }

            Assert.Equal(options.Length, seen.Count);
            Assert.Contains(forward ? "> Model 39" : "> Model 00", snapshot, StringComparison.Ordinal);
        }
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
