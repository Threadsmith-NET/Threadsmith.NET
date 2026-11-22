namespace Threadsmith.Extensions.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Xunit;

/// <summary>
/// Regression tests added by the Milestone 7 PR-review remediation. Each test pins one finding so the
/// fixed behavior cannot silently regress:
/// <list type="bullet">
/// <item>F2: the host constructs an extension exactly once during load (no throwaway prototype).</item>
/// <item>F3: <see cref="ExtensionApprovalRequirement.HostPolicy"/> maps faithfully to <see cref="ApprovalLevel.HostPolicy"/>,
/// not silently to <see cref="ApprovalLevel.None"/>.</item>
/// <item>F6: repeated invocation leases on the same thread/generation are all tracked (monotonic LeaseId).</item>
/// <item>F11: a load failure publishes <see cref="ExtensionLoadFailed"/>, not <see cref="ExtensionUnloadFailed"/>.</item>
/// </list>
/// </summary>
public sealed class ExtensionReviewRemediationTests
{
    /// <summary>Asserts <see cref="ExtensionApprovalRequirement.HostPolicy"/> maps to <see cref="ApprovalLevel.HostPolicy"/> (F3).</summary>
    [Fact]
    public async Task CapabilityProxy_maps_host_policy_approval_faithfully()
    {
        var generation = await LoadConstructionCountGenerationAsync();
        var leaseAuthority = new InvocationLeaseAuthority(NullLogger.Instance);

        var hostPolicyProxy = new CapabilityProxy(
            new HostPolicyToolCapability(ExtensionApprovalRequirement.HostPolicy),
            CapabilityId.New(),
            generation,
            leaseAuthority,
            NullLogger.Instance);
        Assert.Equal(ApprovalLevel.HostPolicy, hostPolicyProxy.Definition.RequiredApproval);

        var userProxy = new CapabilityProxy(
            new HostPolicyToolCapability(ExtensionApprovalRequirement.User),
            CapabilityId.New(),
            generation,
            leaseAuthority,
            NullLogger.Instance);
        Assert.Equal(ApprovalLevel.User, userProxy.Definition.RequiredApproval);

        var noneProxy = new CapabilityProxy(
            new HostPolicyToolCapability(ExtensionApprovalRequirement.None),
            CapabilityId.New(),
            generation,
            leaseAuthority,
            NullLogger.Instance);
        Assert.Equal(ApprovalLevel.None, noneProxy.Definition.RequiredApproval);
    }

    /// <summary>A staged manifest cannot lower the permission-derived floor supplied by the caller.</summary>
    [Fact]
    public static async Task LoadAsync_RejectsCallerPermissionFloorBeforeTypeDiscovery()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;

            // This is a valid assembly without an extension type. Discovery would fail with a different error.
            var entryAssembly = typeof(SessionId).Assembly.Location;
            File.Copy(entryAssembly, Path.Combine(package, Path.GetFileName(entryAssembly)));
            await File.WriteAllTextAsync(
                Path.Combine(package, "threadsmith.extension.json"),
                """{"Id":"threadsmith.tests.permission-floor","Name":"Permission floor fixture","Permissions":[]}""",
                token);
            var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
            var request = new ExtensionLoadRequest
            {
                ExtensionDirectory = package,
                ShadowStagingRoot = Path.Combine(root, "staging"),
                EffectiveTrust = RepositoryTrustLevel.TrustedRead,
                SessionId = SessionId.New(),
                Manifest = new ExtensionManifest
                {
                    Id = "threadsmith.tests.permission-floor",
                    Name = "Permission floor fixture",
                    EntryAssembly = Path.GetFileName(entryAssembly),
                    Permissions = ["secret-access"],
                },
            };

            var failure = await Assert.ThrowsAsync<ExtensionIncompatibleException>(() => host.LoadAsync(request, token));

            Assert.Contains(nameof(RepositoryTrustLevel.FullyTrustedAutomation), failure.Message, StringComparison.Ordinal);
            Assert.Empty(host.Generations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Uses the staged assembly and class selections on the manual manager path.</summary>
    [Fact]
    public static async Task ManagerLoad_UsesStagedEntryAssemblyAndClass()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        var session = SessionId.New();
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        try
        {
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools"));
            const string assemblyName = "Threadsmith.Tests.Fixtures.ConstructionCountExtension.dll";
            File.Copy(Path.Combine(ResolveFixtureOutputDirectory("ConstructionCountExtension"), assemblyName), Path.Combine(tools.FullName, assemblyName));
            await File.WriteAllTextAsync(
                Path.Combine(root, "threadsmith.extension.json"),
                """{"Id":"manager-entry","Name":"Staged manager fixture","HostApiVersion":"current","EntryAssembly":"tools/Threadsmith.Tests.Fixtures.ConstructionCountExtension.dll","EntryClass":"Threadsmith.Tests.Fixtures.ConstructionCount.ConstructionCountExtension"}""",
                token);
            host.SetDiscoveryDirectory(root);
            IExtensionManager manager = host;

            var discovered = Assert.Single(await manager.DiscoverAsync(token));
            Assert.Equal("manager-entry", discovered.ExtensionId);
            Assert.Null(await manager.LoadAsync(Path.GetFileNameWithoutExtension(assemblyName), session, token));

            var result = await manager.LoadAsync("MANAGER-ENTRY", session, token);

            Assert.NotNull(result);
            Assert.True(result.IsLoaded);
            Assert.Equal("Staged manager fixture", result.Name);
            Assert.Equal(1, result.ToolCount);
            Assert.Equal("manager-entry", Assert.Single(host.Generations).Descriptor.Id);
        }
        finally
        {
            foreach (var generation in host.Generations)
            {
                await host.UnloadAsync(generation, session, token);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Rejects an incompatible staged API on the manual manager path.</summary>
    [Fact]
    public static async Task ManagerLoad_RejectsIncompatibleStagedApi()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "threadsmith.extension.json"),
                """{"Id":"manager-incompatible","Name":"Incompatible fixture","HostApiVersion":"999.0"}""",
                token);
            host.SetDiscoveryDirectory(root);
            IExtensionManager manager = host;

            var failure = await Assert.ThrowsAsync<ExtensionIncompatibleException>(() => manager.LoadAsync("manager-incompatible", SessionId.New(), token));

            Assert.Contains("host API version '999.0'", failure.Message, StringComparison.Ordinal);
            Assert.Empty(host.Generations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Retains the caller's API constraint when a staged manifest is present.</summary>
    [Fact]
    public static async Task LoadAsync_RejectsIncompatibleCallerApiWithCompatibleStagedManifest()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
            var request = new ExtensionLoadRequest
            {
                ExtensionDirectory = ResolveFixtureOutputDirectory("ConstructionCountExtension"),
                ShadowStagingRoot = root,
                EffectiveTrust = RepositoryTrustLevel.TrustedRead,
                SessionId = SessionId.New(),
                Manifest = new ExtensionManifest
                {
                    Id = "threadsmith.tests.construction-count",
                    Name = "Construction Count Fixture",
                    HostApiVersion = "999.0",
                },
            };

            var failure = await Assert.ThrowsAsync<ExtensionIncompatibleException>(() => host.LoadAsync(request, token));

            Assert.Contains("host API version '999.0'", failure.Message, StringComparison.Ordinal);
            Assert.Empty(host.Generations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Rejects reassigned or removed manifest identities before looking for entry types.</summary>
    [Theory]
    [InlineData(false, "replacement")]
    [InlineData(false, "SELECTED")]
    [InlineData(true, "replacement")]
    [InlineData(true, null)]
    public static async Task LoadAsync_RejectsStagedIdentityChangesBeforeTypeDiscovery(bool useManager, string? stagedId)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
            var manifestPath = Path.Combine(package, "threadsmith.extension.json");
            var session = SessionId.New();
            IExtensionManager manager = host;
            if (useManager)
            {
                await File.WriteAllTextAsync(manifestPath, """{"Id":"selected","Name":"Selected fixture"}""", token);
                host.SetDiscoveryDirectory(package);
                Assert.Equal("selected", Assert.Single(await manager.DiscoverAsync(token)).ExtensionId);
            }

            // There is deliberately no DLL: a load that proceeds past identity validation would
            // fail for a different reason before reaching any extension code.
            if (stagedId is not null)
            {
                await File.WriteAllTextAsync(manifestPath, $$"""{"Id":"{{stagedId}}","Name":"Replacement fixture"}""", token);
            }
            else
            {
                File.Delete(manifestPath);
            }

            var request = new ExtensionLoadRequest
            {
                ExtensionDirectory = package,
                ShadowStagingRoot = Path.Combine(root, "staging"),
                SessionId = session,
                Manifest = new ExtensionManifest { Id = "selected", Name = "Selected fixture" },
            };
            var failure = await Assert.ThrowsAsync<ExtensionIncompatibleException>(() => useManager
                ? manager.LoadAsync("selected", session, token)
                : host.LoadAsync(request, token));

            Assert.Contains("selected extension identity", failure.Message, StringComparison.Ordinal);
            Assert.Empty(host.Generations);
            Assert.Collection(
                events.Published,
                item => Assert.IsType<ExtensionDiscovered>(item),
                item => Assert.IsType<ExtensionLoadFailed>(item));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Accepts caller and staged paths that identify the same nested assembly.</summary>
    [Fact]
    public static async Task LoadAsync_AcceptsEquivalentEntryAssemblyPaths()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        var session = SessionId.New();
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
            var tools = Directory.CreateDirectory(Path.Combine(package, "tools")).FullName;
            const string assemblyName = "Threadsmith.Tests.Fixtures.ConstructionCountExtension.dll";
            File.Copy(Path.Combine(ResolveFixtureOutputDirectory("ConstructionCountExtension"), assemblyName), Path.Combine(tools, assemblyName));
            await File.WriteAllTextAsync(
                Path.Combine(package, "threadsmith.extension.json"),
                $$"""{"Id":"equivalent-entry","Name":"Equivalent entry fixture","EntryAssembly":"tools/{{assemblyName}}","EntryClass":"Threadsmith.Tests.Fixtures.ConstructionCount.ConstructionCountExtension"}""",
                token);
            var request = new ExtensionLoadRequest
            {
                ExtensionDirectory = package,
                ShadowStagingRoot = Path.Combine(root, "staging"),
                EffectiveTrust = RepositoryTrustLevel.TrustedRead,
                SessionId = session,
                Manifest = new ExtensionManifest
                {
                    Id = "equivalent-entry",
                    Name = "Equivalent entry fixture",
                    EntryAssembly = Path.Combine(".", "tools", assemblyName),
                },
            };

            var generation = await host.LoadAsync(request, token);

            Assert.Equal(Path.Combine(generation.StagingPath, "tools", assemblyName), generation.EntryAssemblyPath);
            Assert.Single(generation.Tools);
        }
        finally
        {
            foreach (var generation in host.Generations)
            {
                await host.UnloadAsync(generation, session, token);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Rejects case-only differences in entry selection before opening the assembly.</summary>
    [Fact]
    public static async Task LoadAsync_RejectsCaseOnlyEntryAssemblyConflict()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(package, "threadsmith.extension.json"),
                """{"Id":"fixture","Name":"Fixture","EntryAssembly":"Tool.dll"}""",
                token);
            // Use one placeholder so the test needs neither a real assembly nor a case-sensitive directory.
            await File.WriteAllTextAsync(Path.Combine(package, "Tool.dll"), string.Empty, token);
            var request = new ExtensionLoadRequest
            {
                ExtensionDirectory = package,
                ShadowStagingRoot = Path.Combine(root, "staging"),
                SessionId = SessionId.New(),
                Manifest = new ExtensionManifest { Id = "fixture", Name = "Fixture", EntryAssembly = "tool.dll" },
            };

            var error = await Assert.ThrowsAsync<ExtensionIncompatibleException>(() => host.LoadAsync(request, token));

            Assert.Contains("different extension entry assemblies", error.Message, StringComparison.Ordinal);
            Assert.Empty(host.Generations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Reports a failed shadow copy under the identity selected by the manager.</summary>
    [Fact]
    public static async Task ManagerLoad_ReportsStagingFailureForSelectedExtension()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(package, "threadsmith.extension.json"),
                """{"Id":"missing-package","Name":"Missing package fixture"}""",
                token);
            host.SetDiscoveryDirectory(root);
            IExtensionManager manager = host;
            var selected = Assert.Single(await manager.DiscoverAsync(token));
            Directory.Delete(package, recursive: true);
            var session = SessionId.New();

            var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => manager.LoadAsync(selected.ExtensionId, session, token));

            Assert.Empty(host.Generations);
            var published = events.Published;
            Assert.Equal(2, published.Count);
            var discovered = Assert.IsType<ExtensionDiscovered>(published[0]);
            var failure = Assert.IsType<ExtensionLoadFailed>(published[1]);
            Assert.Equal(session, discovered.SessionId);
            Assert.Equal(session, failure.SessionId);
            Assert.Equal(discovered.ExtensionId, failure.ExtensionId);
            Assert.Equal(error.Message, failure.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Reports direct-load failures before a staged identity is available as a correlated event pair.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task LoadAsync_CorrelatesEarlyFailureWithoutCallerManifest(bool malformedManifest)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory().FullName;
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        try
        {
            var package = Path.Combine(root, "package");
            if (malformedManifest)
            {
                Directory.CreateDirectory(package);
                await File.WriteAllTextAsync(Path.Combine(package, "threadsmith.extension.json"), "{", token);
            }

            var session = SessionId.New();
            var request = new ExtensionLoadRequest
            {
                ExtensionDirectory = package,
                ShadowStagingRoot = Path.Combine(root, "staging"),
                SessionId = session,
            };

            var error = malformedManifest
                ? await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => host.LoadAsync(request, token))
                : (Exception)await Assert.ThrowsAsync<DirectoryNotFoundException>(() => host.LoadAsync(request, token));

            Assert.Empty(host.Generations);
            var published = events.Published;
            Assert.Equal(2, published.Count);
            var discovered = Assert.IsType<ExtensionDiscovered>(published[0]);
            var failure = Assert.IsType<ExtensionLoadFailed>(published[1]);
            Assert.Equal(session, discovered.SessionId);
            Assert.Equal(session, failure.SessionId);
            Assert.Equal(discovered.ExtensionId, failure.ExtensionId);
            Assert.Equal(error.Message, failure.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ExtensionGeneration> LoadConstructionCountGenerationAsync()
    {
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        return await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("ConstructionCountExtension"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = NewStagingRoot(),
        });
    }

    private static string NewStagingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveFixtureOutputDirectory(string fixtureProjectDirName)
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "tests", "Threadsmith.Extensions.Tests", "Fixtures", fixtureProjectDirName, "bin", configuration, "net10.0");
    }
}

/// <summary>A metadata-only tool capability whose <see cref="ExtensionToolDefinition.RequiredApproval"/> is configurable (F3).</summary>
internal sealed class HostPolicyToolCapability : IToolCapability
{
    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Id = "test_host_policy",
        Kind = CapabilityKind.Tool,
        DisplayName = "Host Policy Test Tool",
    };

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; }

    /// <summary>Initializes a new instance of the <see cref="HostPolicyToolCapability"/> class with the supplied approval requirement.</summary>
    /// <param name="approval">The approval requirement the proxy must map.</param>
    public HostPolicyToolCapability(ExtensionApprovalRequirement approval)
    {
        Definition = new ExtensionToolDefinition
        {
            Id = "test_host_policy",
            Version = "1.0",
            Description = "Host policy approval mapping fixture.",
            Category = ExtensionToolCategory.RepositoryInspection,
            InputSchema = new ExtensionToolSchema("None", 1, "{\"type\":\"object\"}"),
            OutputSchema = new ExtensionToolSchema("None", 1, "{\"type\":\"object\"}"),
            RequiredTrust = ExtensionTrustRequirement.None,
            RequiredApproval = approval,
            SideEffect = ExtensionToolSideEffect.ReadOnly,
            Idempotency = ExtensionToolIdempotency.Idempotent,
            SupportsCancellation = true,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context)
    {
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public string? GetExecutable(string argumentsJson)
    {
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("The host policy fixture is metadata-only and is never invoked.");
    }
}
