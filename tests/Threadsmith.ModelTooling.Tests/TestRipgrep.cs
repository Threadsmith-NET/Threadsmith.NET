namespace Threadsmith.ModelTooling.Tests;

using Xunit;

/// <summary>Uses the staged pinned executable in CLI and IDE test hosts without searching machine tools.</summary>
internal static class TestRipgrep
{
    internal static string Executable => ResolveExecutable(
        Environment.GetEnvironmentVariable("THREADSMITH_TEST_RIPGREP"),
        TestPromptLoader.FindRepositoryRoot());

    internal static string ResolveExecutable(string? configuredPath, string repositoryRoot)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(repositoryRoot, "artifacts", "test-ripgrep", "stage", "tools", OperatingSystem.IsWindows() ? "rg.exe" : "rg")
            : configuredPath;
        Assert.True(
            Path.IsPathFullyQualified(path) && File.Exists(path),
            $"Stage pinned ripgrep with eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep. "
            + $"For a custom staging location, set THREADSMITH_TEST_RIPGREP to its absolute executable path. Missing or invalid fixture: {path}");
        return path;
    }
}
