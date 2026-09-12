namespace Threadsmith.Embeddings.Local;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Threadsmith.Core;

/// <summary>Owns one lazy, serialized CPU MiniLM session using only verified application assets.</summary>
public sealed class LocalTextEmbeddingGenerator : ITextEmbeddingGenerator, IAsyncDisposable
{
    /// <summary>Gets the exact number of MiniLM vector components.</summary>
    internal const int Dimensions = MiniLmEmbedderEngine.Dimensions;

    /// <summary>Gets the complete MiniLM sequence limit.</summary>
    internal const int MaximumTokens = MiniLmEmbedderEngine.MaximumTokens;
    private const string SpaceId = "minilm-l12-v2:9bc18616990647530c139b95df1d1aa30cd115b7:mlnet-bert-default-wrapped:mean-mask-f32:l2-f32:256:v2";
    private readonly TimeSpan _disposalTimeout;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _assetDirectory;
    private readonly Action? _inferenceStarted;
    private MiniLmEmbedderEngine? _engine;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="LocalTextEmbeddingGenerator"/> class.</summary>
    public LocalTextEmbeddingGenerator(int disposalTimeoutMilliseconds = 10000)
        : this(Path.Combine(AppContext.BaseDirectory, "embeddings", "all-MiniLM-L12-v2"), disposalTimeoutMilliseconds: disposalTimeoutMilliseconds)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LocalTextEmbeddingGenerator"/> class using a test-owned asset root.</summary>
    internal LocalTextEmbeddingGenerator(string assetDirectory, Action? inferenceStarted = null, int disposalTimeoutMilliseconds = 10000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(disposalTimeoutMilliseconds);
        _disposalTimeout = TimeSpan.FromMilliseconds(disposalTimeoutMilliseconds);
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

        // An abandoned non-cooperative native initialization owns eventual disposal in its finally.
        // The managed gate/source remain valid for callers that raced disposal before admission.
    }

    private TextEmbeddingResult GenerateOnWorker(string text, CancellationTokenSource linked)
    {
        try
        {
            var cancellationToken = linked.Token;
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLoaded(cancellationToken);
            var engine = _engine ?? throw new TextEmbeddingUnavailableException("Local embedding engine did not initialize.");
            return engine.Embed(text, _inferenceStarted, cancellationToken);
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

        using var manifestStream = typeof(LocalTextEmbeddingGenerator).Assembly.GetManifestResourceStream(
            "Threadsmith.Embeddings.Local.minilm-assets.json")
            ?? throw new TextEmbeddingUnavailableException("The application embedding asset manifest is missing.");
        using var manifest = JsonDocument.Parse(manifestStream);
        var artifacts = manifest.RootElement.GetProperty("artifacts");
        var model = ReadVerifiedArtifact(artifacts, "model.onnx", cancellationToken);
        var vocabulary = ReadVerifiedArtifact(artifacts, "vocab.txt", cancellationToken);
        using var vocabularyStream = new MemoryStream(vocabulary, writable: false);
        cancellationToken.ThrowIfCancellationRequested();
        var engine = new MiniLmEmbedderEngine(model, vocabularyStream);
        SessionLoadDuration = engine.SessionLoadDuration;
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

    private void DisposeEngine()
    {
        _engine?.Dispose();
        _engine = null;
    }
}
