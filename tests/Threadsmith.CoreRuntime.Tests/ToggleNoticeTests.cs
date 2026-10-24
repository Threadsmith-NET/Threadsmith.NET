namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Contracts;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Selection notices cannot leak into another tool's retained display.</summary>
[Collection("TUIKit terminal")]
public static class ToggleNoticeTests
{
    /// <summary>Reason text follows the selected tool without duplicate or stale rows.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(100, 30)]
    public static void SwitchingAndReconciliationClearPriorReason(int width, int height)
    {
        const string reason = "Disabled by default";
        var option = new InteractionToggleOption("ask_user", "Ask user", "Tools", false, Reason: reason);
        var modal = new ToggleModal(new("Tools", [option, new("datetime", "Date time", "Tools", true)]), _ => CellStyle.Default, () => { }, () => { });
        var cells = new CellBuffer(width, height);
        modal.Render(new BufferSurface(cells));
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.Reconcile("ask_user", new(false, reason));
        modal.Render(new BufferSurface(cells));
        Assert.Equal(1, TUIKit.Testing.Snapshot.ToText(cells).Split(reason, StringSplitOptions.None).Length - 1);
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.Render(new BufferSurface(cells));
        Assert.DoesNotContain(reason, TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.Up));
        modal.Reconcile("ask_user", new(true) { UpdatedOption = option with { Enabled = true, Reason = null } });
        modal.Render(new BufferSurface(cells));
        Assert.DoesNotContain(reason, TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F2));
        modal.Render(new BufferSurface(new CellBuffer(40, 12)));
        modal.Render(new BufferSurface(cells));
        Assert.DoesNotContain(reason, TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Contains(modal.Members("group:Tools"), item => item.Id == "ask_user" && item.Enabled);
    }
}
