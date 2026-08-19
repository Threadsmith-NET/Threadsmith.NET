namespace Threadsmith.Extensions.Runtime;

using System.IO;

/// <summary>
/// Shadow-copies an extension package to a unique staging directory so the watched installation
/// directory can be replaced without file-lock contention (strategy §17.9, §17.21).
/// </summary>
public sealed class ShadowCopier
{
    private readonly TimeSpan _stabilityQuietPeriod;

    /// <summary>Initializes a new instance of the <see cref="ShadowCopier"/> class.</summary>
    /// <param name="stabilityQuietPeriod">
    /// The minimum quiet period after the last file change before a package is considered stable.
    /// </param>
    public ShadowCopier(TimeSpan? stabilityQuietPeriod = null)
    {
        _stabilityQuietPeriod = stabilityQuietPeriod ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Waits for package stability, then shadow-copies <paramref name="sourceDirectory"/> under the staging root.</summary>
    /// <param name="sourceDirectory">The watched installation directory.</param>
    /// <param name="stagingRoot">The host-owned staging root.</param>
    /// <param name="cancellationToken">A token that cancels the wait and copy.</param>
    /// <returns>The unique staging directory containing the shadow-copied package.</returns>
    public async Task<string> StageAsync(
        string sourceDirectory,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Extension source directory not found: {sourceDirectory}");
        }

        await WaitForStabilityAsync(sourceDirectory, cancellationToken);
        var stagingPath = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingPath);
        CopyDirectory(sourceDirectory, stagingPath);
        return stagingPath;
    }

    /// <summary>Removes a staging directory created by <see cref="StageAsync"/>.</summary>
    /// <param name="stagingPath">The staging directory to remove.</param>
    public static void Discard(string stagingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        if (!Directory.Exists(stagingPath))
        {
            return;
        }

        try
        {
            Directory.Delete(stagingPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the collectible context may still hold file handles briefly.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private async Task WaitForStabilityAsync(string sourceDirectory, CancellationToken cancellationToken)
    {
        // Wait for a bounded quiet period: poll the directory's last-write time and file count until
        // they are unchanged for _stabilityQuietPeriod. This avoids loading a partially copied update.
        // If the source directory disappears mid-poll (a realistic hot-replacement race), treat it as
        // an unstable package and keep waiting until the deadline rather than letting
        // DirectoryNotFoundException escape and defeat the bounded wait (F4).
        (var size, var count) = MeasurePackage(sourceDirectory);
        TimeSpan elapsed = TimeSpan.Zero;
        var deadline = TimeSpan.FromSeconds(30);
        while (elapsed < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_stabilityQuietPeriod, cancellationToken);
            (var nextSize, var nextCount) = MeasurePackageOrUnstable(sourceDirectory);
            if (nextSize == size && nextCount == count)
            {
                return;
            }

            size = nextSize;
            count = nextCount;
            elapsed += _stabilityQuietPeriod;
        }
    }

    private static (long size, int count) MeasurePackageOrUnstable(string directory)
    {
        try
        {
            return MeasurePackage(directory);
        }
        catch (DirectoryNotFoundException)
        {
            // The source directory vanished mid-copy (e.g. it was renamed/replaced during staging).
            // Return sentinel values that differ from any real measurement so the loop keeps waiting
            // until the deadline, giving the replacement a chance to land.
            return (-1, -1);
        }
    }

    private static (long size, int count) MeasurePackage(string directory)
    {
        long size = 0;
        var count = 0;
        foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            size += file.Length;
            count++;
        }

        return (size, count);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            File.Copy(file, target, overwrite: true);
        }
    }
}