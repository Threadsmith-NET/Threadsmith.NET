namespace Threadsmith.Tools;

/// <summary>Applies the existing repository read grants to host-owned review capture and report publication.</summary>
public static class ReviewPathAccess
{
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
