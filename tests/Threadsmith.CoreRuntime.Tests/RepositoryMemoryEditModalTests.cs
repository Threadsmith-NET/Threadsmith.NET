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

    private static async Task WaitForOutputAsync(string expected, ObservedBackend backend, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        while (!output.ToString().Contains(expected, StringComparison.Ordinal))
        {
            output.Append(await backend.ReadOutputAsync(cancellationToken));
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
