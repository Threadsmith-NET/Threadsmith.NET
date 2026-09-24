namespace Threadsmith.Tools.PullRequests;

using Threadsmith.Tools;

/// <summary>Applies the same repository path authority to acquisition and captured-evidence reads.</summary>
internal static class PrEvidencePathScope
{
    /// <summary>Checks both current and previous paths of a changed file.</summary>
    internal static bool IsAllowed(PullRequestFile file, ToolInvocationContext context) =>
        IsAllowed(file.Path, context)
        && (file.PreviousPath is null || IsAllowed(file.PreviousPath, context));

    /// <summary>Checks whether the caller's roots or prohibited paths narrow the repository.</summary>
    internal static bool IsRestricted(ToolInvocationContext context)
    {
        if (context.ProhibitedPaths.Count > 0)
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        return !context.ApprovedRoots.Any(root =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot))
                .Equals(repositoryRoot, comparison));
    }

    private static bool IsAllowed(string path, ToolInvocationContext context)
    {
        try
        {
            _ = ToolPathRules.NormalizeAndValidate(path, context, inspectFileSystem: false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
