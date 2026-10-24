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
        var mount = Path.Combine(_paths.RepositoryRoot, "Mount");
        var rootAttached = false;
        var childAttached = false;
        var processes = new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance);
        try
        {
            var rootImage = await CreateDiskImageAsync("root", rootSensitive, processes, token);
            var childImage = await CreateDiskImageAsync("child", !rootSensitive, processes, token);
            Directory.Delete(Path.Combine(_paths.RepositoryRoot, ".threadsmith"));
            // Nested mountpoints must retain real ownership rather than disk-image
            // noowners semantics, which macOS rejects for unprivileged attachment.
            await RunDiskImageAsync(["attach", "-nobrowse", "-owners", "on", "-mountpoint", _paths.RepositoryRoot, rootImage], token);
            rootAttached = true;
            Directory.CreateDirectory(Path.Combine(_paths.RepositoryRoot, ".threadsmith"));
            Directory.CreateDirectory(mount);
            await RunDiskImageAsync(["attach", "-nobrowse", "-owners", "on", "-mountpoint", mount, childImage], token);
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
            var request = DiskImageRequest(arguments);
            var result = await processes.RunAsync(request, cancellationToken);
            Assert.True(result.ExitCode == 0, $"hdiutil {string.Join(' ', arguments)}: {result.StandardError}{result.StandardOutput}");
        }
    }

    /// <summary>Only the observed transient provisioning error receives bounded recovery.</summary>
    [Theory]
    [InlineData(1, "hdiutil: create failed - Resource busy\n", 2, true)]
    [InlineData(3, "hdiutil: create failed - Resource busy\n", 3, false)]
    [InlineData(1, "hdiutil: create failed - Permission denied\n", 1, false)]
    public async Task DiskImageProvisioningRetriesOnlyBusyCreation(int failures, string diagnostic, int expectedCalls, bool succeeds)
    {
        var processes = new DiskImageProcessFixture(failures, diagnostic);
        var operation = CreateDiskImageAsync("probe", true, processes, TestContext.Current.CancellationToken);
        if (succeeds)
        {
            var path = await operation;
            Assert.Equal(processes.Requests[^1].Arguments[^1], path);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation.WaitAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(expectedCalls, processes.Requests.Count);
        Assert.Equal(expectedCalls, processes.Requests.Select(request => request.Arguments[^1]).Distinct(StringComparer.Ordinal).Count());
        Assert.All(processes.Requests, request =>
        {
            Assert.Equal("create", request.Arguments[0]);
            Assert.Equal(_root, Path.GetDirectoryName(request.Arguments[^1]));
            Assert.DoesNotContain("-ov", request.Arguments);
        });
    }

    private async Task<string> CreateDiskImageAsync(string name, bool caseSensitive, IProcessManager processes, CancellationToken cancellationToken)
    {
        // macOS can transiently lock newly created images (runner-images#7522).
        // Retry provisioning only, never policy assertions, attach, or the whole test.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_root, $"{name}-{attempt}.sparseimage");
            var request = DiskImageRequest(["create", "-size", "256m", "-type", "SPARSE", "-fs", caseSensitive ? "Case-sensitive APFS" : "APFS", "-volname", $"Threadsmith-{name}-{attempt}", path]);
            var result = await processes.RunAsync(request, cancellationToken);
            if (result.ExitCode == 0)
            {
                return path;
            }

            var diagnostic = $"hdiutil create attempt {attempt}: {path}; exit={result.ExitCode}; {result.StandardError}{result.StandardOutput}";
            TestContext.Current.TestOutputHelper?.WriteLine(diagnostic);
            if (attempt == 3 || result.TimedOut || !result.StandardError.Trim().Equals("hdiutil: create failed - Resource busy", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(diagnostic);
            }
        }

        throw new InvalidOperationException("Disk image provisioning exhausted its attempt budget.");
    }

    private ProcessExecutionRequest DiskImageRequest(IReadOnlyList<string> arguments) => new()
    {
        ToolInvocationId = ToolInvocationId.New(),
        RunId = RunId.New(),
        FileName = "/usr/bin/hdiutil",
        Origin = ProcessRequestOrigin.Host,
        Arguments = arguments,
        WorkingDirectory = _root,
        // Infrastructure watchdog, not an assertion about product execution speed.
        Timeout = TimeSpan.FromMinutes(1),
    };

    private sealed class DiskImageProcessFixture : IProcessManager
    {
        private readonly int _failures;
        private readonly string _diagnostic;

        public DiskImageProcessFixture(int failures, string diagnostic)
        {
            _failures = failures;
            _diagnostic = diagnostic;
        }

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var failed = Requests.Count <= _failures;
            return Task.FromResult(new ProcessExecutionResult(0, failed ? 1 : 0, string.Empty, failed ? _diagnostic : string.Empty, false, false, false, TimeSpan.Zero));
        }
    }
}
