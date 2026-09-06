namespace Threadsmith.DotNet;

/// <summary>Shared filesystem identity checks for repository-confined semantic inputs.</summary>
internal static class SemanticPathSafety
{
    /// <summary>Returns whether a path or any component through the repository root is a reparse point.</summary>
    public static bool HasReparseComponent(string repositoryPath, string path)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        try
        {
            var current = Path.GetFullPath(path);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if (comparer.Equals(Path.TrimEndingDirectorySeparator(current), root))
                {
                    return false;
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return true;
        }

        return true;
    }
}
