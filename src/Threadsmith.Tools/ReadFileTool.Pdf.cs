namespace Threadsmith.Tools;

using System.Security.Cryptography;
using Threadsmith.Core;

/// <summary>PDF page selection with explicit continuation and model-visible image references.</summary>
public sealed record ReadPdfOutput(int TotalPages, int StartPage, int EndPage, int? NextStartPage, IReadOnlyList<ImageArtifact> Pages);

/// <summary>PDF reading through the existing file tool and image store.</summary>
public sealed partial class ReadFileTool
{
    private async Task<ToolExecution<ReadFileOutput>> ReadPdfAsync(
        ReadFileInput input,
        ToolExecutionContext context,
        FileStream stream,
        string path,
        CancellationToken cancellationToken)
    {
        if (input.Mode is not ("auto" or "pdf") || input.Snapshot || input.SnapshotOffset != 0 || input.StartLine != 1 || input.MaximumLines != 0)
        {
            throw new ToolArgumentValidationException("PDFs require auto/pdf mode without text range or snapshot options. Use startPage and maximumPages.");
        }

        var startPage = input.StartPage ?? 1;
        var maximumPages = input.MaximumPages ?? 1;
        if (startPage < 1 || maximumPages is < 1 or > 4)
        {
            throw new ToolArgumentValidationException("startPage must be positive and maximumPages must be between 1 and 4.");
        }

        var artifacts = _artifacts ?? throw new InvalidOperationException("Image artifact storage is unavailable.");
        if (stream.Length > _limits.ReadFileMaximumBytes || stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("The PDF exceeds the configured read limit.");
        }

        stream.Position = 0;
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The PDF changed while being read. Read it again.");
        }

        var rendered = await PdfPageRenderer.RenderAsync(bytes, startPage, maximumPages, _limits, cancellationToken);
        var relative = GetDisplayPath(path, context.Invocation);
        var pages = new List<ImageArtifact>();
        try
        {
            foreach (var page in rendered.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var image = new MemoryStream(page, writable: false);
                pages.Add(await artifacts.StoreImageAsync(
                    image,
                    context.SessionId,
                    $"{relative} (page {startPage + pages.Count})",
                    cancellationToken: cancellationToken));
            }
        }
        catch
        {
            // Failed reads publish no page references; release any partial admission even after cancellation.
            foreach (var page in pages)
            {
                await artifacts.ReleaseImageAsync(page.Id, context.SessionId, CancellationToken.None);
            }

            throw;
        }

        var endPage = startPage + pages.Count - 1;
        var truncated = endPage < rendered.TotalPages;
        var output = new ReadFileOutput(relative, 0, null, 0, [], truncated, null, null)
        {
            BytesRead = bytes.Length,
            ContentDigest = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Pdf = new ReadPdfOutput(rendered.TotalPages, startPage, endPage, truncated ? endPage + 1 : null, pages),
        };
        return new ToolExecution<ReadFileOutput>(output, [new ToolProvenanceSource("file", ToolPathRules.IsWithinScratchpad(path, context.Invocation) ? path : relative, $"pages {startPage}-{endPage}")], truncated, Images: pages);
    }
}
