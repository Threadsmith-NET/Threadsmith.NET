namespace Threadsmith.Validation;

/// <summary>Provides shared fail-closed reparse-point inspection for validation targets.</summary>
internal static class ValidationPathGuard
{
    /// <summary>Rejects targets whose repository-relative path traverses a reparse point.</summary>
    /// <param name="repositoryRoot">Canonical repository root.</param>
    /// <param name="targetPath">Canonical file path to inspect.</param>
    /// <param name="relativeTarget">Repository-relative target used in errors.</param>
    /// <param name="targetKind">Human-readable validation target kind.</param>
    public static void EnsureNoReparsePointTraversal(
        string repositoryRoot,
        string targetPath,
        string relativeTarget,
        string targetKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKind);

        var current = repositoryRoot;
        foreach (var segment in Path.GetRelativePath(repositoryRoot, targetPath)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"{targetKind} '{relativeTarget}' traverses a symbolic link or junction.");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"{targetKind} '{relativeTarget}' could not be safely inspected.",
                    exception);
            }
        }
    }
}
