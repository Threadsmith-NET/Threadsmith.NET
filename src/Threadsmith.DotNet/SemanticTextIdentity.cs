namespace Threadsmith.DotNet;

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.Text;

/// <summary>Hashes decoded text as UTF-8 without retaining another full text or byte copy.</summary>
internal static class SemanticTextIdentity
{
    /// <summary>Hashes the exact immutable Roslyn text, excluding its encoding preamble.</summary>
    internal static string Compute(SourceText text, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        text.Write(writer, cancellationToken);
        writer.Flush();
        stream.FlushFinalBlock();
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.Hash ?? throw new InvalidDataException("The semantic text hash is unavailable."));
    }

    /// <summary>Hashes decoded disk content using fixed buffers, preserving split surrogate pairs.</summary>
    internal static async Task<string> ComputeAsync(TextReader reader, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        await using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        await stream.FlushFinalBlockAsync(cancellationToken);
        return Convert.ToHexString(hash.Hash ?? throw new InvalidDataException("The semantic text hash is unavailable."));
    }
}
