namespace Threadsmith.Architecture.Tests;

using Threadsmith.App;
using Threadsmith.Core;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>The launch override isolates all three OS storage scopes without changing repository identity.</summary>
    [Fact]
    public void HostStorageOverrideIsLaunchOwnedAndAbsolute()
    {
        var previous = Environment.GetEnvironmentVariable("THREADSMITH_STATE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("THREADSMITH_STATE_ROOT", _root);
            var paths = ConfigurationBootstrap.ResolvePaths(_paths.RepositoryRoot);
            Assert.Equal(_paths.RepositoryRoot, paths.RepositoryRoot);
            Assert.Equal(Path.Combine(_root, "profile", ".threadsmith", "config.json"), paths.UserConfiguration);
            Assert.Equal(Path.Combine(_root, "machine", "Threadsmith", "config.json"), paths.MachineConfiguration);
            Assert.Equal(Path.Combine(_root, "local"), HostStoragePaths.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            Environment.SetEnvironmentVariable("THREADSMITH_STATE_ROOT", "relative-state");
            Assert.Throws<InvalidOperationException>(() => ConfigurationBootstrap.ResolvePaths(_paths.RepositoryRoot));

            Environment.SetEnvironmentVariable("THREADSMITH_STATE_ROOT", null);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), HostStoragePaths.GetFolderPath(Environment.SpecialFolder.UserProfile));
            File.WriteAllText(_paths.RepositoryConfiguration, """{"STATE_ROOT":"repository-cannot-redirect-profile"}""");
            var configuration = ConfigurationBootstrap.Build(["--set:STATE_ROOT=launch-setting-is-not-process-environment"], _paths);
            using var configurationLifetime = configuration as IDisposable;
            Assert.Equal("launch-setting-is-not-process-environment", configuration["STATE_ROOT"]);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), HostStoragePaths.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".threadsmith", "config.json"), ConfigurationBootstrap.ResolvePaths(_paths.RepositoryRoot).UserConfiguration);
        }
        finally
        {
            Environment.SetEnvironmentVariable("THREADSMITH_STATE_ROOT", previous);
        }
    }
}
