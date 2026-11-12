namespace Threadsmith.Execution;

using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;

/// <summary>Serializes edit evidence without buffering an unbounded receipt or string token.</summary>
internal sealed class SourceEditReceiptSerialization
{
    private readonly int _maximumBytes;
    private readonly JsonSerializerOptions _options;

    /// <summary>Initializes a new instance of the <see cref="SourceEditReceiptSerialization"/> class for one byte ceiling.</summary>
    internal SourceEditReceiptSerialization(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumBytes = maximumBytes;
        _options = new JsonSerializerOptions();
        _options.Converters.Add(new BoundedStringConverter(maximumBytes));
    }

    /// <summary>Returns complete JSON within the UTF-8 ceiling, or null so callers can omit or reduce evidence.</summary>
    internal string? TrySerialize(SourceEditReceipt receipt)
    {
        var buffer = new ReceiptBufferWriter(_maximumBytes);
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            JsonSerializer.Serialize(writer, receipt, _options);
            writer.Flush();
            return buffer.GetJson();
        }
        catch (ReceiptLimitException)
        {
            // Partial JSON is never evidence. The caller retains the bounded transport fallback.
            return null;
        }
    }

    private sealed class ReceiptLimitException : Exception
    {
        public ReceiptLimitException()
        {
        }

        public ReceiptLimitException(string message)
            : base(message)
        {
        }

        public ReceiptLimitException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class ReceiptBufferWriter : IBufferWriter<byte>
    {
        private readonly int _maximumBytes;
        private byte[] _buffer = [];
        private int _written;

        public ReceiptBufferWriter(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
        }

        public void Advance(int count)
        {
            if (count > _maximumBytes - _written)
            {
                throw new ReceiptLimitException();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            // Utf8JsonWriter reserves worst-case escaped token space, then advances by actual bytes.
            // Permit that bounded slack so a receipt exactly at the ceiling is not rejected.
            if (sizeHint > (6L * _maximumBytes) + 4096)
            {
                throw new ReceiptLimitException();
            }

            var required = (long)_written + Math.Max(sizeHint, 1);
            if (required > Array.MaxLength)
            {
                throw new ReceiptLimitException();
            }

            if (required > _buffer.Length)
            {
                var capacity = (int)Math.Max(required, Math.Min((long)_buffer.Length * 2, _maximumBytes));
                Array.Resize(ref _buffer, capacity);
            }

            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public string GetJson() => Encoding.UTF8.GetString(_buffer.AsSpan(0, _written));
    }

    private sealed class BoundedStringConverter : JsonConverter<string>
    {
        private readonly int _maximumBytes;

        public BoundedStringConverter(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
        }

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // Check before the JSON writer rents escape/transcoding space for a single huge token.
            if (value.Length > _maximumBytes)
            {
                throw new ReceiptLimitException();
            }

            writer.WriteStringValue(value);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (value.Length > _maximumBytes)
            {
                throw new ReceiptLimitException();
            }

            writer.WritePropertyName(value);
        }
    }
}
