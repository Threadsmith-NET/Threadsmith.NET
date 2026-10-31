namespace Threadsmith.Tools.PullRequests;

using Threadsmith.Tools;

/// <summary>Applies the same repository path authority to acquisition and captured-evidence reads.</summary>
internal static class PrEvidencePathScope
{
    /// <summary>Checks both current and previous paths of a changed file.</summary>
    internal static bool IsAllowed(PullRequestFile file, ToolInvocationContext context)
    {
        return IsAllowed(file.Path, context)
        && (file.PreviousPath is null || IsAllowed(file.PreviousPath, context));
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
