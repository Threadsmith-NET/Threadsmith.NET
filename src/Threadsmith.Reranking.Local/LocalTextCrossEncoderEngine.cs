namespace Threadsmith.Reranking.Local;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Threadsmith.Core;

/// <summary>Owns BERT pair tokenization and one CPU ONNX session for raw relevance logits.</summary>
internal sealed class LocalTextCrossEncoderEngine : IDisposable
{
    /// <summary>Gets the complete pair sequence limit.</summary>
    internal const int MaximumTokens = 512;

    /// <summary>Gets the request document limit.</summary>
    internal const int MaximumBatchSize = 8;

    /// <summary>Document-independent recall question budget, leaving room for a complete stored memory.</summary>
    internal const int RecallQueryTokens = 128;
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

    /// <summary>Builds one canonical complete pair or rejects overflow before inference.</summary>
    internal static EncodedPair Encode(BertTokenizer tokenizer, string query, string document, bool useRecallQuery = false, string? taskIntent = null)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);
        var first = tokenizer.EncodeToIds(query, addSpecialTokens: false);
        var context = useRecallQuery && !string.IsNullOrWhiteSpace(taskIntent)
            && !query.Contains(taskIntent, StringComparison.Ordinal)
            ? tokenizer.EncodeToIds(taskIntent, addSpecialTokens: false) : [];
        var originalQueryTokens = first.Count + context.Count;
        if (useRecallQuery)
        {
            // Keep current instructions ahead of historical context, using the same
            // document-independent question across every scoring batch.
            if (first.Count > RecallQueryTokens)
            {
                var firstEnd = query.Length;
                var lastStart = 0;
                for (var index = 0; index < query.Length - 1; index++)
                {
                    if (query[index] is not ('\r' or '\n')
                        && !(query[index] is '.' or '?' or '!' && char.IsWhiteSpace(query[index + 1])))
                    {
                        continue;
                    }

                    var next = index + 1;
                    while (next < query.Length && char.IsWhiteSpace(query[next]))
                    {
                        next++;
                    }

                    if (next < query.Length)
                    {
                        firstEnd = Math.Min(firstEnd, index + 1);
                        lastStart = next;
                    }
                }

                var opening = tokenizer.EncodeToIds(query[..firstEnd], addSpecialTokens: false);
                var closing = tokenizer.EncodeToIds(query[lastStart..], addSpecialTokens: false);
                first = [.. opening.Take(RecallQueryTokens / 2), .. closing.TakeLast(RecallQueryTokens / 2)];
            }
            else
            {
                first = [.. first, .. context.Take(RecallQueryTokens - first.Count)];
            }
        }

        var second = tokenizer.EncodeToIds(document, addSpecialTokens: false);
        var fullCount = checked(first.Count + second.Count + 3);
        if (fullCount > MaximumTokens)
        {
            throw new TextCrossEncoderInputException(first.Count, second.Count, MaximumTokens);
        }

        var length = fullCount;
        var ids = new long[length];
        var mask = new long[length];
        var types = new long[length];
        var position = 0;
        var segment = 0L;
        Add(ClassificationTokenId);
        foreach (var token in first)
        {
            Add(token);
        }

        Add(SeparatorTokenId);
        segment = 1;
        foreach (var token in second)
        {
            Add(token);
        }

        Add(SeparatorTokenId);
        return new EncodedPair(ids, mask, types, fullCount, false)
        {
            QueryTokenCount = originalQueryTokens,
            UsedQueryTokenCount = first.Count,
            DocumentTokenCount = second.Count,
        };

        void Add(long token)
        {
            ids[position] = token;
            mask[position] = 1;
            types[position] = segment;
            position++;
        }
    }

    /// <summary>Runs one cancellable batch inference and returns finite raw logits in input order.</summary>
    internal IReadOnlyList<TextCrossEncoderScore> Score(
        string query,
        IReadOnlyList<string> documents,
        Action? inferenceStarted,
        CancellationToken cancellationToken,
        bool useRecallQuery = false,
        string? taskIntent = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var pairs = documents.Select(document => Encode(_tokenizer, query, document, useRecallQuery, taskIntent)).ToArray();
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

                scores[index] = new TextCrossEncoderScore(logits[index], pairs[index].InputTokenCount, pairs[index].WasTruncated)
                {
                    QueryTokenCount = pairs[index].QueryTokenCount,
                    UsedQueryTokenCount = pairs[index].UsedQueryTokenCount,
                    DocumentTokenCount = pairs[index].DocumentTokenCount,
                };
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
        bool WasTruncated)
    {
        /// <summary>Complete query content length.</summary>
        public int QueryTokenCount { get; init; }

        /// <summary>Query content length after intentional recall reduction.</summary>
        public int UsedQueryTokenCount { get; init; }

        /// <summary>Complete document content length.</summary>
        public int DocumentTokenCount { get; init; }
    }
}
