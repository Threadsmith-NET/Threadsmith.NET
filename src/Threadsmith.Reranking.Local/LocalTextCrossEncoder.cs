namespace Threadsmith.Reranking.Local;

using System.Security.Cryptography;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Owns one lazy, serialized CPU cross-encoder session using only verified application assets.</summary>
public sealed class LocalTextCrossEncoder : ITextCrossEncoder, IAsyncDisposable
{
    private const int MaximumTokens = LocalTextCrossEncoderEngine.MaximumTokens;
    private const int MaximumBatchSize = LocalTextCrossEncoderEngine.MaximumBatchSize;
    private const string ModelId = "cross-encoder-ms-marco-minilm-l6-v2:233902d25c440f23af6f7d6e94d2946bac0bee0a:bert-lower-pair:raw-logit:256:64:v1";
    private readonly TimeSpan _disposalTimeout;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _assetDirectory;
    private readonly int _cpuThreads;
    private readonly Action? _inferenceStarted;
    private LocalTextCrossEncoderEngine? _engine;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="LocalTextCrossEncoder"/> class with a bounded CPU worker count.</summary>
    /// <param name="cpuThreads">Requested positive CPU worker thread count.</param>
    /// <param name="disposalTimeoutMilliseconds">Maximum shutdown wait for active inference.</param>
    public LocalTextCrossEncoder(int cpuThreads = 8, int disposalTimeoutMilliseconds = 10000)
        : this(Path.Combine(AppContext.BaseDirectory, "crossencoders", "ms-marco-MiniLM-L6-v2"), cpuThreads, disposalTimeoutMilliseconds: disposalTimeoutMilliseconds)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LocalTextCrossEncoder"/> class from a test-owned asset root.</summary>
    internal LocalTextCrossEncoder(string assetDirectory, int cpuThreads = 8, Action? inferenceStarted = null, int disposalTimeoutMilliseconds = 10000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(cpuThreads, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(disposalTimeoutMilliseconds);
        _disposalTimeout = TimeSpan.FromMilliseconds(disposalTimeoutMilliseconds);
        _assetDirectory = assetDirectory;
        _cpuThreads = cpuThreads;
        _inferenceStarted = inferenceStarted;
    }

    /// <inheritdoc />
    public TextCrossEncoderModelDescriptor Model { get; } = new(ModelId, MaximumTokens, MaximumBatchSize);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (documents.Count == 0)
        {
            return [];
        }

        if (documents.Count > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(documents), $"A local cross-encoder request accepts at most {MaximumBatchSize} documents.");
        }

        for (var index = 0; index < documents.Count; index++)
        {
            ArgumentNullException.ThrowIfNull(documents[index]);
        }

        var documentCopy = documents.ToArray();
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

        var worker = Task.Run(() => ScoreOnWorker(query, documentCopy, linked), CancellationToken.None);
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
        if (await _inferenceGate.WaitAsync(_disposalTimeout).ConfigureAwait(false))
        {
            try
            {
                DisposeEngine();
            }
            finally
            {
                _inferenceGate.Release();
            }
        }
    }

    private IReadOnlyList<TextCrossEncoderScore> ScoreOnWorker(
        string query,
        IReadOnlyList<string> documents,
        CancellationTokenSource linked)
    {
        try
        {
            var cancellationToken = linked.Token;
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLoaded(cancellationToken);
            var engine = _engine ?? throw new TextCrossEncoderUnavailableException("Local cross-encoder engine did not initialize.");
            return engine.Score(query, documents, _inferenceStarted, cancellationToken);
        }
        catch (Exception exception) when (linked.IsCancellationRequested && exception is not OperationCanceledException)
        {
            throw new OperationCanceledException("Local cross-encoder inference was cancelled.", exception, linked.Token);
        }
        catch (TextCrossEncoderUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TextCrossEncoderUnavailableException(
                "Local cross-encoder reranking is unavailable. Verify the bundled assets and matching native runtime; hybrid retrieval remains available.",
                exception);
        }
        finally
        {
            linked.Dispose();
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeEngine();
            }

            _inferenceGate.Release();
        }
    }

    private void EnsureLoaded(CancellationToken cancellationToken)
    {
        if (_engine is not null)
        {
            return;
        }

        using var manifestStream = typeof(LocalTextCrossEncoder).Assembly.GetManifestResourceStream(
            "Threadsmith.Reranking.Local.crossencoder-assets.json")
            ?? throw new TextCrossEncoderUnavailableException("The application cross-encoder asset manifest is missing.");
        using var manifest = JsonDocument.Parse(manifestStream);
        var artifacts = manifest.RootElement.GetProperty("artifacts");
        var model = ReadVerifiedArtifact(artifacts, "model.onnx", cancellationToken);
        var vocabulary = ReadVerifiedArtifact(artifacts, "vocab.txt", cancellationToken);
        using var vocabularyStream = new MemoryStream(vocabulary, writable: false);
        cancellationToken.ThrowIfCancellationRequested();
        var engine = new LocalTextCrossEncoderEngine(model, vocabularyStream, _cpuThreads);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _engine = engine;
        }
        catch
        {
            engine.Dispose();
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
            throw new TextCrossEncoderUnavailableException("A required bundled cross-encoder asset is missing or has an unexpected size. Reinstall the complete release.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, artifact.GetProperty("sha256").GetString(), StringComparison.Ordinal))
        {
            throw new TextCrossEncoderUnavailableException("A bundled cross-encoder asset failed pinned SHA-256 verification. Reinstall the complete release.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    private void DisposeEngine()
    {
        _engine?.Dispose();
        _engine = null;
    }
}
