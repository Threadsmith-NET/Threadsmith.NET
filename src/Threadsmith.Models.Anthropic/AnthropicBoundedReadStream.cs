namespace Threadsmith.Models.Anthropic;

using Threadsmith.Models;

/// <summary>Enforces decompressed response and individual SSE-frame bounds before SDK parsing.</summary>
internal sealed class AnthropicBoundedReadStream : Stream
{
    private const long MaximumSseFrameBytes = 1024 * 1024;
    private readonly Stream _inner;
    private readonly HttpContent _owner;
    private readonly long _maximumBytes;
    private readonly Action<long>? _observed;
    private readonly bool _sse;
    private long _bytes;
    private long _frameBytes;
    private int _lineBytes;

    /// <summary>Initializes a new instance of the <see cref="AnthropicBoundedReadStream"/> class.</summary>
    internal AnthropicBoundedReadStream(Stream inner, HttpContent owner, long maximumBytes, Action<long>? observed, bool sse)
    {
        _inner = inner;
        _owner = owner;
        _maximumBytes = maximumBytes;
        _observed = observed;
        _sse = sse;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Observe(buffer.AsSpan(offset, read));
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        Observe(buffer.Span[..read]);
        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _owner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (_maximumBytes > 0 && bytes.Length > _maximumBytes - _bytes)
        {
            throw new ModelProviderException("Anthropic response exceeded its byte ceiling.");
        }

        _bytes = checked(_bytes + bytes.Length);
        _observed?.Invoke(bytes.Length);
        if (!_sse)
        {
            return;
        }

        foreach (var value in bytes)
        {
            if (++_frameBytes > MaximumSseFrameBytes)
            {
                throw new ModelProviderException("Anthropic SSE frame exceeded its byte ceiling.");
            }

            if (value == '\n')
            {
                if (_lineBytes == 0)
                {
                    _frameBytes = 0;
                }

                _lineBytes = 0;
            }
            else if (value != '\r')
            {
                _lineBytes++;
            }
        }
    }
}
