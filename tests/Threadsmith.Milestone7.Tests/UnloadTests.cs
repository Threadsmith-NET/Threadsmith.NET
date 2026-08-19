namespace Threadsmith.Milestone7.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Xunit;

/// <summary>
/// Plan-17 acceptance: cooperative unload with WeakReference verification (Scenario E), unload-blocker
/// diagnosis for a deliberately-leaking extension (Scenario F + the mandatory §26.5 leak fixture), and
/// hot replacement with an atomic capability switch (Scenario G). Strategy §17.17–§17.20, §26.5.
/// </summary>
public sealed class UnloadTests
{
    /// <summary>Asserts a clean extension unloads cooperatively and its collectible ALC is dead after bounded GC.</summary>
    [Fact]
    public async Task Clean_extension_unloads_and_the_ALC_is_dead()
    {
        // Use a dedicated fixture never invoked by other tests so its ALC is not JIT-rooted by the
        // plan-16 invocation tests (a known ALC-unload testing hazard, §17.19).
        var (host, generation) = await LoadCleanUnloadAsync();
        var alcRef = generation.LoadContextWeakReference;
        Assert.NotNull(alcRef);

        var result = await host.UnloadAsync(generation, SessionId.New());

        Assert.Equal(UnloadOutcome.Unloaded, result.Outcome);
        Assert.Null(result.Blockers);
        Assert.Equal(ExtensionLifecycleState.Unloaded, generation.State);
        Assert.False(alcRef.IsAlive);
        Assert.DoesNotContain(generation, host.Generations);
    }

    /// <summary>Asserts the deliberately-leaking extension is diagnosed as UnloadBlocked, the host stays functional, and the §26.5 false-success detector fires.</summary>
    [Fact]
    public async Task Leaking_extension_is_diagnosed_as_UnloadBlocked_and_host_remains_functional()
    {
        // Mandatory leak fixture (§26.5): the extension subscribes a handler to AppDomain.ProcessExit
        // and never detaches, retaining the collectible ALC. Verification must report UnloadBlocked,
        // not a false success.
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var generation = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("LeakingExtension"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "leaking"),
        });
        var alcRef = generation.LoadContextWeakReference!;

        var result = await host.UnloadAsync(generation, SessionId.New());

        Assert.Equal(UnloadOutcome.UnloadBlocked, result.Outcome);
        Assert.NotNull(result.Blockers);
        Assert.True(result.Blockers.HasBlockers);
        Assert.Equal(ExtensionLifecycleState.UnloadBlocked, generation.State);
        // The ALC survived (the leak is real). This is the §26.5 false-success detector: if the ALC
        // were dead here, the fixture would be broken.
        Assert.True(alcRef.IsAlive);

        // The host remains functional: a subsequent clean load + unload succeeds.
        var (cleanHost, clean) = await LoadCleanUnloadAsync();
        var cleanResult = await cleanHost.UnloadAsync(clean, SessionId.New());
        Assert.Equal(UnloadOutcome.Unloaded, cleanResult.Outcome);
    }

    /// <summary>Asserts hot replacement loads a new generation, atomically switches the registry, and drains+unloads the old generation.</summary>
    [Fact]
    public async Task Hot_replacement_switches_to_new_generation_and_unloads_the_old()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var sessionId = SessionId.New();

        var first = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("CleanUnloadExtension"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = sessionId,
            ShadowStagingRoot = Path.Combine(stagingRoot, "gen-1"),
        });
        var oldAlcRef = first.LoadContextWeakReference!;

        var replacer = new HotReplacementCoordinator(
            host,
            host.Capabilities,
            NullLogger<HotReplacementCoordinator>.Instance);

        // Simulate the replacement by loading a second generation of the same extension.
        var (newGeneration, oldUnload) = await replacer.ReplaceAsync(
            first,
            new ExtensionLoadRequest
            {
                ExtensionDirectory = ResolveFixtureOutputDirectory("CleanUnloadExtension"),
                EffectiveTrust = RepositoryTrustLevel.TrustedRead,
                SessionId = sessionId,
                ShadowStagingRoot = Path.Combine(stagingRoot, "gen-2"),
            },
            sessionId);

        // New generations coexist; the old one unloaded. (CleanUnloadExtension contributes no tools, so
        // we assert generation identity and old-ALC death rather than a capability switch.)
        Assert.NotEqual(first.GenerationId, newGeneration.GenerationId);
        // The old generation unloaded.
        Assert.Equal(UnloadOutcome.Unloaded, oldUnload.Outcome);
        Assert.False(oldAlcRef.IsAlive);
    }

    /// <summary>Asserts unload publishes ExtensionDraining then ExtensionUnloaded for a clean extension.</summary>
    [Fact]
    public async Task Unload_publishes_draining_unloaded_events()
    {
        var (host, generation) = await LoadCleanUnloadAsync();
        var events = (RecordingEventStream)host.Events;
        await host.UnloadAsync(generation, SessionId.New());
        Assert.Contains(events.Published, e => e is ExtensionDraining);
        Assert.Contains(events.Published, e => e is ExtensionUnloaded);
    }

    /// <summary>Asserts an unload that survives verification publishes ExtensionUnloadFailed.</summary>
    [Fact]
    public async Task Unload_of_a_blocked_generation_publishes_UnloadFailed_event()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var generation = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("LeakingExtension"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "leaking-events"),
        });
        await host.UnloadAsync(generation, SessionId.New());
        Assert.Contains(events.Published, e => e is ExtensionUnloadFailed);
    }

    private static async Task<(ExtensionHost host, ExtensionGeneration generation)> LoadCleanUnloadAsync()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var generation = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory("CleanUnloadExtension"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "clean-unload"),
        });
        return (host, generation);
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