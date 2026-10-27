namespace Threadsmith.Execution;

using System.Text;
using Threadsmith.Core;

/// <summary>Retains bounded, incomplete display units so provider chunk boundaries cannot bypass redaction.</summary>
internal sealed class StreamingDisplaySanitizer
{
    private readonly IOutputSanitizer _sanitizer;
    private readonly int _maximumCharacters;
    private readonly StringBuilder _pending = new();
    private bool _omitted;
    private bool _holdRemainder;

    /// <summary>Initializes a new instance of the <see cref="StreamingDisplaySanitizer"/> class.</summary>
    internal StreamingDisplaySanitizer(IOutputSanitizer sanitizer, int maximumCharacters = 16384)
    {
        _sanitizer = sanitizer;
        _maximumCharacters = maximumCharacters;
    }

    /// <summary>Emits only complete units which the sanitizer certifies as independent of later text.</summary>
    internal IEnumerable<string> Append(string text)
    {
        foreach (var character in text)
        {
            if (_pending.Length < _maximumCharacters && !_omitted)
            {
                _pending.Append(character);
            }
            else
            {
                _omitted = true;
            }

            if (character == '\n' && !_holdRemainder && !_omitted)
            {
                if (_sanitizer is IStreamingOutputSanitizer streaming && streaming.CanFlushStandalone(_pending.ToString()))
                {
                    yield return Complete();
                }
                else
                {
                    _holdRemainder = true;
                }
            }
        }
    }

    /// <summary>Sanitizes the remaining suffix only at the end of its response, including interrupted responses.</summary>
    internal string Complete()
    {
        var safe = _omitted ? "[Oversized sensitive display suffix omitted]\n" : _sanitizer.Sanitize(_pending.ToString());
        _pending.Clear();
        _omitted = false;
        _holdRemainder = false;
        return safe;
    }
}
