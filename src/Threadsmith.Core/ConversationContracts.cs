namespace Threadsmith.Core;

using System.Collections.ObjectModel;

/// <summary>Selects which durable conversational state may enter future model requests.</summary>
public enum ConversationContextMode
{
    /// <summary>Use bounded recent turns and governed memory.</summary>
    ConversationAware,

    /// <summary>Use governed memory but no raw prior messages.</summary>
    GovernedMemoryOnly,

    /// <summary>Use only the current turn and current-run governed state.</summary>
    Stateless,
}

/// <summary>Identifies the host-owned role of an archived visible message.</summary>
public enum ConversationRole
{
    /// <summary>A request accepted from the user.</summary>
    User,

    /// <summary>A final response visible to the user.</summary>
    Assistant,
}

/// <summary>Classifies durable conversation content for policy and provider selection.</summary>
public enum ConversationSensitivity
{
    /// <summary>No sensitive content is known after sanitization.</summary>
    None,

    /// <summary>The sanitized content remains repository-sensitive.</summary>
    Sensitive,
}

/// <summary>Identifies one independently governed memory category.</summary>
public enum ConversationMemoryKind
{
    /// <summary>An explicit user requirement.</summary>
    UserRequirement,

    /// <summary>An accepted or user-authored decision.</summary>
    Decision,

    /// <summary>An explicit constraint.</summary>
    Constraint,

    /// <summary>A question that remains unresolved.</summary>
    UnresolvedQuestion,

    /// <summary>A repository fact backed by governed evidence.</summary>
    RepositoryFinding,

    /// <summary>Work observed by the host to have completed.</summary>
    CompletedWork,

    /// <summary>Information retained only to record rejection or supersession.</summary>
    RejectedOrSuperseded,
}

/// <summary>Describes whether a memory item may be selected for a future request.</summary>
public enum MemoryValidity
{
    /// <summary>The item is active and eligible.</summary>
    Active,

    /// <summary>A repository change made the item stale pending revalidation.</summary>
    Stale,

    /// <summary>A later item explicitly superseded this item.</summary>
    Superseded,

    /// <summary>The item failed host validation and is not eligible.</summary>
    Invalid,
}

/// <summary>Defines current durable conversation schema versions.</summary>
public static class ConversationSchemaVersions
{
    /// <summary>Current archived-message schema.</summary>
    public const int Message = 1;

    /// <summary>Current governed-memory schema.</summary>
    public const int Memory = 1;

    /// <summary>Current summary-snapshot schema.</summary>
    public const int Summary = 1;
}

/// <summary>One sanitized visible conversation message with stable provenance.</summary>
public sealed record ConversationMessage
{
    /// <summary>Stable message identity.</summary>
    public required ConversationMessageId Id { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Run that produced or accepted the message.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Monotonic one-based position within the session archive.</summary>
    public required long Sequence { get; init; }

    /// <summary>Host-owned visible role.</summary>
    public required ConversationRole Role { get; init; }

    /// <summary>Sanitized visible body when retained inline.</summary>
    public string? Content { get; init; }

    /// <summary>Content-addressed artifact hash when the body is externalized.</summary>
    public string? ArtifactId { get; init; }

    /// <summary>SHA-256 hash of the sanitized body.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Estimated body tokens.</summary>
    public required int EstimatedTokens { get; init; }

    /// <summary>Sensitivity remaining after sanitization.</summary>
    public ConversationSensitivity Sensitivity { get; init; }

    /// <summary>Repository revision at capture when known.</summary>
    public string? RepositoryRevision { get; init; }

    /// <summary>Capture timestamp.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Durable message schema version.</summary>
    public int SchemaVersion { get; init; } = ConversationSchemaVersions.Message;
}

/// <summary>One attributable and independently invalidatable governed memory item.</summary>
public sealed record ConversationMemoryItem
{
    /// <summary>Stable memory identity.</summary>
    public required ConversationMemoryId Id { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Typed memory category.</summary>
    public required ConversationMemoryKind Kind { get; init; }

    /// <summary>Sanitized bounded content.</summary>
    public required string Content { get; init; }

    /// <summary>Originating archived messages.</summary>
    public required IReadOnlyList<ConversationMessageId> SourceMessageIds { get; init; }

    /// <summary>Originating runs.</summary>
    public IReadOnlyList<RunId> SourceRunIds { get; init; } = [];

    /// <summary>Governed evidence supporting the item.</summary>
    public IReadOnlyList<EvidenceId> SourceEvidenceIds { get; init; } = [];

    /// <summary>Optional supporting artifact identifiers.</summary>
    public IReadOnlyList<string> SourceArtifactIds { get; init; } = [];

    /// <summary>Repository revision supporting a repository-dependent claim.</summary>
    public string? RepositoryRevision { get; init; }

    /// <summary>Whether repository mutation can invalidate the item.</summary>
    public bool RepositoryDependent { get; init; }

    /// <summary>Older item replaced by this item.</summary>
    public ConversationMemoryId? SupersedesId { get; init; }

    /// <summary>Current host-owned validity state.</summary>
    public MemoryValidity Validity { get; init; } = MemoryValidity.Active;

    /// <summary>Creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Latest state-change timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Durable memory schema version.</summary>
    public int SchemaVersion { get; init; } = ConversationSchemaVersions.Memory;
}

/// <summary>An atomic host-owned index over active governed memory.</summary>
public sealed record ConversationSummarySnapshot
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Monotonic snapshot version.</summary>
    public required long Version { get; init; }

    /// <summary>Last archived sequence represented by the snapshot.</summary>
    public required long ThroughMessageSequence { get; init; }

    /// <summary>Repository revision at snapshot creation.</summary>
    public string? RepositoryRevision { get; init; }

    /// <summary>Ordered active IDs grouped by category.</summary>
    public IReadOnlyDictionary<ConversationMemoryKind, IReadOnlyList<ConversationMemoryId>> MemoryIdsByKind { get; init; } =
        new ReadOnlyDictionary<ConversationMemoryKind, IReadOnlyList<ConversationMemoryId>>(
            new Dictionary<ConversationMemoryKind, IReadOnlyList<ConversationMemoryId>>());

    /// <summary>Snapshot creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Durable summary schema version.</summary>
    public int SchemaVersion { get; init; } = ConversationSchemaVersions.Summary;
}

/// <summary>A detached durable conversation state snapshot.</summary>
public sealed record ConversationStateSnapshot
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Effective session mode.</summary>
    public ConversationContextMode Mode { get; init; } = ConversationContextMode.ConversationAware;

    /// <summary>Ordered archived message metadata and retained bodies.</summary>
    public IReadOnlyList<ConversationMessage> Messages { get; init; } = [];

    /// <summary>All governed memory including stale and superseded items.</summary>
    public IReadOnlyList<ConversationMemoryItem> MemoryItems { get; init; } = [];

    /// <summary>Current active summary snapshot.</summary>
    public ConversationSummarySnapshot? Summary { get; init; }

    /// <summary>Bounded restoration warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Archives and restores sanitized conversation state.</summary>
public interface IConversationStore
{
    /// <summary>Archives one visible message and assigns its deterministic session sequence.</summary>
    Task<ConversationMessage> ArchiveMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>Changes the durable session mode without deleting archive or memory.</summary>
    Task SetModeAsync(
        SessionId sessionId,
        ConversationContextMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>Writes memory items and replaces the active snapshot atomically.</summary>
    Task ReplaceSummaryAsync(
        SessionId sessionId,
        IReadOnlyList<ConversationMemoryItem> items,
        ConversationSummarySnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an existing memory item's validity while preserving audit history.</summary>
    Task UpdateMemoryAsync(
        ConversationMemoryItem item,
        CancellationToken cancellationToken = default);

    /// <summary>Gets detached state with tolerant schema handling.</summary>
    Task<ConversationStateSnapshot> GetSnapshotAsync(
        SessionId sessionId,
        bool includeBodies = true,
        CancellationToken cancellationToken = default);

    /// <summary>Removes retained message bodies while preserving metadata and provenance.</summary>
    Task<int> RemoveMessageBodiesOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default);
}

/// <summary>Changes conversation mode for future requests.</summary>
public sealed record SetConversationContextModeCommand(
    SessionId SessionId,
    ConversationContextMode Mode) : ICommand<bool>;

/// <summary>Queries durable archive and governed-memory metadata.</summary>
public sealed record GetConversationStateCommand(
    SessionId SessionId,
    bool IncludeBodies = false) : ICommand<ConversationStateSnapshot>;

/// <summary>Queries the latest context inspection for a run.</summary>
public sealed record GetContextInspectionCommand(RunId RunId) : ICommand<ContextInspectionProjection?>;

/// <summary>Requests compaction at the next safe host-owned turn boundary.</summary>
public sealed record RequestConversationCompactionCommand(SessionId SessionId) : ICommand<bool>;
