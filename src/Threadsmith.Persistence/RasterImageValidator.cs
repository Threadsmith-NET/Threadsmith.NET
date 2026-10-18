namespace Threadsmith.Persistence;

using System.Buffers.Binary;
using System.IO.Compression;
using StbImageSharp;
using Threadsmith.Core;

/// <summary>Validates encoded structure and bounded dimensions before invoking the decoder.</summary>
internal static class RasterImageValidator
{
    /// <summary>Checks structure, dimensions and decodability without transforming source bytes.</summary>
    internal static (string MediaType, int Width, int Height) Validate(byte[] buffer, int length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = buffer.AsSpan(0, length);
        var png = bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217;
        if (!png && !jpeg)
        {
            throw new InvalidDataException("Only complete PNG and JPEG images are supported.");
        }

        using var stream = new MemoryStream(buffer, 0, length, writable: false);
        var info = ImageInfo.FromStream(stream) ?? throw new InvalidDataException("The image header is invalid.");
        if (info.Width <= 0 || info.Height <= 0 || info.Width > ImageArtifactLimits.MaximumDimension
            || info.Height > ImageArtifactLimits.MaximumDimension || (long)info.Width * info.Height > ImageArtifactLimits.MaximumPixels)
        {
            throw new InvalidDataException("The image exceeds the host dimension or pixel limit.");
        }

        if (png)
        {
            ValidatePng(bytes, info.Width, info.Height, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Encoded bytes and decoded dimensions are already bounded. The synchronous decoder is
        // serialized by the store gate and its result is discarded if cancellation arrives.
        stream.Position = 0;
        var decoded = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.Width != info.Width || decoded.Height != info.Height)
        {
            throw new InvalidDataException("Image dimensions changed during decoding.");
        }

        return (png ? "image/png" : "image/jpeg", info.Width, info.Height);
    }

    private static void ValidatePng(ReadOnlySpan<byte> bytes, int width, int height, CancellationToken cancellationToken)
    {
        using var compressed = new MemoryStream();
        var offset = 8;
        var ended = false;
        while (offset <= bytes.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            var size = (int)length;
            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8) || (offset == 8 && !type.SequenceEqual("IHDR"u8)))
            {
                throw new InvalidDataException("Animated or malformed PNG images are unsupported.");
            }

            var crc = uint.MaxValue;
            foreach (var value in bytes.Slice(offset + 4, size + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
                }
            }

            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + size, 4)))
            {
                throw new InvalidDataException("PNG chunk checksum mismatch.");
            }

            if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(bytes.Slice(offset + 8, size));
            }

            offset += size + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                ended = size == 0 && offset == bytes.Length;
                break;
            }
        }

        if (!ended || compressed.Length == 0)
        {
            throw new InvalidDataException("PNG image is incomplete.");
        }

        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var buffer = new byte[8192];
        long expanded = 0;
        var maximum = ((long)width * height * 8) + (height * 8L) + 1024;
        int read;
        while ((read = inflater.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expanded += read;
            if (expanded > maximum)
            {
                throw new InvalidDataException("PNG compressed data exceeds its bounded pixel dimensions.");
            }
        }
    }
}
