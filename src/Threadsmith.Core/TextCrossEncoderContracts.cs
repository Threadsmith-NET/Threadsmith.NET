namespace Threadsmith.Core;

/// <summary>Scores query and document pairs with a fixed raw-logit text cross-encoder.</summary>
public interface ITextCrossEncoder
{
    /// <summary>Gets the immutable identity and operational limits of this cross-encoder.</summary>
    TextCrossEncoderModelDescriptor Model { get; }

    /// <summary>Scores each supplied document against one query in the original document order.</summary>
    /// <remarks>Higher finite raw logits indicate greater relevance. An empty document list returns no scores.</remarks>
    Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken = default);

    /// <summary>Scores recall using one bounded question while preserving complete documents.</summary>
    /// <remarks>Providers without query reduction retain strict complete-pair scoring.</remarks>
    Task<IReadOnlyList<TextCrossEncoderScore>> ScoreRecallAsync(
        TextCrossEncoderRecallQuery query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken = default)
        => ScoreAsync(query.Instruction + (query.TaskIntent is null ? string.Empty : "\n" + query.TaskIntent), documents, cancellationToken);
}

/// <summary>Current instruction takes priority over optional historical task context during recall reduction.</summary>
public sealed record TextCrossEncoderRecallQuery(string Instruction, string? TaskIntent = null);

/// <summary>Identifies pinned cross-encoder weights, pair-tokenization, and execution limits.</summary>
/// <param name="ModelId">Stable identity of the complete cross-encoder transformation.</param>
/// <param name="MaxInputTokens">Maximum pair sequence length including pair boundary tokens.</param>
/// <param name="MaxBatchSize">Maximum documents accepted in one scoring request.</param>
public sealed record TextCrossEncoderModelDescriptor(string ModelId, int MaxInputTokens, int MaxBatchSize);

/// <summary>Owns one finite raw relevance logit and complete pair-input accounting.</summary>
/// <param name="Score">Finite raw relevance logit where higher is more relevant.</param>
/// <param name="InputTokenCount">Encoded pair token count, including boundaries; intentional recall query reduction is accounted separately.</param>
/// <param name="WasTruncated">Whether the encoded pair omitted trailing tokens.</param>
public sealed record TextCrossEncoderScore(double Score, int InputTokenCount, bool WasTruncated)
{
    /// <summary>Complete query content tokens, excluding framing; null for providers without side accounting.</summary>
    public int? QueryTokenCount { get; init; }

    /// <summary>Query tokens actually scored after intentional recall reduction; null means unchanged.</summary>
    public int? UsedQueryTokenCount { get; init; }

    /// <summary>Complete document content tokens, excluding framing; null for providers without side accounting.</summary>
    public int? DocumentTokenCount { get; init; }
}

/// <summary>Rejects a pair before inference when both complete texts cannot fit.</summary>
public sealed class TextCrossEncoderInputException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="TextCrossEncoderInputException"/> class.</summary>
    public TextCrossEncoderInputException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextCrossEncoderInputException"/> class.</summary>
    public TextCrossEncoderInputException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextCrossEncoderInputException"/> class.</summary>
    public TextCrossEncoderInputException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextCrossEncoderInputException"/> class.</summary>
    public TextCrossEncoderInputException(int queryTokens, int documentTokens, int maximumTokens)
        : base($"Complete reranker pair exceeds {maximumTokens} tokens: query={queryTokens}, document={documentTokens}, boundaries=3. No pair score was produced.")
    {
        QueryTokens = queryTokens;
        DocumentTokens = documentTokens;
    }

    /// <summary>Complete query content token count.</summary>
    public int QueryTokens { get; }

    /// <summary>Complete document content token count.</summary>
    public int DocumentTokens { get; }
}

/// <summary>Signals unavailable or invalid local cross-encoder assets/runtime without leaking SDK exception types.</summary>
public sealed class TextCrossEncoderUnavailableException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="TextCrossEncoderUnavailableException"/> class.</summary>
    public TextCrossEncoderUnavailableException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextCrossEncoderUnavailableException"/> class.</summary>
    public TextCrossEncoderUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextCrossEncoderUnavailableException"/> class.</summary>
    public TextCrossEncoderUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
