namespace Threadsmith.Extensions.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Xunit;

/// <summary>Unload failures remain operation outcomes rather than escaping into the host.</summary>
public static class UnloadProcedureTests
{
    /// <summary>An internal deadline retains a live generation and reports the failed unload.</summary>
    [Fact]
    public static async Task DrainTimeoutReturnsBlockedAndPublishesFailure()
    {
        var clock = new FakeTimeProvider();
        var events = new RecordingEventStream();
        var leases = new InvocationLeaseAuthority(NullLogger.Instance, clock);
        var registry = new CapabilityRegistry(leases, NullLogger<CapabilityRegistry>.Instance);
        var extension = new StubExtension();
        var generation = CreateGeneration(extension);
        var budget = new ExtensionInvocationBudget(generation.GenerationId);
        using var held = leases.Acquire(generation.GenerationId, budget, TimeSpan.FromMinutes(1));
        var procedure = CreateProcedure(events, leases, registry, clock);

        // Unload runs synchronously through deadline registration before yielding on the held lease.
        var pending = procedure.UnloadAsync(generation, SessionId.New(), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Equal(ExtensionLifecycleState.Draining, generation.State);
        clock.Advance(TimeSpan.FromSeconds(10));
        var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnloadOutcome.UnloadBlocked, result.Outcome);
        Assert.Equal(ExtensionLifecycleState.UnloadBlocked, generation.State);
        Assert.Same(extension, generation.Instance);
        Assert.False(extension.Deactivated);
        Assert.Equal(1, budget.Reserved);
        Assert.Throws<ExtensionDrainingException>(() => leases.Acquire(generation.GenerationId, budget, TimeSpan.FromMinutes(1)));
        Assert.Contains("in-flight", Assert.Single(events.Published.OfType<ExtensionUnloadFailed>()).Reason, StringComparison.Ordinal);
        Assert.Empty(events.Published.OfType<ExtensionUnloaded>());
    }

    /// <summary>An extension-owned cancellation follows best-effort deactivation handling.</summary>
    [Fact]
    public static async Task UnexpectedDeactivationCancellationDoesNotEscapeUnload()
    {
        var clock = new FakeTimeProvider();
        var events = new RecordingEventStream();
        var leases = new InvocationLeaseAuthority(NullLogger.Instance, clock);
        var registry = new CapabilityRegistry(leases, NullLogger<CapabilityRegistry>.Instance);
        var extension = new StubExtension { CancelDeactivation = true };
        var generation = CreateGeneration(extension);

        var result = await CreateProcedure(events, leases, registry, clock).UnloadAsync(
            generation, SessionId.New(), TestContext.Current.CancellationToken);

        Assert.True(extension.Deactivated);
        Assert.Equal(UnloadOutcome.Unloaded, result.Outcome);
        Assert.Equal(ExtensionLifecycleState.Unloaded, generation.State);
        Assert.Null(generation.Instance);
        Assert.Single(events.Published.OfType<ExtensionUnloaded>());
    }

    /// <summary>Throwing extension callbacks cannot escape the unload boundary or leak their text.</summary>
    [Fact]
    public static async Task LifetimeCancellationCallbackFailureReturnsBlocked()
    {
        var clock = new FakeTimeProvider();
        var events = new RecordingEventStream();
        var leases = new InvocationLeaseAuthority(NullLogger.Instance, clock);
        var registry = new CapabilityRegistry(leases, NullLogger<CapabilityRegistry>.Instance);
        var generation = CreateGeneration(new StubExtension());
        await using var registration = generation.Lifetime.Token.Register(static () => throw new InvalidOperationException("private extension detail"));

        var result = await CreateProcedure(events, leases, registry, clock).UnloadAsync(
            generation, SessionId.New(), TestContext.Current.CancellationToken);

        Assert.Equal(UnloadOutcome.UnloadBlocked, result.Outcome);
        Assert.Equal(ExtensionLifecycleState.UnloadBlocked, generation.State);
        var failure = Assert.Single(events.Published.OfType<ExtensionUnloadFailed>());
        Assert.DoesNotContain("private extension detail", failure.Reason, StringComparison.Ordinal);
        Assert.Empty(events.Published.OfType<ExtensionUnloaded>());
    }

    /// <summary>Failure to notify listeners cannot reverse completed cleanup.</summary>
    [Fact]
    public static async Task UnloadedNotificationFailurePreservesCommittedUnload()
    {
        var attempted = false;
        var events = new RecordingEventStream
        {
            RejectPublication = notification =>
            {
                attempted |= notification is ExtensionUnloaded;
                return notification is ExtensionUnloaded;
            },
        };
        var clock = new FakeTimeProvider();
        var leases = new InvocationLeaseAuthority(NullLogger.Instance, clock);
        var registry = new CapabilityRegistry(leases, NullLogger<CapabilityRegistry>.Instance);
        var generation = CreateGeneration(new StubExtension());
        var acquire = leases.CaptureAdmission(generation.GenerationId);

        var result = await CreateProcedure(events, leases, registry, clock).UnloadAsync(
            generation, SessionId.New(), TestContext.Current.CancellationToken);

        Assert.True(attempted);
        Assert.Equal(UnloadOutcome.Unloaded, result.Outcome);
        Assert.Equal(ExtensionLifecycleState.Unloaded, generation.State);
        Assert.Null(generation.Instance);
        Assert.Empty(events.Published.OfType<ExtensionUnloadFailed>());
        Assert.Throws<ExtensionDrainingException>(() => acquire(new ExtensionInvocationBudget(generation.GenerationId), TimeSpan.Zero));
    }

    /// <summary>Draining can win against an invocation that has not registered its lease yet.</summary>
    [Fact]
    public static async Task DrainRejectsAdmissionThatHasNotRegisteredItsLease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var proceed = new ManualResetEventSlim();
        var clock = new AdmissionClock(proceed, cancellationToken);
        var leases = new InvocationLeaseAuthority(NullLogger.Instance, clock);
        var generation = ExtensionGenerationId.New();
        var budget = new ExtensionInvocationBudget(generation);
        var pending = Task.Run(
            () => Record.Exception(() =>
            {
                using var lease = leases.Acquire(generation, budget, TimeSpan.Zero);
            }),
            cancellationToken);

        try
        {
            await clock.Entered.Task.WaitAsync(cancellationToken);
            leases.BeginDraining(generation);
            Assert.Equal(0, await leases.WaitForDrainAsync(generation, cancellationToken));
        }
        finally
        {
            proceed.Set();
            await pending;
        }

        Assert.IsType<ExtensionDrainingException>(await pending);
        Assert.Equal(0, budget.Reserved);
    }

    /// <summary>An unload retry does not duplicate a deactivation that survived the first deadline.</summary>
    [Fact]
    public static async Task DeactivationTimeoutRetainsCleanupAndRetryJoinsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var events = new RecordingEventStream();
        var leases = new InvocationLeaseAuthority(NullLogger.Instance, clock);
        var registry = new CapabilityRegistry(leases, NullLogger<CapabilityRegistry>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var extension = new StubExtension
        {
            OnDeactivate = async token =>
            {
                Interlocked.Increment(ref calls);
                await using var registration = token.Register(() =>
                {
                    cancellationObserved.TrySetResult();
                    throw new InvalidOperationException("Extension callback failed.");
                });
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            },
        };
        var generation = CreateGeneration(extension);
        var procedure = CreateProcedure(events, leases, registry, clock);
        var session = SessionId.New();

        var first = procedure.UnloadAsync(generation, session, cancellationToken);
        Task<UnloadResult>? retry = null;
        try
        {
            await entered.Task.WaitAsync(cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(UnloadOutcome.UnloadBlocked, (await first.WaitAsync(cancellationToken)).Outcome);
            await cancellationObserved.Task.WaitAsync(cancellationToken);
            Assert.Equal(ExtensionLifecycleState.UnloadBlocked, generation.State);
            Assert.Same(extension, generation.Instance);
            Assert.Contains("cleanup or verification", Assert.Single(events.Published.OfType<ExtensionUnloadFailed>()).Reason, StringComparison.Ordinal);

            retry = procedure.UnloadAsync(generation, session, cancellationToken);
            Assert.False(retry.IsCompleted);
            release.SetResult();
            Assert.Equal(UnloadOutcome.Unloaded, (await retry.WaitAsync(cancellationToken)).Outcome);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Null(generation.Instance);
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (retry is not null)
            {
                await retry;
            }
        }
    }

    /// <summary>A stalled publication cannot prevent a blocked generation from being retried.</summary>
    [Fact]
    public static async Task StalledNotificationsRespectDeadlinesAndAllowRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var drainingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureInDelivery = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;
        var events = new RecordingEventStream
        {
            BeforePublication = (notification, deadline) =>
            {
                Interlocked.Increment(ref deliveries);
                var stall = notification switch
                {
                    ExtensionDraining => drainingStarted.TrySetResult(),
                    ExtensionUnloadFailed => failureInDelivery.TrySetResult(deadline),
                    _ => false,
                };
                return stall ? release.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
            },
        };
        var leases = new InvocationLeaseAuthority(NullLogger.Instance, clock);
        var registry = new CapabilityRegistry(leases, NullLogger<CapabilityRegistry>.Instance);
        var generation = CreateGeneration(new StubExtension());
        using var held = leases.Acquire(generation.GenerationId, new ExtensionInvocationBudget(generation.GenerationId), TimeSpan.Zero);
        var procedure = CreateProcedure(events, leases, registry, clock);
        var session = SessionId.New();

        var first = procedure.UnloadAsync(generation, session, cancellationToken);
        try
        {
            await drainingStarted.Task.WaitAsync(cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(10));
            // Only the final failure publisher supplies this token. Its delivery cannot finish
            // until release is signaled, even when its own deadline expires.
            var outcomeDeadline = await failureInDelivery.Task.WaitAsync(cancellationToken);
            Assert.False(outcomeDeadline.IsCancellationRequested);
            Assert.Equal(ExtensionLifecycleState.UnloadBlocked, generation.State);
            Assert.Equal(1, generation.UnloadGate.CurrentCount);
            held.Dispose();

            // The retry can commit even while the first attempt is delivering its outcome.
            var retry = await procedure.UnloadAsync(generation, session, cancellationToken).WaitAsync(cancellationToken);
            Assert.Equal(UnloadOutcome.Unloaded, retry.Outcome);
            Assert.Equal(2, Volatile.Read(ref deliveries));
            Assert.False(outcomeDeadline.IsCancellationRequested);
            Assert.False(first.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(UnloadOutcome.UnloadBlocked, (await first.WaitAsync(cancellationToken)).Outcome);
            Assert.True(outcomeDeadline.IsCancellationRequested);
            Assert.Empty(events.Published);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(cancellationToken);
        }
    }

    private static ExtensionGeneration CreateGeneration(IThreadsmithExtension extension)
    {
        // No loader, real ALC, GC verification, staged files, or shared directories participate.
        return new ExtensionGeneration(
            ExtensionId.New(),
            ExtensionGenerationId.New(),
            extension.Descriptor,
            null,
            null!,
            extension,
            Path.Combine(Path.GetTempPath(), "threadsmith-unused-unload-" + Guid.NewGuid().ToString("N")),
            "unused")
        {
            State = ExtensionLifecycleState.Active,
        };
    }

    private static UnloadProcedure CreateProcedure(
        RecordingEventStream events,
        InvocationLeaseAuthority leases,
        CapabilityRegistry registry,
        TimeProvider clock)
    {
        return new UnloadProcedure(events, leases, registry, new UnloadBlockerCatalog(NullLogger.Instance), NullLogger<UnloadProcedure>.Instance, clock);
    }

    private sealed class AdmissionClock : TimeProvider
    {
        private readonly ManualResetEventSlim _proceed;
        private readonly CancellationToken _cancellationToken;

        public AdmissionClock(ManualResetEventSlim proceed, CancellationToken cancellationToken)
        {
            _proceed = proceed;
            _cancellationToken = cancellationToken;
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            Entered.TrySetResult();
            _proceed.Wait(_cancellationToken);
            return DateTimeOffset.UnixEpoch;
        }
    }

    private sealed class StubExtension : IThreadsmithExtension
    {
        public ExtensionDescriptor Descriptor { get; } = new() { Id = "unload-fixture", Name = "Unload fixture" };

        internal bool CancelDeactivation { get; init; }

        internal Func<CancellationToken, ValueTask>? OnDeactivate { get; init; }

        internal bool Deactivated { get; private set; }

        public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
        {
            Deactivated = true;
            return CancelDeactivation
                ? ValueTask.FromException(new OperationCanceledException("extension-owned cancellation"))
                : OnDeactivate is { } deactivate ? deactivate(cancellationToken) : ValueTask.CompletedTask;
        }
    }
}
