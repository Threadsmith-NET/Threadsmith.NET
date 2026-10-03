namespace Threadsmith.Mutations.Tests;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies lazy allocation and lossless baseline encoding metadata.</summary>
public static class BaselineFileSnapshotTests
{
    /// <summary>Capture retains raw bytes without allocating a decoded payload.</summary>
    [Fact]
    public static void Capture_DoesNotAllocateDecodedPayloadUntilTextIsRequested()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 1024 * 1024));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _ = BaselineFileSnapshot.FromBytes([65], "warmup").Text;
        var before = GC.GetAllocatedBytesForCurrentThread();

        var snapshot = BaselineFileSnapshot.FromBytes(bytes, hash);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < bytes.Length, $"Capture allocated {allocated} bytes before text was requested.");
        Assert.Equal(bytes.Length, snapshot.Text.Length);
        Assert.Same(snapshot.Text, snapshot.Text);
    }

    /// <summary>Lazy decoding preserves supported encodings and preambles.</summary>
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public static void LazyText_PreservesEncodingAndPreamble(string encodingName)
    {
        var encoding = encodingName switch
        {
            "utf8bom" => new UTF8Encoding(true),
            "utf16le" => Encoding.Unicode,
            "utf16be" => Encoding.BigEndianUnicode,
            _ => new UTF8Encoding(false),
        };
        const string text = "// α😀\r\nsource\n";
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var snapshot = BaselineFileSnapshot.FromBytes(bytes, hash);

        Assert.Equal(text, snapshot.Text);
        Assert.Equal(encoding.CodePage, snapshot.Encoding.CodePage);
        Assert.Equal(encoding.GetPreamble().Length > 0, snapshot.HasPreamble);
        Assert.Equal(bytes, snapshot.Bytes);
        Assert.Equal(hash, snapshot.Sha256);
    }

    /// <summary>Invalid UTF-8 still fails at capture time.</summary>
    [Fact]
    public static void Capture_StillRejectsInvalidUtf8BeforeTextIsRequested()
    {
        Assert.Throws<DecoderFallbackException>(() => BaselineFileSnapshot.FromBytes([0xC3, 0x28], "invalid"));
    }
}
