namespace Threadsmith.ModelTooling.Tests;

using System.Text;
using Microsoft.CodeAnalysis.Text;
using Threadsmith.DotNet;
using Xunit;

/// <summary>Verifies bounded comparison, exact decoding, EOF handling, and cancellation cleanup.</summary>
public static class SemanticDiagnosticTextReaderTests
{
    /// <summary>Only equal decoded content retains the original Roslyn text.</summary>
    [Theory]
    [InlineData(0, "same")]
    [InlineData(1, "same")]
    [InlineData(4096, "same")]
    [InlineData(12000, "same")]
    [InlineData(0, "append")]
    [InlineData(4096, "append")]
    [InlineData(12000, "append")]
    [InlineData(12000, "truncate")]
    [InlineData(12000, "empty")]
    [InlineData(12000, "replace")]
    [InlineData(12000, "new")]
    public static async Task ReadsExactContent(int length, string change)
    {
        var original = new string('a', length);
        var content = change switch
        {
            "append" => original + "😀\r\nend",
            "truncate" => original[..4096],
            "empty" => string.Empty,
            "replace" => original[..8190] + "😀changed\n" + original[8200..],
            _ => original,
        };
        var existing = change == "new" ? null : SourceText.From(original, Encoding.UTF8);
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-diagnostic-text-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            var result = await SemanticDiagnosticTextReader.ReadAsync(path, existing, TestContext.Current.CancellationToken);
            Assert.Equal(content, result.ToString());
            if (change == "same")
            {
                Assert.Same(existing, result);
            }
            else
            {
                Assert.NotSame(existing, result);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>BOM-based decoding agrees with the prior ReadAllText path across buffer boundaries.</summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-32")]
    public static async Task DecodesBomAndUnicode(string encodingName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-diagnostic-encoding-{Guid.NewGuid():N}.cs");
        var content = new string('x', 4095) + "😀é\r\n" + new string('y', 8192);
        var existing = SourceText.From(content, Encoding.UTF8);
        try
        {
            await File.WriteAllTextAsync(path, content, Encoding.GetEncoding(encodingName), TestContext.Current.CancellationToken);
            Assert.Same(existing, await SemanticDiagnosticTextReader.ReadAsync(path, existing, TestContext.Current.CancellationToken));
            var result = await SemanticDiagnosticTextReader.ReadAsync(path, null, TestContext.Current.CancellationToken);
            Assert.Equal(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), result.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Cancellation during comparison aborts the read and releases the file handle.</summary>
    [Fact]
    public static async Task CancellationDuringComparisonReleasesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-diagnostic-cancel-{Guid.NewGuid():N}.cs");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var content = new string('x', 12000);
        try
        {
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            var existing = new CancellingText(content, cancellation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SemanticDiagnosticTextReader.ReadAsync(path, existing, cancellation.Token));
            await using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(content.Length, exclusive.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CancellingText : SourceText
    {
        private readonly string _content;
        private readonly CancellationTokenSource _cancellation;

        public CancellingText(string content, CancellationTokenSource cancellation)
        {
            _content = content;
            _cancellation = cancellation;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override int Length => _content.Length;

        public override char this[int position] => _content[position];

        public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
        {
            _content.CopyTo(sourceIndex, destination, destinationIndex, count);
            _cancellation.Cancel();
        }
    }
}
