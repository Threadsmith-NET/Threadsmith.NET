namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tui.TuiKit;
using TUIKit.Terminal;
using Xunit;

/// <summary>Exercises image capture through the real UI owner, parser and composer lifecycle.</summary>
[Collection("TUIKit terminal")]
public static class TuiKitImageInputTests
{
    private const string Paste = "\u001b[118;7u";

    /// <summary>An image request does not insert the clipboard's text fallback.</summary>
    [Fact]
    public static async Task ImagePasteWithoutImagePreservesDraft()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(100, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend, images: new ImageStore(), captureClipboard: _ => Task.FromResult(new ClipboardCapture("clipboard text", null)));
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("draft" + Paste);
            await ExpectAsync(backend, "No image in clipboard", token);
            backend.FeedInput("\r");
            var input = await read;
            Assert.Equal("draft", input.Text);
            Assert.Empty(input.Images);
        },
            lifetime.Token);
    }

    /// <summary>Validation failures remain visible and preserve the existing ordinary draft.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CaptureFailurePreservesText(bool timedOut)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(100, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend, captureClipboard: _ => Task.FromException<ClipboardCapture>(timedOut ? new OperationCanceledException() : new InvalidDataException("oversized")));
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("preserved" + Paste);
            await ExpectAsync(backend, "Clipboard unavailable", token);
            backend.FeedInput("\r");
            Assert.Equal("preserved", (await read).Text);
        },
            timeout.Token);
    }

    private static async Task ExpectAsync(ObservedBackend backend, string expected, CancellationToken cancellationToken)
    {
        var output = string.Empty;
        while (!output.Contains(expected, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            output += await backend.ReadOutputAsync(cancellationToken);
            if (expected == "Image attached")
            {
                Assert.DoesNotContain("Clipboard unavailable", output, StringComparison.Ordinal);
                Assert.DoesNotContain("Image paste cancelled", output, StringComparison.Ordinal);
            }
        }
    }

    private sealed class ImageStore : IImageArtifactStore
    {
        public ImageArtifact Image { get; } = new() { Id = Guid.NewGuid().ToString("N"), ContentHash = new string('a', 64), MediaType = "image/png", Width = 2, Height = 2, ByteLength = 3, Source = "clipboard" };

        public int Stored { get; private set; }

        public List<string> Released { get; } = [];

        public Func<CancellationToken, Task>? BeforeStoreAsync { get; init; }

        public async Task<ImageArtifact> StoreImageAsync(Stream content, SessionId sessionId, string source, string? responseId = null, long maximumBytes = ImageArtifactLimits.MaximumBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeStoreAsync is { } beforeStore)
            {
                await beforeStore(cancellationToken);
            }

            Stored++;
            return Image;
        }

        public Task ReleaseImageAsync(string artifactId, SessionId sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Released.Add(artifactId);
            return Task.CompletedTask;
        }
    }
}
