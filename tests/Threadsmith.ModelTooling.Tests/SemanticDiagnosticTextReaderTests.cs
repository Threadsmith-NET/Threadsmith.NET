namespace Threadsmith.ModelTooling.Tests;

using System.Text;
using Microsoft.CodeAnalysis.Text;
using Threadsmith.DotNet;
using Xunit;

/// <summary>Verifies bounded comparison, exact decoding, EOF handling, and cancellation cleanup.</summary>
public static class SemanticDiagnosticTextReaderTests
{
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
