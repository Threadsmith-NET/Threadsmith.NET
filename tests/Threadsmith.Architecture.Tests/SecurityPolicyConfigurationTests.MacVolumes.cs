namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Real APFS volumes exercise root and nested filesystem case modes through read and write admission.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MacVolumesEnforceProhibitionsAcrossCaseBoundaries(bool rootSensitive)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires macOS disk-image volumes.");
        var token = TestContext.Current.CancellationToken;
        var rootImage = Path.Combine(_root, "root.sparseimage");
        var childImage = Path.Combine(_root, "child.sparseimage");
        var mount = Path.Combine(_paths.RepositoryRoot, "Mount");
        var rootAttached = false;
        var childAttached = false;
        var processes = new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance);
        try
        {
            await RunDiskImageAsync(["create", "-size", "256m", "-type", "SPARSE", "-fs", rootSensitive ? "APFSX" : "APFS", "-volname", "ThreadsmithRoot", rootImage], token);
            await RunDiskImageAsync(["create", "-size", "256m", "-type", "SPARSE", "-fs", rootSensitive ? "APFS" : "APFSX", "-volname", "ThreadsmithChild", childImage], token);
            Directory.Delete(Path.Combine(_paths.RepositoryRoot, ".threadsmith"));
            await RunDiskImageAsync(["attach", "-nobrowse", "-mountpoint", _paths.RepositoryRoot, rootImage], token);
            rootAttached = true;
            Directory.CreateDirectory(Path.Combine(_paths.RepositoryRoot, ".threadsmith"));
            Directory.CreateDirectory(mount);
            await RunDiskImageAsync(["attach", "-nobrowse", "-mountpoint", mount, childImage], token);
            childAttached = true;
            foreach (var directory in new[] { _paths.RepositoryRoot, mount })
            {
                Directory.CreateDirectory(Path.Combine(directory, "private"));
                await File.WriteAllTextAsync(Path.Combine(directory, "private", "file.txt"), "fixture", token);
            }

            // Verify the actual mounts, independently of the policy's own case detector.
            Assert.Equal(!rootSensitive, File.Exists(Path.Combine(_paths.RepositoryRoot, "PRIVATE", "file.txt")));
            Assert.Equal(rootSensitive, File.Exists(Path.Combine(mount, "PRIVATE", "file.txt")));
            await File.WriteAllTextAsync(_paths.UserConfiguration, """{"prohibitedPaths":["private/**","Mount/private/**"],"tools":{"writeFile":{"allowedFolders":["."]}}}""", token);
            var policy = Load();
            AssertPathAdmission(policy, "private/file.txt", false);
            AssertPathAdmission(policy, "PRIVATE/file.txt", rootSensitive);
            AssertPathAdmission(policy, "Mount/private/file.txt", false);
            AssertPathAdmission(policy, "Mount/PRIVATE/file.txt", !rootSensitive);
            AssertPathAdmission(policy, "public.txt", true);
        }
        finally
        {
            try
            {
                if (childAttached)
                {
                    await RunDiskImageAsync(["detach", mount], CancellationToken.None);
                }
            }
            finally
            {
                if (rootAttached)
                {
                    await RunDiskImageAsync(["detach", _paths.RepositoryRoot], CancellationToken.None);
                }
            }
        }

        async Task RunDiskImageAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var request = new ProcessExecutionRequest
            {
                ToolInvocationId = ToolInvocationId.New(),
                RunId = RunId.New(),
                FileName = "/usr/bin/hdiutil",
                Origin = ProcessRequestOrigin.Host,
                Arguments = arguments,
                WorkingDirectory = _root,
                Timeout = cancellationToken.CanBeCanceled ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(30),
            };
            var result = await processes.RunAsync(request, cancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
        }
    }
}
