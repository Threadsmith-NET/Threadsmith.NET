namespace Threadsmith.DotNet;

/// <summary>Shared path policy for files that must not drive semantic refresh work.</summary>
internal static class SemanticRefreshPathPolicy
{
    /// <summary>Returns whether the path is generated, transient, or otherwise irrelevant to semantic refresh.</summary>
    public static bool IsIgnoredPath(string repositoryPath, string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetRelativePath(repositoryPath, path).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return true;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(IsIgnoredDirectorySegment))
        {
            return true;
        }

        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return name.StartsWith('~')
            || name.EndsWith('~')
            || extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".swp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".swo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns whether a directory segment represents generated or tool-local churn.</summary>
    public static bool IsIgnoredDirectorySegment(string segment)
    {
        return segment.Equals(".codegraph", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".idea", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".inbox", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".threadsmith", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vscode", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase);
    }
}
