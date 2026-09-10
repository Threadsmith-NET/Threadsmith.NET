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
}

/// <summary>Identifies pinned cross-encoder weights, pair-tokenization, and execution limits.</summary>
/// <param name="ModelId">Stable identity of the complete cross-encoder transformation.</param>
/// <param name="MaxInputTokens">Maximum pair sequence length including pair boundary tokens.</param>
/// <param name="MaxBatchSize">Maximum documents accepted in one scoring request.</param>
public sealed record TextCrossEncoderModelDescriptor(string ModelId, int MaxInputTokens, int MaxBatchSize);

/// <summary>Owns one finite raw relevance logit and complete pair-input accounting.</summary>
/// <param name="Score">Finite raw relevance logit where higher is more relevant.</param>
/// <param name="InputTokenCount">Pair token count before truncation, including pair boundary tokens.</param>
/// <param name="WasTruncated">Whether the encoded pair omitted trailing tokens.</param>
public sealed record TextCrossEncoderScore(double Score, int InputTokenCount, bool WasTruncated);

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
