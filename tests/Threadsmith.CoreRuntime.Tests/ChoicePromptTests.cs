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

    /// <summary>Wrapped and separated entries remain selectable and searchable by hidden details.</summary>
    [Fact]
    public static void SpacedChoicesWrapCleanlyAndKeepDetailsSearchable()
    {
        var modal = new ChoiceModal(
            "Resume session",
            [
                new("first", "Idle · First session\nPreview: A short first sentence followed by more words to wrap.") { Details = "Model: first profile" },
                new("second", "Active · Second session\nPreview: No visible messages") { Details = "Model: second profile" },
            ],
            spaceBetweenOptions: true);
        var cells = new CellBuffer(52, 24);

        modal.Render(new BufferSurface(cells));

        var initial = TUIKit.Testing.Snapshot.ToText(cells);
        var rows = initial.Split('\n');
        var first = Array.FindIndex(rows, row => row.Contains("> Idle · First session", StringComparison.Ordinal));
        var continuation = Array.FindIndex(rows, row => row.Contains("to wrap.", StringComparison.Ordinal));
        var second = Array.FindIndex(rows, row => row.Contains("Active · Second session", StringComparison.Ordinal));
        Assert.True(first >= 0);
        Assert.True(continuation > first + 1);
        Assert.Equal(continuation + 2, second);
        Assert.Equal(rows[first + 1].IndexOf("Preview:", StringComparison.Ordinal), rows[continuation].IndexOf("by more", StringComparison.Ordinal));
        Assert.Empty(rows[continuation + 1].Trim().Trim('│').Trim());
        Assert.DoesNotContain("Model:", initial, StringComparison.Ordinal);

        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("> Active · Second session", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);

        modal.HandlePaste("first profile");
        modal.Render(new BufferSurface(cells));
        var filtered = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("> Idle · First session", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("Second session", filtered, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("Model: first profile", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.True(modal.IsClosed);
    }

    /// <summary>Choice filtering bounds its search without truncating the detail view.</summary>
    [Fact]
    public static void LongDetailsStayVisibleButOnlyTheirPrefixIsSearchable()
    {
        var modal = new ChoiceModal(
            "Sessions",
            [new("session", "Session choice") { Details = "profile prefix\n" + new string('x', 4096) + "\nprofile tail" }]);
        var cells = new CellBuffer(80, 24);
        var surface = new BufferSurface(cells);

        modal.HandlePaste("profile tail");
        modal.Render(surface);
        Assert.DoesNotContain("Session choice", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        for (var index = 0; index < "profile tail".Length; index++)
        {
            modal.HandleKey(KeyEvent.Special(KeyCode.Backspace));
        }

        modal.HandlePaste("PROFILE PREFIX");
        modal.Render(surface);
        Assert.Contains("> Session choice", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.Render(surface);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        modal.Render(surface);
        Assert.Contains("profile tail", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
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
