namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Applies the existing repository read grants to host-owned review capture and report publication.</summary>
public static class ReviewPathAccess
{
    /// <summary>Revalidates frozen source grants without traversing unrelated local paths for a remote snapshot.</summary>
    public static void ValidateTarget(FocusedReviewTarget target, ToolInvocationContext context)
    {
        foreach (var file in target.Files)
        {
            _ = target.Mode == "remoteBranch"
                ? ToolPathRules.NormalizeAndValidate(file.Path, context, inspectFileSystem: false)
                : Resolve(file.Path, context);
        }
    }

    /// <summary>Resolves a path under current grants, rejecting links including a linked repository root.</summary>
    public static string Resolve(
        string path,
        ToolInvocationContext context)
    {
        var resolved = ToolPathRules.NormalizeAndValidate(path, context);
        for (var current = new DirectoryInfo(Path.GetFullPath(context.RepositoryPath)); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Review paths cannot traverse a linked repository root.");
            }
        }

        return resolved;
    }
}
