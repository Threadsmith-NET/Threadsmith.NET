namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Essential input and selection stay usable when modal prose exceeds the viewport.</summary>
[Collection("TUIKit terminal")]
public static class CompactModalInteractionTests
{
    /// <summary>A full-length filter can be cleared without resizing or closing the choice dialog.</summary>
    [Fact]
    public static void ChoiceFilterOverflowRemainsEditable()
    {
        var limits = new TuiResourceLimits();
        var modal = new ChoiceModal("Choose", [new("first", "First")], limits);
        var cells = new CellBuffer(40, 12);
        modal.Render(new BufferSurface(cells));
        modal.HandlePaste(new string('x', limits.MaximumFilterCharacters));
        modal.Render(new BufferSurface(cells));

        modal.HandleKey(KeyEvent.Special(KeyCode.Backspace));
        Assert.Equal(limits.MaximumFilterCharacters - 1, modal.FilterText.Length);
        for (var index = 1; index < limits.MaximumFilterCharacters; index++)
        {
            modal.HandleKey(KeyEvent.Special(KeyCode.Backspace));
        }

        modal.Render(new BufferSurface(cells));
        Assert.Empty(modal.FilterText);
        Assert.Contains("> First", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.False(modal.IsClosed);
    }

    /// <summary>Trailing blank cells cannot save or discard a draft, but action labels remain clickable.</summary>
    [Fact]
    public static void MemoryEditorClicksOnlyActivatePaintedControls()
    {
        foreach (var size in new[] { new Size(40, 12), new Size(80, 24) })
        {
            var modal = new RepositoryMemoryEditModal("Original", _ => CellStyle.Default, new(), () => true, () => { });
            var cells = new CellBuffer(size.Width, size.Height);
            modal.Render(new BufferSurface(cells));
            modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
            modal.HandlePaste("Draft");
            modal.Render(new BufferSurface(cells));
            Assert.True(modal.CanSave);
            var rows = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
            var saveRow = Array.FindIndex(rows, row => row.Contains("[Save Changes]", StringComparison.Ordinal));
            var cancelRow = Array.FindIndex(rows, row => row.Contains("[Cancel, No Changes]", StringComparison.Ordinal));
            Assert.True(saveRow >= 0 && cancelRow > saveRow);
            var bounds = ModalFrame.Bounds(size, large: true);
            var content = WorkspaceLayout.ModalContent(new Size(bounds.Width, bounds.Height));
            var blankX = bounds.X + content.Right - 1;

            modal.HandleMouse(new(MouseEventKind.Press, MouseButton.Left, blankX, cancelRow, KeyModifiers.None, 1));
            Assert.False(modal.IsClosed);
            modal.HandleMouse(new(MouseEventKind.Press, MouseButton.Left, blankX, saveRow, KeyModifiers.None, 1));
            Assert.False(modal.Saves.TryRead(out _));
            Assert.Equal("Draft", modal.Text);

            var saveX = rows[saveRow].IndexOf("[Save Changes]", StringComparison.Ordinal);
            modal.HandleMouse(new(MouseEventKind.Press, MouseButton.Left, saveX, saveRow, KeyModifiers.None, 1));
            Assert.True(modal.Saves.TryRead(out var saved));
            Assert.Equal("Draft", saved);
            modal.SaveFailed("Try again: " + new string('x', 2000));
            modal.Render(new BufferSurface(cells));
            modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
            modal.HandlePaste("Corrected");
            modal.HandleKey(KeyEvent.Char('s', KeyModifiers.Ctrl));
            Assert.True(modal.Saves.TryRead(out var retried));
            Assert.Equal("Corrected", retried);
            modal.SaveFailed("Try again");
            modal.Render(new BufferSurface(cells));
            rows = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
            cancelRow = Array.FindIndex(rows, row => row.Contains("[Cancel, No Changes]", StringComparison.Ordinal));
            var cancelX = rows[cancelRow].IndexOf("[Cancel, No Changes]", StringComparison.Ordinal);
            modal.HandleMouse(new(MouseEventKind.Press, MouseButton.Left, cancelX, cancelRow, KeyModifiers.None, 1));
            Assert.True(modal.IsClosed);
            Assert.False(modal.Saves.TryRead(out _));
        }
    }

    /// <summary>Long details and maximum-length queries cannot displace the highlighted command.</summary>
    [Fact]
    public static void CommandPaletteReservesSelectedResultRow()
    {
        string? inserted = null;
        var discovery = new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, command => inserted = command);
        var modal = new CommandPaletteModal(discovery, "/mcp", () => new Size(40, 12), _ => CellStyle.Default, () => { }, () => { }, () => { });
        var cells = new CellBuffer(40, 12);
        modal.Render(new BufferSurface(cells));
        var highlight = CellStyle.Default.WithAttribute(CellAttributes.Reverse, true);
        Assert.Contains("/mcp", StyledText(cells, highlight), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal("/mcp", inserted);

        // Even the maximum query preview must leave the selected result visible.
        var longName = "/" + new string('x', discovery.Limits.MaximumFilterCharacters - 1);
        discovery = new TuiKitCommandDiscovery([new(longName, longName, "Long command")], command => inserted = command);
        modal = new CommandPaletteModal(discovery, longName, () => new Size(40, 12), _ => CellStyle.Default, () => { }, () => { }, () => { });
        modal.Render(new BufferSurface(cells));
        Assert.Contains("/xxxx", StyledText(cells, highlight), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal(longName, inserted);
    }

    /// <summary>Input usage and enlargement guidance take priority over explanatory metadata.</summary>
    [Fact]
    public static void CompactContextUsageKeepsInputSummaryAndGuidance()
    {
        var snapshot = new ContextUsageSnapshot
        {
            RunId = RunId.New(),
            InvocationId = Guid.NewGuid(),
            Stage = "generation",
            ModelName = "Example model",
            CapturedAt = DateTimeOffset.UnixEpoch,
            InputTokens = 1234,
            ContextWindow = 10000,
            EstimationBasis = "Estimates include serialized request content and tool schemas; provider accounting may differ.",
        };
        var modal = new ContextUsageModal(snapshot, _ => CellStyle.Default, () => { }, () => { });
        foreach (var size in new[] { new Size(40, 12), new Size(130, 35), new Size(40, 12) })
        {
            var cells = new CellBuffer(size.Width, size.Height);
            modal.Render(new BufferSurface(cells));
            var rendered = TUIKit.Testing.Snapshot.ToText(cells);
            Assert.Contains($"Input {snapshot.InputTokens:N0}", rendered, StringComparison.Ordinal);
            if (size.Height == 12)
            {
                Assert.Contains("Enlarge terminal for graphs;", rendered, StringComparison.Ordinal);
                Assert.Contains("closes.", rendered, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Returning focus to choices reveals the answer while retaining the Other draft.</summary>
    [Fact]
    public static void QuestionChoicesRemainVisibleAfterLeavingOtherEditor()
    {
        var request = new UserQuestionRequest(
            Guid.NewGuid(),
            SessionId.New(),
            RunId.New(),
            ToolInvocationId.New(),
            "call",
            "repository",
            0,
            [new("first", "Which option?", [new("a", "First"), new("b", "Second")]), new("next", "Explain why", [])],
            DateTimeOffset.UnixEpoch);
        var limits = new TuiResourceLimits();
        var draft = new QuestionDraft(request, limits);
        var modal = new QuestionModal(draft, limits);
        var cells = new CellBuffer(40, 16);
        modal.Render(new BufferSurface(cells));
        modal.HandleKey(KeyEvent.Char('3'));
        modal.Render(new BufferSurface(cells));
        modal.HandlePaste("Retained answer");
        modal.HandleKey(KeyEvent.Special(KeyCode.Tab, KeyModifiers.Shift));
        modal.Render(new BufferSurface(cells));

        Assert.Contains("> [x] 3. Other", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Equal("Retained answer", draft.Editors[0].Text);
        Assert.True(draft.OtherSelected[0]);
        modal.HandleKey(KeyEvent.Special(KeyCode.Up));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("> [ ] 2. Second", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Char(' '));
        Assert.Equal("b", draft.SelectedOptions[0]);
        Assert.False(modal.IsClosed);
    }

    /// <summary>Locked labels include their prefix and native character width before reflow.</summary>
    [Fact]
    public static void LockedToggleLabelWrapsWithoutLosingTail()
    {
        var label = "Profile " + new string('x', 120) + string.Concat(Enumerable.Repeat("e\u0301", 12)) + " LOCKEDTAIL";
        var modal = new ToggleModal(new("MCP", [new("profile", label, "Profiles", false, Locked: true)]), _ => CellStyle.Default, () => { }, () => { });
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        foreach (var width in new[] { 70, 130, 70 })
        {
            var cells = new CellBuffer(width, 30);
            modal.Render(new BufferSurface(cells));
            var rendered = TUIKit.Testing.Snapshot.ToText(cells);
            Assert.Contains("[locked] Profile", rendered, StringComparison.Ordinal);
            Assert.Contains("LOCKEDTAIL", rendered, StringComparison.Ordinal);
        }

        modal.HandleKey(KeyEvent.Char(' '));
        Assert.False(modal.Changes.TryRead(out _));
    }

    private static string StyledText(CellBuffer cells, CellStyle style)
    {
        var text = new StringBuilder();
        for (var y = 0; y < cells.Height; y++)
        {
            for (var x = 0; x < cells.Width; x++)
            {
                var cell = cells.Get(x, y);
                if (cell.Style == style)
                {
                    text.Append(cell.Grapheme);
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
