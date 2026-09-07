namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Tui.TuiKit;
using Xunit;

/// <summary>Checks exact reversible completion and rejection of stale input destinations.</summary>
public static class ComposerCommandCompletionTests
{
    /// <summary>Completion preserves surrounding text and the previous undo stack.</summary>
    [Theory]
    [InlineData("/rea", 4, "/reasoning")]
    [InlineData("/ReA", 4, "/reasoning")]
    [InlineData("/remainder", 3, "/reasoning")]
    [InlineData("/rea  ", 4, "/reasoning  ")]
    [InlineData("", 0, "/reasoning")]
    [InlineData("  ", 1, "/reasoning")]
    public static void CompletionIsOneUndoableEdit(string text, int caret, string expected)
    {
        var buffer = new ComposerBuffer();
        buffer.Insert(text);
        buffer.MoveTo(caret);
        var completion = CreateCompletion();
        var target = Assert.IsType<ComposerCompletionTarget>(completion.Capture(buffer, ComposerPurpose.Conversation, 1, allowEmpty: true));

        Assert.True(completion.TryApply(target, buffer, ComposerPurpose.Conversation, 1, "/REASONING"));
        Assert.Equal(expected, buffer.Text);
        Assert.Equal("/reasoning".Length, buffer.Caret);
        Assert.Equal(buffer.Caret, buffer.Anchor);
        buffer.Undo();
        Assert.Equal(text, buffer.Text);
        Assert.Equal(caret, buffer.Caret);
        buffer.Redo();
        Assert.Equal(expected, buffer.Text);
        buffer.Undo();
        buffer.Undo();
        Assert.Empty(buffer.Text);
    }

    /// <summary>Neither completion entry point changes prose, arguments, or exact command names.</summary>
    [Theory]
    [InlineData("prose")]
    [InlineData(" /rea")]
    [InlineData("/rea arg")]
    [InlineData("/rea\n")]
    [InlineData("\n")]
    [InlineData("/rea\u001b")]
    [InlineData("/help")]
    [InlineData("/HELP")]
    [InlineData("/help ")]
    public static void PaletteAndInlineRejectIneligibleDrafts(string text)
    {
        var buffer = new ComposerBuffer();
        buffer.Reset(text);
        var completion = CreateCompletion();
        Assert.Null(completion.Capture(buffer, ComposerPurpose.Conversation, 1));
        Assert.Null(completion.Capture(buffer, ComposerPurpose.Conversation, 1, allowEmpty: true));
        Assert.Equal(text, buffer.Text);
    }

    /// <summary>Blank palette drafts are eligible, while selected text is preserved.</summary>
    [Fact]
    public static void OnlyPaletteAcceptsEmptyDraftAndSelectionDisablesCompletion()
    {
        var completion = CreateCompletion();
        var buffer = new ComposerBuffer();
        Assert.Null(completion.Capture(buffer, ComposerPurpose.Conversation, 1));
        Assert.NotNull(completion.Capture(buffer, ComposerPurpose.Conversation, 1, allowEmpty: true));
        buffer.Insert("/rea");
        buffer.SelectAll();
        Assert.Null(completion.Capture(buffer, ComposerPurpose.Conversation, 1, allowEmpty: true));
        buffer.MoveTo(0);
        Assert.Null(completion.Capture(buffer, ComposerPurpose.Conversation, 1));
    }

    /// <summary>Other workflow inputs never receive conversation command completion.</summary>
    [Theory]
    [InlineData(ComposerPurpose.Secondary)]
    [InlineData(ComposerPurpose.Steering)]
    public static void NonConversationPurposesNeverComplete(ComposerPurpose purpose)
    {
        var buffer = new ComposerBuffer();
        buffer.Insert("/rea");
        var completion = CreateCompletion();
        Assert.Null(completion.Capture(buffer, purpose, 1));
        Assert.Null(completion.Capture(buffer, purpose, 1, allowEmpty: true));
    }

    /// <summary>Stale draft, caret, ownership, and identity changes cannot overwrite current input.</summary>
    [Theory]
    [InlineData("edit")]
    [InlineData("caret")]
    [InlineData("selection")]
    [InlineData("undo")]
    [InlineData("buffer")]
    [InlineData("epoch")]
    [InlineData("purpose")]
    [InlineData("unknown")]
    [InlineData("forged")]
    public static void StaleOrUnknownAcceptanceDoesNotMutate(string change)
    {
        var buffer = new ComposerBuffer();
        buffer.Insert("/rea");
        var completion = CreateCompletion();
        var target = Assert.IsType<ComposerCompletionTarget>(completion.Capture(buffer, ComposerPurpose.Conversation, 1));
        var purpose = ComposerPurpose.Conversation;
        var epoch = 1L;
        var name = "/reasoning";
        switch (change)
        {
            case "edit": buffer.Insert("x"); break;
            case "caret": buffer.MoveTo(2); break;
            case "selection": buffer.SelectAll(); break;
            case "undo": buffer.Insert("x"); buffer.Undo(); break;
            case "buffer": buffer = new ComposerBuffer(); buffer.Insert("/rea"); break;
            case "epoch": epoch++; break;
            case "purpose": purpose = ComposerPurpose.Steering; break;
            case "unknown": name = "/not-a-command"; break;
            case "forged": target = target with { End = 0 }; break;
        }

        var before = (buffer.Text, buffer.Caret, buffer.Anchor, buffer.Revision);
        Assert.False(completion.TryApply(target, buffer, purpose, epoch, name));
        Assert.Equal(before, (buffer.Text, buffer.Caret, buffer.Anchor, buffer.Revision));
    }

    /// <summary>Invalid offsets fail before mutation and valid edits restore the original selection on undo.</summary>
    [Fact]
    public static void RangeReplacementPreservesGraphemesAndUndoSelection()
    {
        var buffer = new ComposerBuffer();
        buffer.Reset("e\u0301\U0001f600tail");
        buffer.SelectAll();
        var before = (buffer.Text, buffer.Caret, buffer.Anchor);
        Assert.Throws<ArgumentException>(() => buffer.ReplaceRange(1, 2, "x"));
        Assert.Throws<ArgumentException>(() => buffer.ReplaceRange(2, 3, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ReplaceRange(-1, 0, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ReplaceRange(2, 1, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ReplaceRange(0, 100, "x"));
        Assert.Equal(before, (buffer.Text, buffer.Caret, buffer.Anchor));

        buffer.ReplaceRange(2, 4, "new\r\n");
        Assert.Equal("e\u0301new\ntail", buffer.Text);
        buffer.Undo();
        Assert.Equal(before, (buffer.Text, buffer.Caret, buffer.Anchor));
    }

    /// <summary>Rejected text cannot corrupt the draft or its undo history.</summary>
    [Fact]
    public static void OverLimitReplacementAndMalformedUnicodeLeaveDraftUnchanged()
    {
        var buffer = new ComposerBuffer();
        buffer.Insert("draft");
        var before = (buffer.Text, buffer.Caret, buffer.Anchor, buffer.Revision);
        // The actual production byte boundary is the contract being verified here.
        Assert.Throws<InvalidOperationException>(() => buffer.ReplaceRange(0, 0, new string('x', ComposerBuffer.MaximumDraftBytes)));
        Assert.Throws<System.Text.EncoderFallbackException>(() => buffer.ReplaceRange(0, 0, "\ud800"));
        Assert.Equal(before, (buffer.Text, buffer.Caret, buffer.Anchor, buffer.Revision));
        buffer.Undo();
        Assert.Empty(buffer.Text);
    }

    private static ComposerCommandCompletion CreateCompletion()
    {
        return new(new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, _ => Assert.Fail("Completion executed a command.")));
    }
}
