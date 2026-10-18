namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class MultimodalTests
{
    private static readonly byte[] GreenPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAD0lEQVR4nGNg+M8AQhAKABvyA/1tVLjHAAAAAElFTkSuQmCC");

    /// <summary>Verifies configured bounds and reference-aware capacity through production entry points.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageQuotaReclaimsRemovedAttachmentsAndUnownedBodies(bool leaveLegacyBody)
    {
        await using var fixture = await Fixture.CreateAsync(maximumRetainedImageBytes: Png.Length);
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.StoreImageAsync(new MemoryStream(GreenPng), session, "clipboard", cancellationToken: token));
            var body = Path.Combine(fixture.Root, "artifacts", image.ContentHash[..2], image.ContentHash[2..4], image.ContentHash + ".bin");
            if (leaveLegacyBody)
            {
                // Normal reference pruning leaves young bodies for retention. Quota admission must reclaim them regardless of age.
                Assert.Equal(1, await fixture.Store.PruneImageReferencesAsync(DateTimeOffset.MaxValue, token));
                Assert.True(File.Exists(body));
            }
            else
            {
                await fixture.Store.ReleaseImageAsync(image.Id, session, token);
                Assert.False(File.Exists(body));
                Assert.Empty(await fixture.Store.ListAsync(null, token));
            }

            var replacement = await fixture.Store.StoreImageAsync(new MemoryStream(GreenPng), session, "clipboard", cancellationToken: token);
            Assert.False(File.Exists(body));
            Assert.Equal(GreenPng, (await fixture.Store.ReadImageAsync(replacement, session, token)).ToArray());
            await fixture.Store.ReleaseImageAsync(replacement.Id, session, token);
        }
    }

    /// <summary>Verifies configured bounds and reference-aware capacity through production entry points.</summary>
    [Fact]
    public async Task ImageQuotaDeduplicatesAndPreservesSharedBodies()
    {
        await using var fixture = await Fixture.CreateAsync(maximumRetainedImageBytes: Png.Length);
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        var sharedSession = SessionId.New();
        var image = await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
        var duplicate = await fixture.Store.StoreImageAsync(new MemoryStream(Png), sharedSession, "read_file", cancellationToken: token);
        await fixture.Store.ReleaseImageAsync(image.Id, session, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.StoreImageAsync(new MemoryStream(GreenPng), session, "clipboard", cancellationToken: token));
        Assert.Equal(Png, (await fixture.Store.ReadImageAsync(duplicate, sharedSession, token)).ToArray());
        Assert.Single(await fixture.Store.ListAsync(null, token));
        await fixture.Store.ReleaseImageAsync(duplicate.Id, sharedSession, token);
        Assert.Empty(await fixture.Store.ListAsync(null, token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.GrantImageAsync(image.Id, session, sharedSession, token));
    }

    /// <summary>Verifies configured bounds and reference-aware capacity through production entry points.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ImageQuotaRejectsNonpositiveConfiguration(long bytes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ArtifactStore("Data Source=:memory:", "unused", new SecretOutputSanitizer(), maximumRetainedImageBytes: bytes));

    /// <summary>Verifies configured bounds and reference-aware capacity through production entry points.</summary>
    [Fact]
    public async Task ImageQuotaAcceptsLargerConfiguredBudget()
    {
        await using var fixture = await Fixture.CreateAsync(maximumRetainedImageBytes: (long)Png.Length + GreenPng.Length);
        var token = TestContext.Current.CancellationToken;
        var session = SessionId.New();
        await fixture.Store.StoreImageAsync(new MemoryStream(Png), session, "clipboard", cancellationToken: token);
        await fixture.Store.StoreImageAsync(new MemoryStream(GreenPng), session, "clipboard", cancellationToken: token);
        Assert.Equal(2, (await fixture.Store.ListAsync(null, token)).Count);
    }

    /// <summary>Verifies configured bounds and reference-aware capacity through production entry points.</summary>
    [Fact]
    public async Task FailedMultipagePdfReadReleasesPartialAdmission()
    {
        var token = TestContext.Current.CancellationToken;
        var rendered = await PdfPageRenderer.RenderAsync(CreatePdf(), 1, 2, ToolLimits.Default, token);
        await using var fixture = await Fixture.CreateAsync(maximumRetainedImageBytes: rendered.Pages[0].Length);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "quota.pdf"), CreatePdf(), token);
        var session = SessionId.New();
        var failed = await fixture.InvokeAsync(session, "read_file", "{\"path\":\"quota.pdf\",\"maximumPages\":2}", token);
        Assert.False(failed.Succeeded);
        Assert.Contains("quota", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(failed.Images);
        Assert.Empty(await fixture.Store.ListAsync(null, token));
        var retried = await fixture.InvokeAsync(session, "read_file", "{\"path\":\"quota.pdf\"}", token);
        Assert.True(retried.Succeeded, retried.Error);
        Assert.Single(retried.Images);
    }

    /// <summary>Verifies configured bounds and reference-aware capacity through production entry points.</summary>
    [Theory]
    [InlineData(144, 1600, 600, 400)]
    [InlineData(288, 3200, 1200, 800)]
    [InlineData(288, 900, 900, 600)]
    [InlineData(10000, 8192, 5016, 3344)]
    public async Task PdfReadHonorsConfiguredResolutionAndImageAdmissionBounds(int dpi, int dimension, int width, int height)
    {
        await using var fixture = await Fixture.CreateAsync(limits: new ToolLimits { PdfRenderDpi = dpi, PdfRenderMaximumDimension = dimension });
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "resolution.pdf"), CreatePdf(), token);
        var result = await fixture.InvokeAsync(SessionId.New(), "read_file", "{\"path\":\"resolution.pdf\"}", token);
        Assert.True(result.Succeeded, result.Error);
        var image = Assert.Single(result.Images);
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.True((long)image.Width * image.Height <= ImageArtifactLimits.MaximumPixels);
    }

    /// <summary>Verifies configured bounds and reference-aware capacity through production entry points.</summary>
    [Theory]
    [InlineData(0, 1600)]
    [InlineData(-1, 1600)]
    [InlineData(144, 0)]
    [InlineData(144, -1)]
    [InlineData(144, 8193)]
    public void PdfResolutionRejectsInvalidConfiguration(int dpi, int dimension)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ToolLimits { PdfRenderDpi = dpi, PdfRenderMaximumDimension = dimension }.Validate());
}
