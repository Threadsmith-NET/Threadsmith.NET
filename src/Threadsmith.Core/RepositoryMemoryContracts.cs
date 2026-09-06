namespace Threadsmith.Core;

/// <summary>Identifies one repository-scoped memory category.</summary>
public enum RepositoryMemoryKind
{
    /// <summary>An explicit preference the user wants applied in this repository.</summary>
    UserPreference,

    /// <summary>An explicit constraint the user wants applied in this repository.</summary>
    UserConstraint,

    /// <summary>A repository convention observed or authored for future work.</summary>
    RepositoryConvention,

    /// <summary>An architecture decision relevant to this repository.</summary>
    ArchitectureDecision,

    /// <summary>A repeatable workflow fact for this repository.</summary>
    WorkflowFact,

    /// <summary>A known failure mode or remediation fact for this repository.</summary>
    KnownFailure,

    /// <summary>An unresolved repository question retained for follow-up.</summary>
    UnresolvedQuestion,

    /// <summary>A repository fact backed by governed host evidence.</summary>
    EvidenceBackedRepositoryFact,

    /// <summary>An audit item retained only because it was rejected or superseded.</summary>
    RejectedOrSuperseded,
}

/// <summary>Classifies the authority supporting a repository memory item.</summary>
public enum RepositoryMemoryAuthority
{
    /// <summary>The user explicitly authored the memory through a host command path.</summary>
    UserAuthored,

    /// <summary>The host observed the fact through an authoritative workflow boundary.</summary>
    HostObserved,

    /// <summary>The fact is backed by governed evidence with repository provenance.</summary>
    EvidenceBacked,

    /// <summary>The model proposed the item and strict host validation accepted it.</summary>
    ModelProposedValidated,
}

/// <summary>Describes whether a repository memory item may be selected for future context.</summary>
public enum RepositoryMemoryValidity
{
    /// <summary>The item is active and eligible for retrieval.</summary>
    Active,

    /// <summary>Repository state changed and the item must be revalidated before retrieval.</summary>
    Stale,

    /// <summary>A newer item explicitly replaced this item.</summary>
    Superseded,

    /// <summary>The user intentionally forgot the item; audit metadata remains.</summary>
    Forgotten,

    /// <summary>The item failed host validation and is retained only for audit.</summary>
    Rejected,
}

/// <summary>Identifies the kind of provenance source supporting repository memory.</summary>
public enum RepositoryMemorySourceKind
{
    /// <summary>An explicit user command created or corrected the item.</summary>
    UserCommand,

    /// <summary>An archived visible conversation message supports the item.</summary>
    ConversationMessage,

    /// <summary>A host run supports the item.</summary>
    Run,

    /// <summary>A governed evidence item supports the item.</summary>
    Evidence,

    /// <summary>A durable artifact supports the item.</summary>
    Artifact,

    /// <summary>A host-owned domain event supports the item.</summary>
    HostEvent,

    /// <summary>A validation or test outcome supports the item.</summary>
    ValidationResult,
}

/// <summary>Defines current repository-memory schema versions.</summary>
public static class RepositoryMemorySchemaVersions
{
    /// <summary>Current repository-memory item schema.</summary>
    public const int Item = 1;

    /// <summary>Current repository-memory snapshot schema.</summary>
    public const int Snapshot = 1;
}

/// <summary>Path, symbol, and project scope metadata for repository memory invalidation.</summary>
public sealed record RepositoryMemoryScope
{
    /// <summary>Repository-relative paths that support the item.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Stable symbol names or ids that support the item.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>Repository-relative project paths or project identities that support the item.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];
}

/// <summary>One bounded provenance edge supporting repository memory.</summary>
public sealed record RepositoryMemorySource
{
    /// <summary>Source category.</summary>
    public required RepositoryMemorySourceKind Kind { get; init; }

    /// <summary>Stable source identifier.</summary>
    public required string SourceId { get; init; }

    /// <summary>Bounded source description suitable for inspection output.</summary>
    public string? Description { get; init; }
}

/// <summary>One local repository-scoped memory item with host-owned authority and audit state.</summary>
public sealed record RepositoryMemoryItem
{
    /// <summary>Stable memory identity.</summary>
    public required RepositoryMemoryId Id { get; init; }

    /// <summary>Stable repository identity that owns the item.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Typed repository-memory category.</summary>
    public required RepositoryMemoryKind Kind { get; init; }

    /// <summary>Authority supporting the item.</summary>
    public required RepositoryMemoryAuthority Authority { get; init; }

    /// <summary>Current host-owned validity state.</summary>
    public RepositoryMemoryValidity Validity { get; init; } = RepositoryMemoryValidity.Active;

    /// <summary>Sensitivity remaining after sanitization.</summary>
    public ConversationSensitivity Sensitivity { get; init; } = ConversationSensitivity.Sensitive;

    /// <summary>Sanitized bounded memory content.</summary>
    public required string Content { get; init; }

    /// <summary>SHA-256 hash of the sanitized content, populated by persistence.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>Repository revision that supports repository-dependent memory when known.</summary>
    public string? RepositoryRevision { get; init; }

    /// <summary>Path, symbol, and project scope used for invalidation and relevance.</summary>
    public RepositoryMemoryScope Scope { get; init; } = new();

    /// <summary>Provenance sources that authorized or support the item.</summary>
    public IReadOnlyList<RepositoryMemorySource> Sources { get; init; } = [];

    /// <summary>Older item replaced by this item.</summary>
    public RepositoryMemoryId? SupersedesId { get; init; }

    /// <summary>Reason an item was rejected, forgotten, or made stale.</summary>
    public string? StateReason { get; init; }

    /// <summary>Creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Latest state-change timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Durable repository-memory schema version.</summary>
    public int SchemaVersion { get; init; } = RepositoryMemorySchemaVersions.Item;
}

/// <summary>A detached repository-memory snapshot restored from local repository persistence.</summary>
public sealed record RepositoryMemorySnapshot
{
    /// <summary>Repository identity represented by this snapshot.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>All repository memory, including stale, superseded, rejected, and forgotten audit items.</summary>
    public IReadOnlyList<RepositoryMemoryItem> Items { get; init; } = [];

    /// <summary>Bounded restoration warnings for unsupported or malformed state.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Describes one durable repository-memory state update performed by a governor operation.</summary>
public sealed record RepositoryMemoryStateUpdate(
    RepositoryMemoryId MemoryId,
    RepositoryMemoryValidity PreviousValidity,
    RepositoryMemoryValidity Validity,
    string Reason);

/// <summary>Reports whether remember inserted an item and any capacity demotions it required.</summary>
public sealed record RepositoryMemoryRememberResult(
    RepositoryMemoryItem Item,
    bool WasInserted,
    IReadOnlyList<RepositoryMemoryStateUpdate> StateUpdates);

/// <summary>Describes one host-observed repository-memory candidate from an authoritative run boundary.</summary>
public sealed record HostObservedRepositoryMemoryPromotion(
    SessionId SessionId,
    RunId RunId,
    string RepositoryIdentity,
    RepositoryMemoryKind Kind,
    string Content);

/// <summary>Reports a replacement item and any capacity demotions required before it became active.</summary>
public sealed record RepositoryMemorySupersedeResult(
    RepositoryMemoryItem Item,
    IReadOnlyList<RepositoryMemoryStateUpdate> StateUpdates);

/// <summary>Reports the post-validation snapshot and the durable validity updates that produced it.</summary>
public sealed record RepositoryMemoryValidationResult(
    RepositoryMemorySnapshot Snapshot,
    IReadOnlyList<RepositoryMemoryStateUpdate> StateUpdates);

/// <summary>Persists local repository-scoped memory inside the repository database boundary.</summary>
public interface IRepositoryMemoryStore
{
    /// <summary>Inserts or updates one repository-memory item and its provenance.</summary>
    Task<RepositoryMemoryItem> UpsertAsync(
        RepositoryMemoryItem item,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically applies capacity state changes and inserts or updates one repository-memory item.</summary>
    Task<RepositoryMemoryItem> UpsertWithStateUpdatesAsync(
        RepositoryMemoryItem item,
        IReadOnlyList<RepositoryMemoryStateUpdate> stateUpdates,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically inserts one item and demotes overflow under the supplied active-item bound.</summary>
    Task<RepositoryMemoryRememberResult> InsertBoundedAsync(
        RepositoryMemoryItem item,
        int maximumActiveItems,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically inserts a replacement item and marks the superseded item inactive.</summary>
    Task<RepositoryMemoryItem> SupersedeAsync(
        string repositoryIdentity,
        RepositoryMemoryId supersededId,
        RepositoryMemoryItem replacement,
        IReadOnlyList<RepositoryMemoryStateUpdate> stateUpdates,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Updates one item validity without deleting its audit metadata.</summary>
    Task<bool> UpdateValidityAsync(
        string repositoryIdentity,
        RepositoryMemoryId memoryId,
        RepositoryMemoryValidity validity,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Gets detached repository-memory state with tolerant schema handling.</summary>
    Task<RepositoryMemorySnapshot> GetSnapshotAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>Applies host-owned policy to repository-scoped memory commands.</summary>
public interface IRepositoryMemoryGovernor
{
    /// <summary>Creates an explicit user-authored repository-memory item.</summary>
    Task<RepositoryMemoryRememberResult> RememberAsync(
        RememberRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Promotes one host-observed run outcome through the same bounded policy as explicit memory.</summary>
    Task<RepositoryMemoryRememberResult> PromoteHostObservedAsync(
        HostObservedRepositoryMemoryPromotion promotion,
        CancellationToken cancellationToken = default);

    /// <summary>Lists repository memory using host-owned filters.</summary>
    Task<RepositoryMemorySnapshot> ListAsync(
        ListRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Inspects one repository memory item.</summary>
    Task<RepositoryMemoryItem?> InspectAsync(
        InspectRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Corrects a repository memory item by superseding it with replacement text.</summary>
    Task<RepositoryMemorySupersedeResult> SupersedeAsync(
        SupersedeRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets one repository memory item without deleting its audit metadata.</summary>
    Task<bool> ForgetAsync(
        ForgetRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Revalidates repository-dependent memory at an explicit host boundary.</summary>
    Task<RepositoryMemoryValidationResult> ValidateAsync(
        ValidateRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates an explicit repository-scoped memory item.</summary>
public sealed record RememberRepositoryMemoryCommand(
    SessionId SessionId,
    string RepositoryIdentity,
    string Text,
    RepositoryMemoryKind Kind) : ICommand<RepositoryMemoryItem>;

/// <summary>Lists repository-scoped memory using a host-owned filter.</summary>
public sealed record ListRepositoryMemoryCommand(
    SessionId SessionId,
    string RepositoryIdentity,
    RepositoryMemoryValidity? Validity = null) : ICommand<RepositoryMemorySnapshot>;

/// <summary>Inspects one repository-scoped memory item and its provenance.</summary>
public sealed record InspectRepositoryMemoryCommand(
    SessionId SessionId,
    string RepositoryIdentity,
    RepositoryMemoryId MemoryId) : ICommand<RepositoryMemoryItem?>;

/// <summary>Supersedes one repository-scoped memory item with replacement text.</summary>
public sealed record SupersedeRepositoryMemoryCommand(
    SessionId SessionId,
    string RepositoryIdentity,
    RepositoryMemoryId MemoryId,
    string ReplacementText) : ICommand<RepositoryMemoryItem>;

/// <summary>Forgets one repository-scoped memory item without deleting audit metadata.</summary>
public sealed record ForgetRepositoryMemoryCommand(
    SessionId SessionId,
    string RepositoryIdentity,
    RepositoryMemoryId MemoryId) : ICommand<bool>;

/// <summary>Requests host-owned revalidation of repository-dependent memory.</summary>
public sealed record ValidateRepositoryMemoryCommand(
    SessionId SessionId,
    string RepositoryIdentity) : ICommand<RepositoryMemorySnapshot>;
