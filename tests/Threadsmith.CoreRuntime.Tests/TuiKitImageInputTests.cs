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

    /// <summary>Quit can leave the composer with pending or failed images, which normal disposal releases.</summary>
    [Theory]
    [InlineData("/quit", false)]
    [InlineData(" /QUIT ", true)]
    public static async Task QuitRetainsAttachmentOwnershipForDisposal(string command, bool failedSubmission)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(110, 30);
        var store = new ImageStore();
        var failedImage = store.Image with { Id = "failed-image" };
        await using (var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend, images: store, captureClipboard: _ => Task.FromResult(new ClipboardCapture(null, [1, 2, 3]))))
        {
            await surface.RunAsync(
                async token =>
            {
                var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput(Paste);
                await ExpectAsync(backend, "Image attached", token);
                if (failedSubmission)
                {
                    await surface.RestoreSubmissionAsync(new InteractionInput(true, "failed", token) { Images = [failedImage] }, token);
                }

                backend.FeedInput(command + "\r");
                var input = await read;
                Assert.Equal(command, input.Text);
                Assert.Equal(store.Image, Assert.Single(input.Images));
                Assert.Empty(store.Released);
            },
                lifetime.Token);
        }

        Assert.Contains(store.Image.Id, store.Released);
        if (failedSubmission)
        {
            Assert.Contains(failedImage.Id, store.Released);
        }
    }

    /// <summary>Capture and admission show progress, ignore repeat pastes and recover after every handled outcome.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("capture failure")]
    [InlineData("capture timeout")]
    [InlineData("empty clipboard")]
    [InlineData("store failure")]
    public static async Task ImagePasteShowsProgressAndIgnoresRepeats(string outcome)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(110, 30);
        var capture = new TaskCompletionSource<ClipboardCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        var stores = 0;
        var store = new ImageStore
        {
            BeforeStoreAsync = async token =>
            {
                if (Interlocked.Increment(ref stores) == 1 && outcome is "success" or "store failure")
                {
                    storeEntered.SetResult();
                    await releaseStore.Task.WaitAsync(token);
                }
            },
        };
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend, images: store, captureClipboard: token =>
            Interlocked.Increment(ref captures) == 1 ? capture.Task.WaitAsync(token) : Task.FromResult(new ClipboardCapture(null, [1, 2, 3])));
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(Paste);
            await ExpectAsync(backend, "Attaching image ...", token);

            // Text following repeated paste keys proves that the UI processed the keys while capture was blocked.
            backend.FeedInput(Paste + Paste + "capturing");
            await ExpectAsync(backend, "capturing", token);
            Assert.Equal(1, Volatile.Read(ref captures));
            Assert.Equal(0, Volatile.Read(ref stores));
            if (outcome is "capture failure" or "capture timeout")
            {
                capture.SetException(outcome == "capture timeout" ? new OperationCanceledException() : new InvalidDataException("capture failed"));
            }
            else
            {
                capture.SetResult(new ClipboardCapture(null, outcome == "empty clipboard" ? null : [1, 2, 3]));
            }

            if (outcome is "success" or "store failure")
            {
                await storeEntered.Task.WaitAsync(token);
                backend.FeedInput(Paste + Paste + " storing");
                await ExpectAsync(backend, "storing", token);
                Assert.Equal(1, Volatile.Read(ref captures));
                Assert.Equal(1, Volatile.Read(ref stores));
                if (outcome == "store failure")
                {
                    releaseStore.SetException(new InvalidDataException("admission failed"));
                }
                else
                {
                    releaseStore.SetResult();
                }
            }

            await ExpectAsync(backend, outcome == "success" ? "Image attached to next message" : outcome == "empty clipboard" ? "No image in clipboard" : "Clipboard unavailable", token);
            backend.FeedInput(Paste);
            await ExpectAsync(backend, "Image attached to next message", token);
            Assert.Equal(2, Volatile.Read(ref captures));
            backend.FeedInput("\r");
            var input = await read;
            Assert.Equal(outcome == "success" ? 2 : 1, input.Images.Count);
            Assert.StartsWith("capturing", input.Text, StringComparison.Ordinal);
        },
            lifetime.Token);
    }

    /// <summary>Explicit paste becomes an image-only ordinary submission, not base64 text.</summary>
    [Theory]
    [InlineData(Paste, false)]
    [InlineData("\u001b\u0016", false)]
    [InlineData("\u001b[20~", true)]
    public static async Task ImagePasteSubmitsThroughOrdinaryComposer(string shortcut, bool menu)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(100, 24);
        var store = new ImageStore();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend, images: store, captureClipboard: _ => Task.FromResult(new ClipboardCapture(null, [1, 2, 3])));
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(shortcut);
            if (menu)
            {
                await ExpectAsync(backend, "Paste image from clipboard", token);
                backend.FeedInput("\r");
            }

            await ExpectAsync(backend, "Image attached to next message: image/png, 2×2, 3 bytes", token);
            backend.FeedInput("\r");
            var input = await read;
            Assert.Empty(input.Text);
            Assert.Equal(store.Image, Assert.Single(input.Images));
            Assert.Empty(store.Released);
        },
            timeout.Token);
    }

    /// <summary>Terminal text belongs to the paste event, even when the local clipboard contains an image.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("terminal text")]
    [InlineData(" ")]
    public static async Task TerminalTextPasteDoesNotCaptureClipboard(string text)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(100, 24);
        var store = new ImageStore();
        var captures = 0;
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend, images: store, captureClipboard: _ =>
        {
            captures++;
            return Task.FromResult(new ClipboardCapture(null, [1]));
        });
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\u001b[200~" + text + "\u001b[201~suffix\r");
            var input = await read;
            Assert.Equal(text + "suffix", input.Text);
            Assert.Empty(input.Images);
            Assert.Equal(0, captures);
        },
            lifetime.Token);
    }

    /// <summary>Ordinary paste shortcuts retain the pre-image text reader even with an image available.</summary>
    [Theory]
    [InlineData("\u0016")]
    [InlineData("\u001b[118;5u")]
    [InlineData("\u001b[2;2~")]
    public static async Task TextPasteShortcutDoesNotCaptureImage(string shortcut)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(100, 24);
        var captures = 0;
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend, readClipboard: _ => Task.FromResult<string?>("clipboard text"), images: new ImageStore(), captureClipboard: _ =>
        {
            captures++;
            return Task.FromResult(new ClipboardCapture(null, [1]));
        });
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(shortcut);
            await ExpectAsync(backend, "clipboard text", token);
            backend.FeedInput("\r");
            var input = await read;
            Assert.Equal("clipboard text", input.Text);
            Assert.Empty(input.Images);
            Assert.Equal(0, captures);
        },
            lifetime.Token);
    }

    /// <summary>An image request does not insert the clipboard's text fallback.</summary>
    [Fact]
    public static async Task ImagePasteWithoutImagePreservesDraft()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(100, 24);
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

    /// <summary>The clipboard menu keeps removal available after adding its paste action.</summary>
    [Fact]
    public static async Task AttachmentMenuStillRemovesExistingImage()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(100, 24);
        var store = new ImageStore();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend, images: store, captureClipboard: _ => Task.FromResult(new ClipboardCapture(null, [1])));
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(Paste);
            await ExpectAsync(backend, "Image attached", token);
            backend.FeedInput("\u001b[20~");
            await ExpectAsync(backend, "Remove 1:", token);
            backend.FeedInput("\u001b[B\r");
            await ExpectAsync(backend, "Image removed", token);
            backend.FeedInput("draft\r");
            var input = await read;
            Assert.Empty(input.Images);
            Assert.Equal("draft", input.Text);
            Assert.Contains(store.Image.Id, store.Released);
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
        using var backend = new HeadlessBackend(100, 24);
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

    /// <summary>Secondary prompts refuse image capture without acquiring an artifact.</summary>
    [Theory]
    [InlineData(ComposerPurpose.Secondary, Paste)]
    [InlineData(ComposerPurpose.Secondary, "\u001b\u0016")]
    [InlineData(ComposerPurpose.Steering, Paste)]
    [InlineData(ComposerPurpose.Steering, "\u001b\u0016")]
    public static async Task SecondaryImagePasteIsExplicitlyRefused(ComposerPurpose purpose, string shortcut)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(100, 24);
        var store = new ImageStore();
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend, images: store, captureClipboard: _ => Task.FromResult(new ClipboardCapture(null, [1])));
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("question > ") { Purpose = purpose }, token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(shortcut);
            await ExpectAsync(backend, "ordinary conversation composer", token);
            backend.FeedInput("answer\r");
            Assert.Empty((await read).Images);
            Assert.Equal(0, store.Stored);
        },
            timeout.Token);
    }

    /// <summary>Closing a composer releases its unsubmitted draft ownership.</summary>
    [Fact]
    public static async Task ClosingReleasesUnsubmittedImage()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new HeadlessBackend(100, 24);
        var store = new ImageStore();
        var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend, images: store, captureClipboard: _ => Task.FromResult(new ClipboardCapture(null, [1])));
        await surface.RunAsync(
            async token =>
        {
            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = SessionId.New() }, readCancellation.Token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(Paste);
            await ExpectAsync(backend, "Image attached", token);
            await readCancellation.CancelAsync();
            try
            {
                await read;
                Assert.Fail("Cancelled composer unexpectedly submitted.");
            }
            catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
            {
            }
        },
            timeout.Token);
        await surface.DisposeAsync();
        Assert.Contains(store.Image.Id, store.Released);
    }

    /// <summary>Opt-in real clipboard capture through Ctrl+Alt+V and actual image store, without changing clipboard contents.</summary>
    [Fact]
    public static async Task CurrentWindowsClipboardReachesComposer()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("THREADSMITH_CLIPBOARD_CURRENT") != "1")
        {
            Assert.Skip("Set THREADSMITH_CLIPBOARD_CURRENT=1 with an image already on the local Windows clipboard.");
        }

        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "threadsmith-clipboard-composer-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var backend = new HeadlessBackend(110, 30);
            var store = new ArtifactStore($"Data Source={Path.Combine(root, "state.db")};Pooling=False", Path.Combine(root, "artifacts"), new SecretOutputSanitizer());
            await store.InitializeAsync(lifetime.Token);
            await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], lifetime.Cancel, backend, images: store);
            await surface.RunAsync(
                async token =>
            {
                var session = SessionId.New();
                var read = surface.ReadComposerAsync(new ComposerRequest("repo > ") { SessionId = session }, token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput("\u001b\u0016");
                await ExpectAsync(backend, "Image attached", token);
                backend.FeedInput("\r");
                var input = await read;
                var image = Assert.Single(input.Images);
                var bytes = await store.ReadImageAsync(image, session, token);
                Assert.Equal(image.ByteLength, bytes.Length);
                Assert.True(image.Width > 0 && image.Height > 0);
                TestContext.Current.TestOutputHelper?.WriteLine($"Native clipboard through Ctrl+Alt+V: {image.MediaType}, {image.Width}x{image.Height}, {bytes.Length} bytes.");
            },
                lifetime.Token);
        }
        finally
        {
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                && Path.GetFileName(root).StartsWith("threadsmith-clipboard-composer-", StringComparison.Ordinal))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Opt-in Windows bitmap capture smoke test restores the previous clipboard in the helper's finally block.</summary>
    [Fact]
    public static async Task NativeWindowsClipboardSmoke()
    {
        var fixture = Environment.GetEnvironmentVariable("THREADSMITH_CLIPBOARD_LIVE_IMAGE");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fixture))
        {
            Assert.Skip("Set THREADSMITH_CLIPBOARD_LIVE_IMAGE to the 420x320 M34 fixture on a local Windows desktop.");
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "threadsmith-clipboard-" + Guid.NewGuid().ToString("N") + ".ps1");
        const string script = """
            param([string]$ImagePath)
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing
            $old = [System.Windows.Forms.Clipboard]::GetDataObject()
            $snapshot = New-Object System.Windows.Forms.DataObject
            if ($null -ne $old) {
                foreach ($format in $old.GetFormats($false)) {
                    $snapshot.SetData($format, $false, $old.GetData($format, $false))
                }
            }
            $image = [System.Drawing.Bitmap]::new($ImagePath)
            try {
                [System.Windows.Forms.Clipboard]::SetImage($image)
                [Console]::WriteLine('ready')
                [Console]::ReadLine() | Out-Null
            } finally {
                if ($null -ne $old) { [System.Windows.Forms.Clipboard]::SetDataObject($snapshot, $true) }
                else { [System.Windows.Forms.Clipboard]::Clear() }
                $image.Dispose()
            }
            """;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        await File.WriteAllTextAsync(scriptPath, script, token);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-File", scriptPath, fixture })
        {
            start.ArgumentList.Add(argument);
        }

        using var helper = new System.Diagnostics.Process { StartInfo = start };
        try
        {
            Assert.True(helper.Start());
            Assert.Equal("ready", await helper.StandardOutput.ReadLineAsync(token));
            var captured = await ClipboardReader.CaptureAsync(new TuiResourceLimits(), token);
            Assert.NotNull(captured.Image);
            Assert.Null(captured.Text);
            Assert.Equal(420, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(captured.Image.AsSpan(16, 4)));
            Assert.Equal(320, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(captured.Image.AsSpan(20, 4)));
        }
        finally
        {
            if (!helper.HasExited)
            {
                await helper.StandardInput.WriteLineAsync("restore".AsMemory(), CancellationToken.None);
                using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await helper.WaitForExitAsync(restoreTimeout.Token);
            }

            File.Delete(scriptPath);
        }

        Assert.Equal(0, helper.ExitCode);
    }

    private static async Task ExpectAsync(HeadlessBackend backend, string expected, CancellationToken cancellationToken)
    {
        var output = string.Empty;
        while (!output.Contains(expected, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            output += backend.TakeOutput();
            if (expected == "Image attached")
            {
                Assert.DoesNotContain("Clipboard unavailable", output, StringComparison.Ordinal);
                Assert.DoesNotContain("Image paste cancelled", output, StringComparison.Ordinal);
            }

            await Task.Delay(10, cancellationToken);
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
