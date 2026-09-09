namespace Threadsmith.Persistence;

/// <summary>Validates the narrowly owned repository memory database path before cross-repository retention writes.</summary>
internal static class RepositoryMemoryDatabasePath
{
    /// <summary>Checks the canonical default file and rejects linked paths in the repository-owned segment.</summary>
    internal static string Verify(string repositoryRoot, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var expected = Path.Combine(root, ".threadsmith", "threadsmith.db");
        var actual = Path.GetFullPath(databasePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expected, actual, comparison))
        {
            throw new InvalidOperationException("Memory retention target must be the registered repository's .threadsmith/threadsmith.db file.");
        }

        foreach (var path in new[] { root, Path.Combine(root, ".threadsmith"), actual })
        {
            if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Memory retention target must not redirect through a linked path.");
            }
        }

        return actual;
    }
}
