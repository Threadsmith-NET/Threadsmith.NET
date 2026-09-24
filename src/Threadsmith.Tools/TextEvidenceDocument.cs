namespace Threadsmith.Tools;

/// <summary>Provides bounded, line-addressable reads over one or two retained text segments.</summary>
internal sealed class TextEvidenceDocument
{
    /// <summary>Maximum characters returned by one evidence read.</summary>
    internal const int MaximumReadCharacters = 12_000;

    private const int MaximumSerializedBytesPerCharacter = 6;

    private readonly string _prefix;
    private readonly string _content;
    private readonly int[] _lineStarts;

    /// <summary>Initializes a new instance of the <see cref="TextEvidenceDocument"/> class over one retained string.</summary>
    public TextEvidenceDocument(string content)
        : this(string.Empty, content)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextEvidenceDocument"/> class without concatenating its retained segments.</summary>
    public TextEvidenceDocument(string prefix, string content)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(content);
        _prefix = prefix;
        _content = content;
        var starts = new List<int> { 0 };
        AddLineStarts(prefix, 0, starts);
        AddLineStarts(content, prefix.Length, starts);
        _lineStarts = [.. starts];
    }

    /// <summary>Reads a bounded portion, including a column cursor for unusually long lines.</summary>
    public TextEvidenceReadResult Read(
        int startLine = 1,
        int? endLine = null,
        int startColumn = 1,
        int maximumCharacters = MaximumReadCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        if (startLine < 1 || startLine > _lineStarts.Length || startColumn < 1
            || endLine is < 1 || (endLine is { } end && end < startLine))
        {
            throw new ToolArgumentValidationException("Evidence lines and columns must be positive, ordered, and within the captured evidence.");
        }

        var totalLength = _prefix.Length + _content.Length;
        var lineStart = _lineStarts[startLine - 1];
        var nextLineStart = startLine < _lineStarts.Length ? _lineStarts[startLine] : totalLength + 1;
        var position = lineStart + startColumn - 1;
        if (position >= nextLineStart || position > totalLength)
        {
            throw new ToolArgumentValidationException("The requested evidence column is outside its line.");
        }

        var lastLine = Math.Min(endLine ?? _lineStarts.Length, _lineStarts.Length);
        var endPosition = lastLine < _lineStarts.Length ? _lineStarts[lastLine] : totalLength;
        if (endLine is not null && endPosition > position && CharacterAt(endPosition - 1) == '\n')
        {
            endPosition--;
        }

        var count = Math.Min(Math.Min(MaximumReadCharacters, maximumCharacters), endPosition - position);
        if (count > 0 && position + count < totalLength && char.IsHighSurrogate(CharacterAt(position + count - 1)))
        {
            count--;
        }

        var content = Slice(position, count);
        var nextPosition = position + count;
        var nextLineIndex = Array.BinarySearch(_lineStarts, nextPosition);
        if (nextLineIndex < 0)
        {
            nextLineIndex = ~nextLineIndex - 1;
        }

        return new TextEvidenceReadResult(
            content,
            startLine,
            startColumn,
            _lineStarts.Length,
            nextPosition < endPosition ? nextLineIndex + 1 : null,
            nextPosition < endPosition ? nextPosition - _lineStarts[nextLineIndex] + 1 : null);
    }

    /// <summary>Converts a serialized-byte allowance into a worst-case JSON-safe text allowance.</summary>
    internal static int GetMaximumReadCharacters(int deliveryBudgetBytes, int envelopeBytes)
    {
        var availableBytes = Math.Max(0, deliveryBudgetBytes - envelopeBytes);
        return Math.Min(MaximumReadCharacters, availableBytes / MaximumSerializedBytesPerCharacter);
    }

    private static void AddLineStarts(string text, int offset, List<int> starts)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(offset + index + 1);
            }
        }
    }

    private string Slice(int position, int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        if (position >= _prefix.Length)
        {
            return _content.Substring(position - _prefix.Length, length);
        }

        var prefixLength = Math.Min(length, _prefix.Length - position);
        return prefixLength == length
            ? _prefix.Substring(position, length)
            : string.Concat(_prefix.AsSpan(position, prefixLength), _content.AsSpan(0, length - prefixLength));
    }

    private char CharacterAt(int position) => position < _prefix.Length
        ? _prefix[position]
        : _content[position - _prefix.Length];
}

/// <summary>A bounded text-evidence segment and its exact continuation position.</summary>
internal sealed record TextEvidenceReadResult(
    string Content,
    int StartLine,
    int StartColumn,
    int TotalLines,
    int? NextLine,
    int? NextColumn);
