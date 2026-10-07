namespace Threadsmith.Core;

using System.Text.Json.Serialization;

/// <summary>Applies one ordered set of source instructions through exact authorization and the transactional writer.</summary>
public sealed record ApplySourceEditCommand(
    SessionId SessionId,
    RunId RunId,
    WorkspaceId WorkspaceId,
    Guid EffectId,
    MutationProposalSet Instructions,
    bool RequireReview = false) : ICommand<SourceEditReceipt>;

/// <summary>Resolves an exact pending edit review without starting another execution workflow.</summary>
public sealed record AuthorizeSourceEditCommand(
    SessionId SessionId,
    MutationSetId MutationSetId,
    MutationApproval Approval) : ICommand<MutationCommitResult>;

/// <summary>Declines an exact pending edit.</summary>
public sealed record RejectSourceEditCommand(
    SessionId SessionId,
    MutationSetId MutationSetId) : ICommand<bool>;

/// <summary>Durable disk outcome, independent of advisory compiler findings.</summary>
public enum SourceEditStatus
{
    /// <summary>Exact final bytes were verified.</summary>
    Applied,

    /// <summary>No effect was applied, or compensation restored the exact original bytes.</summary>
    NotApplied,

    /// <summary>The exact change was not authorized.</summary>
    Denied,

    /// <summary>The repository no longer satisfies the source preconditions.</summary>
    Conflict,

    /// <summary>The effect requires explicit reconciliation; it must never be replayed automatically.</summary>
    RecoveryRequired,
}

/// <summary>Bounded model-visible result of an edit.</summary>
public sealed record SourceEditReceipt(
    Guid EffectId,
    MutationSetId MutationSetId,
    SourceEditStatus Status,
    IReadOnlyList<string> ChangedFiles,
    string Detail)
{
    /// <summary>Whether the proven outcome was durably recorded; false blocks admission of later edits.</summary>
    public bool DurableOutcomeRecorded { get; init; } = true;

    /// <summary>Authoritative writer result when available.</summary>
    public MutationCommitResult? Commit { get; init; }

    /// <summary>Versioned advisory analysis; compiler findings never change the disk outcome.</summary>
    public SourceEditAnalysis? Analysis { get; init; }
}

/// <summary>Exact byte identities owned by the writer for one touched endpoint.</summary>
public sealed record MutationEndpointSnapshot(
    string RelativePath,
    string? BeforeSha256,
    string? AfterSha256)
{
    /// <summary>Ephemeral exact encoded candidate, never persisted in effect artifacts.</summary>
    [JsonIgnore]
    public byte[]? FinalBytes { get; init; }
}

/// <summary>Exact authorized subset and its endpoint pre/post identities.</summary>
public sealed record MutationEffectSnapshot(
    MutationSetId MutationSetId,
    IReadOnlyList<MutationId> AppliedMutations,
    IReadOnlyList<MutationEndpointSnapshot> Endpoints);

/// <summary>Conservative comparison of durable intent with current disk bytes.</summary>
public enum MutationEffectReconciliation
{
    /// <summary>Every original identity remains present.</summary>
    Original,

    /// <summary>Every authorized final identity is present.</summary>
    Applied,

    /// <summary>Neither complete identity set can be established.</summary>
    Indeterminate,
}

/// <summary>Plan-independent durable write intent and terminal receipt.</summary>
public sealed record MutationEffectRecord
{
    /// <summary>Original text evidence for the authorized endpoints, retained before the write for cumulative diff reporting.</summary>
    public IReadOnlyDictionary<string, ExecutionArtifactReference?> OriginalFiles { get; init; } = new Dictionary<string, ExecutionArtifactReference?>();

    /// <summary>Version of this record, independent of historical plan checkpoints.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Host-derived retry identity, stable across replay of the same invocation.</summary>
    public required Guid EffectId { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning conversation run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Workspace at admission.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Canonical repository identity for recovery across workspace bindings.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Digest of the exact instructions; retry identity cannot authorize different arguments.</summary>
    public required string RequestIdentity { get; init; }

    /// <summary>Transaction identifier.</summary>
    public required MutationSetId MutationSetId { get; init; }

    /// <summary>Content-addressed exact before/after snapshot, published before write intent.</summary>
    public ExecutionArtifactReference? SnapshotArtifact { get; init; }

    /// <summary>Whether this record admitted a writer invocation; false records only an immutable rejection.</summary>
    public bool HasWriteIntent { get; init; } = true;

    /// <summary>Verified terminal outcome; null means intent was written but delivery remains uncertain.</summary>
    public SourceEditReceipt? Receipt { get; init; }
}

/// <summary>Extends the execution checkpoint owner with independently keyed edit intents.</summary>
public interface IMutationEffectStore
{
    /// <summary>Streams one owner's effects in durable insertion order for cumulative evidence without loading other runs.</summary>
    IAsyncEnumerable<MutationEffectRecord> ReadRunEffectsAsync(SessionId sessionId, RunId runId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Run effect history is unavailable.");
    }

    /// <summary>Atomically inserts immutable intent; false means the identity already exists.</summary>
    Task<bool> TryBeginEffectAsync(MutationEffectRecord record, CancellationToken cancellationToken = default);

    /// <summary>Gets the exact durable effect identity.</summary>
    Task<MutationEffectRecord?> GetEffectAsync(Guid effectId, CancellationToken cancellationToken = default);

    /// <summary>Persists the terminal outcome of an existing intent.</summary>
    Task CompleteEffectAsync(Guid effectId, SourceEditReceipt receipt, CancellationToken cancellationToken = default);

    /// <summary>Gets unresolved intents for a repository before allowing further writes.</summary>
    Task<IReadOnlyList<MutationEffectRecord>> GetUnresolvedEffectsAsync(string repositoryIdentity, CancellationToken cancellationToken = default);
}
