namespace Threadsmith.DotNet;

using Threadsmith.Core;

/// <summary>Shared filesystem identity checks for repository-confined semantic inputs.</summary>
internal static class SemanticPathSafety
{
    /// <summary>Returns whether a path or any component through the repository root is a reparse point.</summary>
    public static bool HasReparseComponent(string repositoryPath, string path)
    {
        try
        {
            ScopedFilePaths.RejectLinks(path, repositoryPath);
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
