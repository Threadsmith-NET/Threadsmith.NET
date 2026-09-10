namespace Threadsmith.Embeddings.Local;

using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Threadsmith.Core;

/// <summary>Owns BERT tokenization, CPU MiniLM inference, and complete normalized vectors.</summary>
/// <remarks>The generator serializes inference and disposal and supplies verified model assets.</remarks>
internal sealed class MiniLmEmbedderEngine : IDisposable
{
    /// <summary>The complete number of components emitted by the pinned model.</summary>
    internal const int Dimensions = 384;

    /// <summary>The complete sequence bound, including the reference boundary-token framing.</summary>
    internal const int MaximumTokens = 256;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="MiniLmEmbedderEngine"/> class from verified model assets.</summary>
    internal MiniLmEmbedderEngine(byte[] model, Stream vocabulary)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentOutOfRangeException.ThrowIfZero(model.Length, nameof(model));
        var tokenizer = CreateTokenizer(vocabulary);
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        var timer = Stopwatch.StartNew();
        var session = new InferenceSession(model, options);
        SessionLoadDuration = timer.Elapsed;
        try
        {
            ValidateMetadata(session);
            _session = session;
            _tokenizer = tokenizer;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Gets successful native session construction time separately from tokenization and inference.</summary>
    internal TimeSpan SessionLoadDuration { get; }

    /// <summary>Releases the native session after the owning generator has drained inference.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _session.Dispose();
        }
    }

    /// <summary>Uses the reference engine's built-in BERT options and explicit special-token vocabulary.</summary>
    internal static BertTokenizer CreateTokenizer(Stream vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        return BertTokenizer.Create(
            vocabulary,
            new BertOptions
            {
                LowerCaseBeforeTokenization = true,
                ApplyBasicTokenization = true,
                SplitOnSpecialTokens = true,
                UnknownToken = "[UNK]",
                SeparatorToken = "[SEP]",
                PaddingToken = "[PAD]",
                ClassificationToken = "[CLS]",
                MaskingToken = "[MASK]",
            });
    }

    /// <summary>Counts complete input and preserves the reference engine's additional boundary pair.</summary>
    internal static EncodedInput Encode(BertTokenizer tokenizer, string text)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(text);

        // Preserve the production reference as-is: default BERT tokenization includes CLS/SEP,
        // and the engine adds another pair. Count both pairs before clipping the inner sequence.
        var content = tokenizer.EncodeToIds(text);
        var fullCount = checked(content.Count + 2);
        var length = Math.Min(fullCount, MaximumTokens);
        var ids = new long[length];
        var mask = new long[length];
        ids[0] = tokenizer.ClassificationTokenId;
        ids[^1] = tokenizer.SeparatorTokenId;
        for (var index = 1; index < length - 1; index++)
        {
            ids[index] = content[index - 1];
        }

        // The pinned graph accepts the actual sequence length; no fixed-length padding is needed.
        Array.Fill(mask, 1L);
        return new EncodedInput(ids, mask, new long[length], fullCount, fullCount > MaximumTokens);
    }

    /// <summary>Mean-pools attended states and returns all normalized float32 components.</summary>
    internal static float[] PoolAndNormalize(Tensor<float> hidden, ReadOnlySpan<long> attentionMask)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        if (hidden.Dimensions.Length != 3 || hidden.Dimensions[0] != 1
            || hidden.Dimensions[1] != attentionMask.Length || hidden.Dimensions[2] != Dimensions)
        {
            throw new TextEmbeddingUnavailableException("Local embedding model returned an incompatible output shape.");
        }

        var pooled = new float[Dimensions];
        var included = 0;
        for (var token = 0; token < attentionMask.Length; token++)
        {
            if (attentionMask[token] == 0)
            {
                continue;
            }

            if (attentionMask[token] != 1)
            {
                throw new TextEmbeddingUnavailableException("Local embedding model received an invalid attention mask.");
            }

            included++;
            for (var component = 0; component < Dimensions; component++)
            {
                pooled[component] += hidden[0, token, component];
            }
        }

        if (included == 0)
        {
            throw new TextEmbeddingUnavailableException("Local embedding model returned no attended token states.");
        }

        // Preserve the reference engine's float32 mean and L2 accumulation order.
        var squaredNorm = 0f;
        for (var component = 0; component < Dimensions; component++)
        {
            pooled[component] /= included;
            squaredNorm += pooled[component] * pooled[component];
        }

        var norm = MathF.Sqrt(squaredNorm);
        if (!float.IsFinite(norm) || norm <= 0)
        {
            throw new TextEmbeddingUnavailableException("Local embedding model returned a nonfinite or zero vector.");
        }

        for (var component = 0; component < Dimensions; component++)
        {
            pooled[component] /= norm;
            if (!float.IsFinite(pooled[component]))
            {
                throw new TextEmbeddingUnavailableException("Local embedding model returned a nonfinite normalized component.");
            }
        }

        return pooled;
    }

    /// <summary>Runs one cancellable inference and returns a detached, complete embedding and input accounting.</summary>
    internal TextEmbeddingResult Embed(string text, Action? inferenceStarted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = Encode(_tokenizer, text);
        cancellationToken.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        int[] shape = [1, encoded.Ids.Length];
        NamedOnnxValue[] inputs =
        [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(encoded.Ids, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(encoded.AttentionMask, shape)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(encoded.TokenTypeIds, shape)),
        ];

        try
        {
            inferenceStarted?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            using var output = _session.Run(inputs, ["last_hidden_state"], options);
            cancellationToken.ThrowIfCancellationRequested();
            var vector = PoolAndNormalize(output[0].AsTensor<float>(), encoded.AttentionMask);
            cancellationToken.ThrowIfCancellationRequested();
            return new TextEmbeddingResult(vector, encoded.FullTokenCount, encoded.WasTruncated);
        }
        catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Local embedding inference was cancelled.", exception, cancellationToken);
        }
    }

    private static void ValidateMetadata(InferenceSession session)
    {
        foreach (var name in (string[])["input_ids", "attention_mask", "token_type_ids"])
        {
            if (!session.InputMetadata.TryGetValue(name, out var input)
                || input.ElementType != typeof(long) || input.Dimensions.Length != 2
                || input.Dimensions[0] is not (-1 or 1) || input.Dimensions[1] != -1)
            {
                throw new TextEmbeddingUnavailableException("Local embedding model has incompatible input names, types, or dynamic sequence dimensions.");
            }
        }

        if (session.InputMetadata.Count != 3 || session.OutputMetadata.Count != 1
            || !session.OutputMetadata.TryGetValue("last_hidden_state", out var output)
            || output.ElementType != typeof(float) || output.Dimensions.Length != 3
            || output.Dimensions[0] is not (-1 or 1) || output.Dimensions[1] != -1
            || output.Dimensions[2] != Dimensions)
        {
            throw new TextEmbeddingUnavailableException("Local embedding model has incompatible output metadata.");
        }
    }

    /// <summary>Owns dynamic input tensors and complete pre-truncation token accounting.</summary>
    internal sealed record EncodedInput(long[] Ids, long[] AttentionMask, long[] TokenTypeIds, int FullTokenCount, bool WasTruncated);
}
