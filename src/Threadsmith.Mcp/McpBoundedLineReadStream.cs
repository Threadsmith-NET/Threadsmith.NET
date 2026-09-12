namespace Threadsmith.Mcp;

/// <summary>Rejects an overlong newline-delimited protocol frame before returning it to a parser.</summary>
internal sealed class McpBoundedLineReadStream : Stream
{
    /// <summary>Maximum encoded bytes accepted in one newline-delimited protocol frame.</summary>
    internal const int MaximumLineBytes = 1024 * 1024;

    private readonly int _maximumLineBytes;
    private readonly Stream _inner;
    private long _currentLineBytes;

    /// <summary>Initializes a new instance of the <see cref="McpBoundedLineReadStream"/> class.</summary>
    internal McpBoundedLineReadStream(Stream inner, int maximumLineBytes = MaximumLineBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineBytes);
        _maximumLineBytes = maximumLineBytes;
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
            if (_currentLineBytes > _maximumLineBytes)
            {
                throw new InvalidDataException("The MCP stdio message exceeds the host wire bound.");
            }
        }
    }
}
