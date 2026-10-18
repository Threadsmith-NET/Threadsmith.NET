namespace Threadsmith.ModelTooling.Tests;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

public sealed partial class MultimodalTests
{
    /// <summary>Scratchpad binary reads must not identify an unrelated same-named repository file.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BinaryReadPreservesScratchpadProvenance(bool pdf)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Root, "scratchpad")).FullName;
        var path = Path.Combine(scratchpad, pdf ? "document.pdf" : "image.png");
        await File.WriteAllBytesAsync(path, pdf ? CreatePdf() : Png, token);
        var invocation = fixture.Invocation with
        {
            Scratchpad = new ScratchpadSessionCapability
            {
                IsActive = true,
                DisabledReason = ScratchpadDisabledReason.None,
                RootPath = scratchpad,
                ModelPath = scratchpad,
            },
        };
        var result = await fixture.Pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "read_file",
                ArgumentsJson = JsonSerializer.Serialize(new { path }),
                Context = invocation,
            },
            token);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(path, Assert.Single(result.Sources).Identifier);
    }

    /// <summary>Exercises PDF reading through production admission and rendering.</summary>
    [Fact]
    public async Task PdfReadRendersTextAndScannedPagesThroughOrdinaryArtifacts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var bytes = CreatePdf();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "pages.pdf"), bytes, token);
        var session = SessionId.New();
        var first = await fixture.InvokeAsync(session, "read_file", "{\"path\":\"pages.pdf\"}", token);
        Assert.True(first.Succeeded, first.Error);
        var firstRead = JsonSerializer.Deserialize<ReadFileOutput>(first.ResultJson!);
        Assert.NotNull(firstRead);
        Assert.Equal(bytes.Length, firstRead.BytesRead);
        Assert.Null(firstRead.EndLine);
        Assert.Empty(firstRead.Lines);
        Assert.Equal((2, 1, 1, 2), (firstRead.Pdf!.TotalPages, firstRead.Pdf.StartPage, firstRead.Pdf.EndPage, firstRead.Pdf.NextStartPage));
        var firstImage = Assert.Single(first.Images);
        Assert.Equal(firstImage, Assert.Single(firstRead.Pdf.Pages));
        Assert.Equal((600, 400), (firstImage.Width, firstImage.Height));
        Assert.EndsWith("(page 1)", firstImage.Source, StringComparison.Ordinal);
        Assert.True((await fixture.Store.ReadImageAsync(firstImage, session, token)).Length > 0);

        var second = await fixture.InvokeAsync(session, "read_file", "{\"path\":\"pages.pdf\",\"startPage\":2}", token);
        Assert.True(second.Succeeded, second.Error);
        var secondRead = JsonSerializer.Deserialize<ReadFileOutput>(second.ResultJson!);
        Assert.Null(secondRead!.Pdf!.NextStartPage);
        Assert.False(secondRead.IsTruncated);
        var secondImage = Assert.Single(second.Images);
        Assert.NotEqual(firstImage.ContentHash, secondImage.ContentHash);
        Assert.True((await fixture.Store.ReadImageAsync(secondImage, session, token)).Length > 0);

        var all = await fixture.InvokeAsync(session, "read_file", "{\"path\":\"pages.pdf\",\"maximumPages\":4}", token);
        Assert.True(all.Succeeded, all.Error);
        Assert.Equal(2, all.Images.Count);
        Assert.False(all.IsTruncated);
    }

    /// <summary>Rejects invalid PDF requests without admitting image evidence.</summary>
    [Theory]
    [InlineData("{\"path\":\"pages.pdf\",\"mode\":\"text\"}")]
    [InlineData("{\"path\":\"pages.pdf\",\"mode\":\"image\"}")]
    [InlineData("{\"path\":\"pages.pdf\",\"startLine\":2}")]
    [InlineData("{\"path\":\"pages.pdf\",\"snapshot\":true}")]
    [InlineData("{\"path\":\"pages.pdf\",\"startPage\":0}")]
    [InlineData("{\"path\":\"pages.pdf\",\"startPage\":3}")]
    [InlineData("{\"path\":\"pages.pdf\",\"startPage\":2147483647}")]
    [InlineData("{\"path\":\"pages.pdf\",\"maximumPages\":5}")]
    [InlineData("{\"path\":\"pages.pdf\",\"maximumPages\":0}")]
    [InlineData("{\"path\":\"text.txt\",\"startPage\":1}")]
    [InlineData("{\"path\":\"text.txt\",\"mode\":\"pdf\"}")]
    public async Task PdfReadRejectsIncompatibleOptionsAndInvalidRanges(string arguments)
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "pages.pdf"), CreatePdf(), token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "text.txt"), "Ordinary text", token);
        var result = await fixture.InvokeAsync(SessionId.New(), "read_file", arguments, token);
        Assert.False(result.Succeeded);
        Assert.Empty(result.Images);
    }

    /// <summary>Exercises PDF reading through production admission and rendering.</summary>
    [Fact]
    public async Task PdfReadRejectsDamagedDocumentAndHonorsCancellation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "broken.pdf"), "%PDF-1.7\nnot a document", token);
        var result = await fixture.InvokeAsync(SessionId.New(), "read_file", "{\"path\":\"broken.pdf\"}", token);
        Assert.False(result.Succeeded);
        Assert.Contains("PDF", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Images);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfPageRenderer.RenderAsync(CreatePdf(), 1, 1, ToolLimits.Default, cancelled.Token));
    }

    /// <summary>Password-protected PDFs fail with an actionable explanation.</summary>
    [Fact]
    public async Task PdfReadRejectsPasswordProtectedDocument()
    {
        await using var fixture = await Fixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var bytes = Convert.FromBase64String("JVBERi0xLjMKJeLjz9MKMSAwIG9iago8PAovUHJvZHVjZXIgPGIxNzQ5Nzc5NmQ+Cj4+CmVuZG9iagoyIDAgb2JqCjw8Ci9UeXBlIC9QYWdlcwovQ291bnQgMQovS2lkcyBbIDQgMCBSIF0KPj4KZW5kb2JqCjMgMCBvYmoKPDwKL1R5cGUgL0NhdGFsb2cKL1BhZ2VzIDIgMCBSCj4+CmVuZG9iago0IDAgb2JqCjw8Ci9UeXBlIC9QYWdlCi9SZXNvdXJjZXMgPDwKPj4KL01lZGlhQm94IFsgMC4wIDAuMCAzMDAgMjAwIF0KL1BhcmVudCAyIDAgUgo+PgplbmRvYmoKNSAwIG9iago8PAovViAyCi9SIDMKL0xlbmd0aCAxMjgKL1AgNDI5NDk2NzI5MgovRmlsdGVyIC9TdGFuZGFyZAovTyA8MDA2MTNkMDNlNzI0NTc2ZDVhY2IxYjg1ZjhjNmQ5ZDdhMzNmZDY0MDEyM2FjNTRmMzEyY2E1NDc4MzZjYzJiZD4KL1UgPGJiOGQzZmVmNjRlZGFhOGEyNzRmYTQxMzBhMmZmZjUyMjhiZjRlNWU0ZTc1OGE0MTY0MDA0ZTU2ZmZmYTAxMDg+Cj4+CmVuZG9iagp4cmVmCjAgNgowMDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMTUgMDAwMDAgbiAKMDAwMDAwMDA1OSAwMDAwMCBuIAowMDAwMDAwMTE4IDAwMDAwIG4gCjAwMDAwMDAxNjcgMDAwMDAgbiAKMDAwMDAwMDI2MSAwMDAwMCBuIAp0cmFpbGVyCjw8Ci9TaXplIDYKL1Jvb3QgMyAwIFIKL0luZm8gMSAwIFIKL0lEIFsgPDMzMzYzMDY0MzAzNzM0NjUzMjM3MzYzMDMzMzEzMTM2MzA2MzYzMzU2MTYxMzAzNzY0NjUzNzM0Mzc2MjYzNjU+IDwzMzM2MzA2NDMwMzczNDY1MzIzNzM2MzAzMzMxMzEzNjMwNjM2MzM1NjE2MTMwMzc2NDY1MzczNDM3NjI2MzY1PiBdCi9FbmNyeXB0IDUgMCBSCj4+CnN0YXJ0eHJlZgo0NzYKJSVFT0YK");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "locked.pdf"), bytes, token);
        var result = await fixture.InvokeAsync(SessionId.New(), "read_file", "{\"path\":\"locked.pdf\"}", token);
        Assert.False(result.Succeeded);
        Assert.Contains("password protected", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Images);
    }

    private static byte[] CreatePdf()
    {
        const string text = "BT /F1 24 Tf 20 130 Td (PDF text K7Q2) Tj ET 1 0 0 rg 20 20 60 60 re f";
        const string scan = "q 200 0 0 150 20 20 cm BI /W 2 /H 2 /CS /RGB /BPC 8 /F /AHx ID FF00000000FF00FF00000000> EI Q";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {text.Length} >>\nstream\n{text}\nendstream",
            $"<< /Length {scan.Length} >>\nstream\n{scan}\nendstream",
        ];
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var item in objects)
        {
            offsets.Add(builder.Length);
            builder.Append($"{offsets.Count} 0 obj\n{item}\nendobj\n");
        }

        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
