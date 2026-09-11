namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Modals;
using TUIKit.Terminal;
using Xunit;

/// <summary>Checks external-refresh notification routing, terminal behavior, and native toast rendering.</summary>
[Collection("TUIKit terminal")]
public static class SemanticRefreshNotificationTests
{
    /// <summary>Only external changes and recovery produce transient echoes, regardless of transcript wording.</summary>
    [Theory]
    [InlineData(SemanticRefreshReason.ExternalChange, true)]
    [InlineData(SemanticRefreshReason.Recovery, true)]
    [InlineData(SemanticRefreshReason.HostMutation, false)]
    [InlineData(SemanticRefreshReason.Manual, false)]
    [InlineData(SemanticRefreshReason.UserAdmission, false)]
    public static void RefreshAttributionControlsNotifications(SemanticRefreshReason reason, bool visible)
    {
        var session = SessionId.New();
        var workspace = WorkspaceId.New();
        var refresh = SemanticRefreshId.New();
        var now = DateTimeOffset.UtcNow;
        IDomainEvent[] events =
        [
            new SemanticRefreshStarted(session, now, refresh, workspace, reason, SemanticRefreshMode.Incremental, 1, 2),
            new SemanticRefreshCompleted(session, now, refresh, workspace, reason, SemanticRefreshMode.Incremental, 1, 2, 2, SemanticConfidenceLevel.FullSemantic, 240),
            new SemanticRefreshFailed(session, now, refresh, workspace, reason, SemanticRefreshMode.Full, 1, 2, 1, SemanticRefreshFailureKind.Infrastructure, "diagnostic details", 240),
        ];

        var notifications = events.Select(InteractionEventSegments.CreateNotification).ToArray();

        if (visible)
        {
            Assert.Equal(
                [PresentationTextRole.Status, PresentationTextRole.Success, PresentationTextRole.Error],
                notifications.Select(notification => Assert.IsType<PresentationNotification>(notification).Role));
            Assert.All(notifications, notification => Assert.InRange(notification!.Text.Length, 1, 38));
        }
        else
        {
            Assert.All(notifications, Assert.Null);
        }

        Assert.Null(InteractionEventSegments.CreateNotification(new ModelOutputObserved(session, now, "External changes; refreshing semantics")));
    }

    /// <summary>Native toasts stay in output content, follow live theme roles, and disappear at their timeout.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(120, 35)]
    public static void NativeToastsRespectOutputBoundsThemeAndExpiration(int width, int height)
    {
        var cells = new CellBuffer(width, height);
        var output = new BufferSurface(cells).CreateView(new Rect(0, 2, width, height - 6));
        var content = output.CreateView(WorkspaceLayout.OutputContent(output.Size));
        var notifications = new NotificationCenter();
        var toast = notifications.Add("Semantic model updated", NotificationSeverity.Success, 100, 4000);
        var style = CellStyle.Default.WithForeground(Color.FromPalette(5)).WithBackground(Color.FromPalette(4));
        cells.Fill(new Rect(0, 0, width, height), Cell.Glyph("X", CellStyle.Default, 1));

        NotificationOverlay.Render(content, notifications, 100, role => role == PresentationTextRole.Success ? style : CellStyle.Default);

        Assert.Contains(toast.Text, TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        var painted = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (cells.Get(x, y).Grapheme != "X")
                {
                    Assert.InRange(x, 2, width - 3);
                    Assert.Equal(5, y);
                    Assert.Equal(style, cells.Get(x, y).Style);
                    painted++;
                }
            }
        }

        Assert.True(painted > 0);
        style = CellStyle.Default;
        cells.Fill(new Rect(0, 0, width, height), Cell.Glyph("X", CellStyle.Default, 1));
        NotificationOverlay.Render(content, notifications, 4099, _ => style);
        Assert.Contains(toast.Text, TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Equal(CellStyle.Default, cells.Get(width - 6, 5).Style);

        cells.Fill(new Rect(0, 0, width, height), Cell.Glyph("X", CellStyle.Default, 1));
        NotificationOverlay.Render(content, notifications, 4100, _ => style);
        Assert.DoesNotContain(toast.Text, TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.Empty(notifications.Active(4100));
    }

    /// <summary>A live UI loop displays the echo while preserving the draft and copying only the original output.</summary>
    [Fact]
    public static async Task ToastPreservesComposerAndOriginalTranscript()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var backend = new HeadlessBackend(120, 35);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("Threadsmith > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("draft");
            await surface.PresentAsync(new PresentationBatch([]), token);
            _ = backend.TakeOutput();
            var started = new SemanticRefreshStarted(SessionId.New(), DateTimeOffset.UtcNow, SemanticRefreshId.New(), WorkspaceId.New(), SemanticRefreshReason.ExternalChange, SemanticRefreshMode.Incremental, 1, 2);
            var notification = Assert.IsType<PresentationNotification>(InteractionEventSegments.CreateNotification(started));
            const string transcript = "External changes detected; updating semantic model...\n";
            await surface.PresentAsync(
                new PresentationBatch([new PresentationTextItem([new(transcript, PresentationTextRole.Status)]) { Notification = notification }]),
                token);
            await surface.PresentAsync(new PresentationBatch([]), token);

            Assert.Contains(notification.Text, backend.TakeOutput(), StringComparison.Ordinal);
            Assert.False(read.IsCompleted);
            backend.FeedInput("\u001b[18~\u0001\u0003\u001b[18~\r");
            Assert.Equal("draft", (await read).Text);
            var copy = Assert.Single(backend.TakeOutput().Split("\u001b]52;c;").Skip(1));
            var copied = Encoding.UTF8.GetString(Convert.FromBase64String(copy[..copy.IndexOf('\u001b')]));
            Assert.Equal(transcript.TrimEnd(), copied.TrimEnd());
        },
            timeout.Token);
    }
}
