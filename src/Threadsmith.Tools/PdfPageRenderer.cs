namespace Threadsmith.Tools;

using System.Runtime.Versioning;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;
using Threadsmith.Core;

/// <summary>Renders bounded page selections for the existing image admission path.</summary>
internal static class PdfPageRenderer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Keeps native work serialized, with cancellable admission and no late artifact publication.</summary>
    internal static async Task<RenderedPdf> RenderAsync(byte[] bytes, int startPage, int maximumPages, ToolLimits limits, CancellationToken cancellationToken)
    {
        limits.Validate();
        await Gate.WaitAsync(cancellationToken);

        // The worker owns its input and native resources even if the caller abandons it.
        // A stuck native parser retains the gate instead of spawning unbounded workers.
        var work = Task.Run(
            async () =>
            {
                try
                {
                    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        return await RenderPagesAsync(bytes, startPage, maximumPages, limits, cancellationToken);
                    }

                    throw new PlatformNotSupportedException("PDF reading is supported on Windows, Linux and macOS.");
                }
                finally
                {
                    Gate.Release();
                }
            },
            CancellationToken.None);
        try
        {
            return await work.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _ = work.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static async Task<RenderedPdf> RenderPagesAsync(byte[] bytes, int startPage, int maximumPages, ToolLimits limits, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var total = Conversion.GetPageCount(bytes);
            if (startPage > total)
            {
                throw new ToolArgumentValidationException($"startPage exceeds the PDF's {total} pages.");
            }

            var pages = new List<byte[]>();
            var count = Math.Min(maximumPages, total - startPage + 1);
            for (var offset = 0; offset < count; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = startPage - 1 + offset;
                var size = Conversion.GetPageSize(bytes, index);
                if (!float.IsFinite(size.Width) || !float.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
                {
                    throw new InvalidDataException("The PDF page has invalid dimensions.");
                }

                var scale = Math.Min(limits.PdfRenderDpi / 72d, limits.PdfRenderMaximumDimension / (double)Math.Max(size.Width, size.Height));
                scale = Math.Min(scale, Math.Sqrt(ImageArtifactLimits.MaximumPixels / ((double)size.Width * size.Height)));

                // Round down so neither the dimension nor total-pixel limit is exceeded.
                var width = Math.Clamp((int)Math.Floor(size.Width * scale), 1, limits.PdfRenderMaximumDimension);
                var height = Math.Clamp((int)Math.Floor(size.Height * scale), 1, limits.PdfRenderMaximumDimension);
                var options = new RenderOptions(Width: width, Height: height, WithAnnotations: true);
                await foreach (var bitmap in Conversion.ToImagesAsync(bytes, [index], options: options, cancellationToken: cancellationToken))
                {
                    using (bitmap)
                    using (var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (encoded.Size > ImageArtifactLimits.MaximumBytes)
                        {
                            throw new InvalidDataException("The rendered PDF page exceeds the 5 MiB image limit.");
                        }

                        pages.Add(encoded.ToArray());
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new RenderedPdf(total, pages);
        }
        catch (PdfPasswordProtectedException exception)
        {
            throw new InvalidDataException("The PDF is password protected. Provide an unlocked copy.", exception);
        }
        catch (PdfException exception)
        {
            throw new InvalidDataException("The PDF could not be read. It may be damaged or use unsupported encryption.", exception);
        }
    }

    /// <summary>Encoded pages awaiting ordinary image artifact admission.</summary>
    internal sealed record RenderedPdf(int TotalPages, IReadOnlyList<byte[]> Pages);
}
