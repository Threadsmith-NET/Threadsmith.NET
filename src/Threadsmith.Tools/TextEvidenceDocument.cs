namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Applies tool argument classification to the shared captured-text pager.</summary>
internal sealed class TextEvidenceDocument
{
    /// <summary>Maximum characters returned by one evidence read.</summary>
    internal const int MaximumReadCharacters = CapturedTextDocument.MaximumReadCharacters;
    private readonly CapturedTextDocument _document;

    /// <summary>Initializes a new instance of the <see cref="TextEvidenceDocument"/> class.</summary>
    public TextEvidenceDocument(string content)
        : this(string.Empty, content)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextEvidenceDocument"/> class over two segments.</summary>
    public TextEvidenceDocument(string prefix, string content)
    {
        _document = new CapturedTextDocument(prefix, content);
    }

    /// <summary>Reads a page and classifies invalid tool arguments.</summary>
    public TextEvidenceReadResult Read(int startLine = 1, int? endLine = null, int startColumn = 1, int maximumCharacters = MaximumReadCharacters)
    {
        try
        {
            return _document.Read(startLine, endLine, startColumn, maximumCharacters);
        }
        catch (ArgumentException exception)
        {
            throw new ToolArgumentValidationException(exception.Message);
        }
    }

    /// <summary>Calculates the text allowance within a JSON delivery budget.</summary>
    internal static int GetMaximumReadCharacters(int deliveryBudgetBytes, int envelopeBytes)
        => CapturedTextDocument.GetMaximumReadCharacters(deliveryBudgetBytes, envelopeBytes);
}
