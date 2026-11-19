namespace Threadsmith.Core;

using System.Buffers.Binary;
using System.Text;

/// <summary>Messages exchanged over the private pipes of one single-use scripting worker.</summary>
public enum ScriptFrameKind : byte
{
    /// <summary>Starts the invocation.</summary>
    Start,

    /// <summary>Requests an invocation-scoped tool.</summary>
    Request,

    /// <summary>Returns the bounded result of a tool request.</summary>
    Reply,

    /// <summary>Completes the script.</summary>
    Final,
}

/// <summary>A decoded worker message.</summary>
public sealed record ScriptFrame(ScriptFrameKind Kind, string Payload);

/// <summary>The only tool-call fields accepted from the worker.</summary>
public sealed record ScriptToolRequest(string ToolId, string ArgumentsJson);

/// <summary>Sanitized result delivered to a script, not a grant of further authority.</summary>
public record ScriptToolReply(bool Succeeded, string? ResultJson, bool IsTruncated, string? ErrorClassification, string? Error);

/// <summary>Versioned UTF-8 frames. Each direction has one writer and one reader.</summary>
public static class ScriptWorkerProtocol
{
    /// <summary>Maximum UTF-8 argument bytes, before wire escaping.</summary>
    public const int ArgumentBytes = 16 * 1024;

    /// <summary>Maximum cumulative result bytes, before wire escaping.</summary>
    public const int ResultBytes = 8 * 1024 * 1024;

    /// <summary>Maximum nested calls, including rejected requests.</summary>
    public const int Attempts = 64;

    private static readonly UTF8Encoding _encoding = new(false, true);

    /// <summary>Writes one complete frame before yielding the pipe to the peer.</summary>
    public static async Task WriteAsync(Stream stream, ScriptFrameKind kind, int sequence, string payload, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = _encoding.GetByteCount(payload);
        if (length > maximumBytes)
        {
            throw new InvalidDataException("Script frame exceeds the byte limit.");
        }

        var header = new byte[10];
        header[0] = 1;
        header[1] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(6), length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(_encoding.GetBytes(payload), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Checks the header against this exchange state before allocating the body.</summary>
    public static async Task<ScriptFrame> ReadAsync(Stream stream, int sequence, Func<ScriptFrameKind, int> maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(maximumBytes);
        var header = new byte[10];
        var headerBytes = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (headerBytes == 0)
        {
            throw new EndOfStreamException("Worker pipe closed between frames.");
        }

        if (headerBytes != header.Length)
        {
            throw new InvalidDataException("Incomplete script frame header.");
        }

        var kind = (ScriptFrameKind)header[1];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(6));
        if (header[0] != 1 || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2)) != sequence || length < 0 || length > maximumBytes(kind))
        {
            throw new InvalidDataException("Invalid script protocol version, sequence, kind, or length.");
        }

        var bytes = new byte[length];
        if (await stream.ReadAtLeastAsync(bytes, length, throwOnEndOfStream: false, cancellationToken) != length)
        {
            throw new InvalidDataException("Incomplete script frame payload.");
        }

        return new ScriptFrame(kind, _encoding.GetString(bytes));
    }
}
