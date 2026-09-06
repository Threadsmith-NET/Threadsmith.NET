namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Handles explicit repository-memory commands for interactive and headless surfaces.</summary>
public sealed class RepositoryMemoryApplication :
    ICommandHandler<RememberRepositoryMemoryCommand, RepositoryMemoryItem>,
    ICommandHandler<ListRepositoryMemoryCommand, RepositoryMemorySnapshot>,
    ICommandHandler<InspectRepositoryMemoryCommand, RepositoryMemoryItem?>,
    ICommandHandler<SupersedeRepositoryMemoryCommand, RepositoryMemoryItem>,
    ICommandHandler<ForgetRepositoryMemoryCommand, bool>,
    ICommandHandler<ValidateRepositoryMemoryCommand, RepositoryMemorySnapshot>
{
    private readonly IDomainEventStream _events;
    private readonly IRepositoryMemoryGovernor _governor;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryApplication"/> class.</summary>
    public RepositoryMemoryApplication(
        IRepositoryMemoryGovernor governor,
        IDomainEventStream events,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(events);
        _governor = governor;
        _events = events;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryItem> HandleAsync(
        RememberRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await _governor.RememberAsync(command, cancellationToken);
        await PublishValidityChangesAsync(command.SessionId, command.RepositoryIdentity, result.StateUpdates, cancellationToken);
        if (result.WasInserted)
        {
            await _events.PublishAsync(
                new RepositoryMemoryRemembered(
                    command.SessionId,
                    _timeProvider.GetUtcNow(),
                    command.RepositoryIdentity,
                    result.Item.Id,
                    result.Item.Kind,
                    result.Item.Authority),
                cancellationToken);
        }

        return result.Item;
    }

    /// <inheritdoc />
    public Task<RepositoryMemorySnapshot> HandleAsync(
        ListRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        return _governor.ListAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RepositoryMemoryItem?> HandleAsync(
        InspectRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        return _governor.InspectAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryItem> HandleAsync(
        SupersedeRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await _governor.SupersedeAsync(command, cancellationToken);
        await PublishValidityChangesAsync(command.SessionId, command.RepositoryIdentity, result.StateUpdates, cancellationToken);
        await _events.PublishAsync(
            new RepositoryMemorySuperseded(
                command.SessionId,
                _timeProvider.GetUtcNow(),
                command.RepositoryIdentity,
                command.MemoryId,
                result.Item.Id),
            cancellationToken);
        return result.Item;
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        ForgetRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        var forgotten = await _governor.ForgetAsync(command, cancellationToken);
        if (forgotten)
        {
            await _events.PublishAsync(
                new RepositoryMemoryValidityChanged(
                    command.SessionId,
                    _timeProvider.GetUtcNow(),
                    command.RepositoryIdentity,
                    command.MemoryId,
                    RepositoryMemoryValidity.Forgotten,
                    "User forgot this repository memory item."),
                cancellationToken);
        }

        return forgotten;
    }

    /// <inheritdoc />
    public async Task<RepositoryMemorySnapshot> HandleAsync(
        ValidateRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await _governor.ValidateAsync(command, cancellationToken);
        await PublishValidityChangesAsync(command.SessionId, command.RepositoryIdentity, result.StateUpdates, cancellationToken);
        return result.Snapshot;
    }

    private async Task PublishValidityChangesAsync(
        SessionId sessionId,
        string repositoryIdentity,
        IReadOnlyList<RepositoryMemoryStateUpdate> changes,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes.Where(change => change.PreviousValidity != change.Validity))
        {
            await _events.PublishAsync(
                new RepositoryMemoryValidityChanged(
                    sessionId,
                    _timeProvider.GetUtcNow(),
                    repositoryIdentity,
                    change.MemoryId,
                    change.Validity,
                    change.Reason),
                cancellationToken);
        }
    }
}
