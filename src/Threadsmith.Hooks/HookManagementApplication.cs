namespace Threadsmith.Hooks;

using Threadsmith.Core;

/// <summary>Shared interactive/headless hook-management command application.</summary>
public sealed class HookManagementApplication :
    ICommandHandler<ListHooksCommand, IReadOnlyList<HookHandlerDescriptor>>,
    ICommandHandler<InspectHookCommand, HookHandlerDescriptor?>,
    ICommandHandler<SetHookEnabledCommand, bool>,
    ICommandHandler<QueryHookAuditCommand, IReadOnlyList<HookAuditRecord>>,
    ICommandHandler<ApproveRepositoryHookCommand, bool>,
    ICommandHandler<RevokeRepositoryHookCommand, bool>,
    ICommandHandler<TestHookCommand, HookBoundaryDecision>
{
    private readonly IHookCoordinator _coordinator;
    private readonly IDomainEventStream? _events;
    private readonly IHookStore _store;

    /// <summary>Initializes a new instance of the <see cref="HookManagementApplication"/> class.</summary>
    public HookManagementApplication(IHookCoordinator coordinator, IHookStore store, IDomainEventStream? events = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(store);
        _coordinator = coordinator;
        _store = store;
        _events = events;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HookHandlerDescriptor>> HandleAsync(ListHooksCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_coordinator.Handlers);
    }

    /// <inheritdoc />
    public Task<HookHandlerDescriptor?> HandleAsync(InspectHookCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_coordinator.GetHandler(command.HandlerId));
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(SetHookEnabledCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_coordinator.SetEnabled(command.HandlerId, command.Enabled));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HookAuditRecord>> HandleAsync(QueryHookAuditCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _store.QueryAuditAsync(command.RepositoryIdentity, command.HandlerId, Math.Clamp(command.MaximumCount, 1, 1000), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(ApproveRepositoryHookCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var approval = command.Approval;
        var descriptor = _coordinator.Handlers.FirstOrDefault(handler => handler.Identity == approval.HandlerIdentity);
        if (descriptor is null
            || descriptor.Scope != HookHandlerScope.Repository
            || descriptor.RequestedAuthority != HookAuthority.Advisory
            || !string.Equals(descriptor.Target, approval.Target, StringComparison.Ordinal)
            || !descriptor.HookPoints.SequenceEqual(approval.HookPoints)
            || !descriptor.SecretReferences.SequenceEqual(approval.SecretReferences, StringComparer.Ordinal))
        {
            return false;
        }

        await _store.SaveApprovalAsync(approval, cancellationToken);
        if (_events is not null)
        {
            await _events.PublishAsync(
                new HookRepositoryApprovalChanged(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    approval.RepositoryIdentity,
                    approval.HandlerIdentity.Id,
                    approval.HandlerIdentity.ConfigurationDigest,
                    true),
                cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(RevokeRepositoryHookCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var descriptor = _coordinator.GetHandler(command.HandlerId);
        await _store.RevokeApprovalAsync(command.RepositoryIdentity, command.HandlerId, cancellationToken);
        if (_events is not null && descriptor is not null)
        {
            await _events.PublishAsync(
                new HookRepositoryApprovalChanged(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    command.RepositoryIdentity,
                    command.HandlerId,
                    descriptor.Identity.ConfigurationDigest,
                    false),
                cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public Task<HookBoundaryDecision> HandleAsync(TestHookCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var descriptor = _coordinator.Handlers.FirstOrDefault(handler => handler.Identity.Id == command.HandlerId);
        if (descriptor is null)
        {
            return Task.FromResult(new HookBoundaryDecision(HookDecisionKind.Continue, [], ["handler not found"]));
        }

        var point = descriptor.HookPoints[0];
        return _coordinator.InvokeHandlerAsync(
            command.HandlerId,
            point,
            command.SessionId,
            null,
            command.RepositoryIdentity,
            Guid.NewGuid(),
            0,
            new Dictionary<string, string> { ["mode"] = "test" },
            cancellationToken: cancellationToken);
    }
}

/// <summary>Thread-safe non-durable store for tests and hosts without persistence.</summary>
public sealed class InMemoryHookStore : IHookStore
{
    private readonly List<HookAuditRecord> _audit = [];
    private readonly Dictionary<string, HookRepositoryApproval> _approvals = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public Task<HookRepositoryApproval?> GetApprovalAsync(string repositoryIdentity, HookHandlerIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _approvals.TryGetValue(Key(repositoryIdentity, identity.Id), out var approval);
            return Task.FromResult(approval?.HandlerIdentity == identity ? approval : null);
        }
    }

    /// <inheritdoc />
    public Task SaveApprovalAsync(HookRepositoryApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _approvals[Key(approval.RepositoryIdentity, approval.HandlerIdentity.Id)] = approval;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RevokeApprovalAsync(string repositoryIdentity, HookHandlerId handlerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _approvals.Remove(Key(repositoryIdentity, handlerId));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AppendAuditAsync(HookAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _audit.Add(record);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HookAuditRecord>> QueryAuditAsync(string? repositoryIdentity, HookHandlerId? handlerId, int maximumCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<HookAuditRecord> result = [.. _audit
                .Where(record => repositoryIdentity is null || string.Equals(record.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal))
                .Where(record => handlerId is null || record.HandlerIdentity.Id == handlerId)
                .OrderByDescending(record => record.RecordedAt)
                .Take(maximumCount)];
            return Task.FromResult(result);
        }
    }

    private static string Key(string repositoryIdentity, HookHandlerId handlerId)
    {
        return $"{repositoryIdentity}\n{handlerId.Value}";
    }
}
