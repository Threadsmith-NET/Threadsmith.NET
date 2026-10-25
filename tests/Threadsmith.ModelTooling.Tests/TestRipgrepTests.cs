namespace Threadsmith.ModelTooling.Tests;

using Xunit;
using Xunit.Sdk;

/// <summary>Verifies IDE-local staging discovery without mutating the test host environment.</summary>
public sealed class TestRipgrepTests
{
    /// <summary>A missing process override resolves the fixed repository-owned staged executable.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ResolveExecutable_WithoutEnvironmentOverride_UsesLocalStagedFixture(string? configured)
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-ripgrep-discovery-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "artifacts", "test-ripgrep", "stage", "tools");
        Directory.CreateDirectory(directory);
        try
        {
            var expected = Path.Combine(directory, OperatingSystem.IsWindows() ? "rg.exe" : "rg");
            File.WriteAllText(expected, "fixture path only; never executed");

            Assert.Equal(expected, TestRipgrep.ResolveExecutable(configured, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An explicit invalid path cannot be hidden by a valid local staging directory.</summary>
    [Fact]
    public void ResolveExecutable_InvalidExplicitPath_DoesNotFallBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-ripgrep-discovery-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "artifacts", "test-ripgrep", "stage", "tools");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, OperatingSystem.IsWindows() ? "rg.exe" : "rg"), "local fixture");

            var error = Assert.ThrowsAny<XunitException>(() => TestRipgrep.ResolveExecutable("relative-rg", root));

            Assert.Contains("Missing or invalid fixture: relative-rg", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A missing staged asset remains a hard failure rather than a skipped integration case.</summary>
    [Fact]
    public void ResolveExecutable_MissingLocalFixture_FailsWithStagingInstructions()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-ripgrep-missing-" + Guid.NewGuid().ToString("N"));

        var error = Assert.ThrowsAny<XunitException>(() => TestRipgrep.ResolveExecutable(null, root));

        Assert.Contains("Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep", error.Message, StringComparison.Ordinal);
    }
}
