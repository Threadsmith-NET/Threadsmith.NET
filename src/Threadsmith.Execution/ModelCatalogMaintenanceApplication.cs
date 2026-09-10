namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Inspects detached provider metadata without changing active selection.</summary>
public sealed record GetModelCatalogStatusCommand(string ProviderId) : ICommand<ModelCatalogProviderStatus>;

/// <summary>Explicitly refreshes provider metadata for the next application startup.</summary>
public sealed record RefreshModelCatalogCommand(string ProviderId) : ICommand<ModelCatalogRefreshResult>;

/// <summary>Shares provider catalog maintenance authority across command surfaces.</summary>
public sealed class ModelCatalogMaintenanceApplication :
    ICommandHandler<GetModelCatalogStatusCommand, ModelCatalogProviderStatus>,
    ICommandHandler<RefreshModelCatalogCommand, ModelCatalogRefreshResult>
{
    private readonly IModelCatalogMaintenance _catalogs;

    /// <summary>Initializes a new instance of the <see cref="ModelCatalogMaintenanceApplication"/> class.</summary>
    public ModelCatalogMaintenanceApplication(IModelCatalogMaintenance catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        _catalogs = catalogs;
    }

    /// <inheritdoc />
    public Task<ModelCatalogProviderStatus> HandleAsync(
        GetModelCatalogStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderId);
        return _catalogs.GetStatusAsync(command.ProviderId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ModelCatalogRefreshResult> HandleAsync(
        RefreshModelCatalogCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderId);
        return _catalogs.RefreshAsync(command.ProviderId, cancellationToken);
    }
}
