namespace Threadsmith.Core;

/// <summary>Protects exact application control files against generic mutation, including their ancestors.</summary>
public static class AuthorityControlPathGuard
{
    /// <summary>Rejects changes to control files or directories containing them.</summary>
    public static void Validate(string candidate, string repositoryPath, IEnumerable<string>? controlPaths = null)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate, repositoryPath));
        if (OperatingSystem.IsWindows())
        {
            var pathRoot = Path.GetPathRoot(normalized) ?? throw new UnauthorizedAccessException("The mutation path has no filesystem root.");
            var relative = normalized[pathRoot.Length..];
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':')))
            {
                throw new UnauthorizedAccessException("AuthoritySettingReviewRequired: ambiguous Windows path aliases cannot be mutated.");
            }
        }

        var secretRoot = Path.Combine(repositoryPath, ".threadsmith", "secrets");
        var comparer = RepositoryPathPolicy.GetPathComparer(normalized);
        var settingsDirectory = Path.GetDirectoryName(normalized);
        if (settingsDirectory is not null && comparer.Equals(Path.GetFileName(settingsDirectory), ".threadsmith")
            && new[] { "config.json", "session.json", "extensions.json", "providers.json" }.Contains(Path.GetFileName(normalized), comparer))
        {
            throw new UnauthorizedAccessException("AuthoritySettingReviewRequired: generic file mutations cannot change nested repository authority-control files.");
        }

        if (RepositoryPathPolicy.ContainsPath(secretRoot, normalized, comparer))
        {
            throw new UnauthorizedAccessException("AuthoritySettingReviewRequired: generic file mutations cannot change secret stores.");
        }

        var defaults = new[] { "config.json", "session.json", "extensions.json", "providers.json" }
            .Select(name => Path.Combine(repositoryPath, ".threadsmith", name));
        foreach (var path in defaults.Concat(controlPaths ?? []))
        {
            var control = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (RepositoryPathPolicy.ContainsPath(normalized, control, comparer))
            {
                throw new UnauthorizedAccessException("AuthoritySettingReviewRequired: generic file mutations cannot change authority-control files or containing directories. Edit the configuration manually through the supported settings path.");
            }
        }
    }
}
