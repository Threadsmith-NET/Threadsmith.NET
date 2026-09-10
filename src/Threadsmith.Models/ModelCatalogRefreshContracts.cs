namespace Threadsmith.Models;

/// <summary>Secret-free status for one configured provider catalog.</summary>
public sealed record ModelCatalogProviderStatus
{
    /// <summary>Configured provider instance identifier.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Whether an eligible immutable model snapshot is available.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Whether a credential was resolvable at the last status operation.</summary>
    public bool HasCredential { get; init; }

    /// <summary>Whether a successful authenticated remote operation has occurred in this process.</summary>
    public bool IsAuthenticated { get; init; }

    /// <summary>Age of the detached metadata cache when present.</summary>
    public TimeSpan? CacheAge { get; init; }

    /// <summary>Bounded safe diagnostic.</summary>
    public string Status { get; init; } = "unavailable";
}

/// <summary>Result of an explicit metadata refresh.</summary>
public sealed record ModelCatalogRefreshResult
{
    /// <summary>Provider instance that was refreshed.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Whether a complete cache snapshot was atomically written.</summary>
    public bool Refreshed { get; init; }

    /// <summary>Whether restart is needed to rebuild immutable selectable profiles.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>Bounded safe outcome text.</summary>
    public string Status { get; init; } = "unavailable";
}

/// <summary>Host command backend for provider metadata status and explicit refresh.</summary>
public interface IModelCatalogMaintenance
{
    /// <summary>Gets secret-free metadata status for one configured provider.</summary>
    Task<ModelCatalogProviderStatus> GetStatusAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Refreshes complete detached metadata for one configured provider.</summary>
    Task<ModelCatalogRefreshResult> RefreshAsync(string providerId, CancellationToken cancellationToken = default);
}
