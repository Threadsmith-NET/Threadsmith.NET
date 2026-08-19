namespace Threadsmith.Core;

/// <summary>Host-owned extension manager contract exposed to the interactive shell and headless surfaces.
/// Defined in Core so the TUI can depend on it without referencing the extension runtime (§8.1 dependency
/// direction; the runtime implements this contract, the shell consumes it).</summary>
public interface IExtensionManager
{
    /// <summary>A snapshot of every discovered extension and its current lifecycle state, in stable order.</summary>
    IReadOnlyList<ExtensionSummary> Summaries { get; }

    /// <summary>Discovers extension packages in the configured discovery directory and returns their summaries
    /// without loading them. Idempotent: re-scans the directory each call.</summary>
    /// <param name="cancellationToken">A token that cancels the scan.</param>
    /// <returns>Summaries of all discovered extension packages.</returns>
    Task<IReadOnlyList<ExtensionSummary>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads (activates) the discovered extension with the given id. No-op when already loaded.</summary>
    /// <param name="extensionId">The stable extension id to load.</param>
    /// <param name="sessionId">The session owning the operation.</param>
    /// <param name="cancellationToken">A token that cancels the load.</param>
    /// <returns>The summary of the now-loaded extension, or null when the id is not discovered.</returns>
    Task<ExtensionSummary?> LoadAsync(string extensionId, SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>Cooperatively unloads the active generation of the given extension id with verification.</summary>
    /// <param name="extensionId">The stable extension id to unload.</param>
    /// <param name="sessionId">The session owning the operation.</param>
    /// <param name="cancellationToken">A token that cancels the unload.</param>
    /// <returns>True when the extension was loaded and has been unloaded; false when it was not loaded.</returns>
    Task<bool> UnloadAsync(string extensionId, SessionId sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Host-owned, serializable summary of one extension (no extension runtime types, §7.1).</summary>
public sealed record ExtensionSummary
{
    /// <summary>The stable extension id (manifest descriptor id).</summary>
    public required string ExtensionId { get; init; }

    /// <summary>The active generation id, or null when not loaded.</summary>
    public ExtensionGenerationId? GenerationId { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>The extension version.</summary>
    public required string Version { get; init; }

    /// <summary>The lifecycle state label (e.g. "Active", "Unloaded", "UnloadBlocked", "Discovered").</summary>
    public required string State { get; init; }

    /// <summary>True when the extension is currently loaded (has an active generation).</summary>
    public bool IsLoaded { get; init; }

    /// <summary>The number of tool capabilities contributed by the extension.</summary>
    public int ToolCount { get; init; }

    /// <summary>The number of model-preference contributors contributed by the extension.</summary>
    public int ModelPreferenceContributorCount { get; init; }

    /// <summary>The directory the extension package was discovered in.</summary>
    public required string Directory { get; init; }
}