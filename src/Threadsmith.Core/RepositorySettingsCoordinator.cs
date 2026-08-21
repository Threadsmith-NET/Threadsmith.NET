namespace Threadsmith.Core;

using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>Coordinates atomic read-modify-replace operations for repository settings files.</summary>
public static class RepositorySettingsCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);

    /// <summary>Gets the shared permissive JSON syntax accepted by repository configuration.</summary>
    public static JsonDocumentOptions DocumentOptions => new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Runs one complete settings-file update while excluding every other in-process writer.</summary>
    public static async Task ExecuteWriteAsync(
        string configurationPath,
        Func<CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentNullException.ThrowIfNull(writeAsync);
        var normalizedPath = Path.GetFullPath(configurationPath);
        var gate = Gates.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureUnlinkedRepositorySettingsPath(normalizedPath);
            await writeAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Rejects an existing repository root, settings directory, or settings file that is a link or reparse point.</summary>
    public static void EnsureUnlinkedRepositorySettingsPath(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var normalizedPath = Path.GetFullPath(configurationPath);
        var settingsDirectory = Path.GetDirectoryName(normalizedPath)
            ?? throw new InvalidOperationException("Repository configuration path has no parent directory.");
        var repositoryRoot = Path.GetDirectoryName(settingsDirectory)
            ?? throw new InvalidOperationException("Repository configuration path must be under a repository directory.");

        RejectReparsePointIfPresent(repositoryRoot);
        RejectReparsePointIfPresent(settingsDirectory);
        RejectReparsePointIfPresent(normalizedPath);
    }

    private static void RejectReparsePointIfPresent(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                "Repository settings paths cannot contain symbolic links, junctions, or reparse points.");
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
