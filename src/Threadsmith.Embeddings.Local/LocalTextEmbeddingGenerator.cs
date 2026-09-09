namespace Threadsmith.Embeddings.Local;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Threadsmith.Core;

/// <summary>Owns one lazy, serialized CPU MiniLM session using only verified application assets.</summary>
public sealed class LocalTextEmbeddingGenerator : ITextEmbeddingGenerator, IAsyncDisposable
{
    /// <summary>Fixed cosine minimum selected by the versioned MiniLM calibration fixture.</summary>
    public const double SemanticMinimum = 0.47;

    /// <summary>Gets the exact number of MiniLM vector components.</summary>
    internal const int Dimensions = 384;

    /// <summary>Gets the complete MiniLM sequence limit.</summary>
    internal const int MaximumTokens = 256;
    private const string SpaceId = "minilm-l12-v2:9bc18616990647530c139b95df1d1aa30cd115b7:bert-uncased:mean-mask:l2:256:v1";
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _assetDirectory;
    private readonly Action? _inferenceStarted;
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="LocalTextEmbeddingGenerator"/> class.</summary>
    public LocalTextEmbeddingGenerator()
        : this(Path.Combine(AppContext.BaseDirectory, "embeddings", "all-MiniLM-L12-v2"))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LocalTextEmbeddingGenerator"/> class using a test-owned asset root.</summary>
    internal LocalTextEmbeddingGenerator(string assetDirectory, Action? inferenceStarted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDirectory);
        _assetDirectory = assetDirectory;
        _inferenceStarted = inferenceStarted;
    }

    /// <inheritdoc />
    public TextEmbeddingModelDescriptor Model { get; } = new(SpaceId, Dimensions, MaximumTokens);

    /// <summary>Gets the measured successful native session construction time for offline verification.</summary>
    internal TimeSpan SessionLoadDuration { get; private set; }

    /// <inheritdoc />
    public async Task<TextEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await _inferenceGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            linked.Dispose();
            throw;
        }

        // The worker retains the gate and native lifetime when a caller abandons a cold load/run.
        // A cancelled caller never permits an additional native session or concurrent inference.
        var worker = Task.Run(() => GenerateOnWorker(text, linked), CancellationToken.None);
        return await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels pending work and releases native resources without an unbounded terminal shutdown.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (await _inferenceGate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
        {
            try
            {
                DisposeSession();
            }
            finally
            {
                _inferenceGate.Release();
            }
        }

        // An abandoned non-cooperative native initialization owns eventual disposal in its finally.
        // The managed gate/source remain valid for callers that raced disposal before admission.
    }

    /// <summary>Constructs the pinned BERT tokenization behavior.</summary>
    internal static BertTokenizer CreateTokenizer(Stream vocabulary) => BertTokenizer.Create(
        vocabulary,
        new BertOptions
        {
            LowerCaseBeforeTokenization = false,
            ApplyBasicTokenization = true,
            IndividuallyTokenizeCjk = true,
            RemoveNonSpacingMarks = true,
            SplitOnSpecialTokens = true,
            Normalizer = new MiniLmNormalizer(),
            PreTokenizer = new MiniLmPreTokenizer(),
        });

    /// <summary>Encodes a complete count and bounded, dynamically padded sequence.</summary>
    internal static EncodedInput Encode(BertTokenizer tokenizer, string text)
    {
        // This overload explicitly excludes boundary tokens. Add exactly one pair ourselves,
        // retaining the pre-truncation count and ensuring [SEP] survives overflow.
        var content = tokenizer.EncodeToIds(text, addSpecialTokens: false);
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

        Array.Fill(mask, 1L);
        return new EncodedInput(ids, mask, new long[length], fullCount, fullCount > MaximumTokens);
    }

    /// <summary>Mean-pools only attended tokens and returns every normalized component.</summary>
    internal static float[] PoolAndNormalize(Tensor<float> hidden, ReadOnlySpan<long> attentionMask)
    {
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

            included++;
            for (var component = 0; component < Dimensions; component++)
            {
                pooled[component] += hidden[0, token, component];
            }
        }

        var squaredNorm = 0d;
        for (var component = 0; component < Dimensions; component++)
        {
            pooled[component] /= Math.Max(1, included);
            squaredNorm += (double)pooled[component] * pooled[component];
        }

        if (!double.IsFinite(squaredNorm) || squaredNorm <= 0)
        {
            throw new TextEmbeddingUnavailableException("Local embedding model returned a nonfinite or zero vector.");
        }

        var norm = Math.Sqrt(squaredNorm);
        for (var component = 0; component < Dimensions; component++)
        {
            pooled[component] = (float)(pooled[component] / norm);
        }

        return pooled;
    }

    private TextEmbeddingResult GenerateOnWorker(string text, CancellationTokenSource linked)
    {
        try
        {
            var cancellationToken = linked.Token;
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLoaded(cancellationToken);
            var tokenizer = _tokenizer ?? throw new TextEmbeddingUnavailableException("Local embedding tokenizer did not initialize.");
            var session = _session ?? throw new TextEmbeddingUnavailableException("Local embedding runtime did not initialize.");
            var encoded = Encode(tokenizer, text);
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
            _inferenceStarted?.Invoke();
            using var output = session.Run(inputs, ["last_hidden_state"], options);
            cancellationToken.ThrowIfCancellationRequested();
            var vector = PoolAndNormalize(output[0].AsTensor<float>(), encoded.AttentionMask);
            return new TextEmbeddingResult(vector, encoded.FullTokenCount, encoded.WasTruncated);
        }
        catch (Exception ex) when (linked.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("Local embedding generation was cancelled.", ex, linked.Token);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            throw new TextEmbeddingUnavailableException(
                "Local embeddings are unavailable. Verify the bundled MiniLM assets and matching native runtime; list, inspect, remove, and lexical retrieval remain available.",
                ex);
        }
        finally
        {
            linked.Dispose();
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeSession();
            }

            _inferenceGate.Release();
        }
    }

    private void EnsureLoaded(CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            return;
        }

        using var manifestStream = typeof(LocalTextEmbeddingGenerator).Assembly.GetManifestResourceStream(
            "Threadsmith.Embeddings.Local.minilm-assets.json")
            ?? throw new TextEmbeddingUnavailableException("The application embedding asset manifest is missing.");
        using var manifest = JsonDocument.Parse(manifestStream);
        var artifacts = manifest.RootElement.GetProperty("artifacts");
        var model = ReadVerifiedArtifact(artifacts, "model.onnx", cancellationToken);
        var vocabulary = ReadVerifiedArtifact(artifacts, "vocab.txt", cancellationToken);
        using var vocabularyStream = new MemoryStream(vocabulary, writable: false);
        var tokenizer = CreateTokenizer(vocabularyStream);
        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var name in (string[])["input_ids", "attention_mask", "token_type_ids"])
            {
                if (!session.InputMetadata.TryGetValue(name, out var input) || input.ElementType != typeof(long)
                    || input.Dimensions.Length != 2 || input.Dimensions[1] >= 0)
                {
                    throw new TextEmbeddingUnavailableException("Local embedding model has incompatible input names, types, or dynamic sequence dimensions.");
                }
            }

            if (session.InputMetadata.Count != 3
                || !session.OutputMetadata.TryGetValue("last_hidden_state", out var output)
                || output.ElementType != typeof(float) || output.Dimensions.Length != 3
                || output.Dimensions[2] != Dimensions)
            {
                throw new TextEmbeddingUnavailableException("Local embedding model has incompatible output metadata.");
            }

            _tokenizer = tokenizer;
            _session = session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private byte[] ReadVerifiedArtifact(JsonElement artifacts, string name, CancellationToken cancellationToken)
    {
        var artifact = artifacts.EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var path = Path.Combine(_assetDirectory, name);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != artifact.GetProperty("bytes").GetInt64()
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new TextEmbeddingUnavailableException("A required bundled MiniLM asset is missing or has an unexpected size. Run eng/Stage-EmbeddingAssets.ps1 during source setup or reinstall the complete release.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, artifact.GetProperty("sha256").GetString(), StringComparison.Ordinal))
        {
            throw new TextEmbeddingUnavailableException("A bundled MiniLM asset failed its pinned SHA-256 verification. Re-stage the official assets or reinstall the release.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
        _tokenizer = null;
    }

    /// <summary>Owns encoded inputs and pre-truncation accounting within the adapter.</summary>
    internal sealed record EncodedInput(long[] Ids, long[] AttentionMask, long[] TokenTypeIds, int FullTokenCount, bool WasTruncated);
}
