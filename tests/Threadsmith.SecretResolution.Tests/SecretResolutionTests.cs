namespace Threadsmith.SecretResolution.Tests;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Threadsmith.Tools;
using Xunit;

/// <summary>Milestone 22.2 extensible secret-discovery acceptance coverage.</summary>
public sealed class SecretResolutionTests
{
    /// <summary>References are canonical, bounded, and reject traversal or ambiguous separators.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("secret:value")]
    [InlineData("secrets:")]
    [InlineData("secrets:../value")]
    [InlineData("secrets:a::b")]
    [InlineData("secrets:a/b")]
    public void SecretReference_RejectsMalformedNames(string value)
    {
        Assert.False(SecretReference.TryParse(value, out _));
    }

    /// <summary>Accepted configured identifiers become stable non-disclosing diagnostic tokens.</summary>
    [Fact]
    public void Request_CreatesSafeComponentIdFromConfiguredIdentifier()
    {
        const string configuredIdentifier = "local server\r\nwith accepted punctuation!";

        var first = SecretResolutionRequest.CreateConfiguredComponentId("mcp:stdio", configuredIdentifier);
        var second = SecretResolutionRequest.CreateConfiguredComponentId("mcp:stdio", configuredIdentifier);

        Assert.Equal(first, second);
        Assert.StartsWith("mcp:stdio:", first, StringComparison.Ordinal);
        Assert.DoesNotContain("local server", first, StringComparison.Ordinal);
        Assert.Equal(74, first.Length);
    }

    /// <summary>Environment overrides repository and user values through fixed host-owned precedence.</summary>
    [Fact]
    public async Task Resolver_UsesDeterministicEnvironmentPrecedenceAsync()
    {
        using var fixture = new SecretFixture();
        fixture.WriteUser("user-value");
        fixture.WriteRepository("repository-value", ignored: true);
        var variable = "THREADSMITH_secrets__tests__" + fixture.Id;
        Environment.SetEnvironmentVariable(variable, "environment-value");
        try
        {
            var resolver = fixture.CreateResolver();
            var result = await resolver.ResolveAsync(fixture.CreateRequest());

            Assert.Equal("environment", result.ProviderId);
            Assert.Equal("environment-value", result.Value?.Reveal());
            Assert.DoesNotContain("environment-value", result.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>A repository value cannot satisfy a user-owned request and the user fallback remains available.</summary>
    [Fact]
    public async Task Resolver_FiltersRepositoryProviderByMinimumTrustAsync()
    {
        using var fixture = new SecretFixture();
        fixture.WriteRepository("repository-value", ignored: true);
        fixture.WriteUser("user-value");

        var result = await fixture.CreateResolver().ResolveAsync(
            fixture.CreateRequest(SecretProviderTrust.UserOwned));

        Assert.Equal("user-file", result.ProviderId);
        Assert.Equal("user-value", result.Value?.Reveal());
        Assert.Contains("repository-file:policy-skipped", result.Diagnostics);
    }

    /// <summary>The repository provider rejects tracked content before returning its value.</summary>
    [Fact]
    public async Task RepositoryProvider_RejectsTrackedStoreAsync()
    {
        using var fixture = new SecretFixture();
        fixture.WriteRepository("canary-secret", ignored: false);
        fixture.Git("add", "-f", ".threadsmith/secrets/config.json");

        var result = await new RepositoryFileSecretProvider(fixture.RepositoryRoot)
            .TryResolveAsync(fixture.CreateRequest());

        Assert.Equal(SecretResolutionFailure.RepositoryGitProofFailed, result.Failure);
        Assert.Equal("tracked-or-staged", result.DiagnosticCode);
        Assert.Null(result.Value);
        Assert.DoesNotContain("canary-secret", result.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A tracked ancestor representation rejects an ignored descendant replacement.</summary>
    [Fact]
    public async Task RepositoryProvider_RejectsTrackedAncestorRepresentationAsync()
    {
        using var fixture = new SecretFixture();
        var metadata = Path.Combine(fixture.RepositoryRoot, ".threadsmith");
        var ancestor = Path.Combine(metadata, "secrets");
        Directory.CreateDirectory(metadata);
        await File.WriteAllTextAsync(ancestor, "tracked ancestor", new UTF8Encoding(false));
        fixture.Git("add", ".threadsmith/secrets");
        File.Delete(ancestor);
        fixture.WriteRepository("canary-secret", ignored: true);

        var result = await new RepositoryFileSecretProvider(fixture.RepositoryRoot)
            .TryResolveAsync(fixture.CreateRequest());

        Assert.Equal(SecretResolutionFailure.RepositoryGitProofFailed, result.Failure);
        Assert.Equal("tracked-or-staged", result.DiagnosticCode);
        Assert.Null(result.Value);
    }

    /// <summary>A tracked store remains rejected when the opened repository is a worktree subdirectory.</summary>
    [Fact]
    public async Task RepositoryProvider_RejectsTrackedStoreUnderOpenedSubdirectoryAsync()
    {
        using var fixture = new SecretFixture();
        var openedRepository = Path.Combine(fixture.RepositoryRoot, "opened-subdirectory");
        var storePath = Path.Combine(openedRepository, ".threadsmith", "secrets", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath) ?? openedRepository);
        await File.WriteAllTextAsync(
            storePath,
            $"{{\"secrets\":{{\"tests\":{{\"{fixture.Id}\":\"canary-secret\"}}}}}}",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(openedRepository, ".gitignore"),
            ".threadsmith/secrets/config.json\n",
            new UTF8Encoding(false));
        fixture.Git("add", "-f", "opened-subdirectory/.threadsmith/secrets/config.json");

        var result = await new RepositoryFileSecretProvider(openedRepository)
            .TryResolveAsync(fixture.CreateRequest() with { RepositoryRoot = openedRepository });

        Assert.Equal(SecretResolutionFailure.RepositoryGitProofFailed, result.Failure);
        Assert.Equal("tracked-or-staged", result.DiagnosticCode);
        Assert.Null(result.Value);
    }

    /// <summary>An exact untracked ignored store resolves for a repository-trust request.</summary>
    [Fact]
    public async Task RepositoryProvider_ResolvesOnlyIgnoredUntrackedStoreAsync()
    {
        using var fixture = new SecretFixture();
        fixture.WriteRepository("repository-value", ignored: true);

        var result = await new RepositoryFileSecretProvider(fixture.RepositoryRoot)
            .TryResolveAsync(fixture.CreateRequest());

        Assert.Equal("repository-value", result.Value?.Reveal());
        Assert.Equal(SecretResolutionFailure.None, result.Failure);
    }

    /// <summary>A repository-controlled replacement cannot redirect the post-validation read through a link.</summary>
    [Fact]
    public async Task RepositoryProvider_RejectsLinkReplacementAfterValidationAsync()
    {
        using var fixture = new SecretFixture();
        var storePath = Path.Combine(fixture.Root, "race", "config.json");
        var outsidePath = Path.Combine(fixture.Root, "outside.json");
        var probePath = Path.Combine(fixture.Root, "link-probe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath) ?? fixture.Root);
        await File.WriteAllTextAsync(storePath, "{}", new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            outsidePath,
            $"{{\"secrets\":{{\"tests\":{{\"{fixture.Id}\":\"outside-secret\"}}}}}}",
            new UTF8Encoding(false));
        if (!TryCreateFileSymbolicLink(probePath, outsidePath))
        {
            return;
        }

        File.Delete(probePath);
        var provider = new ReplacingFileSecretProvider(storePath, outsidePath);

        var result = await provider.TryResolveAsync(fixture.CreateRequest());

        Assert.Equal(SecretResolutionFailure.UnsafeStore, result.Failure);
        Assert.Null(result.Value);
        Assert.DoesNotContain("outside-secret", result.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A repository FIFO is rejected promptly before any blocking read can begin.</summary>
    [Fact]
    public async Task RepositoryProvider_RejectsUnixFifoWithoutBlockingAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SecretFixture();
        fixture.InitializeGitRepository();
        var storePath = Path.Combine(fixture.RepositoryRoot, ".threadsmith", "secrets", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath) ?? fixture.RepositoryRoot);
        Assert.Equal(0, CreateFifoUnix(storePath, 0x180));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.RepositoryRoot, ".gitignore"),
            ".threadsmith/secrets/config.json\n",
            new UTF8Encoding(false));

        var result = await new RepositoryFileSecretProvider(fixture.RepositoryRoot)
            .TryResolveAsync(fixture.CreateRequest())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SecretResolutionFailure.UnsafeStore, result.Failure);
        Assert.Null(result.Value);
    }

    /// <summary>Case-insensitive duplicate names fail closed and do not expose either value.</summary>
    [Fact]
    public async Task UserProvider_RejectsDuplicatePropertiesAsync()
    {
        using var fixture = new SecretFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.UserStorePath) ?? fixture.Root);
        await File.WriteAllTextAsync(
            fixture.UserStorePath,
            $"{{\"secrets\":{{\"tests\":{{\"{fixture.Id}\":\"first\",\"{fixture.Id.ToUpperInvariant()}\":\"second\"}}}}}}",
            new UTF8Encoding(false));
        fixture.SecureUserStore();

        var result = await new UserFileSecretProvider(fixture.UserStorePath)
            .TryResolveAsync(fixture.CreateRequest());

        Assert.Equal(SecretResolutionFailure.MalformedStore, result.Failure);
        Assert.DoesNotContain("first", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("second", result.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A Windows user store readable by another local principal is rejected.</summary>
    [Fact]
    public async Task UserProvider_RejectsUnsafeWindowsAclAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SecretFixture();
        fixture.WriteUser("canary-secret");
        fixture.GrantWindowsWorldRead();

        var result = await new UserFileSecretProvider(fixture.UserStorePath)
            .TryResolveAsync(fixture.CreateRequest());

        Assert.Equal(SecretResolutionFailure.UnsafeStore, result.Failure);
        Assert.Equal("unsafe-permissions", result.DiagnosticCode);
        Assert.Null(result.Value);
    }

    /// <summary>Repository transitions atomically redirect future repository lookups.</summary>
    [Fact]
    public async Task RepositoryProvider_RebindsToActiveRepositoryAsync()
    {
        using var first = new SecretFixture();
        using var second = new SecretFixture();
        first.WriteRepository("first-value", ignored: true);
        second.WriteRepository("second-value", ignored: true);
        var provider = new RepositorySecretProvider(first.RepositoryRoot);

        var before = await provider.TryResolveAsync(first.CreateRequest());
        provider.BindRepository(second.RepositoryRoot);
        var after = await provider.TryResolveAsync(second.CreateRequest());

        Assert.Equal("first-value", before.Value?.Reveal());
        Assert.Equal("second-value", after.Value?.Reveal());
    }

    /// <summary>A future provider plugs into the resolver without changing a consumer.</summary>
    [Fact]
    public async Task Resolver_AcceptsFutureProviderContractAsync()
    {
        var resolver = new SecretResolver([new FakeExternalProvider()]);
        var result = await resolver.ResolveAsync(new SecretResolutionRequest
        {
            Reference = SecretReference.Parse("secrets:external:example"),
            ComponentId = "tests:future-provider",
            Purpose = "verify provider-neutral extensibility",
            MinimumTrust = SecretProviderTrust.Managed,
        });

        Assert.Equal("fake-external", result.ProviderId);
        Assert.Equal("external-value", result.Value?.Reveal());
    }

    /// <summary>Untrusted provider diagnostic text cannot enter aggregate diagnostics.</summary>
    [Fact]
    public async Task Resolver_SanitizesProviderDiagnosticCodeAsync()
    {
        var resolver = new SecretResolver([new FakeExternalProvider("value-leak with whitespace")]);
        var result = await resolver.ResolveAsync(new SecretResolutionRequest
        {
            Reference = SecretReference.Parse("secrets:external:missing"),
            ComponentId = "tests:diagnostics",
            Purpose = "verify safe provider diagnostics",
            MinimumTrust = SecretProviderTrust.Managed,
        });

        Assert.Contains("fake-external:invalid-provider-diagnostic", result.Diagnostics);
        Assert.DoesNotContain("value-leak", string.Join(',', result.Diagnostics), StringComparison.Ordinal);
    }

    /// <summary>Duplicate provider IDs and priorities are rejected at composition.</summary>
    [Fact]
    public void Resolver_RejectsAmbiguousProviderRegistration()
    {
        Assert.Throws<ArgumentException>(() => new SecretResolver(
                [new EnvironmentSecretProvider(), new EnvironmentSecretProvider()]));
    }

    /// <summary>A provider timeout cancels the token supplied to the active provider.</summary>
    [Fact]
    public async Task Resolver_ProviderTimeoutCancelsProviderWorkAsync()
    {
        var provider = new CancellationObservingProvider();
        var resolver = new SecretResolver([provider]);
        var request = new SecretResolutionRequest
        {
            Reference = SecretReference.Parse("secrets:external:timeout"),
            ComponentId = "tests:provider-timeout",
            Purpose = "verify provider work cancellation",
            MinimumTrust = SecretProviderTrust.Managed,
            ProviderTimeout = TimeSpan.FromMilliseconds(50),
        };

        var result = await resolver.ResolveAsync(request);
        await provider.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(SecretResolutionFailure.ProviderUnavailable, result.Failure);
        Assert.Contains("cancellation-observer:provider-timeout", result.Diagnostics);
    }

    /// <summary>Repository Git validation rejects caller cancellation before launching a probe.</summary>
    [Fact]
    public async Task RepositoryProvider_GitValidationHonorsCancellationAsync()
    {
        using var fixture = new SecretFixture();
        fixture.WriteRepository("repository-value", ignored: true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new RepositoryFileSecretProvider(fixture.RepositoryRoot)
            .TryResolveAsync(fixture.CreateRequest(), cancellation.Token));
    }

    /// <summary>Repository identity comparison remains case-sensitive outside Windows.</summary>
    [Fact]
    public async Task RepositoryProvider_UsesHostPathCaseSemanticsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SecretFixture();
        fixture.WriteRepository("repository-value", ignored: true);
        var caseDistinctRoot = Path.Combine(fixture.Root, "REPO");

        var result = await new RepositoryFileSecretProvider(fixture.RepositoryRoot)
            .TryResolveAsync(fixture.CreateRequest() with { RepositoryRoot = caseDistinctRoot });

        Assert.Equal(SecretResolutionFailure.UnsafeStore, result.Failure);
        Assert.Equal("repository-path-rejected", result.DiagnosticCode);
    }

    /// <summary>Caller cancellation propagates rather than being converted into a provider diagnostic.</summary>
    [Fact]
    public async Task Resolver_PropagatesCallerCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var resolver = new SecretResolver([new EnvironmentSecretProvider()]);
        var request = new SecretResolutionRequest
        {
            Reference = SecretReference.Parse("secrets:tests:cancel"),
            ComponentId = "tests",
            Purpose = "verify cancellation",
            MinimumTrust = SecretProviderTrust.UserOwned,
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(request, cancellation.Token));
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int CreateFifoUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    private sealed class ReplacingFileSecretProvider : JsonFileSecretProvider
    {
        private readonly string _replacementTarget;

        internal ReplacingFileSecretProvider(string storePath, string replacementTarget)
            : base(storePath)
        {
            _replacementTarget = replacementTarget;
        }

        public override string Id => "replacing-repository-file";

        public override int Priority => 200;

        public override SecretProviderTrust Trust => SecretProviderTrust.RepositoryOwned;

        public override SecretProviderSourceKind SourceKind => SecretProviderSourceKind.RepositoryFile;

        public override IReadOnlyList<string> SupportedPrefixes => ["secrets:"];

        protected override Task<SecretProviderResult> ValidateStoreAsync(
            SecretResolutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(StorePath);
            File.CreateSymbolicLink(StorePath, _replacementTarget);
            return Task.FromResult(SecretProviderResult.NotFound("store-safe"));
        }
    }

    private sealed class CancellationObservingProvider : ISecretProvider
    {
        private readonly TaskCompletionSource<bool> _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "cancellation-observer";

        public int Priority => 400;

        public SecretProviderTrust Trust => SecretProviderTrust.Managed;

        public SecretProviderSourceKind SourceKind => SecretProviderSourceKind.External;

        public IReadOnlyList<string> SupportedPrefixes => ["secrets:external:"];

        public async Task<SecretProviderResult> TryResolveAsync(
            SecretResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return SecretProviderResult.NotFound();
            }
            catch (OperationCanceledException)
            {
                _cancellationObserved.TrySetResult(true);
                throw;
            }
        }

        internal Task CancellationObserved => _cancellationObserved.Task;
    }

    private sealed class FakeExternalProvider : ISecretProvider
    {
        private readonly string _missingDiagnostic;

        internal FakeExternalProvider(string missingDiagnostic = "not-found")
        {
            _missingDiagnostic = missingDiagnostic;
        }

        public string Id => "fake-external";

        public int Priority => 400;

        public SecretProviderTrust Trust => SecretProviderTrust.Managed;

        public SecretProviderSourceKind SourceKind => SecretProviderSourceKind.External;

        public IReadOnlyList<string> SupportedPrefixes => ["secrets:external:"];

        public Task<SecretProviderResult> TryResolveAsync(
            SecretResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(request.Reference.CanonicalName.EndsWith(":example", StringComparison.Ordinal)
                ? SecretProviderResult.Found("external-value")
                : new SecretProviderResult
                {
                    Failure = SecretResolutionFailure.NotFound,
                    DiagnosticCode = _missingDiagnostic,
                });
        }
    }

    private sealed class SecretFixture : IDisposable
    {
        private bool _gitInitialized;

        internal SecretFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "Threadsmith", "secret-tests", Guid.NewGuid().ToString("N"));
            RepositoryRoot = Path.Combine(Root, "repo");
            UserStorePath = Path.Combine(Root, "user", "secrets", "config.json");
            Id = "key" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(RepositoryRoot);
        }

        internal string Id { get; }

        internal string RepositoryRoot { get; }

        internal string Root { get; }

        internal string UserStorePath { get; }

        public void Dispose()
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "Threadsmith",
                "secret-tests")));
            if (!string.Equals(
                    Path.GetDirectoryName(normalizedRoot),
                    normalizedParent,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileName(normalizedRoot), "N", out _))
            {
                throw new InvalidOperationException("Refusing to delete an unowned secret-test directory.");
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(
                         normalizedRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }

            File.SetAttributes(normalizedRoot, File.GetAttributes(normalizedRoot) & ~FileAttributes.ReadOnly);
            Directory.Delete(normalizedRoot, recursive: true);
        }

        internal SecretResolutionRequest CreateRequest(
            SecretProviderTrust minimumTrust = SecretProviderTrust.RepositoryOwned)
        {
            return new()
            {
                Reference = SecretReference.Parse("secrets:tests:" + Id),
                ComponentId = "tests:milestone-22-2",
                Purpose = "verify secret discovery",
                RepositoryRoot = RepositoryRoot,
                MinimumTrust = minimumTrust,
            };
        }

        internal SecretResolver CreateResolver()
        {
            return new(
            [
                new EnvironmentSecretProvider(),
                new RepositoryFileSecretProvider(RepositoryRoot),
                new UserFileSecretProvider(UserStorePath),
            ]);
        }

        internal void Git(params string[] arguments)
        {
            InitializeGitRepository();
            RunGit(arguments);
        }

        internal void InitializeGitRepository()
        {
            if (_gitInitialized)
            {
                return;
            }

            RunGit("init", "--quiet");
            _gitInitialized = true;
        }

        internal void SecureUserStore()
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var currentUser = identity.User;
                Assert.NotNull(currentUser);

                var security = new FileSecurity();
                security.SetOwner(currentUser);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                new FileInfo(UserStorePath).SetAccessControl(security);
                return;
            }

            File.SetUnixFileMode(UserStorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        [SupportedOSPlatform("windows")]
        internal void GrantWindowsWorldRead()
        {
            var file = new FileInfo(UserStorePath);
            var security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
                FileSystemRights.ReadData,
                AccessControlType.Allow));
            file.SetAccessControl(security);
        }

        internal void WriteRepository(string value, bool ignored)
        {
            InitializeGitRepository();
            var store = Path.Combine(RepositoryRoot, ".threadsmith", "secrets", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(store) ?? RepositoryRoot);
            File.WriteAllText(store, Json(value), new UTF8Encoding(false));
            if (ignored)
            {
                File.WriteAllText(
                    Path.Combine(RepositoryRoot, ".gitignore"),
                    ".threadsmith/secrets/config.json\n",
                    new UTF8Encoding(false));
            }
        }

        internal void WriteUser(string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserStorePath) ?? Root);
            File.WriteAllText(UserStorePath, Json(value), new UTF8Encoding(false));
            SecureUserStore();
        }

        private void RunGit(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = RepositoryRoot,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            Assert.True(process.Start());
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
        }

        private string Json(string value)
        {
            return $"{{\"secrets\":{{\"tests\":{{\"{Id}\":\"{value}\"}}}}}}";
        }
    }
}
