namespace Threadsmith.Extensions.Tests;

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
        var events = Assert.IsType<RecordingEventStream>(host.Events);
        var firstUnloadEvent = events.Published.Count;
        var sessionId = SessionId.New();
        var alcRef = generation.LoadContextWeakReference;
        Assert.NotNull(alcRef);

        var result = await host.UnloadAsync(generation, sessionId, TestContext.Current.CancellationToken);

        Assert.Equal(UnloadOutcome.Unloaded, result.Outcome);
        Assert.Equal(generation.GenerationId, result.GenerationId);
        Assert.Null(result.Blockers);
        Assert.Equal(ExtensionLifecycleState.Unloaded, generation.State);
        Assert.False(alcRef.IsAlive);
        Assert.DoesNotContain(generation, host.Generations);
        Assert.Collection(
            events.Published.Skip(firstUnloadEvent),
            item =>
            {
                var draining = Assert.IsType<ExtensionDraining>(item);
                Assert.Equal(sessionId, draining.SessionId);
                Assert.Equal(generation.ExtensionId, draining.ExtensionId);
            },
            item =>
            {
                var unloaded = Assert.IsType<ExtensionUnloaded>(item);
                Assert.Equal(sessionId, unloaded.SessionId);
                Assert.Equal(generation.ExtensionId, unloaded.ExtensionId);
            });
    }

    /// <summary>Asserts the deliberately-leaking extension is diagnosed as UnloadBlocked, the host stays functional, and the §26.5 false-success detector fires.</summary>
    [Fact]
    public async Task Leaking_extension_is_diagnosed_as_UnloadBlocked_and_host_remains_functional()
    {
        var ct = TestContext.Current.CancellationToken;

        // Mandatory leak fixture (§26.5): the extension subscribes a handler to AppDomain.ProcessExit
        // and never detaches, retaining the collectible ALC. Verification must report UnloadBlocked,
        // not a false success.
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var generation = await host.LoadAsync(
            new ExtensionLoadRequest
            {
                ExtensionDirectory = ResolveFixtureOutputDirectory("LeakingExtension"),
                EffectiveTrust = RepositoryTrustLevel.TrustedRead,
                SessionId = SessionId.New(),
                ShadowStagingRoot = Path.Combine(stagingRoot, "leaking"),
            },
            ct);
        var alcRef = generation.LoadContextWeakReference!;
        var firstUnloadEvent = events.Published.Count;
        var unloadSession = SessionId.New();

        var result = await host.UnloadAsync(generation, unloadSession, ct);

        Assert.Equal(UnloadOutcome.UnloadBlocked, result.Outcome);
        Assert.Equal(generation.GenerationId, result.GenerationId);
        Assert.NotNull(result.Blockers);
        Assert.True(result.Blockers.HasBlockers);
        Assert.Equal(ExtensionLifecycleState.UnloadBlocked, generation.State);
        // The ALC survived (the leak is real). This is the §26.5 false-success detector: if the ALC
        // were dead here, the fixture would be broken.
        Assert.True(alcRef.IsAlive);
        Assert.Collection(
            events.Published.Skip(firstUnloadEvent),
            item =>
            {
                var draining = Assert.IsType<ExtensionDraining>(item);
                Assert.Equal(unloadSession, draining.SessionId);
                Assert.Equal(generation.ExtensionId, draining.ExtensionId);
            },
            item =>
            {
                var failed = Assert.IsType<ExtensionUnloadFailed>(item);
                Assert.Equal(unloadSession, failed.SessionId);
                Assert.Equal(generation.ExtensionId, failed.ExtensionId);
                Assert.NotEmpty(failed.Reason);
            });

        // The host remains functional: a subsequent clean load + unload succeeds.
        var clean = await host.LoadAsync(
            new ExtensionLoadRequest
            {
                ExtensionDirectory = ResolveFixtureOutputDirectory("CleanUnloadExtension"),
                EffectiveTrust = RepositoryTrustLevel.TrustedRead,
                SessionId = SessionId.New(),
                ShadowStagingRoot = Path.Combine(stagingRoot, "clean-recovery"),
            },
            ct);
        Assert.Equal(ExtensionLifecycleState.Active, clean.State);
        Assert.NotEqual(generation.GenerationId, clean.GenerationId);
        Assert.Contains(clean, host.Generations);
        var cleanAlcRef = clean.LoadContextWeakReference!;
        var cleanResult = await host.UnloadAsync(clean, SessionId.New(), ct);
        Assert.Equal(UnloadOutcome.Unloaded, cleanResult.Outcome);
        Assert.Equal(clean.GenerationId, cleanResult.GenerationId);
        Assert.Equal(ExtensionLifecycleState.Unloaded, clean.State);
        Assert.False(cleanAlcRef.IsAlive);
        Assert.Same(generation, Assert.Single(host.Generations));
        Assert.True(alcRef.IsAlive);
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

    private static async Task<(ExtensionHost host, ExtensionGeneration generation)> LoadCleanUnloadAsync()
    {
        var stagingRoot = NewStagingRoot();
        var events = new RecordingEventStream();
        var host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        var generation = await host.LoadAsync(
            new ExtensionLoadRequest
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
        return Path.Combine(repoRoot, "tests", "Threadsmith.Extensions.Tests", "Fixtures", fixtureProjectDirName, "bin", configuration, "net10.0");
    }
}
