namespace Threadsmith.Mcp;

using System.Net;

/// <summary>Enforces a wire-response ceiling before SDK JSON materialization.</summary>
internal sealed class McpBoundedHttpResponseHandler : DelegatingHandler
{
    /// <summary>Maximum encoded bytes accepted from one HTTP response.</summary>
    internal const int MaximumResponseBytes = 1024 * 1024;

    /// <summary>Initializes a new instance of the <see cref="McpBoundedHttpResponseHandler"/> class.</summary>
    internal McpBoundedHttpResponseHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var isEventStream = string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase);
        if (!isEventStream && response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            response.Dispose();
            throw new InvalidDataException("The MCP HTTP response exceeds the host wire bound.");
        }

        response.Content = new BoundedHttpContent(
            response.Content,
            MaximumResponseBytes,
            isEventStream);
        return response;
    }

    private sealed class BoundedHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly int _maximumBytes;
        private readonly bool _resetAtSseEventBoundary;

        internal BoundedHttpContent(
            HttpContent inner,
            int maximumBytes,
            bool resetAtSseEventBoundary)
        {
            _inner = inner;
            _maximumBytes = maximumBytes;
            _resetAtSseEventBoundary = resetAtSseEventBoundary;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using var source = new BoundedReadStream(
                await _inner.ReadAsStreamAsync(cancellationToken),
                _maximumBytes,
                _resetAtSseEventBoundary,
                leaveOpen: true);
            await source.CopyToAsync(stream, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return CreateContentReadStreamAsync(CancellationToken.None);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            return new BoundedReadStream(
                await _inner.ReadAsStreamAsync(cancellationToken),
                _maximumBytes,
                _resetAtSseEventBoundary,
                leaveOpen: true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly long _maximumBytes;
        private readonly bool _resetAtSseEventBoundary;
        private long _bytesRead;
        private bool _sseLineHasContent;

        internal BoundedReadStream(
            Stream inner,
            long maximumBytes,
            bool resetAtSseEventBoundary,
            bool leaveOpen)
        {
            _inner = inner;
            _maximumBytes = maximumBytes;
            _resetAtSseEventBoundary = resetAtSseEventBoundary;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, LimitCount(count));
            Inspect(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(
                buffer[..LimitCount(buffer.Length)],
                cancellationToken);
            Inspect(buffer.Span[..read]);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int LimitCount(int requestedCount)
        {
            var remainingWithSentinel = (_maximumBytes + 1) - _bytesRead;
            return (int)Math.Min(requestedCount, Math.Max(1, remainingWithSentinel));
        }

        private void Inspect(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                _bytesRead++;
                if (_bytesRead > _maximumBytes)
                {
                    throw new InvalidDataException("The MCP HTTP response exceeds the host wire bound.");
                }

                if (!_resetAtSseEventBoundary)
                {
                    continue;
                }

                if (value == (byte)'\n')
                {
                    if (!_sseLineHasContent)
                    {
                        _bytesRead = 0;
                    }

                    _sseLineHasContent = false;
                }
                else if (value != (byte)'\r')
                {
                    _sseLineHasContent = true;
                }
            }
        }
    }
}
