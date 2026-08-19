namespace Threadsmith.Milestone7.Tests;

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
    /// <summary>Asserts the host constructs the extension exactly once during load (F2).</summary>
    /// <remarks>The count is observed through the extension's own tool (executed in the collectible ALC) so the
    /// test reads the ALC's copy of the static counter, not the default-context type reference.</remarks>
    [Fact]
    public async Task LoadAsync_constructs_the_extension_exactly_once()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        ExtensionGeneration generation = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("ConstructionCountExtension"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "construction-count"),
        });
        Assert.Equal(ExtensionLifecycleState.Active, generation.State);
        Assert.Single(generation.Tools);

        // The tool runs in the extension's collectible ALC and reports the count that the ALC's copy of
        // the static counter recorded. A throwaway descriptor-reading prototype (the F2 bug) would have
        // run the constructor twice before the tool executes, reporting 2.
        ExtensionToolResult result = await generation.Tools[0].ExecuteAsync("{}", MakeToolContext(), CancellationToken.None);
        Assert.NotNull(result.ResultJson);
        Assert.Contains("\"count\":1", result.ResultJson, StringComparison.Ordinal);
    }

    /// <summary>Asserts <see cref="ExtensionApprovalRequirement.HostPolicy"/> maps to <see cref="ApprovalLevel.HostPolicy"/> (F3).</summary>
    [Fact]
    public async Task CapabilityProxy_maps_host_policy_approval_faithfully()
    {
        ExtensionGeneration generation = await LoadConstructionCountGenerationAsync();
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

    /// <summary>Asserts repeated leases on the same thread/generation are all tracked — no LeaseId collisions (F6).</summary>
    [Fact]
    public async Task Repeated_leases_on_the_same_thread_are_all_tracked()
    {
        var leaseAuthority = new InvocationLeaseAuthority(NullLogger.Instance);
        ExtensionGenerationId generationId = ExtensionGenerationId.New();
        var budget = new ExtensionInvocationBudget(generationId, maxInvocationsPerTurn: 16);
        var leases = new List<IInvocationLease>();
        for (var i = 0; i < 8; i++)
        {
            // Sequential acquires reuse the same managed thread; the old hash-based LeaseId collided here,
            // so _held.TryAdd would drop all but the first lease and under-count InFlight.
            leases.Add(leaseAuthority.Acquire(generationId, budget, TimeSpan.FromSeconds(30)));
        }

        leaseAuthority.BeginDraining(generationId);

        // A pre-cancelled token makes WaitForDrainAsync return book.InFlight immediately (it never
        // reaches the cancellable Task.Delay), exposing the in-flight count while all 8 leases are held.
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();
        var inFlightWhileHeld = await leaseAuthority.WaitForDrainAsync(generationId, alreadyCancelled.Token);
        Assert.Equal(8, inFlightWhileHeld);

        foreach (IInvocationLease lease in leases)
        {
            lease.Dispose();
        }

        // After releasing all, the drain wait reports zero immediately.
        var inFlightAfterRelease = await leaseAuthority.WaitForDrainAsync(generationId, CancellationToken.None);
        Assert.Equal(0, inFlightAfterRelease);
    }

    /// <summary>Asserts a load failure publishes <see cref="ExtensionLoadFailed"/>, not <see cref="ExtensionUnloadFailed"/> (F11).</summary>
    [Fact]
    public async Task Load_failure_publishes_ExtensionLoadFailed_not_UnloadFailed()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        await Assert.ThrowsAsync<ExtensionActivationException>(() => host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("ThrowingExtension"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "throwing"),
        }));
        Assert.Contains(events.Published, e => e is ExtensionLoadFailed);
        Assert.DoesNotContain(events.Published, e => e is ExtensionUnloadFailed);
    }

    /// <summary>Asserts concurrent DiscoverAsync and LoadAsync-by-id do not observe a torn discovered map (F5).</summary>
    [Fact]
    public async Task Concurrent_discover_and_load_by_id_do_not_throw_or_return_stale()
    {
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        host.SetDiscoveryDirectory(ResolveFixtureOutputDirectory("ConstructionCountExtension"));

        var tasks = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(() => host.DiscoverAsync(CancellationToken.None)));
            tasks.Add(Task.Run(async () =>
            {
                // LoadAsync-by-id refreshes discovery internally; under the old unsynchronized map a
                // concurrent Discover could clear/repopulate mid-read and throw or return null.
                ExtensionSummary? summary = await ((IExtensionManager)host).LoadAsync(
                    "threadsmith.tests.construction-count",
                    SessionId.New());
                Assert.NotNull(summary);
                Assert.True(summary.IsLoaded);
                // Unload so a later iteration can reload (clean ALC lifecycle).
                await ((IExtensionManager)host).UnloadAsync(
                    "threadsmith.tests.construction-count",
                    SessionId.New());
            }));
        }

        await Task.WhenAll(tasks);
        // After the storm, a fresh discover reports the extension as discovered (not loaded, since each
        // load was paired with an unload).
        IReadOnlyList<ExtensionSummary> summaries = await host.DiscoverAsync();
        Assert.Contains(summaries, s => s.ExtensionId == "threadsmith.tests.construction-count");
    }

    private static ExtensionToolInvocationContext MakeToolContext()
    {
        return new() { RequestedBy = "test" };
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
        return Path.Combine(repoRoot, "tests", "Threadsmith.Milestone7.Tests", "Fixtures", fixtureProjectDirName, "bin", configuration, "net10.0");
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