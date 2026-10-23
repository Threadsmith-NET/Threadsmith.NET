namespace Threadsmith.ModelTooling.Tests;

using Xunit;

/// <summary>Uses the explicitly staged, checksum-verified executable on every test platform.</summary>
internal static class TestRipgrep
{
    internal static string Executable
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("THREADSMITH_TEST_RIPGREP");
            Assert.False(string.IsNullOrWhiteSpace(path), "Stage pinned ripgrep with eng/Stage-DevelopmentRipgrep.ps1 and set THREADSMITH_TEST_RIPGREP to its absolute executable path; see CONTRIBUTING.md.");
            Assert.NotNull(path);
            Assert.True(Path.IsPathFullyQualified(path) && File.Exists(path), $"The staged ripgrep executable is missing or not absolute: {path}");
            return path;
        }
    }
}
