namespace Threadsmith.Execution;

/// <summary>Resolves the deepest existing repository directory shared by affected paths.</summary>
internal static class RepositoryWorkingScope
{
    /// <summary>Resolves an authoritative scope for hierarchical repository instructions.</summary>
    internal static string Resolve(
        string repositoryPath,
        IEnumerable<string>? affectedPaths = null,
        string? fallbackPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var directories = affectedPaths?
            .Select(path => Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty)
            .ToArray() ?? [];
        var candidate = directories.Length == 0
            ? ResolveFallback(root, fallbackPath)
            : ResolveCommonDirectory(root, directories);
        while (!Directory.Exists(candidate) && !PathsEqual(candidate, root))
        {
            candidate = Path.GetDirectoryName(candidate)
                ?? throw new InvalidOperationException("The repository working scope has no parent directory.");
        }

        EnsureContained(root, candidate);
        return candidate;
    }

    private static string ResolveCommonDirectory(string root, IReadOnlyList<string> directories)
    {
        var common = Path.GetFullPath(directories[0], root);
        EnsureContained(root, common);
        foreach (var directory in directories.Skip(1))
        {
            var candidate = Path.GetFullPath(directory, root);
            EnsureContained(root, candidate);
            while (!IsContained(common, candidate))
            {
                common = Path.GetDirectoryName(common)
                    ?? throw new InvalidOperationException("Affected files do not share a repository scope.");
                EnsureContained(root, common);
            }
        }

        return common;
    }

    private static string ResolveFallback(string root, string? fallbackPath)
    {
        if (string.IsNullOrWhiteSpace(fallbackPath))
        {
            return root;
        }

        var candidate = Path.GetFullPath(fallbackPath);
        return Directory.Exists(candidate) && IsContained(root, candidate)
            ? candidate
            : root;
    }

    private static void EnsureContained(string root, string candidate)
    {
        if (!IsContained(root, candidate))
        {
            throw new UnauthorizedAccessException("The repository working scope escapes the repository root.");
        }
    }

    private static bool IsContained(string root, string candidate)
    {
        return PathsEqual(root, candidate)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool PathsEqual(string first, string second)
    {
        return first.Equals(second, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
