namespace Threadsmith.Mcp;

/// <summary>Rejects an overlong newline-delimited protocol frame before returning it to a parser.</summary>
internal sealed class McpBoundedLineReadStream : Stream
{
    /// <summary>Maximum encoded bytes accepted in one newline-delimited protocol frame.</summary>
    internal const int MaximumLineBytes = 1024 * 1024;

    private readonly Stream _inner;
    private int _currentLineBytes;

    /// <summary>Initializes a new instance of the <see cref="McpBoundedLineReadStream"/> class.</summary>
    internal McpBoundedLineReadStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Inspect(buffer.AsSpan(offset, read));
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        Inspect(buffer.Span[..read]);
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                _currentLineBytes = 0;
                continue;
            }

            _currentLineBytes++;
            if (_currentLineBytes > MaximumLineBytes)
            {
                throw new InvalidDataException("The MCP stdio message exceeds the host wire bound.");
            }
        }
    }
}
