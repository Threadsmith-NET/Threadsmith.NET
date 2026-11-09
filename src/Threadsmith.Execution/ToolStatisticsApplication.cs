namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Routes repository statistics commands to the ordered storage owner.</summary>
public sealed class ToolStatisticsApplication :
    ICommandHandler<ListToolStatisticsCommand, ToolStatisticsSnapshot>,
    ICommandHandler<ResetToolStatisticsCommand, bool>
{
    private readonly IToolStatisticsStore _store;
    private readonly IRepositoryStorageBindingSource _bindings;

    /// <summary>Initializes a new instance of the <see cref="ToolStatisticsApplication"/> class.</summary>
    public ToolStatisticsApplication(IToolStatisticsStore store, IRepositoryStorageBindingSource bindings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    /// <inheritdoc />
    public Task<ToolStatisticsSnapshot> HandleAsync(ListToolStatisticsCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = _bindings.CaptureBinding(command.RepositoryIdentity);
        return _store.GetSnapshotAsync(binding, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(ResetToolStatisticsCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = _bindings.CaptureBinding(command.RepositoryIdentity);
        await _store.ResetAsync(binding, cancellationToken);
        return true;
    }
}
