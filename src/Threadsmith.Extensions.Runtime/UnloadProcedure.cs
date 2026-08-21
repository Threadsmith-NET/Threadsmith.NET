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

    /// <summary>The ALC survived unload verification; retained references were diagnosed.</summary>
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
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly int GcRetries = 3;

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
    /// <param name="cancellationToken">A token that cancels the drain wait.</param>
    /// <returns>The unload result with outcome and blocker diagnostics.</returns>
    public async Task<UnloadResult> UnloadAsync(
        ExtensionGeneration generation,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var startedAt = _timeProvider.GetUtcNow();

        // 1. Begin draining: block all new invocation leases.
        _leaseAuthority.BeginDraining(generation.GenerationId);
        await _events.PublishAsync(
            new ExtensionDraining(sessionId, _timeProvider.GetUtcNow(), generation.ExtensionId),
            cancellationToken);

        // 2. Drain in-flight leases (bounded wait).
        var drainStart = _timeProvider.GetUtcNow();
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCts.CancelAfter(DefaultDrainTimeout);
        await _leaseAuthority.WaitForDrainAsync(generation.GenerationId, drainCts.Token).ConfigureAwait(false);
        var drainWait = _timeProvider.GetUtcNow() - drainStart;

        // 3. Deactivate (best effort; deactivation failure does not block unload).
        await DeactivateAsync(generation, sessionId, cancellationToken);

        // 4. Remove capability + model-preference registrations for this generation and clear the
        //    generation's cached capability lists so extension types are released.
        _capabilities.RemoveGeneration(generation.GenerationId);
        generation.ClearCapabilities();

        // 5. Clear the extension instance reference and cancel the lifetime token.
        generation.Instance = null;
        try
        {
            await generation.Lifetime.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed; ignore.
        }

        // 6. Unload the collectible context.
        generation.State = ExtensionLifecycleState.Unloading;
        var weakRef = generation.LoadContextWeakReference;
        try
        {
            generation.LoadContext?.Unload();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Unloading generation {GenerationId} raised {Exception}: {Message}",
                generation.GenerationId,
                exception.GetType().Name,
                exception.Message);
        }

        // Drop the host's strong reference to the ALC so it can be collected. Do NOT hold a local ALC
        // reference across verification — that would pin the context and defeat the WeakReference check.
        generation.LoadContext = null;

        // 7. WeakReference verification (§17.19): bounded GC until the ALC is dead.
        var dead = await VerifyDeadAsync(weakRef);
        var total = _timeProvider.GetUtcNow() - startedAt;

        if (dead)
        {
            generation.State = ExtensionLifecycleState.Unloaded;
            _leaseAuthority.RemoveBook(generation.GenerationId);
            ShadowCopier.Discard(generation.StagingPath);
            await _events.PublishAsync(
                new ExtensionUnloaded(sessionId, _timeProvider.GetUtcNow(), generation.ExtensionId),
                CancellationToken.None);
            return new UnloadResult
            {
                GenerationId = generation.GenerationId,
                Outcome = UnloadOutcome.Unloaded,
                DrainWait = drainWait,
                TotalDuration = total,
            };
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
        await _events.PublishAsync(
            new ExtensionUnloadFailed(
                sessionId,
                _timeProvider.GetUtcNow(),
                generation.ExtensionId,
                $"Unload verification found {blockers.Blockers.Count} retained-reference blocker(s). A restart may be necessary."),
            CancellationToken.None);
        return new UnloadResult
        {
            GenerationId = generation.GenerationId,
            Outcome = UnloadOutcome.UnloadBlocked,
            Blockers = blockers,
            DrainWait = drainWait,
            TotalDuration = total,
        };
    }

    private static async Task<bool> VerifyDeadAsync(WeakReference? weakRef)
    {
        if (weakRef is null)
        {
            return true;
        }

        for (var i = 0; i < GcRetries; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weakRef.IsAlive)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return !weakRef.IsAlive;
    }

    private async Task DeactivateAsync(
        ExtensionGeneration generation,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        if (generation.Instance is null)
        {
            return;
        }

        // Transition to Deactivating *before* invoking DeactivateAsync so the transient state is
        // observable and the Active → Deactivating → Unloading path is honored (F7). The state
        // machine is the source of truth for generation.State after load completes.
        generation.State = ExtensionLifecycleState.Deactivating;
        var deactivationContext = new ExtensionDeactivationContext(
            new ExtensionLogger(NullLogger.Instance),
            generation.Lifetime.Token);
        try
        {
            await generation.Instance.DeactivateAsync(deactivationContext, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deactivation failure is logged but does not block unload (§17.17).
            _logger.LogWarning(
                "Extension {ExtensionId} deactivation failed during unload: {Message}",
                generation.ExtensionId,
                exception.Message);
            generation.State = ExtensionLifecycleState.DeactivationFailed;
        }
    }
}