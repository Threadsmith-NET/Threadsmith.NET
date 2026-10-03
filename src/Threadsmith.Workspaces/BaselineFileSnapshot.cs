namespace Threadsmith.Workspaces;

using System.Security.Cryptography;
using System.Text;

/// <summary>Privately owned captured bytes with validated encoding and lazily cached text.</summary>
internal sealed class BaselineFileSnapshot
{
    private readonly Lazy<string> _text;

    private BaselineFileSnapshot(byte[] bytes, string sha256, Encoding encoding, int preambleLength)
    {
        Bytes = bytes;
        Sha256 = sha256;
        Encoding = encoding;
        HasPreamble = preambleLength > 0;

        // Preserve capture-time encoding failures without retaining a decoded copy.
        _ = encoding.GetCharCount(bytes, preambleLength, bytes.Length - preambleLength);
        _text = new Lazy<string>(() => encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength));
    }

    /// <summary>Gets owned bytes, which must never be mutated.</summary>
    internal byte[] Bytes { get; }

    /// <summary>Gets the hash of the captured bytes.</summary>
    internal string Sha256 { get; }

    /// <summary>Gets the encoding to preserve when editing this file.</summary>
    internal Encoding Encoding { get; }

    /// <summary>Gets whether the captured bytes contain an encoding preamble.</summary>
    internal bool HasPreamble { get; }

    /// <summary>Gets text decoded once, on first demand, from the captured bytes.</summary>
    internal string Text => _text.Value;

    /// <summary>Reads exactly the admitted length; growth or truncation cannot expand the allocation.</summary>
    internal static async Task<BaselineFileSnapshot> CaptureAsync(
        string path,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        if (expectedLength > Array.MaxLength)
        {
            throw new InvalidOperationException($"Workspace baseline file '{path}' exceeds the supported snapshot size.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
        {
            throw new InvalidOperationException($"Workspace baseline file '{path}' changed before capture completed.");
        }

        var bytes = new byte[checked((int)expectedLength)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.Length != expectedLength)
        {
            throw new InvalidOperationException($"Workspace baseline file '{path}' changed before capture completed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = FromBytes(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    /// <summary>Takes ownership of bytes which callers must never subsequently mutate.</summary>
    internal static BaselineFileSnapshot FromBytes(byte[] bytes, string sha256)
    {
        Encoding encoding;
        var preambleLength = 0;
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            preambleLength = Encoding.UTF8.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            encoding = Encoding.Unicode;
            preambleLength = Encoding.Unicode.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            encoding = Encoding.BigEndianUnicode;
            preambleLength = Encoding.BigEndianUnicode.GetPreamble().Length;
        }
        else
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }

        return new BaselineFileSnapshot(bytes, sha256, encoding, preambleLength);
    }
}
