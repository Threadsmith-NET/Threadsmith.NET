namespace Threadsmith.Core;

/// <summary>Host-resolved file authority supplied to the scripting worker.</summary>
public sealed record ScriptFileScope
{
    /// <summary>Gets the repository used to interpret prohibitions.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>Gets the transient writable root.</summary>
    public required string ScratchpadPath { get; init; }

    /// <summary>Gets normalized readable roots, including any narrowed repository scope.</summary>
    public IReadOnlyList<string> ReadRoots { get; init; } = [];

    /// <summary>Gets inherited path prohibitions.</summary>
    public IReadOnlyList<string> ProhibitedPaths { get; init; } = [];

    /// <summary>Gets protected control files.</summary>
    public IReadOnlyList<string> ControlPaths { get; init; } = [];

    /// <summary>Gets protected control directories.</summary>
    public IReadOnlyList<string> ControlDirectories { get; init; } = [];

    /// <summary>Gets the maximum bytes written by one script invocation.</summary>
    public int MaximumWriteBytes { get; init; } = 1024 * 1024;
}

/// <summary>Shared path checks for ordinary reads and worker file operations; not an OS sandbox.</summary>
public static class ScopedFilePaths
{
    /// <summary>Validates a path against explicit read roots and inherited exclusions.</summary>
    public static string ValidateRead(string path, string basePath, string repository, IEnumerable<string> roots, IReadOnlyList<string> prohibited)
    {
        var normalized = Normalize(path, basePath);
        var readRoot = roots.FirstOrDefault(root => RepositoryPathPolicy.ContainsPath(root, normalized))
            ?? throw new UnauthorizedAccessException("File is outside the authorized read locations.");

        if (RepositoryPathPolicy.IsProhibited(Path.GetRelativePath(repository, normalized).Replace('\\', '/'), prohibited, repository))
        {
            throw new UnauthorizedAccessException("File matches a prohibited path.");
        }

        RejectLinks(normalized, RepositoryPathPolicy.ContainsPath(repository, normalized) ? repository : readRoot);
        return normalized;
    }

    /// <summary>Validates an output beneath the session scratchpad.</summary>
    public static string ValidateWrite(string path, ScriptFileScope scope)
    {
        var normalized = Normalize(path, scope.ScratchpadPath);
        if (!RepositoryPathPolicy.ContainsPath(scope.ScratchpadPath, normalized)
            || RepositoryPathPolicy.GetPathComparer(normalized).Equals(Path.TrimEndingDirectorySeparator(normalized), Path.TrimEndingDirectorySeparator(scope.ScratchpadPath)))
        {
            throw new UnauthorizedAccessException("Writes must target a file or subdirectory inside the scratchpad.");
        }

        if (Path.GetExtension(normalized).Equals(".py", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(normalized).Equals(".pyw", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Python scripting is prohibited. Use csharp_script.");
        }

        AuthorityControlPathGuard.Validate(normalized, scope.RepositoryPath, scope.ControlPaths, scope.ControlDirectories);
        RejectLinks(normalized, scope.RepositoryPath);
        return normalized;
    }

    /// <summary>Rejects links from the authority root through the target, including dangling links.</summary>
    public static void RejectLinks(string path, string root)
    {
        path = Path.GetFullPath(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!RepositoryPathPolicy.ContainsPath(root, path))
        {
            throw new UnauthorizedAccessException("File is outside its authority root.");
        }

        var comparer = RepositoryPathPolicy.GetPathComparer(root);
        for (var current = path; current is not null; current = comparer.Equals(Path.TrimEndingDirectorySeparator(current), root) ? null : Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("File access cannot traverse symbolic links or junctions.");
                }
            }
            catch (FileNotFoundException)
            {
                // Missing descendants are valid write targets.
            }
            catch (DirectoryNotFoundException)
            {
                // Missing descendants are valid write targets.
            }
        }
    }

    /// <summary>Detects reserved Windows device segments.</summary>
    public static bool ContainsReservedWindowsDeviceName(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(IsReservedWindowsDeviceName);
    }

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        var normalized = segment.TrimEnd(' ', '.');
        var extensionSeparator = normalized.IndexOf('.');
        var baseName = extensionSeparator >= 0 ? normalized[..extensionSeparator] : normalized;
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return baseName.Length == 4
            && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && baseName[3] is (>= '1' and <= '9') or '¹' or '²' or '³';
    }

    private static string Normalize(string path, string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Any(char.IsControl) || (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)))
        {
            throw new UnauthorizedAccessException("Use an unambiguous relative or fully qualified file path.");
        }

        var normalized = Path.GetFullPath(path, basePath);
        var relative = normalized[(Path.GetPathRoot(normalized)?.Length ?? 0)..];
        if (ContainsReservedWindowsDeviceName(relative)
            || (OperatingSystem.IsWindows() && relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.Contains(':') || segment.EndsWith('.') || segment.EndsWith(' '))))
        {
            throw new UnauthorizedAccessException("File path contains an ambiguous or reserved Windows name.");
        }

        return normalized;
    }
}
