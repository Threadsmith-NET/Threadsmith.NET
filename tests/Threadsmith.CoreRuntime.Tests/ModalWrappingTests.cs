namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Modal prose consumes physical rows instead of disappearing at the right border.</summary>
[Collection("TUIKit terminal")]
public static class ModalWrappingTests
{
    /// <summary>Chrome reserves actual display rows and preserves sanitized paragraph boundaries.</summary>
    [Fact]
    public static void ChromeWrappingResizesBodyAndPreservesParagraphs()
    {
        const string subtitle = "Filter: settings and integration providers";
        const string status = "Arrows navigate; Space applies now; Esc closes";
        const string footer = "Declared access: database read and write operations require FullyTrustedAutomation. DETAILTAIL";
        var heights = new List<int>();
        foreach (var width in new[] { 32, 86, 32 })
        {
            var cells = new CellBuffer(width, 24);
            var surface = new BufferSurface(cells);
            var body = ModalFrame.CreateBody(surface, new CachedTextRun(), _ => CellStyle.Default, "Settings", subtitle, status, footer);
            var bounds = ModalFrame.BodyBounds(surface.Size, "Settings", subtitle, status, footer);
            Assert.Equal(bounds.Height, body.Size.Height);
            body.Fill(new Rect(0, 0, body.Size.Width, body.Size.Height), Cell.Glyph("#", CellStyle.Default, 1));
            var rendered = TUIKit.Testing.Snapshot.ToText(cells);
            Assert.Contains("DETAILTAIL", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(rendered.Split('\n'), line => line.Contains('#') && line.Any(char.IsLetter));
            heights.Add(body.Size.Height);
        }

        Assert.True(heights[1] > heights[0]);
        Assert.Equal(heights[0], heights[2]);
        var lines = ModalFrame.Wrap("界界 e\u0301\n\n" + new string('x', 70) + "TAIL\u001b", 12);
        Assert.Contains(string.Empty, lines);
        Assert.All(lines, line => Assert.True(UnicodeWidth.GetWidth(line) <= 12));
        Assert.Contains("TAIL", string.Concat(lines), StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', string.Concat(lines));
    }

    /// <summary>Extension details reflow with resize and retain a usable minimum-size fallback.</summary>
    [Fact]
    public static void ExtensionReasonWrapsWithoutOverlappingTree()
    {
        const string reason = "[unloaded] Neo4j — Discovered — requires FullyTrustedAutomation — Declared access: database operations are restricted to the configured server. REASONTAIL";
        var modal = new ToggleModal(new("Extensions", [new("neo4j", "Neo4j", "Extensions", false, Reason: reason), new("other", "Another extension", "Extensions", false)]), _ => CellStyle.Default, () => { }, () => { });
        modal.Render(new BufferSurface(new CellBuffer(130, 30)));
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        foreach (var width in new[] { 70, 130, 70 })
        {
            var cells = new CellBuffer(width, 30);
            modal.Render(new BufferSurface(cells));
            var rendered = TUIKit.Testing.Snapshot.ToText(cells);
            Assert.Contains("[unloaded] Neo4j", rendered, StringComparison.Ordinal);
            Assert.Contains("Another extension", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(rendered.Split('\n'), line => line.Contains("Neo4j", StringComparison.Ordinal) && line.Contains("F2 details", StringComparison.Ordinal));
        }

        var minimum = new CellBuffer(40, 12);
        modal.Render(new BufferSurface(minimum));
        Assert.Contains("Neo4j", TUIKit.Testing.Snapshot.ToText(minimum), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.Render(new BufferSurface(minimum));
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        modal.Render(new BufferSurface(minimum));
        Assert.Contains("REASONTAIL", TUIKit.Testing.Snapshot.ToText(minimum), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        var changed = new CellBuffer(70, 30);
        modal.Render(new BufferSurface(changed));
        Assert.DoesNotContain("REASONTAIL", TUIKit.Testing.Snapshot.ToText(changed), StringComparison.Ordinal);
        Assert.False(modal.IsClosed);
    }

    /// <summary>Long details reserve no more than three physical rows at every supported size.</summary>
    [Fact]
    public static void ToggleDetailsPreviewIsCappedAcrossResize()
    {
        var reason = "DETAILONE\n" + string.Concat(Enumerable.Repeat("界e\u0301", 100)) + "\nDETAILTHREE\nHIDDENTAIL";
        var modal = new ToggleModal(new("MCP", [new("server", "Server", "Profiles", false, Reason: reason)]), _ => CellStyle.Default, () => { }, () => { });
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        foreach (var size in new[] { new Size(70, 30), new Size(130, 30), new Size(40, 12), new Size(70, 30) })
        {
            var cells = new CellBuffer(size.Width, size.Height);
            modal.Render(new BufferSurface(cells));
            var rendered = TUIKit.Testing.Snapshot.ToText(cells);
            var lines = rendered.Split('\n');
            var start = Array.FindIndex(lines, line => line.Contains("DETAILONE", StringComparison.Ordinal));
            Assert.True(start >= 0);
            Assert.Contains("界e\u0301", lines[start + 1], StringComparison.Ordinal);
            Assert.Contains("… F2 details", lines[start + 2], StringComparison.Ordinal);
            Assert.DoesNotContain("DETAILTHREE", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("HIDDENTAIL", rendered, StringComparison.Ordinal);
            Assert.Contains("Server", rendered, StringComparison.Ordinal);
        }

        modal.Reconcile("server", new(false) { UpdatedOption = new("server", "Server", "Profiles", false, Reason: "ONE\nTWO\nTHREE") });
        var exact = new CellBuffer(70, 30);
        modal.Render(new BufferSurface(exact));
        Assert.Contains("THREE", TUIKit.Testing.Snapshot.ToText(exact), StringComparison.Ordinal);
        Assert.DoesNotContain("… F2 details", TUIKit.Testing.Snapshot.ToText(exact), StringComparison.Ordinal);
    }

    /// <summary>Full details stay scrollable and read-only, then return to the same selected action target.</summary>
    [Fact]
    public static void ToggleDetailsReturnPreservesSelectionAndActions()
    {
        var reason = string.Join('\n', Enumerable.Repeat("Detail 界 e\u0301", 40)) + "\nHIDDENTAIL";
        var modal = new ToggleModal(new("Skills", [new("first", "First skill", "Native", true), new("selected", "Selected skill", "Native", false, Reason: reason) { Actions = [new("verify", "Verify")] }]), _ => CellStyle.Default, () => { }, () => { }) { SupportsActions = true };
        var cells = new CellBuffer(70, 30);
        modal.Render(new BufferSurface(cells));
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("Skills — details", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Char(' '));
        modal.HandleKey(KeyEvent.Special(KeyCode.F3));
        modal.HandlePaste("not a filter");
        Assert.False(modal.Changes.TryRead(out _));
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("HIDDENTAIL", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        modal.Render(new BufferSurface(cells));
        Assert.False(modal.IsClosed);
        Assert.DoesNotContain("HIDDENTAIL", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.DoesNotContain("not a filter", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F3));
        Assert.True(modal.Changes.TryRead(out var action));
        Assert.Equal("item:selected", action.Id);
        Assert.True(action.Actions);
        modal.CompleteChange();
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.HandleKey(KeyEvent.Char(' '));
        Assert.True(modal.Changes.TryRead(out var toggle));
        Assert.Equal("item:selected", toggle.Id);
        Assert.True(toggle.Enabled);
        Assert.False(toggle.Actions);
    }

    /// <summary>Logical selection and scrolling still reach wrapped labels and the final help line.</summary>
    [Fact]
    public static void WrappedChoicesKeepNavigationAndHelpTailReachable()
    {
        var modal = new ChoiceModal("Choose a provider", [new("first", "A provider with a long description that must continue onto another display row CHOICETAIL"), new("last", "Last provider")]);
        foreach (var width in new[] { 60, 120, 60 })
        {
            var cells = new CellBuffer(width, 30);
            modal.HandleKey(KeyEvent.Special(KeyCode.Home));
            modal.Render(new BufferSurface(cells));
            Assert.Contains("CHOICETAIL", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
            modal.HandleKey(KeyEvent.Special(KeyCode.End));
            modal.Render(new BufferSurface(cells));
            Assert.Contains("> Last provider", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        }

        var help = new KeyHelpModal("Keyboard help with a heading that also wraps", [new string('x', 700) + " HELPTAIL"]);
        var helpCells = new CellBuffer(40, 12);
        help.Render(new BufferSurface(helpCells));
        help.HandleKey(KeyEvent.Special(KeyCode.End));
        help.Render(new BufferSurface(helpCells));
        Assert.Contains("HELPTAIL", TUIKit.Testing.Snapshot.ToText(helpCells), StringComparison.Ordinal);
        Assert.False(modal.IsClosed);
    }

    /// <summary>Tree rows, wrapped selections and unused space retain the modal theme after reflow and filtering.</summary>
    [Fact]
    public static void ToggleListPreservesThemeAcrossWrappingAndFiltering()
    {
        var normal = CellStyle.Default.WithForeground(Color.FromPalette(6)).WithBackground(Color.FromPalette(8));
        var selection = normal.WithBackground(Color.FromPalette(4));
        var highlight = selection.WithAttribute(CellAttributes.Reverse, true);
        var label = "Selected skill " + new string('x', 140) + " SELECTEDTAIL";
        var modal = new ToggleModal(new("Skills", [new("first", "First skill", "Native", true), new("last", label, "Native", false)]), role => role == PresentationTextRole.SelectionHighlight ? selection : normal, () => { }, () => { });
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        foreach (var width in new[] { 70, 130, 70 })
        {
            var cells = new CellBuffer(width, 30);
            modal.Render(new BufferSurface(cells));
            var selected = AssertThemedContent(cells, normal, highlight, new Size(120, 24));
            Assert.Contains("SELECTEDTAIL", selected, StringComparison.Ordinal);
            Assert.True(selected.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 1);
            Assert.Contains("First skill", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        }

        modal.HandlePaste("no matching skill");
        var filtered = new CellBuffer(70, 30);
        modal.Render(new BufferSurface(filtered));
        AssertThemedContent(filtered, normal, highlight, new Size(120, 24));
        Assert.DoesNotContain("SELECTEDTAIL", TUIKit.Testing.Snapshot.ToText(filtered), StringComparison.Ordinal);
    }

    /// <summary>Palette native default colors cannot replace themed rows, including after all matches disappear.</summary>
    [Fact]
    public static void CommandPaletteListPreservesThemeAcrossWrappingAndFiltering()
    {
        var normal = CellStyle.Default.WithForeground(Color.FromPalette(6)).WithBackground(Color.FromPalette(8));
        var selection = normal.WithBackground(Color.FromPalette(4));
        var highlight = selection.WithAttribute(CellAttributes.Reverse, true);
        var entry = new InteractiveCommandDescriptor("/selected", "/selected", "Selected command " + new string('x', 140) + " SELECTEDTAIL");
        var discovery = new TuiKitCommandDiscovery([new("/first", "/first", "First command"), entry], _ => { });
        var modal = new CommandPaletteModal(discovery, string.Empty, () => new Size(130, 30), role => role == PresentationTextRole.SelectionHighlight ? selection : normal, () => { }, () => { }, () => { });
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        foreach (var width in new[] { 70, 130, 70 })
        {
            var cells = new CellBuffer(width, 30);
            modal.Render(new BufferSurface(cells));
            var selected = AssertThemedContent(cells, normal, highlight);
            Assert.Contains("SELECTEDTAIL", selected, StringComparison.Ordinal);
            Assert.True(selected.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 1);
            Assert.Contains("First command", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        }

        modal.HandlePaste("no matching command");
        var filtered = new CellBuffer(70, 30);
        modal.Render(new BufferSurface(filtered));
        AssertThemedContent(filtered, normal, highlight);
        Assert.Contains("No matching commands", TUIKit.Testing.Snapshot.ToText(filtered), StringComparison.Ordinal);
    }

    /// <summary>Startup wraps long labels and reserves essential timers when a label exceeds the screen.</summary>
    [Fact]
    public static void StartupLongPhaseLabelWrapsWithoutEllipsis()
    {
        var phase = new SemanticStartupPhaseSnapshot(default, SemanticStartupPhaseState.Running, TimeSpan.Zero)
        {
            Label = "Loading a provider whose descriptive name continues beyond the ordinary startup item column STARTUPTAIL",
        };
        var modal = new StartupModal("Threadsmith.NET", null, [phase], () => { }, _ => CellStyle.Default);
        var cells = new CellBuffer(90, 30);
        modal.Render(new BufferSurface(cells));
        var rendered = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("STARTUPTAIL", rendered, StringComparison.Ordinal);
        Assert.Contains("Running", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("...", rendered, StringComparison.Ordinal);

        var overflow = new StartupModal("Threadsmith.NET", "Loading solution " + new string('x', 230), [], () => { }, _ => CellStyle.Default)
        {
            TotalElapsed = () => TimeSpan.FromSeconds(12),
        };
        overflow.Finish(SemanticStartupPhaseState.Running);
        overflow.Render(new BufferSurface(cells));
        var minimum = new CellBuffer(40, 12);
        overflow.Render(new BufferSurface(minimum));
        var compact = TUIKit.Testing.Snapshot.ToText(minimum);
        Assert.Contains("12.0s Total startup", compact, StringComparison.Ordinal);
        Assert.Contains("Running; label overflow", compact, StringComparison.Ordinal);
    }

    private static string AssertThemedContent(CellBuffer cells, CellStyle normal, CellStyle highlight, Size? preferredSize = null)
    {
        var frame = ModalFrame.Bounds(new Size(cells.Width, cells.Height), preferredSize: preferredSize);
        var content = WorkspaceLayout.ModalContent(new Size(frame.Width, frame.Height));
        var selected = new List<string>();
        var hasBlankNormalRow = false;
        for (var row = frame.Y + content.Y; row < frame.Y + content.Bottom; row++)
        {
            var style = cells.Get(frame.X + content.X, row).Style;
            Assert.True(style == normal || style == highlight, $"Unexpected modal style at row {row}: {style}");
            var text = new System.Text.StringBuilder();
            for (var column = frame.X + content.X; column < frame.X + content.Right; column++)
            {
                var cell = cells.Get(column, row);
                Assert.Equal(style, cell.Style);
                text.Append(cell.Grapheme);
            }

            if (style == highlight)
            {
                selected.Add(text.ToString());
            }
            else if (string.IsNullOrWhiteSpace(text.ToString()))
            {
                hasBlankNormalRow = true;
            }
        }

        Assert.True(hasBlankNormalRow);
        return string.Join('\n', selected);
    }
}
