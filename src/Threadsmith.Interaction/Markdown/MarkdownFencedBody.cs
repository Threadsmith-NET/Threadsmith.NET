namespace Threadsmith.Interaction.Markdown;

using System.Collections.Immutable;
using System.Text;

/// <summary>Associates one fence body with the exact admitted source parsed by the host.</summary>
public sealed class MarkdownFencedBody
{
    private readonly string _source;
    private readonly ImmutableArray<(int Start, int Length)> _ranges;

    /// <summary>Initializes a new instance of the <see cref="MarkdownFencedBody"/> class from checked parser ranges.</summary>
    internal MarkdownFencedBody(string source, int ordinal, bool isClosed, ImmutableArray<(int Start, int Length)> ranges)
    {
        _source = source;
        Ordinal = ordinal;
        IsClosed = isClosed;
        _ranges = ranges;
        CharacterCount = ranges.Sum(range => range.Length);
    }

    /// <summary>Gets the source-order identity within one answer item.</summary>
    public int Ordinal { get; }

    /// <summary>Gets whether the parser observed a closing fence, rather than an unfinished fragment.</summary>
    public bool IsClosed { get; }

    /// <summary>Gets the bounded source-mapping storage charge, excluding the already retained source.</summary>
    public int MetadataBytes => 64 + (_ranges.Length * 8);

    /// <summary>Gets the number of original body characters, including original line endings.</summary>
    public int CharacterCount { get; }

    /// <summary>Checks the admitted-source association without reading or allocating body text.</summary>
    public bool IsAssociatedWith(string source) => ReferenceEquals(source, _source);

    /// <summary>Reads exact body characters only for the original admitted source association.</summary>
    public bool TryGetText(string source, out string text)
    {
        text = string.Empty;
        if (!ReferenceEquals(source, _source))
        {
            return false;
        }

        var body = new StringBuilder();
        foreach (var (start, length) in _ranges)
        {
            body.Append(source.AsSpan(start, length));
        }

        text = body.ToString();
        return true;
    }
}
