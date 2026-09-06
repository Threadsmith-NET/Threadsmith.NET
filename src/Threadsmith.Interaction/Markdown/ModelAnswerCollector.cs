namespace Threadsmith.Interaction.Markdown;

using System.Text;
using Threadsmith.Interaction.Presentation;

/// <summary>Collects one answer until an ordered visible-state boundary.</summary>
internal sealed class ModelAnswerCollector
{
    private readonly bool _renderMarkdown;
    private readonly IMarkdownParser _parser;
    private readonly StringBuilder _source = new();
    private long _sourceBytes;
    private bool _answerVisible;
    private bool _sourceStreaming;

    /// <summary>Initializes a new instance of the <see cref="ModelAnswerCollector"/> class.</summary>
    internal ModelAnswerCollector(bool renderMarkdown, IMarkdownParser? parser = null)
    {
        _renderMarkdown = renderMarkdown;
        _parser = parser ?? new MarkdownParser();
    }

    /// <summary>Appends a model delta and returns immediate safe-source output only in source mode.</summary>
    internal PresentationItem? Append(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Length == 0)
        {
            return null;
        }

        if (!_renderMarkdown || _sourceStreaming)
        {
            var startsSourceBlock = !_answerVisible;
            _answerVisible = true;
            return CreateSourceOutput(delta, startsSourceBlock);
        }

        _source.Append(delta);
        _sourceBytes += Encoding.UTF8.GetByteCount(delta);
        if (_sourceBytes <= MarkdownParser.MaximumSourceBytes)
        {
            return null;
        }

        _sourceStreaming = true;
        var source = _source.ToString();
        _source.Clear();
        _sourceBytes = 0;
        var startsAnswerBlock = !_answerVisible;
        _answerVisible = true;
        return CreateSourceOutput(source, startsAnswerBlock);
    }

    /// <summary>Closes the current answer before the next ordered boundary.</summary>
    internal PresentationItem? Flush(CancellationToken cancellationToken = default)
    {
        if (_sourceStreaming)
        {
            _answerVisible = false;
            _sourceStreaming = false;
            return null;
        }

        if (_source.Length == 0)
        {
            _answerVisible = false;
            return null;
        }

        var source = _source.ToString();
        _answerVisible = false;
        _source.Clear();
        _sourceBytes = 0;
        if (!_renderMarkdown || cancellationToken.IsCancellationRequested)
        {
            return CreateSourceOutput(source, startsAnswerBlock: true);
        }

        try
        {
            var parsed = _parser.Parse(source);
            if (parsed.Document is { } document)
            {
                MarkdownValidator.Validate(document);
                return new PresentationMarkdownItem(
                    document,
                    source,
                    TerminalControlEncoder.Encode(source),
                    StartsAnswerBlock: true);
            }

            return new PresentationSourceItem(
                source,
                TerminalControlEncoder.Encode(parsed.SafeSource),
                StartsAnswerBlock: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CreateSourceOutput(source, startsAnswerBlock: true);
        }
    }

    private static PresentationSourceItem CreateSourceOutput(string source, bool startsAnswerBlock)
    {
        return new(source, TerminalControlEncoder.Encode(source), startsAnswerBlock);
    }
}
