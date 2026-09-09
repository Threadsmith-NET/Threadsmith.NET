namespace Threadsmith.Core;

/// <summary>Generates detached, normalized text vectors without exposing inference implementation types.</summary>
public interface ITextEmbeddingGenerator
{
    /// <summary>Gets the immutable identity and complete-input limit of this embedding space.</summary>
    TextEmbeddingModelDescriptor Model { get; }

    /// <summary>Encodes text and reports its complete token count, including special tokens.</summary>
    /// <remarks>Consumers storing complete text must reject truncated results.</remarks>
    Task<TextEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Identifies weights, tokenization, pooling, normalization, and input-limit behavior.</summary>
/// <param name="SpaceId">Stable identity of the entire embedding transformation.</param>
/// <param name="Dimensions">Number of vector components.</param>
/// <param name="MaxInputTokens">Maximum complete sequence length including special tokens.</param>
public sealed record TextEmbeddingModelDescriptor(string SpaceId, int Dimensions, int MaxInputTokens);

/// <summary>Owns a detached finite, nonzero, L2-normalized vector and complete-input accounting.</summary>
/// <param name="Vector">Detached vector with all model dimensions retained.</param>
/// <param name="InputTokenCount">Token count before truncation, including special tokens.</param>
/// <param name="WasTruncated">Whether the encoded sequence omitted input content.</param>
public sealed record TextEmbeddingResult(ReadOnlyMemory<float> Vector, int InputTokenCount, bool WasTruncated);

/// <summary>Signals unavailable or invalid local embedding assets/runtime without leaking SDK exception types.</summary>
public sealed class TextEmbeddingUnavailableException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="TextEmbeddingUnavailableException"/> class.</summary>
    public TextEmbeddingUnavailableException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextEmbeddingUnavailableException"/> class.</summary>
    public TextEmbeddingUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TextEmbeddingUnavailableException"/> class.</summary>
    public TextEmbeddingUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
