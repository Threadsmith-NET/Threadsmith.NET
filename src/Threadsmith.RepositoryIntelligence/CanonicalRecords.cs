namespace Threadsmith.RepositoryIntelligence;

using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Independent applicability, never a freshness or confidence score.</summary>
internal enum IntelligenceApplicability
{
    Current,
    Superseded,
    PartiallySuperseded,
    HistoricalOnly,
    Uncertain,
}

/// <summary>Strength of an interpretation, independent of documentation and recency.</summary>
internal enum IntelligenceConfidence
{
    Unknown,
    Low,
    Medium,
    High,
}

/// <summary>Provenance strength, independent of current applicability.</summary>
internal enum IntelligenceEvidenceClass
{
    Unknown,
    Documented,
    StronglyReconstructed,
    Inferred,
}

/// <summary>Evaluation state; storage never grants admission-time freshness.</summary>
internal enum IntelligenceFreshness
{
    Unknown,
    Evaluated,
    Unaffected,
    PendingReevaluation,
    Incompatible,
    Unavailable,
}

/// <summary>Typed directed links, evaluated against exact endpoint revisions.</summary>
internal enum IntelligenceRelationshipKind
{
    Motivation,
    Preservation,
    Supersession,
    Reversal,
    Correction,
    Implementation,
    Reinforcement,
    Association,
}

/// <summary>Kind-specific knowledge; null means unsupported or unknown, never an invented value.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DecisionKnowledge), "Decision")]
[JsonDerivedType(typeof(ConstraintKnowledge), "Constraint")]
[JsonDerivedType(typeof(ConventionKnowledge), "Convention")]
[JsonDerivedType(typeof(MigrationKnowledge), "Migration")]
[JsonDerivedType(typeof(ReversalKnowledge), "Reversal")]
[JsonDerivedType(typeof(HistoricalFailureKnowledge), "HistoricalFailure")]
[JsonDerivedType(typeof(PersistentPatternKnowledge), "PersistentPattern")]
[JsonDerivedType(typeof(OpenTensionKnowledge), "OpenTension")]
internal abstract record IntelligenceKnowledge;

/// <summary>A consequential choice with only supported rationale and alternatives.</summary>
internal sealed record DecisionKnowledge(string? Rationale, IReadOnlyList<string> Alternatives, string? EarlierChoice = null, string? Replacement = null) : IntelligenceKnowledge;

/// <summary>An invariant whose unsupported rationale remains unknown.</summary>
internal sealed record ConstraintKnowledge(string? Rationale, string? EarlierForm = null, string? Replacement = null) : IntelligenceKnowledge;

/// <summary>An intentional practice with optional rationale.</summary>
internal sealed record ConventionKnowledge(string? Rationale, string? EarlierForm = null, string? Replacement = null) : IntelligenceKnowledge;

/// <summary>A transition retains both mechanisms and the invariant that survived.</summary>
internal sealed record MigrationKnowledge(string? Original, string? Replacement, string? SurvivingInvariant) : IntelligenceKnowledge;

/// <summary>An abandoned approach and supported replacement or lesson.</summary>
internal sealed record ReversalKnowledge(string? Original, string? Replacement, string? Lesson) : IntelligenceKnowledge;

/// <summary>A historical failure retains impact and any supported resolution.</summary>
internal sealed record HistoricalFailureKnowledge(string? Impact, string? Resolution, string? Lesson, string? FailedApproach = null) : IntelligenceKnowledge;

/// <summary>A practice with supported historical persistence.</summary>
internal sealed record PersistentPatternKnowledge(string? HistoricalBounds, string? Invariant, string? EarlierForm = null, string? Replacement = null) : IntelligenceKnowledge;

/// <summary>An unresolved question without manufactured consensus.</summary>
internal sealed record OpenTensionKnowledge(string? Question, IReadOnlyList<string> Explanations, string? PriorExplanation = null, string? ProposedResolution = null) : IntelligenceKnowledge;

/// <summary>Preserves the causal claim and its exact supporting quote without turning sequence into rationale.</summary>
internal sealed record IntelligenceCausalSupport(bool Asserted, string? ExactQuote);

/// <summary>Scope anchors come from host observations; absent anchors remain absent.</summary>
internal sealed record IntelligenceScope(bool RepositoryWide, IReadOnlyList<string> Paths, IReadOnlyList<string> Modules, IReadOnlyList<string> Symbols);

/// <summary>Exact citations continue to describe the original revision after merges.</summary>
internal sealed record IntelligenceRevisionReference(Guid ItemId, long Revision);

/// <summary>Feature provenance references ordinary host activities without copying their event stream.</summary>
internal sealed record IntelligenceProvenance(
    string OperationId,
    SessionId SessionId,
    RunId RunId,
    ToolInvocationId InvocationId,
    GitSnapshotMetadata Target,
    DateTimeOffset EvaluatedAt,
    BoundedModelSelection? Model,
    string Method,
    string? UserAttribution = null);

/// <summary>Durable knowledge revision; assessment dimensions and runtime projection stay independent.</summary>
internal sealed record IntelligenceItemRevision(
    IntelligenceRevisionReference Reference,
    string IdentityKey,
    string Title,
    string Statement,
    IntelligenceKnowledge Knowledge,
    IntelligenceApplicability Applicability,
    IntelligenceConfidence Confidence,
    IntelligenceEvidenceClass EvidenceClass,
    IntelligenceFreshness Freshness,
    string Coverage,
    string? LastEvaluatedCommit,
    IntelligenceScope Scope,
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> SupportingEvidenceIds,
    IReadOnlyList<string> ConflictingEvidenceIds,
    string? Uncertainty,
    IntelligenceProvenance Created,
    IntelligenceProvenance Evaluated,
    string? Capsule,
    string? AtTarget = null,
    IntelligenceCausalSupport? CausalSupport = null);

/// <summary>Addressable conflict or unknown linked to its exact knowledge and source evidence.</summary>
internal sealed record IntelligenceUncertainty(Guid Id, IntelligenceRevisionReference Item, string Question, IReadOnlyList<string> EvidenceIds, IntelligenceProvenance Provenance);

/// <summary>Relationship provenance is separate from either endpoint's assessment.</summary>
internal sealed record IntelligenceRelationship(Guid Id, IntelligenceRevisionReference From, IntelligenceRevisionReference To, IntelligenceRelationshipKind Kind, string Explanation, IntelligenceConfidence Confidence, IReadOnlyList<string> EvidenceIds, IntelligenceProvenance Provenance, string? RationaleQuote = null);

/// <summary>Episodes retain constituent commits, source references and derived knowledge.</summary>
internal sealed record IntelligenceEpisode(string Id, string Scope, IReadOnlyList<string> Commits, IReadOnlyList<string> EvidenceIds, IReadOnlyList<IntelligenceRevisionReference> Items, IntelligenceProvenance Provenance);

/// <summary>A head change or merge alias with an optimistic precondition.</summary>
internal sealed record IntelligenceItemWrite(IntelligenceItemRevision Item, long ExpectedRevision, Guid? AliasTo = null);

/// <summary>A bounded coherent publication unit, never an incomplete candidate or baseline-completion flag.</summary>
internal sealed record IntelligencePublication(
    long ExpectedGeneration,
    IReadOnlyList<IntelligenceItemWrite> Items,
    IReadOnlyList<RepositoryEvidenceExcerpt> Evidence,
    IReadOnlyList<IntelligenceEpisode> Episodes,
    IReadOnlyList<IntelligenceRelationship> Relationships,
    IReadOnlyList<IntelligenceUncertainty> Uncertainties,
    IntelligenceContinuation? Continuation = null,
    IntelligenceReconciliationReceipt? Receipt = null,
    IReadOnlyList<IntelligenceClaimIdentity>? Claims = null);

/// <summary>Pinned unfinished work for later recovery; it does not duplicate durable host run events.</summary>
internal sealed record IntelligenceContinuation(string UnitId, IntelligenceProvenance Provenance, string Scope, string State, string? Cursor);

/// <summary>A detached bounded read tied to the set generation observed in one transaction.</summary>
internal sealed record IntelligenceReadSnapshot(long Generation, IReadOnlyList<IntelligenceItemRevision> Items, IReadOnlyDictionary<Guid, Guid> Aliases, bool HasMore = false);

/// <summary>Bounded graph inspection retains exact citations and explicitly reports more available records.</summary>
internal sealed record IntelligenceRelatedRecords(
    IReadOnlyList<IntelligenceRelationship> Relationships,
    IReadOnlyList<IntelligenceEpisode> Episodes,
    IReadOnlyList<IntelligenceUncertainty> Uncertainties,
    bool HasMore,
    IReadOnlyList<IntelligenceReconciliationReceipt>? Receipts = null);

/// <summary>Storage failures are explicit and bounded; previous committed knowledge is preserved.</summary>
internal enum IntelligenceStorageFailure
{
    Unavailable,
    Corrupt,
    MigrationFailed,
    RevisionConflict,
    IncompatibleContext,
    ReadLimit,
}

/// <summary>A classified storage failure without raw SQL, paths or database exception text.</summary>
internal sealed class IntelligenceStorageException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="IntelligenceStorageException"/> class.</summary>
    public IntelligenceStorageException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="IntelligenceStorageException"/> class.</summary>
    public IntelligenceStorageException(string? message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="IntelligenceStorageException"/> class.</summary>
    public IntelligenceStorageException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="IntelligenceStorageException"/> class.</summary>
    internal IntelligenceStorageException(IntelligenceStorageFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    /// <summary>The host-visible failure category.</summary>
    internal IntelligenceStorageFailure Failure { get; } = IntelligenceStorageFailure.Unavailable;
}
