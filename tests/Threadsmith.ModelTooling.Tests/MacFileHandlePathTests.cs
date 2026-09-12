namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Tools;
using Xunit;

/// <summary>Checks the native descriptor path lookup on the macOS architectures exercised by CI.</summary>
public sealed class MacFileHandlePathTests
{
    /// <summary>The path comes from the opened descriptor even after a rename and pathname replacement.</summary>
    [Fact]
    public static async Task GetPath_FollowsHeldFileAfterRenameAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"threadsmith-handle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var original = Path.Combine(directory, "original.txt");
            var renamed = Path.Combine(directory, "renamed-π.txt");
            await File.WriteAllTextAsync(original, "held", TestContext.Current.CancellationToken);
            using var handle = File.OpenHandle(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var opened = MacFileHandlePath.GetPath(handle);
            Assert.NotNull(opened);
            Assert.EndsWith("/original.txt", opened, StringComparison.Ordinal);

            File.Move(original, renamed);
            await File.WriteAllTextAsync(original, "replacement", TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(Path.GetDirectoryName(opened)!, "renamed-π.txt"), MacFileHandlePath.GetPath(handle));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
