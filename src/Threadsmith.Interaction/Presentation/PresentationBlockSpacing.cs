namespace Threadsmith.Interaction.Presentation;

/// <summary>Tracks the visible text ending so every agent uses the same lifecycle block spacing.</summary>
internal sealed class PresentationBlockSpacing
{
    private string _tail = string.Empty;

    /// <summary>Records enough trailing text to recognize two line breaks, including CRLF.</summary>
    internal void Observe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length >= 4)
        {
            _tail = text[^4..];
        }
        else if (text.Length > 0)
        {
            var combined = _tail + text;
            _tail = combined.Length > 4 ? combined[^4..] : combined;
        }
    }

    /// <summary>Gets the separator before a block, preserving the first response's leading line.</summary>
    internal string BeforeBlock(bool awaitingFirstResponse = false)
    {
        if (awaitingFirstResponse)
        {
            return Environment.NewLine;
        }

        if (_tail.Length == 0)
        {
            return string.Empty;
        }

        var count = 0;
        for (var index = _tail.Length - 1; index >= 0;)
        {
            if (_tail[index] == '\n')
            {
                count++;
                index--;
                if (index >= 0 && _tail[index] == '\r')
                {
                    index--;
                }
            }
            else if (_tail[index] == '\r')
            {
                count++;
                index--;
            }
            else
            {
                break;
            }
        }

        return count switch
        {
            0 => Environment.NewLine + Environment.NewLine,
            1 => Environment.NewLine,
            _ => string.Empty,
        };
    }
}
