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

/// <summary>Creates an explicit manual repository memory.</summary>
public sealed record RememberRepositoryMemoryCommand(SessionId SessionId, string RepositoryIdentity, string Text)
    : ICommand<RepositoryMemoryEntry>;

/// <summary>Lists current repository memories.</summary>
public sealed record ListRepositoryMemoryCommand(SessionId SessionId, string RepositoryIdentity)
    : ICommand<RepositoryMemoryReadSnapshot>;

/// <summary>Inspects one current memory including usage and embedding availability.</summary>
public sealed record InspectRepositoryMemoryCommand(SessionId SessionId, string RepositoryIdentity, RepositoryMemoryId MemoryId)
    : ICommand<RepositoryMemoryEntry?>;

/// <summary>Corrects an existing entry in place, preserving its stable ID.</summary>
public sealed record UpdateRepositoryMemoryCommand(SessionId SessionId, string RepositoryIdentity, RepositoryMemoryId MemoryId, string ReplacementText)
    : ICommand<RepositoryMemoryEntry>;

/// <summary>Compatibility alias for an in-place update; no supersession record is created.</summary>
public sealed record SupersedeRepositoryMemoryCommand(SessionId SessionId, string RepositoryIdentity, RepositoryMemoryId MemoryId, string ReplacementText)
    : ICommand<RepositoryMemoryEntry>;

/// <summary>Deletes a repository memory and its current search and usage rows.</summary>
public sealed record ForgetRepositoryMemoryCommand(SessionId SessionId, string RepositoryIdentity, RepositoryMemoryId MemoryId)
    : ICommand<bool>;

/// <summary>Retired command retained solely to return an actionable migration message.</summary>
public sealed record ValidateRepositoryMemoryCommand(SessionId SessionId, string RepositoryIdentity)
    : ICommand<RepositoryMemoryReadSnapshot>;
