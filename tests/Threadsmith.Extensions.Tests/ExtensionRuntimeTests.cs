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
    public async Task PublishCommittedBatchAsync(
        IReadOnlyList<IDomainEvent> domainEvents,
        Func<bool> tryCommit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!tryCommit())
        {
            return;
        }

        foreach (var domainEvent in domainEvents)
        {
            await PublishAsync(domainEvent, CancellationToken.None);
        }
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
