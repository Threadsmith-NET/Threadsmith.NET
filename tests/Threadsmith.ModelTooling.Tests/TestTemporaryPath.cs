namespace Threadsmith.ModelTooling.Tests;

/// <summary>Resolves platform temporary-directory aliases before fixtures exercise link-rejecting tools.</summary>
internal static class TestTemporaryPath
{
    internal static string PhysicalPath()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
        var resolved = Path.GetPathRoot(path)!;
        foreach (var segment in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(resolved, segment));
            resolved = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }
}
