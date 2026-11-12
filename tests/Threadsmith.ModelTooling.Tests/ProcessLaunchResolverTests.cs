namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Tools;
using Xunit;

/// <summary>Checks preparation-local launch lookup caching.</summary>
public static class ProcessLaunchResolverTests
{
    /// <summary>Fresh preparations observe filesystem changes without repeating lookups within a preparation.</summary>
    [Fact]
    public static void ResolutionCachesHitsAndMissesOnlyWithinOnePreparation()
    {
        // Arrange: isolated absolute PATH, without depending on machine executables.
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var present = Path.Combine(root, "present" + extension);
        var missing = Path.Combine(root, "missing" + extension);
        var environment = new Dictionary<string, string> { ["PATH"] = root, ["PATHEXT"] = ".exe" };
        try
        {
            File.WriteAllText(present, string.Empty);
            var resolver = new ProcessLaunchResolver(environment);

            // Act: capture both a hit and a miss, then change the filesystem.
            Assert.Equal(present, resolver.Resolve("present"));
            Assert.Throws<FileNotFoundException>(() => resolver.Resolve("missing"));
            File.Delete(present);
            File.WriteAllText(missing, string.Empty);

            // Assert: this preparation stays stable; fresh launch scopes observe changes.
            Assert.Equal(present, resolver.Resolve("present"));
            Assert.Throws<FileNotFoundException>(() => resolver.Resolve("missing"));
            Assert.Throws<FileNotFoundException>(() => ProcessLaunchResolver.Resolve("present", environment));
            Assert.Equal(missing, ProcessLaunchResolver.Resolve("missing", environment));
            Assert.Throws<InvalidOperationException>(() => resolver.Resolve("./missing"));
            Assert.Equal(missing, resolver.Resolve(missing));
            Assert.Throws<FileNotFoundException>(() => ProcessLaunchResolver.Resolve("missing", new Dictionary<string, string> { ["PATH"] = string.Empty }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
