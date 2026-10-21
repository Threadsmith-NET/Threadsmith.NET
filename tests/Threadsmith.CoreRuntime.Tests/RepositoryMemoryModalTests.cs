namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Stored-memory tabs preserve ownership, full text, navigation and safe rendering.</summary>
public static class RepositoryMemoryModalTests
{
    /// <summary>Native tabs partition intelligence by ownership even when its type is situational.</summary>
    [Theory]
    [InlineData(RepositoryMemoryType.Situational)]
    [InlineData(RepositoryMemoryType.StandingPreference)]
    public static void Tabs_show_separate_columnar_lists_and_full_ids(RepositoryMemoryType intelligenceType)
    {
        var preference = Entry("Address the user directly.", RepositoryMemoryType.StandingPreference);
        var situational = Entry("Use the cancellation pipeline.");
        var intelligence = Entry("Reuse the shared architecture.", intelligenceType) with { Ownership = RepositoryMemoryOwnership.Intelligence };
        var modal = Create([preference, situational, intelligence]);
        var initial = Render(modal);
        Assert.Contains("Preferences", initial, StringComparison.Ordinal);
        Assert.Contains("Situational", initial, StringComparison.Ordinal);
        Assert.Contains("Intelligence", initial, StringComparison.Ordinal);
        Assert.Contains("ID", initial, StringComparison.Ordinal);
        Assert.Contains("Origin", initial, StringComparison.Ordinal);
        Assert.Contains(preference.Text, initial, StringComparison.Ordinal);
        Assert.DoesNotContain(situational.Text, initial, StringComparison.Ordinal);
        Assert.DoesNotContain(intelligence.Text, initial, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Tab));
        Assert.Contains(situational.Text, Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Right));
        Assert.Contains(intelligence.Text, Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        Assert.Contains(intelligence.Id.Value.ToString("D"), Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Left));
        Assert.Contains(situational.Text, Render(modal), StringComparison.Ordinal);
    }

    /// <summary>Long memories remain readable, scroll independently and reflow on terminal resize.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(80, 24)]
    [InlineData(160, 50)]
    public static void Long_rows_scroll_and_resize_without_losing_the_tail(int width, int height)
    {
        var text = string.Join(" ", Enumerable.Repeat("Unicode 界 and e\u0301 content", 300)) + " FINAL-MARKER";
        var modal = Create([Entry(text, RepositoryMemoryType.StandingPreference)]);
        var initial = Render(modal, width, height);
        Assert.DoesNotContain("FINAL-MARKER", initial, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        Assert.Contains("FINAL-MARKER", Render(modal, width, height), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Tab));
        Assert.Contains("0 entries", Render(modal, width, height), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Left));
        Assert.Contains("FINAL-MARKER", Render(modal, width, height), StringComparison.Ordinal);
        Render(modal, 100, 30);
        modal.HandleKey(KeyEvent.Special(KeyCode.Home));
        Assert.Contains("Unicode", Render(modal, 100, 30), StringComparison.Ordinal);
    }

    /// <summary>Mouse coordinates follow the centered frame and capture-disable gate.</summary>
    [Fact]
    public static void Mouse_tabs_respect_capture_and_warnings_are_visible()
    {
        var capture = false;
        var modal = new RepositoryMemoryModal(new([Entry("Ordinary note")], ["Store warning"]), _ => CellStyle.Default, () => { }, () => { }, () => capture);
        var text = Render(modal);
        Assert.Contains("Store warning", text, StringComparison.Ordinal);
        var lines = text.Split('\n');
        var y = Array.FindIndex(lines, line => line.Contains("Preferences", StringComparison.Ordinal) && line.Contains("Situational", StringComparison.Ordinal));
        var x = lines[y].IndexOf("Situational", StringComparison.Ordinal);
        modal.HandleMouse(new MouseEvent(MouseEventKind.Press, MouseButton.Left, x, y, KeyModifiers.None, 1));
        Assert.DoesNotContain("Ordinary note", Render(modal), StringComparison.Ordinal);
        capture = true;
        modal.HandleMouse(new MouseEvent(MouseEventKind.Press, MouseButton.Left, x, y, KeyModifiers.None, 1));
        Assert.Contains("Ordinary note", Render(modal), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", Render(Create([Entry("Unsafe \u001b[31m text", RepositoryMemoryType.StandingPreference)])), StringComparison.Ordinal);
    }

    /// <summary>Each tab sends the selected stable identity and reconciles changes in place.</summary>
    [Theory]
    [InlineData(0, RepositoryMemoryType.StandingPreference, RepositoryMemoryOwnership.Ordinary)]
    [InlineData(1, RepositoryMemoryType.Situational, RepositoryMemoryOwnership.Ordinary)]
    [InlineData(2, RepositoryMemoryType.Situational, RepositoryMemoryOwnership.Intelligence)]
    public static void Selection_edit_delete_and_refresh_preserve_the_tab(int tab, RepositoryMemoryType type, RepositoryMemoryOwnership ownership)
    {
        var first = Entry("First memory", type) with { Ownership = ownership };
        var second = Entry("Second memory", type) with { Ownership = ownership };
        var modal = new RepositoryMemoryModal(new([first, second], []), _ => CellStyle.Default, () => { }, () => { }, () => true) { CanManage = true };
        Render(modal);
        for (var index = 0; index < tab; index++)
        {
            modal.HandleKey(KeyEvent.Special(KeyCode.Tab));
        }

        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.True(modal.Changes.TryRead(out var edit));
        Assert.Equal(second.Id, Assert.IsType<RepositoryMemoryListItem>(edit.Item).Id);
        Assert.False(edit.Delete);
        modal.HandleKey(KeyEvent.Special(KeyCode.Delete));
        Assert.False(modal.Changes.TryRead(out _));
        modal.CompleteChange(new([first, second with { Text = "Changed text" }], []));
        Assert.Contains("Changed text", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Delete));
        Assert.True(modal.Changes.TryRead(out var delete));
        Assert.Equal(second.Id, Assert.IsType<RepositoryMemoryListItem>(delete.Item).Id);
        Assert.True(delete.Delete);
        modal.CompleteChange(new([first], []));
        Render(modal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.True(modal.Changes.TryRead(out edit));
        Assert.Equal(first.Id, Assert.IsType<RepositoryMemoryListItem>(edit.Item).Id);
    }

    /// <summary>Cached rows retain stable identity across removal and content changes.</summary>
    [Fact]
    public static void Refresh_preserves_selection_when_cached_entries_shift_and_updates_changed_rows()
    {
        var first = Entry("First", RepositoryMemoryType.StandingPreference);
        var second = Entry("Second", RepositoryMemoryType.StandingPreference);
        var third = Entry("Third", RepositoryMemoryType.StandingPreference);
        var modal = new RepositoryMemoryModal(new([first, second, third], []), _ => CellStyle.Default, () => { }, () => { }, () => true) { CanManage = true };
        Render(modal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        Render(modal);
        modal.CompleteChange(new([second with { Text = "Revised second", Revision = 2 }, third], ["New warning"]));
        var rendered = Render(modal);
        Assert.Contains("Revised second", rendered, StringComparison.Ordinal);
        Assert.Contains("New warning", rendered, StringComparison.Ordinal);
        Assert.Contains(">" + third.Id.Value.ToString("D")[..8], rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("First", rendered, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.True(modal.Changes.TryRead(out var selected));
        Assert.Equal(third.Id, Assert.IsType<RepositoryMemoryListItem>(selected.Item).Id);
        modal.CompleteChange(new([second with { Text = "Revised second", Revision = 2 }, third], []));
        Assert.DoesNotContain("New warning", Render(modal), StringComparison.Ordinal);
    }

    private static RepositoryMemoryListItem Entry(string text, RepositoryMemoryType type = RepositoryMemoryType.Situational)
        => new(RepositoryMemoryId.New(), text, RepositoryMemoryOrigin.Model, type, RepositoryMemoryOwnership.Ordinary);

    private static RepositoryMemoryModal Create(IReadOnlyList<RepositoryMemoryListItem> entries)
        => new(new(entries, []), _ => CellStyle.Default, () => { }, () => { }, () => true);

    private static string Render(RepositoryMemoryModal modal, int width = 120, int height = 35)
    {
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        return TUIKit.Testing.Snapshot.ToText(cells);
    }
}
