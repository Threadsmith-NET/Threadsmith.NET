namespace Threadsmith.Workspaces;

/// <summary>Shared repository path safety checks for plan approval policy persistence.</summary>
internal static class PlanApprovalPathSafety
{
    /// <summary>Ensures a plan-policy storage path stays under the repository and does not traverse existing reparse points.</summary>
    /// <param name="repositoryRoot">Normalized repository root.</param>
    /// <param name="candidatePath">Path to inspect.</param>
    public static void EnsureRepositoryConfinedWithoutReparsePoints(
        string repositoryRoot,
        string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var fullPath = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(normalizedRoot, fullPath);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException(
                "Repository plan-policy persistence must stay inside the repository.");
        }

        var currentPath = normalizedRoot;
        if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "Repository plan-policy persistence cannot traverse a symbolic link or junction.");
        }

        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                break;
            }

            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "Repository plan-policy persistence cannot traverse a symbolic link or junction.");
            }
        }
    }
}
