namespace Threadsmith.Extensions.Runtime;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;

/// <summary>Host-owned invocation lease authority (strategy Â§17.15). Leases prevent unload while a capability is executing.</summary>
public sealed class InvocationLeaseAuthority
{
    /// <summary>Default lease timeout (generous; per-capability overrides may shorten it, Â§17.17 open decision).</summary>
    public static readonly TimeSpan DefaultLeaseTimeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<ExtensionGenerationId, ExtensionLeaseBook> _books = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="InvocationLeaseAuthority"/> class.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The time provider.</param>
    public InvocationLeaseAuthority(ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Begins draining for a generation: blocks all new leases and tracks in-flight leases (plan-17 foundation, plan-16 Â§3).</summary>
    /// <param name="generationId">The generation entering drain.</param>
    public void BeginDraining(ExtensionGenerationId generationId)
    {
        var book = _books.GetOrAdd(generationId, id => new ExtensionLeaseBook(id));
        book.IsDraining = true;
    }

    /// <summary>Acquires a lease for an invocation, or throws when the generation is draining or the budget is exhausted (Â§17.15, Â§22.2).</summary>
    /// <param name="generationId">The owning generation.</param>
    /// <param name="budget">The per-extension invocation budget for the current turn.</param>
    /// <param name="timeout">The lease timeout.</param>
    /// <returns>The held lease.</returns>
    public IInvocationLease Acquire(ExtensionGenerationId generationId, ExtensionInvocationBudget budget, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var book = _books.GetOrAdd(generationId, id => new ExtensionLeaseBook(id));
        if (book.IsDraining)
        {
            throw new ExtensionDrainingException(
                $"Cannot acquire an invocation lease for generation '{generationId}' because it is draining.");
        }

        if (!budget.TryReserve())
        {
            throw new ExtensionBudgetExhaustedException(
                $"The invocation budget for extension generation '{generationId}' is exhausted for this turn.");
        }

        var lease = new InvocationLease(book, budget, timeout, _timeProvider, _logger);
        book.Add(lease);
        return lease;
    }

    /// <summary>Waits until all in-flight leases for a generation are released (cooperative drain, plan-17).</summary>
    /// <param name="generationId">The generation to wait on.</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>The number of leases that were still held when the wait was abandoned.</returns>
    public async Task<int> WaitForDrainAsync(ExtensionGenerationId generationId, CancellationToken cancellationToken)
    {
        if (!_books.TryGetValue(generationId, out var book))
        {
            return 0;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (book.InFlight == 0)
            {
                return 0;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        return book.InFlight;
    }

    /// <summary>Removes the book for a generation after unload completes.</summary>
    /// <param name="generationId">The generation whose book should be removed.</param>
    public void RemoveBook(ExtensionGenerationId generationId)
    {
        _books.TryRemove(generationId, out _);
    }

    private sealed class ExtensionLeaseBook
    {
        private static int _nextLeaseId;

        private readonly ConcurrentDictionary<int, InvocationLease> _held = new();
        private readonly Lock _gate = new();

        public ExtensionLeaseBook(ExtensionGenerationId generationId)
        {
            GenerationId = generationId;
        }

        public ExtensionGenerationId GenerationId { get; }

        public bool IsDraining
        {
            get
            {
                lock (_gate)
                {
                    return _isDraining;
                }
            }

            set
            {
                lock (_gate)
                {
                    _isDraining = value;
                }
            }
        }

        private bool _isDraining;

        public int InFlight => _held.Count;

        public static int AllocateLeaseId()
        {
            return Interlocked.Increment(ref _nextLeaseId);
        }

        public void Add(InvocationLease lease)
        {
            _held.TryAdd(lease.LeaseId, lease);
        }

        public void Release(InvocationLease lease)
        {
            _held.TryRemove(lease.LeaseId, out _);
        }
    }

    private sealed class InvocationLease : IInvocationLease
    {
        private readonly ExtensionLeaseBook _book;
        private readonly ExtensionInvocationBudget _budget;
        private readonly DateTimeOffset _expiresAt;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private int _state = (int)LeaseState.Held;

        public InvocationLease(
            ExtensionLeaseBook book,
            ExtensionInvocationBudget budget,
            TimeSpan timeout,
            TimeProvider timeProvider,
            ILogger logger)
        {
            _book = book;
            _budget = budget;
            _timeProvider = timeProvider;
            _logger = logger;
            _expiresAt = timeProvider.GetUtcNow() + timeout;
            LeaseId = ExtensionLeaseBook.AllocateLeaseId();
        }

        public int LeaseId { get; }

        public LeaseState State => (LeaseState)Interlocked.CompareExchange(ref _state, 0, 0);

        public void Dispose()
        {
            ReleaseCore(LeaseState.Released);
            GC.SuppressFinalize(this);
        }

        private void ReleaseCore(LeaseState finalState)
        {
            if (Interlocked.CompareExchange(ref _state, (int)finalState, (int)LeaseState.Held) != (int)LeaseState.Held)
            {
                return;
            }

            _budget.Release();
            _book.Release(this);
            if (finalState == LeaseState.TimedOut)
            {
                _logger.LogWarning(
                    "Invocation lease for generation {GenerationId} timed out after the configured duration and was force-released.",
                    _book.GenerationId);
            }
        }

        ~InvocationLease()
        {
            // Finalizer backstop: if a lease is never disposed, release the budget and book slot.
            var now = _timeProvider.GetUtcNow();
            ReleaseCore(now > _expiresAt ? LeaseState.TimedOut : LeaseState.Released);
        }
    }
}

/// <summary>Thrown when an invocation is attempted against a draining generation.</summary>
public sealed class ExtensionDrainingException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionDrainingException"/> class.</summary>
    public ExtensionDrainingException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionDrainingException"/> class.</summary>
    public ExtensionDrainingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionDrainingException"/> class.</summary>
    public ExtensionDrainingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an extension's per-turn invocation budget is exhausted (Â§22.2).</summary>
public sealed class ExtensionBudgetExhaustedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionBudgetExhaustedException"/> class.</summary>
    public ExtensionBudgetExhaustedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionBudgetExhaustedException"/> class.</summary>
    public ExtensionBudgetExhaustedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionBudgetExhaustedException"/> class.</summary>
    public ExtensionBudgetExhaustedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}