namespace Threadsmith.Extensions.Runtime;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;

/// <summary>Outcome of an unload attempt.</summary>
public enum UnloadOutcome
{
    /// <summary>The ALC unloaded and is dead.</summary>
    Unloaded,

    /// <summary>Unload could not complete safely; the generation remains retained for diagnostics.</summary>
    UnloadBlocked,
}

/// <summary>Result of a cooperative unload with verification.</summary>
public sealed record UnloadResult
{
    /// <summary>The generation that was unloaded.</summary>
    public required ExtensionGenerationId GenerationId { get; init; }

    /// <summary>The outcome.</summary>
    public required UnloadOutcome Outcome { get; init; }

    /// <summary>The blocker report when <see cref="Outcome"/> is <see cref="UnloadOutcome.UnloadBlocked"/>.</summary>
    public UnloadBlockerReport? Blockers { get; init; }

    /// <summary>The drain wait duration.</summary>
    public TimeSpan DrainWait { get; init; }

    /// <summary>The total unload duration.</summary>
    public TimeSpan TotalDuration { get; init; }
}

/// <summary>
/// Orchestrates the cooperative unload procedure (strategy §17.17): stop new leases → drain in-flight →
/// deactivate → remove registrations → dispose extension-local services → Unload → WeakReference verify
/// (§17.19). Emits <see cref="ExtensionDraining"/>, <see cref="ExtensionUnloaded"/>, and
/// <see cref="ExtensionUnloadFailed"/> events.
/// </summary>
public sealed class UnloadProcedure
{
    private const int _gcRetries = 3;
    private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly Lock _collectionGate = new();
    private static Task? _collectionPass;

    private readonly IDomainEventStream _events;
    private readonly InvocationLeaseAuthority _leaseAuthority;
    private readonly ICapabilityRegistry _capabilities;
    private readonly UnloadBlockerCatalog _blockerCatalog;
    private readonly ILogger<UnloadProcedure> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="UnloadProcedure"/> class.</summary>
    public UnloadProcedure(
        IDomainEventStream events,
        InvocationLeaseAuthority leaseAuthority,
        ICapabilityRegistry capabilities,
        UnloadBlockerCatalog blockerCatalog,
        ILogger<UnloadProcedure> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(leaseAuthority);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(blockerCatalog);
        ArgumentNullException.ThrowIfNull(logger);
        _events = events;
        _leaseAuthority = leaseAuthority;
        _capabilities = capabilities;
        _blockerCatalog = blockerCatalog;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Cooperatively unloads a generation with WeakReference verification.</summary>
    /// <param name="generation">The generation to unload.</param>
    /// <param name="sessionId">The session owning the operation.</param>
    /// <param name="cancellationToken">A token that cancels the wait before unload commits.</param>
    /// <returns>The unload result with outcome and blocker diagnostics.</returns>
    public async Task<UnloadResult> UnloadAsync(
        ExtensionGeneration generation,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        await generation.UnloadGate.WaitAsync(cancellationToken);
        var startedAt = _timeProvider.GetUtcNow();
        UnloadResult result;
        IDomainEvent notification;
        try
        {
            if (generation.State == ExtensionLifecycleState.Unloaded)
            {
                return new UnloadResult { GenerationId = generation.GenerationId, Outcome = UnloadOutcome.Unloaded };
            }

            (result, notification) = await UnloadCoreAsync(generation, sessionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Admission has closed, so never leave a canceled unload reporting Active.
            generation.State = ExtensionLifecycleState.UnloadBlocked;
            throw;
        }
        catch (Exception exception)
        {
            // Extension callbacks and internal deadlines are operation failures, not host shutdown.
            generation.State = ExtensionLifecycleState.UnloadBlocked;
            var reason = exception switch
            {
                UnloadDeadlineException timeout => timeout.Message,
                _ => "Extension cleanup failed. The extension remains blocked; a restart may be necessary.",
            };
            _logger.LogError(
                "Extension {ExtensionId} generation {GenerationId} could not unload ({FailureType}).",
                generation.ExtensionId,
                generation.GenerationId,
                exception.GetType().Name);
            notification = new ExtensionUnloadFailed(sessionId, _timeProvider.GetUtcNow(), generation.ExtensionId, reason);
            result = new UnloadResult
            {
                GenerationId = generation.GenerationId,
                Outcome = UnloadOutcome.UnloadBlocked,
                TotalDuration = _timeProvider.GetUtcNow() - startedAt,
                Blockers = new UnloadBlockerReport
                {
                    GenerationId = generation.GenerationId,
                    ExtensionId = generation.ExtensionId,
                    Blockers = [new UnloadBlocker { Category = "incomplete-cleanup", Description = reason }],
                },
            };
        }
        finally
        {
            generation.UnloadGate.Release();
        }

        // State is committed. A retry need not wait for delivery of an earlier attempt's diagnostic.
        using var deliveryDeadline = new CancellationTokenSource(_cleanupTimeout, _timeProvider);
        await PublishNotificationAsync(notification, generation, deliveryDeadline.Token);
        return result;
    }

    /// <summary>Retires a successfully activated generation that never reached publication.
    /// There are no admitted calls to drain. Collection cannot be verified while its loader holds it.</summary>
    internal async Task<bool> RollbackActivationAsync(ExtensionGeneration generation)
    {
        var complete = true;
        using var cleanupCancellation = new CancellationTokenSource(_cleanupTimeout, _timeProvider);
        try
        {
            // Cancel before deactivation so activation-owned workers can stop independently of
            // the already-cancelled load caller. Bound cancellation callbacks as well as deactivation.
            await generation.Lifetime.CancelAsync().WaitAsync(cleanupCancellation.Token);
            generation.State = ExtensionLifecycleState.Deactivating;
            complete = await StartDeactivationAsync(generation, cleanupCancellation.Token).WaitAsync(cleanupCancellation.Token);
        }
        catch (Exception)
        {
            // Best effort, just like normal deactivation. Do not replace the original load failure,
            // leak extension exception text, or claim that non-cooperative code has stopped.
            complete = false;
        }
        finally
        {
            generation.ClearCapabilities();
            generation.Instance = null;
            try
            {
                generation.LoadContext?.Unload();
            }
            catch (Exception)
            {
                complete = false;
            }

            generation.LoadContext = null;
            generation.State = ExtensionLifecycleState.Unloading;
        }

        return complete;
    }

    private async Task<(UnloadResult Result, IDomainEvent Notification)> UnloadCoreAsync(
        ExtensionGeneration generation,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();

        // 1. Begin draining: block all new invocation leases.
        _leaseAuthority.BeginDraining(generation.GenerationId);
        generation.State = ExtensionLifecycleState.Draining;

        // 2. Drain in-flight leases while delivering the notification within the same budget.
        // A stalled listener must not prevent cleanup once all leases have been released.
        var drainStart = _timeProvider.GetUtcNow();
        using (var drainDeadline = new CancellationTokenSource(_drainTimeout, _timeProvider))
        using (var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, drainDeadline.Token))
        {
            var notification = PublishNotificationAsync(
                new ExtensionDraining(sessionId, _timeProvider.GetUtcNow(), generation.ExtensionId),
                generation,
                drainCts.Token);
            try
            {
                try
                {
                    if (await _leaseAuthority.WaitForDrainAsync(generation.GenerationId, drainCts.Token) != 0)
                    {
                        // A wait started after cancellation returns the current lease count.
                        drainCts.Token.ThrowIfCancellationRequested();
                    }
                }
                finally
                {
                    await notification;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (drainDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Revoke future credential access without putting this attempt back behind callbacks.
                generation.SecretRetirementCompletion ??= ObserveCleanup(generation.SecretRetirement.CancelAsync());
                throw new UnloadDeadlineException("Unload timed out waiting for in-flight invocations. The extension remains blocked; retry after its operations stop.");
            }
        }

        var drainWait = _timeProvider.GetUtcNow() - drainStart;
        using var cleanupDeadline = new CancellationTokenSource(_cleanupTimeout, _timeProvider);
        using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cleanupDeadline.Token);
        var weakRef = generation.LoadContextWeakReference;
        bool dead;
        try
        {
            await ReleaseGenerationAsync(generation, cleanupCts.Token);
            dead = await VerifyDeadAsync(weakRef, cleanupCts.Token);
        }
        catch (OperationCanceledException) when (cleanupDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new UnloadDeadlineException("Unload timed out waiting for cleanup or verification. The extension remains blocked; retry after cleanup finishes.");
        }

        var total = _timeProvider.GetUtcNow() - startedAt;

        if (dead)
        {
            ShadowCopier.Discard(generation.StagingPath);
            _leaseAuthority.RemoveBook(generation.GenerationId);

            // Cleanup is committed. Delivery is diagnostic and must not change the outcome.
            generation.State = ExtensionLifecycleState.Unloaded;
            return (
                new UnloadResult
                {
                    GenerationId = generation.GenerationId,
                    Outcome = UnloadOutcome.Unloaded,
                    DrainWait = drainWait,
                    TotalDuration = total,
                },
                new ExtensionUnloaded(sessionId, _timeProvider.GetUtcNow(), generation.ExtensionId));
        }

        // 8. Survived: diagnose blockers and report honestly (§17.19, §30.9). The leak keeps the ALC
        //    alive, so re-resolve it from the WeakReference to scan for retained handlers.
        generation.State = ExtensionLifecycleState.UnloadBlocked;
        var survivedAlc = weakRef?.Target as ExtensionLoadContext;
        var blockers = _blockerCatalog.Inspect(generation, survivedAlc);
        _logger.LogError(
            "Extension {ExtensionId} generation {GenerationId} did not unload: {BlockerCount} blocker(s) found. A restart may be necessary.",
            generation.ExtensionId,
            generation.GenerationId,
            blockers.Blockers.Count);
        return (
            new UnloadResult
            {
                GenerationId = generation.GenerationId,
                Outcome = UnloadOutcome.UnloadBlocked,
                Blockers = blockers,
                DrainWait = drainWait,
                TotalDuration = total,
            },
            new ExtensionUnloadFailed(
                sessionId,
                _timeProvider.GetUtcNow(),
                generation.ExtensionId,
                $"Unload verification found {blockers.Blockers.Count} retained-reference blocker(s). A restart may be necessary."));
    }

    private async Task ReleaseGenerationAsync(ExtensionGeneration generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        generation.SecretRetirementCompletion ??= ObserveCleanup(generation.SecretRetirement.CancelAsync());
        try
        {
            await WaitForCallbacksAsync(generation.SecretRetirementCompletion, cancellationToken);
        }
        finally
        {
            if (generation.SecretRetirementCompletion.IsCompleted)
            {
                generation.SecretRetirementCompletion = null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (generation.Instance is not null)
        {
            generation.State = ExtensionLifecycleState.Deactivating;
            if (generation.DeactivationCompletion is null or { IsCanceled: true })
            {
                generation.DeactivationCompletion = StartDeactivationAsync(generation, cancellationToken);
            }

            var deactivated = await generation.DeactivationCompletion.WaitAsync(cancellationToken);
            if (!deactivated)
            {
                generation.State = ExtensionLifecycleState.DeactivationFailed;
            }

            generation.DeactivationCompletion = null;
            generation.Instance = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        generation.LifetimeCancellationCompletion ??= ObserveCleanup(generation.Lifetime.CancelAsync());
        try
        {
            await WaitForCallbacksAsync(generation.LifetimeCancellationCompletion, cancellationToken);
        }
        finally
        {
            if (generation.LifetimeCancellationCompletion.IsCompleted)
            {
                generation.LifetimeCancellationCompletion = null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.RemoveGeneration(generation.GenerationId);
        generation.ClearCapabilities();
        generation.State = ExtensionLifecycleState.Unloading;
        if (generation.LoadContext is not null)
        {
            generation.ContextUnloadCompletion ??= ObserveCleanup(Task.Run(() => RequestContextUnload(generation), CancellationToken.None));
            await generation.ContextUnloadCompletion.WaitAsync(cancellationToken);
            generation.ContextUnloadCompletion = null;
            generation.LoadContext = null;
        }
    }

    private void RequestContextUnload(ExtensionGeneration generation)
    {
        try
        {
            generation.LoadContext?.Unload();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Unloading generation {GenerationId} raised {FailureType}.",
                generation.GenerationId,
                exception.GetType().Name);
        }
    }

    private async Task PublishNotificationAsync(
        IDomainEvent notification,
        ExtensionGeneration generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var slot = notification is ExtensionDraining ? generation.DrainingNotificationSlot : generation.OutcomeNotificationSlot;
            if (!await slot.WaitAsync(0, cancellationToken))
            {
                // This generation already has a delivery of this kind. Do not queue diagnostics.
                return;
            }

            // Only the worker releases the slot. A deadline abandons the wait, not the delivery.
            var delivery = ObserveCleanup(Task.Run(
                async () =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await _events.PublishAsync(notification, cancellationToken);
                    }
                    finally
                    {
                        slot.Release();
                    }
                },
                CancellationToken.None));
            await delivery.WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Extension {ExtensionId} generation {GenerationId} could not deliver {Notification} ({FailureType}).",
                generation.ExtensionId,
                generation.GenerationId,
                notification.GetType().Name,
                exception.GetType().Name);
        }
    }

    private async Task<bool> VerifyDeadAsync(WeakReference? weakRef, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _gcRetries && weakRef is { IsAlive: true }; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RequestCollectionAsync().WaitAsync(cancellationToken);
            if (i + 1 < _gcRetries && weakRef.IsAlive)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken);
            }
        }

        return weakRef is not { IsAlive: true };
    }

    private static Task RequestCollectionAsync()
    {
        // Finalizers are process-wide and may never finish. Share an active pass across unloads
        // rather than starting another finalizer wait every time an operator retries.
        lock (_collectionGate)
        {
            if (_collectionPass is null or { IsCompleted: true })
            {
                _collectionPass = ObserveCleanup(Task.Run(
                    static () =>
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    },
                    CancellationToken.None));
            }

            return _collectionPass;
        }
    }

    private static T ObserveCleanup<T>(T cleanup)
        where T : Task
    {
        // A timeout only abandons the wait. Observe late faults even if nobody retries unload.
        _ = cleanup.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return cleanup;
    }

    private static async Task WaitForCallbacksAsync(Task callbacks, CancellationToken cancellationToken)
    {
        try
        {
            await callbacks.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // The cancellation source was already disposed.
        }
    }

    private Task<bool> StartDeactivationAsync(ExtensionGeneration generation, CancellationToken cancellationToken)
    {
        // The extension can block before it even returns a task. Keep that work off the caller's
        // thread, and observe it if the caller stops waiting.
        return ObserveCleanup(Task.Run(() => DeactivateAsync(generation, cancellationToken), CancellationToken.None));
    }

    private async Task<bool> DeactivateAsync(ExtensionGeneration generation, CancellationToken cancellationToken)
    {
        if (generation.Instance is not { } instance)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var deactivationContext = new ExtensionDeactivationContext(
            new ExtensionLogger(NullLogger.Instance),
            generation.Lifetime.Token);
        using var extensionCancellation = new CancellationTokenSource();
        var cancellationCallbacks = Task.CompletedTask;

        // Only host-owned code runs on the caller and deadline cancellation threads. CancelAsync
        // schedules extension-owned registrations off those threads and captures their failures.
        var relay = cancellationToken.Register(() => cancellationCallbacks = extensionCancellation.CancelAsync());
        try
        {
            extensionCancellation.Token.ThrowIfCancellationRequested();
            await instance.DeactivateAsync(deactivationContext, extensionCancellation.Token);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !extensionCancellation.IsCancellationRequested)
        {
            // Deactivation failure is logged but does not block unload (§17.17).
            _logger.LogWarning(
                "Extension {ExtensionId} deactivation failed during unload: {FailureType}",
                generation.ExtensionId,
                exception.GetType().Name);
            return false;
        }
        finally
        {
            await relay.DisposeAsync();
            try
            {
                await cancellationCallbacks;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Extension {ExtensionId} deactivation cancellation callback failed: {FailureType}",
                    generation.ExtensionId,
                    exception.GetType().Name);
            }
        }
    }

    private sealed class UnloadDeadlineException : TimeoutException
    {
        public UnloadDeadlineException()
        {
        }

        public UnloadDeadlineException(string message)
            : base(message)
        {
        }

        public UnloadDeadlineException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
