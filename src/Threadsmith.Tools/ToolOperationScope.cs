namespace Threadsmith.Tools;

using Threadsmith.Core;
using Threadsmith.Policies.Grants;

/// <summary>Owns transient tool resources for one primary operation, shared with its descendants.</summary>
public sealed class ToolOperationScope : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<object, IAsyncDisposable> _state = [];
    private readonly CancellationTokenSource _lifetime;
    private bool _disposed;
    private CommandPolicyContinuation? _commandPolicy;

    /// <summary>Initializes a new instance of the <see cref="ToolOperationScope"/> class linked to its owning operation.</summary>
    public ToolOperationScope(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>Cancellation of the owning operation and all its acquired resources.</summary>
    public CancellationToken CancellationToken => _lifetime.Token;

    /// <summary>Creates one resource per tool identity under this operation's lifetime.</summary>
    public T GetOrCreate<T>(object key, Func<T> create)
        where T : class, IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(create);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state.TryGetValue(key, out var state))
            {
                return (T)state;
            }

            var result = create();
            _state.Add(key, result);
            return result;
        }
    }

    /// <summary>Advances a future round only through this operation's accepted publications, retaining the caller's ceilings.</summary>
    public ToolInvocationContext RefreshCommandPolicy(ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(context.OperationScope, this) || _commandPolicy is not { } continuation
                || context.SecurityPolicy is not { } captured || !ReferenceEquals(context.PolicyBinding, continuation.Binding)
                || captured.BindingId != continuation.Current.BindingId
                || captured.Generation < continuation.FirstGeneration || captured.Generation >= continuation.Current.Generation)
            {
                return context;
            }

            if (continuation.Grants.Epoch != continuation.Epoch)
            {
                throw new UnauthorizedAccessException(ShellCommandPreparation.DenialReasonCode);
            }

            var next = captured with { Generation = continuation.Current.Generation, CommandPolicies = continuation.Current.CommandPolicies };
            continuation.Binding.Validate(next);
            return context with { SecurityPolicy = next };
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] resources;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            resources = [.. _state.Values];
            _state.Clear();
            _commandPolicy = null;
        }

        await _lifetime.CancelAsync();
        foreach (var resource in resources)
        {
            await resource.DisposeAsync();
        }

        _lifetime.Dispose();
    }

    /// <summary>Records a completed Always publication, never an arbitrary current binding or an approval still in flight.</summary>
    internal void AcceptCommandPolicy(SessionPolicyBinding binding, EffectiveSessionPolicy prior, EffectiveSessionPolicy current, PolicyGrantAuthority grants, long priorEpoch, long epoch)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            binding.Validate(current);
            if (prior.BindingId != current.BindingId || prior.Generation >= current.Generation || grants.Epoch != epoch)
            {
                throw new UnauthorizedAccessException(ShellCommandPreparation.DenialReasonCode);
            }

            var firstGeneration = _commandPolicy is { } previous
                && ReferenceEquals(previous.Binding, binding) && ReferenceEquals(previous.Grants, grants)
                && previous.Current.Generation == prior.Generation && previous.Epoch == priorEpoch
                    ? previous.FirstGeneration : prior.Generation;
            _commandPolicy = new CommandPolicyContinuation(binding, firstGeneration, current, grants, epoch);
        }
    }

    private sealed record CommandPolicyContinuation(SessionPolicyBinding Binding, long FirstGeneration, EffectiveSessionPolicy Current, PolicyGrantAuthority Grants, long Epoch);
}
