namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Routes explicit user commands through the same memory service as the model tool.</summary>
public sealed class RepositoryMemoryApplication :
    ICommandHandler<RememberRepositoryMemoryCommand, RepositoryMemoryEntry>,
    ICommandHandler<ListRepositoryMemoryCommand, RepositoryMemoryReadSnapshot>,
    ICommandHandler<InspectRepositoryMemoryCommand, RepositoryMemoryEntry?>,
    ICommandHandler<UpdateRepositoryMemoryCommand, RepositoryMemoryEntry>,
    ICommandHandler<SupersedeRepositoryMemoryCommand, RepositoryMemoryEntry>,
    ICommandHandler<ForgetRepositoryMemoryCommand, bool>,
    ICommandHandler<ValidateRepositoryMemoryCommand, RepositoryMemoryReadSnapshot>
{
    private readonly IManagedRepositoryMemoryService _memories;
    private readonly IRepositoryMemoryOptionsProvider _options;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryApplication"/> class.</summary>
    public RepositoryMemoryApplication(IManagedRepositoryMemoryService memories, IRepositoryMemoryOptionsProvider options)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(options);
        _memories = memories;
        _options = options;
    }

    /// <inheritdoc />
    public Task<RepositoryMemoryEntry> HandleAsync(RememberRepositoryMemoryCommand command, CancellationToken cancellationToken = default)
        => WriteAsync(command.SessionId, command.RepositoryIdentity, "add", null, command.Text, cancellationToken);

    /// <inheritdoc />
    public Task<RepositoryMemoryReadSnapshot> HandleAsync(ListRepositoryMemoryCommand command, CancellationToken cancellationToken = default)
        => _memories.GetSnapshotAsync(command.RepositoryIdentity, cancellationToken);

    /// <inheritdoc />
    public async Task<RepositoryMemoryEntry?> HandleAsync(InspectRepositoryMemoryCommand command, CancellationToken cancellationToken = default)
    {
        var snapshot = await _memories.GetSnapshotAsync(command.RepositoryIdentity, cancellationToken);
        return snapshot.Entries.FirstOrDefault(item => item.Id == command.MemoryId);
    }

    /// <inheritdoc />
    public Task<RepositoryMemoryEntry> HandleAsync(UpdateRepositoryMemoryCommand command, CancellationToken cancellationToken = default)
        => WriteAsync(command.SessionId, command.RepositoryIdentity, "update", command.MemoryId, command.ReplacementText, cancellationToken);

    /// <inheritdoc />
    public Task<RepositoryMemoryEntry> HandleAsync(SupersedeRepositoryMemoryCommand command, CancellationToken cancellationToken = default)
        => WriteAsync(command.SessionId, command.RepositoryIdentity, "update", command.MemoryId, command.ReplacementText, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> HandleAsync(ForgetRepositoryMemoryCommand command, CancellationToken cancellationToken = default)
    {
        var result = await _memories.ExecuteAsync(
            new RepositoryMemoryOperationRequest
            {
                RepositoryIdentity = command.RepositoryIdentity,
                Action = "remove",
                Id = command.MemoryId,
                Origin = RepositoryMemoryOrigin.Manual,
                SourceSessionId = command.SessionId.Value.ToString("D"),
                Options = _options.Capture(command.RepositoryIdentity),
            },
            cancellationToken);
        return result.Outcome == "removed";
    }

    /// <inheritdoc />
    public Task<RepositoryMemoryReadSnapshot> HandleAsync(ValidateRepositoryMemoryCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Memory validation and categories have been retired. Use /memory list, inspect <id>, update <id> <text>, or forget <id>.");
    }

    private async Task<RepositoryMemoryEntry> WriteAsync(SessionId sessionId, string repositoryIdentity, string action, RepositoryMemoryId? id, string text, CancellationToken cancellationToken)
    {
        var result = await _memories.ExecuteAsync(
            new RepositoryMemoryOperationRequest
            {
                RepositoryIdentity = repositoryIdentity,
                Action = action,
                Id = id,
                Text = text,
                Origin = RepositoryMemoryOrigin.Manual,
                SourceSessionId = sessionId.Value.ToString("D"),
                Options = _options.Capture(repositoryIdentity),
            },
            cancellationToken);
        if (action == "update")
        {
            return result.Outcome switch
            {
                "updated" or "unchanged" => result.Entry ?? throw new InvalidOperationException($"Memory operation returned {result.Outcome} without an entry."),
                "duplicate" => throw new InvalidOperationException($"That text already belongs to memory {result.Id}. The requested memory was not changed."),
                "notfound" => throw new InvalidOperationException("That memory no longer exists. List repository memories and retry the update."),
                "conflict" => throw new InvalidOperationException($"Memory {id} changed while this update was being prepared. Inspect it and retry the update."),
                _ => throw new InvalidOperationException($"Memory operation returned {result.Outcome}."),
            };
        }

        return result.Entry ?? throw new InvalidOperationException($"Memory operation returned {result.Outcome}.");
    }
}
