namespace Threadsmith.Reranking.Local;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Threadsmith.Core;

/// <summary>Owns BERT pair tokenization and one CPU ONNX session for raw relevance logits.</summary>
internal sealed class LocalTextCrossEncoderEngine : IDisposable
{
    /// <summary>Gets the complete pair sequence limit.</summary>
    internal const int MaximumTokens = 256;

    /// <summary>Gets the request document limit.</summary>
    internal const int MaximumBatchSize = 64;
    private const long ClassificationTokenId = 101;
    private const long SeparatorTokenId = 102;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="LocalTextCrossEncoderEngine"/> class from verified assets.</summary>
    internal LocalTextCrossEncoderEngine(byte[] model, Stream vocabulary, int cpuThreads)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentOutOfRangeException.ThrowIfZero(model.Length, nameof(model));
        var tokenizer = BertTokenizer.Create(vocabulary, new BertOptions { LowerCaseBeforeTokenization = true });
        using var options = new SessionOptions
        {
            IntraOpNumThreads = cpuThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        var session = new InferenceSession(model, options);
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

    /// <summary>Releases the native session after the owning provider has drained inference.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _session.Dispose();
        }
    }

    /// <summary>Creates the reference BERT tokenizer using the production lowercasing behavior.</summary>
    internal static BertTokenizer CreateTokenizer(Stream vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        return BertTokenizer.Create(vocabulary, new BertOptions { LowerCaseBeforeTokenization = true });
    }

    /// <summary>Builds the exact production pair frame and records its pre-truncation token count.</summary>
    internal static EncodedPair Encode(BertTokenizer tokenizer, string query, string document)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);
        var first = tokenizer.EncodeToIds(query);
        var second = tokenizer.EncodeToIds(document);
        var fullCount = checked(first.Count + second.Count + 3);
        var length = Math.Min(fullCount, MaximumTokens);
        var ids = new long[length];
        var mask = new long[length];
        var types = new long[length];
        var position = 0;
        var inSecondSegment = false;
        Add(ClassificationTokenId);
        foreach (var token in first)
        {
            Add(token);
        }

        Add(SeparatorTokenId);
        foreach (var token in second)
        {
            Add(token);
        }

        Add(SeparatorTokenId);
        return new EncodedPair(ids, mask, types, fullCount, fullCount > MaximumTokens);

        void Add(long token)
        {
            if (position >= length)
            {
                return;
            }

            ids[position] = token;
            mask[position] = 1;
            types[position] = inSecondSegment ? 1 : 0;
            if (token == SeparatorTokenId && !inSecondSegment)
            {
                inSecondSegment = true;
            }

            position++;
        }
    }

    /// <summary>Runs one cancellable batch inference and returns finite raw logits in input order.</summary>
    internal IReadOnlyList<TextCrossEncoderScore> Score(
        string query,
        IReadOnlyList<string> documents,
        Action? inferenceStarted,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var pairs = documents.Select(document => Encode(_tokenizer, query, document)).ToArray();
        var sequenceLength = pairs.Max(pair => pair.InputIds.Length);
        var batchSize = pairs.Length;
        var elementCount = checked(batchSize * sequenceLength);
        var ids = new long[elementCount];
        var mask = new long[elementCount];
        var types = new long[elementCount];
        for (var row = 0; row < batchSize; row++)
        {
            var offset = row * sequenceLength;
            pairs[row].InputIds.CopyTo(ids, offset);
            pairs[row].AttentionMask.CopyTo(mask, offset);
            pairs[row].TokenTypeIds.CopyTo(types, offset);
        }

        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        NamedOnnxValue[] inputs =
        [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [batchSize, sequenceLength])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [batchSize, sequenceLength])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, [batchSize, sequenceLength])),
        ];

        try
        {
            inferenceStarted?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            using var output = _session.Run(inputs, ["logits"], options);
            cancellationToken.ThrowIfCancellationRequested();
            var logits = output.Single().AsTensor<float>().ToArray();
            if (logits.Length != batchSize)
            {
                throw new TextCrossEncoderUnavailableException("Local cross-encoder model returned an incompatible score count.");
            }

            var scores = new TextCrossEncoderScore[batchSize];
            for (var index = 0; index < batchSize; index++)
            {
                if (!float.IsFinite(logits[index]))
                {
                    throw new TextCrossEncoderUnavailableException("Local cross-encoder model returned a nonfinite relevance logit.");
                }

                scores[index] = new TextCrossEncoderScore(logits[index], pairs[index].InputTokenCount, pairs[index].WasTruncated);
            }

            return scores;
        }
        catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Local cross-encoder inference was cancelled.", exception, cancellationToken);
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
                throw new TextCrossEncoderUnavailableException("Local cross-encoder model has incompatible input names, types, or dynamic sequence dimensions.");
            }
        }

        if (session.InputMetadata.Count != 3 || session.OutputMetadata.Count != 1
            || !session.OutputMetadata.TryGetValue("logits", out var output)
            || output.ElementType != typeof(float) || output.Dimensions.Length is not (1 or 2)
            || output.Dimensions[0] is not (-1 or 1))
        {
            throw new TextCrossEncoderUnavailableException("Local cross-encoder model has incompatible output metadata.");
        }
    }

    /// <summary>Owns one pair's dynamic input tensors and complete pre-truncation accounting.</summary>
    internal sealed record EncodedPair(
        long[] InputIds,
        long[] AttentionMask,
        long[] TokenTypeIds,
        int InputTokenCount,
        bool WasTruncated);
}
