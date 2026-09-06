namespace Threadsmith.Context;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Size limits for untrusted project prompt-append assets.</summary>
public sealed record PromptAppendLimits(
    int MaximumFileBytes = 32 * 1024,
    int MaximumTotalBytes = 64 * 1024);

/// <summary>Safely loads repository prompt append files at turn boundaries.</summary>
public sealed class PromptAppendLoader : IPromptAppendLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Dictionary<string, CachedPromptAppend> _cache;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _invalidatedPaths;
    private readonly HashSet<string> _invalidatedRepositories;
    private readonly PromptAppendLimits _limits;
    private readonly IOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="PromptAppendLoader"/> class.</summary>
    public PromptAppendLoader(IOutputSanitizer sanitizer, PromptAppendLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        _sanitizer = sanitizer;
        _limits = limits ?? new PromptAppendLimits();
        if (_limits.MaximumFileBytes <= 0
            || _limits.MaximumTotalBytes <= 0
            || _limits.MaximumFileBytes > _limits.MaximumTotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }

        _cache = new Dictionary<string, CachedPromptAppend>(PathComparer);
        _invalidatedPaths = new HashSet<string>(PathComparer);
        _invalidatedRepositories = new HashSet<string>(PathComparer);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptAppendSegment>> LoadAsync(
        PromptAppendLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        var repositoryRoot = GetCanonicalRepositoryRoot(request.RepositoryPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        lock (_gate)
        {
            if (_invalidatedRepositories.Remove(repositoryRoot))
            {
                foreach (var cachedPath in _cache
                    .Where(item => PathComparer.Equals(item.Value.RepositoryRoot, repositoryRoot))
                    .Select(item => item.Key)
                    .ToArray())
                {
                    _cache.Remove(cachedPath);
                    _invalidatedPaths.Remove(cachedPath);
                }
            }
        }

        var segments = new List<PromptAppendSegment>();
        var configuredCacheKeys = new HashSet<string>(PathComparer);
        long totalBytes = 0;
        for (var position = 0; position < request.ConfiguredPaths.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuredPath = request.ConfiguredPaths[position];
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            var path = Path.GetFullPath(configuredPath, repositoryRoot);
            if (!path.Equals(repositoryRoot, comparison)
                && !path.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, comparison))
            {
                throw new UnauthorizedAccessException(
                    $"Prompt append path '{configuredPath}' escapes the repository root.");
            }

            var relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            var cacheKey = GetCacheKey(repositoryRoot, path);
            configuredCacheKeys.Add(cacheKey);
            if (RepositoryPathPolicy.IsProhibited(relativePath, request.ProhibitedPaths))
            {
                throw new UnauthorizedAccessException(
                    $"Prompt append path '{configuredPath}' is prohibited.");
            }

            var current = repositoryRoot;
            foreach (var segment in Path.GetRelativePath(repositoryRoot, path).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    break;
                }

                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException(
                        $"Prompt append path '{configuredPath}' traverses a symbolic link or junction.");
                }
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Configured prompt append file does not exist.", path);
            }

            CachedPromptAppend? cached;
            lock (_gate)
            {
                if (_invalidatedPaths.Remove(cacheKey))
                {
                    _cache.Remove(cacheKey);
                }

                _cache.TryGetValue(cacheKey, out cached);
            }

            FileInfo before = new(path);
            if (before.Length > _limits.MaximumFileBytes
                || totalBytes + before.Length > _limits.MaximumTotalBytes)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            FileInfo after = new(path);
            if (bytes.LongLength != before.Length
                || before.LastWriteTimeUtc != after.LastWriteTimeUtc
                || after.Length != before.Length)
            {
                throw new IOException(
                    $"Prompt append path '{configuredPath}' changed while it was read.");
            }

            var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (cached is null
                || !string.Equals(cached.ContentHash, contentHash, StringComparison.Ordinal))
            {
                string content;
                try
                {
                    content = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        $"Prompt append path '{configuredPath}' is not strict UTF-8.",
                        exception);
                }

                cached = new CachedPromptAppend(
                    repositoryRoot,
                    contentHash,
                    bytes.Length,
                    _sanitizer.Sanitize(content));
                lock (_gate)
                {
                    _cache[cacheKey] = cached;
                }
            }

            totalBytes += cached.ByteLength;
            var idHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)));
            segments.Add(new PromptAppendSegment(
                $"project-append:{idHash[..16]}",
                $"sha256:{cached.ContentHash}",
                cached.ContentHash,
                relativePath,
                position,
                cached.Content));
        }

        lock (_gate)
        {
            foreach (var obsoletePath in _cache
                .Where(item => PathComparer.Equals(item.Value.RepositoryRoot, repositoryRoot)
                    && !configuredCacheKeys.Contains(item.Key))
                .Select(item => item.Key)
                .ToArray())
            {
                _cache.Remove(obsoletePath);
                _invalidatedPaths.Remove(obsoletePath);
            }
        }

        return segments;
    }

    /// <inheritdoc />
    public void QueueInvalidation(string repositoryPath, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var repositoryRoot = GetCanonicalRepositoryRoot(repositoryPath);
        var fullPath = Path.GetFullPath(path, repositoryRoot);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.Equals(repositoryRoot, comparison)
            && !fullPath.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new UnauthorizedAccessException(
                $"Prompt append invalidation path '{path}' escapes the repository root.");
        }

        lock (_gate)
        {
            _invalidatedPaths.Add(GetCacheKey(repositoryRoot, fullPath));
        }
    }

    /// <inheritdoc />
    public void QueueRepositoryInvalidation(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        lock (_gate)
        {
            _invalidatedRepositories.Add(GetCanonicalRepositoryRoot(repositoryPath));
        }
    }

    private static string GetCanonicalRepositoryRoot(string repositoryPath)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
    }

    private static string GetCacheKey(string repositoryRoot, string path)
    {
        return string.Concat(repositoryRoot, '\0', path);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record CachedPromptAppend(
        string RepositoryRoot,
        string ContentHash,
        long ByteLength,
        string Content);
}
