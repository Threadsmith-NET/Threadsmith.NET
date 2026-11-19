namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using TUIKit.Testing;
using Xunit;

/// <summary>Exercises fenced projection through the retained transcript's real input and reflow paths.</summary>
public static class FencedBlockTranscriptTests
{
    /// <summary>Frames and inline controls share geometry; copying never uses display-normalized text.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void Render_Copy_PreservesBodyAndFrameInset(bool ascii)
    {
        const string source = "```json\r\n\t{invalid}  界😀\r\n\r\n```";
        var copies = new List<string>();
        var view = Create(source, copies);
        var headingStyle = CellStyle.Default.WithAttribute(CellAttributes.Bold, true);
        view.ResolveStyle = role => role == PresentationTextRole.MarkdownHeading ? headingStyle : CellStyle.Default;
        view.UseAsciiBorders = () => ascii;
        var cells = Render(view, 40);
        var row = CopyRow(cells);
        Assert.True(row >= 0);
        Assert.Equal(ascii ? "+" : "╭", cells.Get(0, row).Grapheme);
        Assert.Equal(ascii ? "-" : "─", cells.Get(38, row).Grapheme);
        Assert.Equal(ascii ? "+" : "╮", cells.Get(39, row).Grapheme);
        var labelStart = Snapshot.ToText(cells).Split('\n')[row].IndexOf("json", StringComparison.Ordinal);
        Assert.True(labelStart >= 0);
        Assert.All(Enumerable.Range(labelStart, "json".Length), x => Assert.Equal(headingStyle, cells.Get(x, row).Style));

        view.HandleMouse(Mouse(MouseEventKind.Press, 33, row));
        view.HandleMouse(Mouse(MouseEventKind.Release, 33, row));
        Assert.Equal(["\t{invalid}  界😀\r\n\r\n"], copies);
        Assert.DoesNotContain("```", Snapshot.ToText(cells), StringComparison.Ordinal);
    }

    /// <summary>A click on Copy does not continue a previous prose selection.</summary>
    [Fact]
    public static void CopyAfterSelectingProse_CopiesOnlyFenceWithoutExtendingSelection()
    {
        var copies = new List<string>();
        var view = Create("```text\n/auth openai-codex login\n```\n\nComplete authentication.", copies);
        var cells = Render(view, 40);
        var prose = Array.FindIndex(Snapshot.ToText(cells).Split('\n'), line => line.Contains("Complete", StringComparison.Ordinal));
        var row = CopyRow(cells);
        Assert.True(row >= 0 && prose > row);
        view.HandleMouse(Mouse(MouseEventKind.Press, 0, prose));
        view.HandleMouse(Mouse(MouseEventKind.Release, 8, prose));
        Assert.Equal("Complete", view.SelectedText());

        view.HandleMouse(Mouse(MouseEventKind.Press, 33, row));
        view.HandleMouse(Mouse(MouseEventKind.Move, 34, row));
        view.HandleMouse(Mouse(MouseEventKind.Release, 34, row));

        Assert.Equal(["/auth openai-codex login\n"], copies);
        Assert.Equal("Complete", view.SelectedText());
    }

    /// <summary>Keyboard activation uses the same focused block rather than the first visual match.</summary>
    [Fact]
    public static void Keyboard_FocusesSecondBlockAndCopiesOnce()
    {
        var copies = new List<string>();
        var view = Create("```\nfirst\n```\n\n```\nsecond\n```", copies);
        Render(view, 40);
        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl)));
        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl)));
        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Enter)));
        Assert.Equal(["second\n"], copies);
        Assert.True(view.HandleKey(KeyEvent.Char(' ')));
        Assert.Equal(["second\n", "second\n"], copies);
    }

    /// <summary>Unsupported, empty, unfinished, and unassociated blocks expose no transport action.</summary>
    [Theory]
    [InlineData("```\nx\n```", false, false, "")]
    [InlineData("```\n```", true, false, "empty")]
    [InlineData("```\nx", true, false, "incomplete")]
    [InlineData("```\nx\n```", true, true, "")]
    public static void InvalidCopyStates_HideAction(string source, bool supported, bool mismatch, string state)
    {
        var copies = new List<string>();
        var view = Create(source, copies, supported, mismatch);
        var cells = Render(view, 40);
        Assert.Equal(-1, CopyRow(cells));
        if (state.Length > 0)
        {
            Assert.Contains(state, Snapshot.ToText(cells), StringComparison.Ordinal);
        }

        Assert.False(view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl)));
        Assert.False(view.HandleKey(KeyEvent.Special(KeyCode.Enter)));
        Assert.Empty(copies);
    }

    /// <summary>Current clipboard eligibility leaves scripted source actions reachable.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void UnavailableFence_DoesNotInterceptScriptDisclosure(bool navigate)
    {
        var copies = new List<string>();
        var view = Create("```\nx\n```", copies);
        var script = new PresentationTextItem([new("Result\n", PresentationTextRole.Success)])
        {
            SourceDetail = new ToolSourceDetail
            {
                ToolInvocationId = ToolInvocationId.New(),
                Language = "csharp",
                Availability = ToolSourceAvailability.Available,
                Source = "// preview",
            },
        };
        view.Present(new PresentationBatch([script]));
        Render(view, 50);
        view.ClipboardAvailable = () => false;

        if (navigate)
        {
            Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl)));
        }

        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Enter)));
        Assert.Contains("// preview", Snapshot.ToText(Render(view, 50)), StringComparison.Ordinal);
        Assert.Empty(copies);
    }

    /// <summary>List markers appear once, before the frame instead of in the body.</summary>
    [Theory]
    [InlineData("1.")]
    [InlineData("-")]
    public static void FenceStartingListItem_RendersMarkerOnHeader(string marker)
    {
        var indent = new string(' ', marker.Length + 1);
        var view = Create($"{marker} ```text\n{indent}body\n{indent}```", []);
        var cells = Render(view, 40);
        var header = CopyRow(cells);
        Assert.True(header >= 0);
        var rows = Snapshot.ToText(cells).Split('\n');
        Assert.StartsWith(marker + " ╭", rows[header], StringComparison.Ordinal);
        Assert.StartsWith(indent + "│ body", rows[header + 1], StringComparison.Ordinal);
        Assert.StartsWith(indent + "╰", rows[header + 2], StringComparison.Ordinal);
    }

    /// <summary>Narrow mode has no clipped target and a later wider layout restores the block action.</summary>
    [Fact]
    public static void Resize_CompactFallbackAndRestoredFrame()
    {
        var copies = new List<string>();
        var view = Create("```unknown\n" + new string('界', 25) + "\n```", copies);
        var narrow = Render(view, 16);
        Assert.Equal(-1, CopyRow(narrow));
        Assert.Contains("```", Snapshot.ToText(narrow), StringComparison.Ordinal);
        var wide = Render(view, 40);
        var row = CopyRow(wide);
        Assert.True(row >= 0);
        view.HandleMouse(Mouse(MouseEventKind.Press, 33, row));
        Assert.Equal(new string('界', 25) + "\n", Assert.Single(copies));
    }

    /// <summary>Old hit maps are rejected before repaint after scroll, replacement, and clear.</summary>
    [Theory]
    [InlineData("scroll")]
    [InlineData("replace")]
    [InlineData("clear")]
    public static void ViewportChanges_InvalidateCoordinates(string change)
    {
        var copies = new List<string>();
        var view = Create("```\nx\n```", copies);
        var row = CopyRow(Render(view, 40));
        if (change == "scroll")
        {
            view.HandleKey(KeyEvent.Special(KeyCode.Down));
        }
        else if (change == "replace")
        {
            view.Present(new PresentationBatch([Item("```\nother\n```")]) { ReplaceOutput = true });
        }
        else
        {
            view.ClearView();
        }

        view.HandleMouse(Mouse(MouseEventKind.Press, 33, row));
        Assert.Empty(copies);
    }

    /// <summary>Partial projected eviction cannot leave a full-copy action or revive it through an old coordinate.</summary>
    [Fact]
    public static void Retention_RemovesBlockAction()
    {
        var copies = new List<string>();
        var view = Create("```\nx\n```", copies);
        var row = CopyRow(Render(view, 40));
        view.SetRetentionBudget(1024, 2);
        view.HandleMouse(Mouse(MouseEventKind.Press, 33, row));
        Assert.Empty(copies);
        Assert.Equal(-1, CopyRow(Render(view, 40)));
    }

    /// <summary>Selection across soft wraps copies only logical body text, at either width.</summary>
    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    public static void Selection_AcrossSoftWraps_DoesNotCopyLayoutPadding(int width)
    {
        var body = new string('x', width - 5) + "界e\u0301" + new string('x', 50) + "\ny";
        var view = Create("```\n" + body + "\n```", []);
        Render(view, width);
        view.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        Assert.Equal(body, view.SelectedText().TrimStart('\n'));
    }

    /// <summary>Focus survives width reflow and continues to activate the second block.</summary>
    [Fact]
    public static void Focus_Resize_PreservesBlockIdentity()
    {
        var copies = new List<string>();
        var view = Create("```\nfirst\n```\n\n```\nsecond\n```", copies);
        Render(view, 40);
        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        Render(view, 30);
        view.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal(["second\n"], copies);
    }

    /// <summary>Later host text and input normalization cannot erase the bottom border.</summary>
    [Fact]
    public static void LaterOutputAndInput_PreserveFrameBoundary()
    {
        var view = Create("```\nx\n```", []);
        Render(view, 40);
        view.Present(new PresentationBatch([new PresentationTextItem([new("following", PresentationTextRole.Default)])]));
        view.EchoInput("User > ", "next");
        var text = Snapshot.ToText(Render(view, 40));
        Assert.Contains("╰", text, StringComparison.Ordinal);
        Assert.Contains("following", text, StringComparison.Ordinal);
        Assert.Contains("next", text, StringComparison.Ordinal);
    }

    /// <summary>MAIN and child views use one callback; switching and closing invalidate old coordinates.</summary>
    [Fact]
    public static void AgentViews_CopyRoutingAndLifecycle_RejectsStaleActions()
    {
        var copies = new List<string>();
        var workspace = new AgentViews(_ => CellStyle.Default, canCopy: () => true, copy: text =>
        {
            copies.Add(text);
            return true;
        });
        var session = SessionId.New();
        workspace.Attach(session);
        workspace.Present(new PresentationBatch([Item("```\nmain\n```")]));
        var mainRow = CopyRow(Render(workspace.Main.Transcript, 40));
        var target = new AgentPresentationTarget(session, DelegationId.New(), AgentAssignmentId.New(), RunId.New(), 1);
        workspace.Update(new AgentPresentationSnapshot(target, "Child", AgentRole.Explorer, AgentRunStatus.Running, 1));
        workspace.Present(new PresentationBatch([Item("```\nchild\n```")]) { Target = target });
        var childView = workspace.Ordered.Single(view => view.Snapshot?.Target == target);
        Assert.True(workspace.Select(childView));
        workspace.Main.Transcript.HandleMouse(Mouse(MouseEventKind.Press, 33, mainRow));
        Assert.Empty(copies);
        var child = workspace.Selected.Transcript;
        var childRow = CopyRow(Render(child, 40));
        child.HandleMouse(Mouse(MouseEventKind.Press, 33, childRow));
        Assert.Equal(["child\n"], copies);
        workspace.Update(new AgentPresentationSnapshot(target, "Child", AgentRole.Explorer, AgentRunStatus.Completed, 2));
        workspace.Close(childView);
        child.HandleMouse(Mouse(MouseEventKind.Press, 33, childRow));
        child.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Single(copies);
        mainRow = CopyRow(Render(workspace.Main.Transcript, 40));
        workspace.Main.Transcript.HandleMouse(Mouse(MouseEventKind.Press, 33, mainRow));
        Assert.Equal(["child\n", "main\n"], copies);
        workspace.Attach(SessionId.New());
        workspace.Main.Transcript.HandleMouse(Mouse(MouseEventKind.Press, 33, mainRow));
        workspace.Main.Transcript.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal(2, copies.Count);
        Assert.Contains("main", Snapshot.ToText(Render(workspace.Main.Transcript, 30)), StringComparison.Ordinal);
        Assert.Equal(-1, CopyRow(Render(workspace.Main.Transcript, 40)));
    }

    /// <summary>Fence identity, not content validation or a known language, enables the same action.</summary>
    [Theory]
    [InlineData("text", "prose")]
    [InlineData("json", "{invalid}")]
    [InlineData("yaml", "key: [invalid")]
    [InlineData("xml", "<unclosed>")]
    [InlineData("csv", "a,b\n1,2")]
    [InlineData("log", "ERROR details")]
    [InlineData("markdown", "**literal**")]
    [InlineData("cs", "var x = ;")]
    [InlineData("unknown", "mixed text and {data}")]
    public static void AnyFencedContent_UsesSameCopyAction(string language, string body)
    {
        var copies = new List<string>();
        var view = Create("```" + language + "\n" + body + "\n```", copies);
        var cells = Render(view, 40);
        var row = CopyRow(cells);
        Assert.True(row >= 0);
        view.HandleMouse(Mouse(MouseEventKind.Press, 33, row));
        Assert.Equal(body + "\n", Assert.Single(copies));
    }

    /// <summary>Small continued storage chunks do not add copied line breaks or retain a competing body store.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public static void SmallStorageChunks_PreserveSelectionAndFullCopy(int chunkSize)
    {
        const string body = "abcdefghijk\nsecond";
        var copies = new List<string>();
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptLineCharacters = chunkSize })
        {
            ClipboardAvailable = () => true,
            CopyRequested = value =>
            {
                copies.Add(value);
                return true;
            },
        };
        view.Present(new PresentationBatch([Item("```\n" + body + "\n```")]));
        var cells = Render(view, 40, 80);
        var row = CopyRow(cells);
        Assert.True(row >= 0);
        view.HandleMouse(Mouse(MouseEventKind.Press, 33, row));
        Assert.Equal(body + "\n", Assert.Single(copies));
        view.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        Assert.Equal(body, view.SelectedText().TrimStart('\n'));
    }

    private static TranscriptView Create(string source, List<string> copies, bool supported = true, bool mismatch = false)
    {
        var view = new TranscriptView
        {
            ClipboardAvailable = () => supported,
            CopyRequested = text =>
            {
                copies.Add(text);
                return true;
            },
        };
        var item = Item(source);
        if (mismatch)
        {
            item = item with { RawSource = new string(source.ToCharArray()) };
        }

        view.Present(new PresentationBatch([item]));
        return view;
    }

    private static PresentationMarkdownItem Item(string source)
    {
        var result = new MarkdownParser().Parse(source);
        return new(Assert.IsType<MarkdownDocument>(result.Document), source, result.SafeSource, true);
    }

    private static CellBuffer Render(TranscriptView view, int width, int height = 20)
    {
        var cells = new CellBuffer(width, height);
        view.Render(new BufferSurface(cells));
        return cells;
    }

    private static int CopyRow(CellBuffer cells)
    {
        var lines = Snapshot.ToText(cells).Split('\n');
        return Array.FindIndex(lines, line => line.Contains("[Copy]", StringComparison.Ordinal));
    }

    private static MouseEvent Mouse(MouseEventKind kind, int x, int y, KeyModifiers modifiers = KeyModifiers.None)
        => new(kind, MouseButton.Left, x, y, modifiers, 1);
}
