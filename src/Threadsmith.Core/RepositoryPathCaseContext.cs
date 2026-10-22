namespace Threadsmith.Core;

using System.Collections.Concurrent;

/// <summary>Caches directory case probes for one path-policy operation, including nested filesystem boundaries.</summary>
public sealed class RepositoryPathCaseContext
{
    private readonly string _repositoryPath;
    private readonly Func<string, bool?> _probe;
    private readonly ConcurrentDictionary<string, bool> _directories = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="RepositoryPathCaseContext"/> class for one operation.</summary>
    public RepositoryPathCaseContext(string repositoryPath)
        : this(repositoryPath, RepositoryPathPolicy.ProbeDirectoryCase)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RepositoryPathCaseContext"/> class with a directory probe.</summary>
    internal RepositoryPathCaseContext(string repositoryPath, Func<string, bool?> probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(probe);
        _repositoryPath = Path.GetFullPath(repositoryPath);
        _probe = probe;
    }

    /// <summary>Resolves the filesystem governing the name at a particular relative-path position.</summary>
    internal bool IsCaseSensitive(string relativePath, int position)
    {
        var separator = relativePath.LastIndexOf('/', position);
        var parent = separator < 0 ? _repositoryPath : Path.GetFullPath(relativePath[..separator], _repositoryPath);
        return _directories.GetOrAdd(parent, Resolve);
    }

    private bool Resolve(string directory)
    {
        if (Directory.Exists(directory))
        {
            // An empty mounted volume cannot inherit its parent's case behavior. Without
            // evidence, fold case for prohibitions so unknown behavior cannot grant access.
            return _probe(directory) ?? false;
        }

        var parent = Path.GetDirectoryName(directory);
        return parent is not null && _directories.GetOrAdd(parent, Resolve);
    }
}
