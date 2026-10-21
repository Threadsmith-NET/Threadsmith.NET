namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using TUIKit.Terminal;
using Xunit;

/// <summary>Checks memory drafts, text editing, and explicit save/cancel behavior.</summary>
[Collection("TUIKit terminal")]
public static class RepositoryMemoryEditModalTests
{
    /// <summary>Unchanged and cancelled drafts never emit save requests.</summary>
    [Fact]
    public static void Save_requires_a_change_and_cancel_never_saves()
    {
        var modal = Create("Original text");
        Assert.False(modal.CanSave);
        modal.HandleKey(KeyEvent.Char('s', KeyModifiers.Ctrl));
        Assert.False(modal.Saves.TryRead(out _));
        modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        modal.HandlePaste("Replacement");
        Assert.True(modal.CanSave);
        modal.HandleKey(KeyEvent.Char('z', KeyModifiers.Ctrl));
        Assert.False(modal.CanSave);
        modal.HandleKey(KeyEvent.Char('y', KeyModifiers.Ctrl));
        Assert.True(modal.CanSave);
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        Assert.True(modal.IsClosed);
        Assert.False(modal.Saves.TryRead(out _));
    }

    /// <summary>Clipboard, overwrite, undo, and failed-save recovery share the composer.</summary>
    [Fact]
    public static void Editor_reuses_clipboard_selection_and_grapheme_overwrite()
    {
        var modal = Create("界e\u0301 text");
        string? clipboard = null;
        modal.CopyRequested = text =>
        {
            clipboard = text;
            return true;
        };
        modal.PasteRequested = () => modal.HandlePaste(clipboard ?? string.Empty);
        modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        modal.HandleKey(KeyEvent.Char('c', KeyModifiers.Ctrl));
        Assert.Equal(modal.Text, clipboard);
        modal.HandleKey(KeyEvent.Char('x', KeyModifiers.Ctrl));
        Assert.Equal(string.Empty, modal.Text);
        modal.HandleKey(KeyEvent.Char('v', KeyModifiers.Ctrl));
        Assert.Equal("界e\u0301 text", modal.Text);
        Assert.False(modal.CanSave);
        modal.HandleKey(KeyEvent.Special(KeyCode.Home));
        modal.HandleKey(KeyEvent.Special(KeyCode.Insert));
        modal.HandleKey(KeyEvent.Char('A'));
        modal.HandleKey(KeyEvent.Char('B'));
        Assert.Equal("AB text", modal.Text);
        modal.HandleKey(KeyEvent.Char('z', KeyModifiers.Ctrl));
        Assert.Equal("Ae\u0301 text", modal.Text);
        modal.HandleKey(KeyEvent.Special(KeyCode.Delete));
        Assert.Equal("A text", modal.Text);
        modal.HandleKey(KeyEvent.Special(KeyCode.Insert));
        modal.HandleKey(KeyEvent.Char('!'));
        Assert.Equal("A! text", modal.Text);
        modal.HandleKey(KeyEvent.Special(KeyCode.Tab));
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.True(modal.Saves.TryRead(out var saved));
        Assert.Equal("A! text", saved);
        Assert.False(modal.CanSave);
        modal.SaveFailed("Revision conflict");
        Assert.True(modal.CanSave);
        Assert.Contains("Revision conflict", Render(modal, 80, 24), StringComparison.Ordinal);
        Assert.Equal("A! text", modal.Text);
        modal.HandleKey(KeyEvent.Special(KeyCode.Tab));
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.True(modal.IsClosed);
        Assert.False(modal.Saves.TryRead(out _));
    }

    /// <summary>Editor actions remain visible at the minimum terminal size.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(80, 24)]
    [InlineData(160, 50)]
    public static void Buttons_and_editor_fit_supported_sizes(int width, int height)
    {
        var modal = Create("Memory text");
        var rendered = Render(modal, width, height);
        Assert.Contains("Save Changes", rendered, StringComparison.Ordinal);
        Assert.Contains("disabled", rendered, StringComparison.Ordinal);
        Assert.Contains("Cancel, No Changes", rendered, StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        modal.HandlePaste("Changed");
        Assert.DoesNotContain("disabled", Render(modal, width, height), StringComparison.Ordinal);
    }

    /// <summary>The actual terminal loop nests the editor, routes text clipboard input, refreshes, and restores the draft.</summary>
    [Fact]
    public static async Task Surface_manages_memories_and_restores_composer_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        using var backend = new HeadlessBackend(120, 35);
        var interrupts = 0;
        await using var surface = new TuiKitSurface(
            BuiltInThemes.Create()[0],
            () => interrupts++,
            backend,
            readClipboard: _ => Task.FromResult<string?>("Edited using clipboard"));
        await surface.RunAsync(
            async token =>
            {
                var draft = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput("Unsent draft\u001b[27u");
                Assert.False((await draft).IsSubmitted);
                var entry = new RepositoryMemoryListItem(RepositoryMemoryId.New(), "Original memory", RepositoryMemoryOrigin.Model, RepositoryMemoryType.StandingPreference, RepositoryMemoryOwnership.Ordinary);
                var changes = new List<string?>();
                _ = backend.TakeOutput();
                var browsing = surface.ManageRepositoryMemoriesAsync(
                    new([entry], []),
                    (item, text, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Assert.Equal(entry.Id, item.Id);
                    changes.Add(text);
                    return Task.FromResult(new RepositoryMemoryListSnapshot(text is null ? [] : [item with { Text = text }], []));
                },
                    token);
                await WaitForOutputAsync("Repository memories", surface, backend, token);
                backend.FeedInput("\r");
                await WaitForOutputAsync("Edit memory", surface, backend, token);
                backend.FeedInput("\u0001\u0003\u0016");
                await WaitForOutputAsync("Edited using clipboard", surface, backend, token);
                backend.FeedInput("\u0013");
                await WaitForOutputAsync("Enter/F4: edit", surface, backend, token);
                Assert.Equal(["Edited using clipboard"], changes);
                backend.FeedInput("\u001b[3~");
                await WaitForOutputAsync("0 entries", surface, backend, token);
                backend.FeedInput("\u001b[27u");
                await browsing;
                Assert.Equal(2, changes.Count);
                Assert.Null(changes[1]);
                Assert.Equal(0, interrupts);
                var resumed = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput("\r");
                Assert.Equal("Unsent draft", (await resumed).Text);
            },
            ct);
    }

    private static async Task WaitForOutputAsync(string expected, TuiKitSurface surface, HeadlessBackend backend, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        while (!output.ToString().Contains(expected, StringComparison.Ordinal))
        {
            await surface.PresentAsync(new PresentationBatch([]), cancellationToken);
            output.Append(backend.TakeOutput());
        }
    }

    private static RepositoryMemoryEditModal Create(string text)
    {
        var modal = new RepositoryMemoryEditModal(text, _ => CellStyle.Default, new(), () => true, () => { });
        Render(modal, 120, 35);
        return modal;
    }

    private static string Render(RepositoryMemoryEditModal modal, int width, int height)
    {
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        return TUIKit.Testing.Snapshot.ToText(cells);
    }
}
