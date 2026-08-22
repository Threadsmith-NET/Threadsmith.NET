namespace Threadsmith.Extensions.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Xunit;

/// <summary>
/// Plan-15 acceptance: drop-in reflection discovery, collectible ALC loading, shared-contract
/// resolution from default, duplicate-contract rejection, conflicting-dep isolation, lifecycle,
/// and activation-failure isolation (strategy §17.6, §17.10, §17.11, §17.16, §17.24, §17.26).
/// </summary>
public sealed class ExtensionRuntimeTests
{
    /// <summary>Loads the sample extension and asserts it activates with one tool and one model-preference contributor.</summary>
    [Fact]
    public async Task LoadAsync_loads_sample_extension_and_collects_capabilities()
    {
        var (host, sessionId) = await LoadSampleAsync();
        _ = sessionId;
        var generation = Assert.Single(host.Generations);
        Assert.Equal(ExtensionLifecycleState.Active, generation.State);
        Assert.Equal("threadsmith.sample.minimal-tool", generation.Descriptor.Id);
        Assert.Single(generation.Tools);
        Assert.Equal("sample_echo", generation.Tools[0].Definition.Id);
        Assert.Single(generation.ModelPreferenceContributors);
    }

    /// <summary>Loads two generations of the same extension and asserts they occupy independent collectible load contexts.</summary>
    [Fact]
    public async Task LoadAsync_loads_two_generations_of_same_extension_in_independent_ALCs()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var sampleDir = ResolveSampleOutputDirectory();
        var request = new ExtensionLoadRequest
        {
            ExtensionDirectory = sampleDir,
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "a"),
        };
        var first = await host.LoadAsync(request);
        request = request with { ShadowStagingRoot = Path.Combine(stagingRoot, "b") };
        var second = await host.LoadAsync(request);
        Assert.NotEqual(first.GenerationId, second.GenerationId);
        Assert.NotSame(first.LoadContext, second.LoadContext);
        Assert.Equal(2, host.Generations.Count);
    }

    /// <summary>Asserts that an extension bundling the shared contract assembly is rejected with DuplicateContractAssemblyException.</summary>
    [Fact]
    public async Task LoadAsync_rejects_duplicate_contract_assembly()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var badDir = ResolveFixtureOutputDirectory("BadContractExtension");
        var request = new ExtensionLoadRequest
        {
            ExtensionDirectory = badDir,
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "bad"),
        };
        var ex = await Assert.ThrowsAsync<DuplicateContractAssemblyException>(
            () => host.LoadAsync(request));
        Assert.Contains("Threadsmith.Extensions.Abstractions", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Generations);
    }

    /// <summary>Asserts that an extension whose activation throws is isolated and the host stays operational for a subsequent clean load.</summary>
    [Fact]
    public async Task LoadAsync_activation_failure_keeps_host_operational()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var throwingDir = ResolveFixtureOutputDirectory("ThrowingExtension");
        var request = new ExtensionLoadRequest
        {
            ExtensionDirectory = throwingDir,
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "throwing"),
        };
        await Assert.ThrowsAsync<ExtensionActivationException>(() => host.LoadAsync(request));
        Assert.Empty(host.Generations);

        // The host remains operational: a subsequent clean load succeeds.
        var clean = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveSampleOutputDirectory(),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "clean"),
        });
        Assert.Equal(ExtensionLifecycleState.Active, clean.State);
    }

    /// <summary>Asserts that two extensions bundling same-named private dependencies of different versions each resolve their own copy per ALC.</summary>
    [Fact]
    public async Task LoadAsync_isolates_conflicting_private_dependencies_per_ALC()
    {
        // Two extensions each bundle a same-named private assembly (Threadsmith.PrivateLib) with
        // different contents. Each must load in its own collectible ALC and resolve its own copy
        // (§17.10, §17.26). Invoking each tool returns the version its ALC resolved.
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var genA = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("ConflictingDepA"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "depA"),
        });
        var genB = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("ConflictingDepB"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "depB"),
        });
        Assert.NotSame(genA.LoadContext, genB.LoadContext);
        var resultA = await genA.Tools[0].ExecuteAsync("{}", MakeContext(), CancellationToken.None);
        var resultB = await genB.Tools[0].ExecuteAsync("{}", MakeContext(), CancellationToken.None);
        Assert.Contains("\"1.0\"", resultA.ResultJson, StringComparison.Ordinal);
        Assert.Contains("\"2.0\"", resultB.ResultJson, StringComparison.Ordinal);
    }

    /// <summary>Asserts the lifecycle state machine rejects illegal transitions and accepts the legal path to Active.</summary>
    [Fact]
    public void Lifecycle_rejects_illegal_transitions()
    {
        var lifecycle = new ExtensionLifecycle();
        Assert.Equal(ExtensionLifecycleState.Discovered, lifecycle.State);
        Assert.Throws<ExtensionLifecycleException>(
            () => lifecycle.TransitionTo(ExtensionLifecycleState.Active));
        Assert.Equal(ExtensionLifecycleState.Discovered, lifecycle.State);
        Assert.False(lifecycle.TryTransitionTo(ExtensionLifecycleState.Unloaded));
        lifecycle.TransitionTo(ExtensionLifecycleState.Validating);
        lifecycle.TransitionTo(ExtensionLifecycleState.Loading);
        lifecycle.TransitionTo(ExtensionLifecycleState.Activating);
        lifecycle.TransitionTo(ExtensionLifecycleState.Active);
        Assert.Equal(ExtensionLifecycleState.Active, lifecycle.State);
    }

    /// <summary>Asserts that loading publishes ExtensionDiscovered and ExtensionActivated events.</summary>
    [Fact]
    public async Task LoadAsync_publishes_discovered_and_activated_events()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveSampleOutputDirectory(),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "sample"),
        });
        Assert.Contains(events.Published, e => e is ExtensionDiscovered);
        Assert.Contains(events.Published, e => e is ExtensionActivated);
    }

    private static async Task<(ExtensionHost host, SessionId sessionId)> LoadSampleAsync()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var sessionId = SessionId.New();
        await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveSampleOutputDirectory(),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = sessionId,
            ShadowStagingRoot = Path.Combine(stagingRoot, "sample"),
        });
        return (host, sessionId);
    }

    private static string NewStagingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ExtensionToolInvocationContext MakeContext()
    {
        return new() { RequestedBy = "test" };
    }

    private static string ResolveSampleOutputDirectory()
    {
        return ResolveProjectOutput("MinimalToolExtension", "Threadsmith.SampleExtensions.MinimalTool");
    }

    private static string ResolveFixtureOutputDirectory(string fixtureProjectDirName)
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "tests", "Threadsmith.Extensions.Tests", "Fixtures", fixtureProjectDirName, "bin", configuration, "net10.0");
    }

    private static string ResolveProjectOutput(string projectDir, string assemblyName)
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "samples", "extensions", projectDir, "bin", configuration, "net10.0");
    }
}

/// <summary>A minimal recording domain event stream for isolated extension host tests.</summary>
internal sealed class RecordingEventStream : IDomainEventStream
{
    private readonly List<IDomainEvent> _published = [];
    private readonly Lock _gate = new();

    /// <summary>Events published in order.</summary>
    public IReadOnlyList<IDomainEvent> Published
    {
        get
        {
            lock (_gate)
            {
                return [.. _published];
            }
        }
    }

    /// <inheritdoc />
    public IDomainEventSubscription Subscribe(Func<IDomainEvent, CancellationToken, Task> handler, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new NullSubscription();
    }

    /// <inheritdoc />
    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        lock (_gate)
        {
            _published.Add(domainEvent);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private sealed class NullSubscription : IDomainEventSubscription
    {
        public ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}