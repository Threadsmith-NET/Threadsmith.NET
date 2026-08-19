namespace Threadsmith.Core;

/// <summary>Host-owned lifecycle state for one durable interactive session.</summary>
public enum SessionLifecycleState
{
    /// <summary>The session is currently active in a host process.</summary>
    Active,

    /// <summary>The session is resumable and has no active work.</summary>
    Idle,

    /// <summary>The session contains interrupted work requiring ordinary recovery.</summary>
    Interrupted,

    /// <summary>The session completed normally and remains resumable.</summary>
    Completed,

    /// <summary>The session was restored only partially from legacy state.</summary>
    Legacy,

    /// <summary>The session cannot safely accept new turns.</summary>
    Unavailable,
}

/// <summary>Identifies the kind of one serialized active-session transition.</summary>
public enum SessionTransitionKind
{
    /// <summary>Creates a fresh empty session.</summary>
    New,

    /// <summary>Activates an existing durable session.</summary>
    Resume,

    /// <summary>Creates and activates an independent governed copy.</summary>
    Clone,
}

/// <summary>Persisted provider-neutral session model and reasoning selection.</summary>
public sealed record SessionModelSelectionRecord
{
    /// <summary>Stable compiled provider identifier.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Stable configured model profile identifier.</summary>
    public required ModelProfileId ProfileId { get; init; }

    /// <summary>Exact bounded host reasoning level name effective for the session.</summary>
    public required string ReasoningLevel { get; init; }

    /// <summary>Monotonic selection generation at persistence.</summary>
    public long Generation { get; init; }

    /// <summary>Version of this durable selection contract.</summary>
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>Bounded repository-scoped catalog metadata for one durable session.</summary>
public sealed record SessionCatalogEntry
{
    /// <summary>Stable session identifier.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Host-derived canonical repository identity.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Non-sensitive repository display name.</summary>
    public required string RepositoryDisplayName { get; init; }

    /// <summary>Creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last durable activity time.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Current durable lifecycle state.</summary>
    public required SessionLifecycleState State { get; init; }

    /// <summary>Sanitized bounded latest visible-message preview.</summary>
    public string? Preview { get; init; }

    /// <summary>Durable visible message count.</summary>
    public long MessageCount { get; init; }

    /// <summary>Active conversation mode.</summary>
    public ConversationContextMode ConversationMode { get; init; }

    /// <summary>Source session when this entry is a clone.</summary>
    public SessionId? CloneSourceSessionId { get; init; }

    /// <summary>Persisted model selection when known.</summary>
    public SessionModelSelectionRecord? ModelSelection { get; init; }

    /// <summary>Whether restoration can accept new turns.</summary>
    public bool IsWritable { get; init; } = true;

    /// <summary>Metadata schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>Immutable result published after one complete session transition.</summary>
public sealed record SessionTransitionResult
{
    /// <summary>Transition kind.</summary>
    public required SessionTransitionKind Kind { get; init; }

    /// <summary>New active session.</summary>
    public required SessionCatalogEntry ActiveSession { get; init; }

    /// <summary>Session left or cloned, when applicable.</summary>
    public SessionId? SourceSessionId { get; init; }

    /// <summary>Bounded restoration warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Restored cumulative provider usage.</summary>
    public long InputTokens { get; init; }

    /// <summary>Restored cumulative provider output usage.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Whether restored usage contains estimates.</summary>
    public bool UsageIsEstimate { get; init; }

    /// <summary>Inherited historical input usage for a clone.</summary>
    public long InheritedInputTokens { get; init; }

    /// <summary>Inherited historical output usage for a clone.</summary>
    public long InheritedOutputTokens { get; init; }
}

/// <summary>Creates and activates a fresh session.</summary>
public sealed record CreateNewSessionCommand : ICommand<SessionTransitionResult>;

/// <summary>Lists bounded resumable sessions for the current repository.</summary>
public sealed record ListResumableSessionsCommand(int MaximumCount = 100)
    : ICommand<IReadOnlyList<SessionCatalogEntry>>;

/// <summary>Resumes one exact durable session.</summary>
public sealed record ResumeSessionCommand(SessionId SessionId) : ICommand<SessionTransitionResult>;

/// <summary>Creates an independent governed copy of the active session.</summary>
public sealed record CloneSessionCommand : ICommand<SessionTransitionResult>;

/// <summary>Gets the one host-owned active session snapshot.</summary>
public sealed record GetActiveSessionCommand : ICommand<SessionCatalogEntry>;

/// <summary>Durable usage subtotal stored without provider request bodies.</summary>
public sealed record SessionDurableUsage(
    long InputTokens,
    long OutputTokens,
    bool IsEstimate,
    bool HasUnknownUsage,
    bool HasObservation,
    long InheritedInputTokens = 0,
    long InheritedOutputTokens = 0);

/// <summary>Persistence boundary for repository-bound session metadata and atomic cloning.</summary>
public interface ISessionLifecycleStore
{
    /// <summary>Creates durable metadata for one session.</summary>
    Task<SessionCatalogEntry> CreateAsync(
        SessionCatalogEntry entry,
        SessionDurableUsage usage,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one entry globally for exact-id mismatch diagnostics.</summary>
    Task<SessionCatalogEntry?> GetAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists repository-bound entries newest first.</summary>
    Task<IReadOnlyList<SessionCatalogEntry>> ListAsync(
        string repositoryIdentity,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>Updates state, model selection, usage, preview, and activity as one checkpoint.</summary>
    Task<SessionCatalogEntry> CheckpointAsync(
        SessionCatalogEntry entry,
        SessionDurableUsage usage,
        CancellationToken cancellationToken = default);

    /// <summary>Reads durable usage for one session.</summary>
    Task<SessionDurableUsage> GetUsageAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically copies reconstructible governed state into a new top-level session.</summary>
    Task<SessionCatalogEntry> CloneAsync(
        SessionId sourceSessionId,
        SessionCatalogEntry destination,
        SessionDurableUsage usage,
        CancellationToken cancellationToken = default);
}
