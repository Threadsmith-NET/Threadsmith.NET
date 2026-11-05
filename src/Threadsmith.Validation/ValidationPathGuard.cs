namespace Threadsmith.Validation;

using Threadsmith.Core;

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

        try
        {
            ScopedFilePaths.RejectLinks(targetPath, repositoryRoot, allowMissing: false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                $"{targetKind} '{relativeTarget}' could not be safely inspected for symbolic link or junction traversal.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"{targetKind} '{relativeTarget}' could not be safely inspected.", exception);
        }
    }
}
