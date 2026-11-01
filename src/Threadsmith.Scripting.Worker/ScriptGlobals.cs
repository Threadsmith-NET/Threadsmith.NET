namespace Threadsmith.Scripting.Worker;

using System.Text;
using Threadsmith.Core;

/// <summary>Host-provided globals available to generated C# scripts.</summary>
public sealed class ScriptGlobals
{
    /// <summary>Initializes a new instance of the <see cref="ScriptGlobals"/> class.</summary>
    public ScriptGlobals(ScriptFileScope scope, CancellationToken cancellationToken)
    {
        Scratchpad = new ScratchpadFiles(scope, cancellationToken);
    }

    /// <summary>Gets file access to authorized inputs and transient outputs.</summary>
    public ScratchpadFiles Scratchpad { get; }
}

/// <summary>Streams authorized input and writes only beneath the active scratchpad.</summary>
public sealed class ScratchpadFiles
{
    private readonly ScriptFileScope _scope;
    private readonly CancellationToken _cancellationToken;
    private long _writtenBytes;

    /// <summary>Initializes a new instance of the <see cref="ScratchpadFiles"/> class.</summary>
    internal ScratchpadFiles(ScriptFileScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Lazily reads lines without loading the whole input.</summary>
    public IEnumerable<string> ReadLines(string path)
    {
        using var reader = new StreamReader(ReadPath(path));
        while (reader.ReadLine() is { } line)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    /// <summary>Lazily reads independent byte chunks of at most one MiB.</summary>
    public IEnumerable<byte[]> ReadChunks(string path, int chunkSize = 65536)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, 1024 * 1024);
        using var stream = File.OpenRead(ReadPath(path));
        var buffer = new byte[chunkSize];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            yield return buffer.AsSpan(0, count).ToArray();
        }
    }

    /// <summary>Creates or explicitly replaces UTF-8 output.</summary>
    public Task WriteAllTextAsync(string path, string text, bool overwrite = false, CancellationToken cancellationToken = default)
        => WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(text), overwrite, cancellationToken);

    /// <summary>Creates or explicitly replaces binary output atomically.</summary>
    public async Task WriteAllBytesAsync(string path, byte[] bytes, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
        var destination = WritePath(path);
        ReserveWrite(bytes.Length);
        await AtomicFileWriter.WriteAsync(destination, bytes, overwrite, () => WritePath(path), linked.Token);
    }

    /// <summary>Appends UTF-8 text for incremental result generation.</summary>
    public Task AppendTextAsync(string path, string text, CancellationToken cancellationToken = default)
        => AppendBytesAsync(path, Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>Appends binary output; cancellation may leave a partial appended chunk.</summary>
    public async Task AppendBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
        var destination = WritePath(path);
        ReserveWrite(bytes.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new IOException("Missing output parent."));
        destination = WritePath(path);
        await using var stream = new FileStream(destination, FileMode.Append, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, linked.Token);
    }

    /// <summary>Creates a subdirectory beneath the scratchpad.</summary>
    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(WritePath(path));
    }

    private string ReadPath(string path)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var normalized = Path.GetFullPath(path, _scope.ScratchpadPath);
        if (RepositoryPathPolicy.ContainsPath(_scope.ScratchpadPath, normalized))
        {
            return ScopedFilePaths.ValidateRead(path, _scope.ScratchpadPath, _scope.RepositoryPath, [_scope.ScratchpadPath], []);
        }

        return ScopedFilePaths.ValidateRead(path, _scope.ScratchpadPath, _scope.RepositoryPath, _scope.ReadRoots, _scope.ProhibitedPaths);
    }

    private string WritePath(string path)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return ScopedFilePaths.ValidateWrite(path, _scope);
    }

    private void ReserveWrite(int bytes)
    {
        if (Interlocked.Add(ref _writtenBytes, bytes) > _scope.MaximumWriteBytes)
        {
            throw new IOException("Script output files exceed the configured write byte limit.");
        }
    }
}
