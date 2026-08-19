namespace Threadsmith.Persistence;

using Threadsmith.Core;

/// <summary>Stores large artifacts outside the main database (strategy §19.3).</summary>
public interface IArtifactStore
{
    /// <summary>Sanitizes and stores content, returning its metadata.</summary>
    /// <param name="content">The artifact content.</param>
    /// <param name="kind">The artifact kind (e.g. <c>processOutput</c>, <c>diff</c>).</param>
    /// <param name="sessionId">The owning session, when applicable.</param>
    /// <param name="cancellationToken">A token that cancels the store.</param>
    /// <returns>The artifact metadata.</returns>
    Task<ArtifactMetadata> StoreAsync(
        string content,
        string kind,
        SessionId? sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an artifact by content hash.</summary>
    /// <param name="contentHash">The SHA-256 content hash.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The sanitized content, or <see langword="null"/> when absent.</returns>
    Task<string?> ReadAsync(string contentHash, CancellationToken cancellationToken = default);

    /// <summary>Deletes an artifact's metadata and persisted body by content hash.</summary>
    /// <param name="contentHash">The SHA-256 content hash.</param>
    /// <param name="cancellationToken">A token that cancels the deletion.</param>
    /// <returns><see langword="true"/> when artifact metadata existed and was removed.</returns>
    Task<bool> DeleteAsync(string contentHash, CancellationToken cancellationToken = default);

    /// <summary>Lists artifact metadata, optionally scoped to a session.</summary>
    /// <param name="sessionId">The owning session, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">A token that cancels the listing.</param>
    /// <returns>The artifact metadata records.</returns>
    Task<IReadOnlyList<ArtifactMetadata>> ListAsync(
        SessionId? sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Ensures the artifact directory and schema exist.</summary>
    /// <param name="cancellationToken">A token that cancels initialization.</param>
    /// <returns>A task that completes when initialized.</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Restores session projections from the durable event stream (strategy §19.4, gap #3).</summary>
public interface ISessionRestorer
{
    /// <summary>Replays a session's events into a projection store with version tolerance.</summary>
    /// <param name="sessionId">The session to restore.</param>
    /// <param name="projectionStore">The target projection store.</param>
    /// <param name="cancellationToken">A token that cancels restoration.</param>
    /// <returns>The restoration outcome.</returns>
    Task<SessionRestorationResult> RestoreAsync(
        SessionId sessionId,
        IProjectionStore projectionStore,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of restoring one session (gap #3: migrate or mark Legacy, never crash).</summary>
public sealed record SessionRestorationResult
{
    /// <summary>The restored session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Number of events replayed.</summary>
    public required int ReplayedEvents { get; init; }

    /// <summary>Number of events that were migrated from an older schema version.</summary>
    public required int MigratedEvents { get; init; }

    /// <summary>Number of events that could not be migrated and were marked <see cref="LegacyState.Legacy"/>.</summary>
    public required int LegacyEvents { get; init; }

    /// <summary>Whether the restored session contains any <see cref="LegacyState.Legacy"/> state.</summary>
    public required bool IsLegacy { get; init; }

    /// <summary>Whether restoration completed without throwing.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>A human-readable summary of warnings, when present.</summary>
    public string? Warnings { get; init; }

    /// <summary>Restored conversation archive and governed memory state when configured.</summary>
    public ConversationStateSnapshot? Conversation { get; init; }
}

/// <summary>Marker for state restored from an older schema version that cannot be fully migrated (gap #3).</summary>
public enum LegacyState
{
    /// <summary>The state was fully restored at the current schema.</summary>
    Current,

    /// <summary>The state was migrated from an older schema version.</summary>
    Migrated,

    /// <summary>The state is from an unsupported schema version and is read-only partial state.</summary>
    Legacy,
}
