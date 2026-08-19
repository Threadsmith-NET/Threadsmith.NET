namespace Threadsmith.Extensions.Runtime;

using System.Collections.Frozen;
using Threadsmith.Extensions.Abstractions;

/// <summary>
/// Cooperative extension-generation lifecycle state machine (strategy §17.16). Enforces legal
/// transitions and rejects illegal ones with <see cref="ExtensionLifecycleException"/>.
/// </summary>
public sealed class ExtensionLifecycle
{
    private static readonly FrozenDictionary<ExtensionLifecycleState, FrozenSet<ExtensionLifecycleState>> _transitions =
        new Dictionary<ExtensionLifecycleState, FrozenSet<ExtensionLifecycleState>>
        {
            [ExtensionLifecycleState.Discovered] = [ExtensionLifecycleState.Validating, ExtensionLifecycleState.Disabled],
            [ExtensionLifecycleState.Validating] =
            [
                ExtensionLifecycleState.Loading,
                ExtensionLifecycleState.Incompatible,
                ExtensionLifecycleState.Disabled,
            ],
            [ExtensionLifecycleState.Loading] =
            [
                ExtensionLifecycleState.Activating,
                ExtensionLifecycleState.LoadFailed,
                ExtensionLifecycleState.Disabled,
            ],
            [ExtensionLifecycleState.Activating] =
            [
                ExtensionLifecycleState.Active,
                ExtensionLifecycleState.ActivationFailed,
                ExtensionLifecycleState.Disabled,
            ],
            [ExtensionLifecycleState.Active] =
            [
                ExtensionLifecycleState.Draining,
                ExtensionLifecycleState.Deactivating,
                ExtensionLifecycleState.Disabled,
            ],
            [ExtensionLifecycleState.Draining] =
            [
                ExtensionLifecycleState.Deactivating,
                ExtensionLifecycleState.DeactivationFailed,
            ],
            [ExtensionLifecycleState.Deactivating] =
            [
                ExtensionLifecycleState.Unloading,
                ExtensionLifecycleState.DeactivationFailed,
            ],
            [ExtensionLifecycleState.Unloading] =
            [
                ExtensionLifecycleState.Unloaded,
                ExtensionLifecycleState.UnloadBlocked,
            ],
        }.ToFrozenDictionary();

    private readonly Lock _gate = new();
    private ExtensionLifecycleState _state = ExtensionLifecycleState.Discovered;

    /// <summary>Initializes a new instance of the <see cref="ExtensionLifecycle"/> class starting in the Discovered state.</summary>
    public ExtensionLifecycle()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionLifecycle"/> class starting in the supplied state.</summary>
    /// <param name="initial">The initial state.</param>
    public ExtensionLifecycle(ExtensionLifecycleState initial)
    {
        _state = initial;
    }

    /// <summary>Gets the current state.</summary>
    public ExtensionLifecycleState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Transitions to <paramref name="next"/> if the transition is legal.</summary>
    /// <param name="next">The desired next state.</param>
    /// <returns>The new state.</returns>
    /// <exception cref="ExtensionLifecycleException">When the transition is illegal.</exception>
    public ExtensionLifecycleState TransitionTo(ExtensionLifecycleState next)
    {
        lock (_gate)
        {
            if (_state == next)
            {
                return _state;
            }

            if (!_transitions.TryGetValue(_state, out var allowed)
                || !allowed.Contains(next))
            {
                throw new ExtensionLifecycleException(
                    $"Illegal extension lifecycle transition {_state} -> {next}.");
            }

            _state = next;
            return _state;
        }
    }

    /// <summary>Attempts a transition without throwing.</summary>
    /// <param name="next">The desired next state.</param>
    /// <returns><see langword="true"/> when the transition succeeded.</returns>
    public bool TryTransitionTo(ExtensionLifecycleState next)
    {
        try
        {
            TransitionTo(next);
            return true;
        }
        catch (ExtensionLifecycleException)
        {
            return false;
        }
    }
}