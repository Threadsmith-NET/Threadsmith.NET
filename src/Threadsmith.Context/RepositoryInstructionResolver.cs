namespace Threadsmith.Context;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Bounds hierarchical repository instruction discovery and decoding.</summary>
public sealed record RepositoryInstructionLimits(
    int MaximumDepth = 32,
    int MaximumFiles = 32,
    int MaximumFileBytes = 64 * 1024,
    int MaximumTotalBytes = 256 * 1024);

/// <summary>Resolves parent-to-child AGENTS.md files and prompt appends without following links.</summary>
public sealed class RepositoryInstructionResolver : IRepositoryInstructionResolver
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Dictionary<string, RepositoryInstructionBundle> _cache;
    private readonly HashSet<string> _invalidatedRepositories;
    private readonly Lock _gate = new();
    private readonly RepositoryInstructionLimits _limits;
    private readonly IOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="RepositoryInstructionResolver"/> class.</summary>
    public RepositoryInstructionResolver(
        IOutputSanitizer sanitizer,
        RepositoryInstructionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        _sanitizer = sanitizer;
        _limits = limits ?? new RepositoryInstructionLimits();
        if (_limits.MaximumDepth <= 0
            || _limits.MaximumFiles <= 0
            || _limits.MaximumFileBytes <= 0
            || _limits.MaximumTotalBytes < _limits.MaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }

        _cache = new Dictionary<string, RepositoryInstructionBundle>(PathComparer);
        _invalidatedRepositories = new HashSet<string>(PathComparer);
    }

    /// <inheritdoc />
    public async Task<RepositoryInstructionBundle> ResolveAsync(
        string repositoryPath,
        string? workingScope,
        IReadOnlyList<PromptAppendSegment> promptAppends,
        IReadOnlyList<string> prohibitedPaths,
        long trustGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(promptAppends);
        ArgumentNullException.ThrowIfNull(prohibitedPaths);
        ArgumentOutOfRangeException.ThrowIfNegative(trustGeneration);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        EnsureNotReparse(root, "repository root");
        var scope = ResolveScope(root, workingScope);
        var relativeScope = Path.GetRelativePath(root, scope).Replace('\\', '/');
        if (relativeScope == ".")
        {
            relativeScope = string.Empty;
        }

        var directories = BuildDirectoryChain(root, scope);
        var sources = new List<RepositoryInstructionSource>();
        long totalBytes = 0;
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparse(directory, "instruction scope");
            var path = Path.Combine(directory, "AGENTS.md");
            if (!File.Exists(path))
            {
                continue;
            }

            EnsureNotReparse(path, "AGENTS.md");
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (RepositoryPathPolicy.IsProhibited(relativePath, prohibitedPaths))
            {
                throw new UnauthorizedAccessException(
                    $"Repository instruction path '{relativePath}' is prohibited.");
            }

            FileInfo before = new(path);
            if (before.Length > _limits.MaximumFileBytes
                || totalBytes + before.Length > _limits.MaximumTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Repository instruction path '{relativePath}' exceeds configured bounds.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            FileInfo after = new(path);
            if (bytes.LongLength != before.Length
                || before.LastWriteTimeUtc != after.LastWriteTimeUtc
                || after.Length != before.Length)
            {
                throw new IOException(
                    $"Repository instruction path '{relativePath}' changed while it was read.");
            }

            totalBytes += bytes.LongLength;
            string content;
            try
            {
                content = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"Repository instruction path '{relativePath}' is not strict UTF-8.",
                    exception);
            }

            var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            sources.Add(new RepositoryInstructionSource(
                RepositoryInstructionSourceKind.Agents,
                $"agents:{relativePath}",
                relativePath,
                $"sha256:{contentHash}",
                _sanitizer.Sanitize(NormalizeNewlines(content)),
                sources.Count));
            if (sources.Count > _limits.MaximumFiles)
            {
                throw new InvalidOperationException("Repository instruction file count exceeds configured bounds.");
            }
        }

        foreach (var append in promptAppends.OrderBy(item => item.Position))
        {
            sources.Add(new RepositoryInstructionSource(
                RepositoryInstructionSourceKind.PromptAppend,
                append.Id,
                append.SourcePath,
                append.Version,
                NormalizeNewlines(append.Content),
                sources.Count));
        }

        var identityParts = new[]
        {
            "repository-instructions:v1",
            root,
            relativeScope,
            trustGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }.Concat(sources.Select(source =>
            $"{source.Kind}|{source.Id}|{source.RelativePath}|{source.Version}|{source.Position}"));
        var identity = string.Join('\n', identityParts);
        var digest = "sha256:"
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var bundle = new RepositoryInstructionBundle
        {
            RepositoryRoot = root,
            WorkingScope = relativeScope,
            Sources = sources,
            Digest = digest,
        };
        var cacheKey = string.Concat(root, '\0', relativeScope, '\0', trustGeneration);
        lock (_gate)
        {
            _invalidatedRepositories.Remove(root);
            if (_cache.TryGetValue(cacheKey, out var cached)
                && string.Equals(cached.Digest, bundle.Digest, StringComparison.Ordinal))
            {
                return cached;
            }

            _cache[cacheKey] = bundle;
        }

        return bundle;
    }

    /// <inheritdoc />
    public void InvalidateRepository(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        lock (_gate)
        {
            _invalidatedRepositories.Add(root);
            foreach (var key in _cache.Keys
                .Where(key => key.StartsWith(root + '\0', PathComparison))
                .ToArray())
            {
                _cache.Remove(key);
            }
        }
    }

    private List<string> BuildDirectoryChain(string root, string scope)
    {
        var chain = new List<string> { root };
        var relative = Path.GetRelativePath(root, scope);
        if (relative == ".")
        {
            return chain;
        }

        var current = root;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (chain.Count >= _limits.MaximumDepth)
            {
                throw new InvalidOperationException("Repository instruction depth exceeds configured bounds.");
            }

            current = Path.Combine(current, segment);
            chain.Add(current);
        }

        return chain;
    }

    private static void EnsureNotReparse(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"The {description} '{path}' is a symbolic link or junction.");
        }
    }

    private static string NormalizeNewlines(string content)
    {
        return content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string ResolveScope(string root, string? workingScope)
    {
        var candidate = string.IsNullOrWhiteSpace(workingScope)
            ? root
            : Path.GetFullPath(workingScope, root);
        if (File.Exists(candidate))
        {
            candidate = Path.GetDirectoryName(candidate)
                ?? throw new InvalidOperationException("The repository instruction scope has no directory.");
        }

        var comparison = PathComparison;
        if (!candidate.Equals(root, comparison)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new UnauthorizedAccessException("The repository instruction scope escapes the repository root.");
        }

        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException(
                $"Repository instruction scope '{candidate}' does not exist.");
        }

        return Path.TrimEndingDirectorySeparator(candidate);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
