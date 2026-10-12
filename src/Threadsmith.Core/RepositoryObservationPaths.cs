namespace Threadsmith.Core;

/// <summary>Established generated and host-output exclusions shared by observation consumers.</summary>
public static class RepositoryObservationPaths
{
    /// <summary>Host bookkeeping and Git metadata cannot become primary architectural evidence.</summary>
    public static bool IsHostStatePath(string relativePath)
    {
        return relativePath.Replace('\\', '/').Split('/').Any(segment =>
            segment.Equals(".threadsmith", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Identifies directory churn that is not repository source by default.</summary>
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
