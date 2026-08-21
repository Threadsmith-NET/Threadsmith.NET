namespace Threadsmith.Context;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Threadsmith.Core;

/// <summary>Classifies the authority supporting a proposed memory item.</summary>
public enum ConversationMemoryTrustClass
{
    /// <summary>Explicit user-authored requirement, correction, decision, constraint, or question.</summary>
    ExplicitUser,

    /// <summary>Outcome observed from host execution events.</summary>
    HostObserved,

    /// <summary>Current repository fact backed by governed evidence.</summary>
    GovernedEvidence,

    /// <summary>Assistant or compaction-model interpretation requiring strict source validation.</summary>
    AssistantProposed,
}

/// <summary>Identifies the execution phase requesting older memory.</summary>
public enum ConversationRetrievalPhase
{
    /// <summary>General conversational intake and evidence collection.</summary>
    General,

    /// <summary>Planning repository work.</summary>
    Planning,

    /// <summary>Preparing or applying a bounded code edit.</summary>
    CodeEdit,

    /// <summary>Validating completed work.</summary>
    Validation,
}

/// <summary>Classifies a compaction outcome without exposing model output.</summary>
public enum ConversationCompactionOutcomeKind
{
    /// <summary>No trigger threshold was met.</summary>
    NotRequired,

    /// <summary>The same source range was already compacted.</summary>
    AlreadyCompacted,

    /// <summary>A new snapshot was validated and persisted.</summary>
    Completed,

    /// <summary>Candidate output was malformed or exceeded hard bounds.</summary>
    MalformedOutput,

    /// <summary>Candidate provenance did not support its claims.</summary>
    UnsupportedProvenance,

    /// <summary>The candidate provider failed transiently.</summary>
    ProviderFailure,

    /// <summary>The operation was cancelled and prior state was retained.</summary>
    Cancelled,

    /// <summary>Persistence failed and prior state was retained.</summary>
    PersistenceFailure,
}

/// <summary>Bounded turn-boundary compaction and retrieval policy.</summary>
public sealed record ConversationCompactionPolicy
{
    /// <summary>Uncompacted token estimate that triggers compaction.</summary>
    public int ArchivedTokenThreshold { get; init; } = 8_000;

    /// <summary>Uncompacted message count that triggers compaction.</summary>
    public int ArchivedMessageThreshold { get; init; } = 12;

    /// <summary>Maximum active items before compaction is required.</summary>
    public int MaximumActiveMemoryItems { get; init; } = 128;

    /// <summary>Maximum candidate items in one replacement.</summary>
    public int MaximumCandidateItems { get; init; } = 64;

    /// <summary>Maximum characters in one item.</summary>
    public int MaximumItemCharacters { get; init; } = 2_000;

    /// <summary>Maximum source messages supplied to one compaction call.</summary>
    public int MaximumSourceMessages { get; init; } = 100;

    /// <summary>Maximum transient provider retries.</summary>
    public int MaximumProviderRetries { get; init; } = 1;

    /// <summary>Maximum compaction model calls per operation including retries.</summary>
    public int MaximumProviderCalls { get; init; } = 2;

    /// <summary>Maximum estimated tokens supplied to compaction.</summary>
    public int MaximumInputTokens { get; init; } = 16_000;

    /// <summary>Validates every configurable bound.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ArchivedTokenThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ArchivedMessageThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumActiveMemoryItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCandidateItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumItemCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSourceMessages);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumProviderRetries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProviderCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInputTokens);
        if (MaximumProviderRetries + 1 > MaximumProviderCalls)
        {
            throw new ArgumentException("Provider retries exceed the compaction call budget.");
        }
    }
}

/// <summary>One typed memory proposal with explicit support and authority.</summary>
public sealed record ConversationMemoryCandidate
{
    /// <summary>Stable proposed identity.</summary>
    public required ConversationMemoryId Id { get; init; }

    /// <summary>Proposed category.</summary>
    public required ConversationMemoryKind Kind { get; init; }

    /// <summary>Sanitized bounded proposed content.</summary>
    public required string Content { get; init; }

    /// <summary>Authority used by validation and preservation policy.</summary>
    public required ConversationMemoryTrustClass TrustClass { get; init; }

    /// <summary>Cited archived messages.</summary>
    public required IReadOnlyList<ConversationMessageId> SourceMessageIds { get; init; }

    /// <summary>Cited runs.</summary>
    public IReadOnlyList<RunId> SourceRunIds { get; init; } = [];

    /// <summary>Cited governed evidence.</summary>
    public IReadOnlyList<EvidenceId> SourceEvidenceIds { get; init; } = [];

    /// <summary>Cited artifacts.</summary>
    public IReadOnlyList<string> SourceArtifactIds { get; init; } = [];

    /// <summary>Repository revision supporting the claim.</summary>
    public string? RepositoryRevision { get; init; }

    /// <summary>Whether repository invalidation applies.</summary>
    public bool RepositoryDependent { get; init; }

    /// <summary>Older item explicitly superseded by this proposal.</summary>
    public ConversationMemoryId? SupersedesId { get; init; }
}

/// <summary>Versioned structured candidate produced for host validation.</summary>
public sealed record ConversationSummaryCandidate
{
    /// <summary>Supported candidate schema.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Last source sequence represented by the candidate.</summary>
    public required long ThroughMessageSequence { get; init; }

    /// <summary>Typed independently attributable proposals.</summary>
    public IReadOnlyList<ConversationMemoryCandidate> Items { get; init; } = [];
}

/// <summary>Bounded provider-neutral compaction input.</summary>
public sealed record ConversationCompactionRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Immutable ordered archive range.</summary>
    public required IReadOnlyList<ConversationMessage> Messages { get; init; }

    /// <summary>Current governed memory including supersession state.</summary>
    public required IReadOnlyList<ConversationMemoryItem> ExistingMemory { get; init; }

    /// <summary>Current active summary when present.</summary>
    public ConversationSummarySnapshot? ExistingSummary { get; init; }

    /// <summary>Current governed evidence available for source support.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
}

/// <summary>Provider-neutral source of structured compaction candidates.</summary>
public interface IConversationSummaryCandidateProvider
{
    /// <summary>Creates an untrusted structured candidate from bounded host input.</summary>
    Task<ConversationSummaryCandidate> CreateCandidateAsync(
        ConversationCompactionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of strict candidate validation.</summary>
public sealed record ConversationSummaryValidationResult(
    bool IsValid,
    ConversationCompactionOutcomeKind FailureKind,
    IReadOnlyList<string> Errors);

/// <summary>Strictly validates source-bound structured summary candidates.</summary>
public interface IConversationSummaryValidator
{
    /// <summary>Validates schema, bounds, provenance, support, sensitivity, and supersession.</summary>
    ConversationSummaryValidationResult Validate(
        ConversationCompactionRequest request,
        ConversationSummaryCandidate candidate);
}

/// <summary>Result of one safe turn-boundary compaction attempt.</summary>
public sealed record ConversationCompactionResult(
    ConversationCompactionOutcomeKind Outcome,
    string Rationale,
    long? SnapshotVersion = null,
    int ProviderCalls = 0);

/// <summary>Compacts eligible archive ranges only at safe turn boundaries.</summary>
public interface IConversationCompactor
{
    /// <summary>Evaluates triggers and safely replaces the active snapshot when validation succeeds.</summary>
    Task<ConversationCompactionResult> CompactAtTurnBoundaryAsync(
        SessionId sessionId,
        IReadOnlyList<Evidence> evidence,
        bool force = false,
        CancellationToken cancellationToken = default);
}

/// <summary>Typed deterministic promotion input from authoritative host boundaries.</summary>
public sealed record ConversationPromotionRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Archived user message supplying explicit provenance.</summary>
    public required ConversationMessage SourceMessage { get; init; }

    /// <summary>Explicit user requirements.</summary>
    public IReadOnlyList<string> UserRequirements { get; init; } = [];

    /// <summary>Explicit decisions or corrections.</summary>
    public IReadOnlyList<string> Decisions { get; init; } = [];

    /// <summary>Explicit user constraints.</summary>
    public IReadOnlyList<string> Constraints { get; init; } = [];

    /// <summary>Unresolved user questions.</summary>
    public IReadOnlyList<string> UnresolvedQuestions { get; init; } = [];

    /// <summary>Host-observed completed work; never inferred from assistant text.</summary>
    public IReadOnlyList<string> CompletedWork { get; init; } = [];

    /// <summary>Current governed evidence eligible for repository-finding promotion.</summary>
    public IReadOnlyList<Evidence> RepositoryEvidence { get; init; } = [];

    /// <summary>Explicit prior item replaced by a user correction.</summary>
    public ConversationMemoryId? SupersedesId { get; init; }
}

/// <summary>Promotes authoritative turn state into structured governed memory.</summary>
public interface IConversationMemoryGovernor
{
    /// <summary>Promotes validated typed sources and atomically replaces the active snapshot.</summary>
    Task<ConversationSummarySnapshot> PromoteAsync(
        ConversationPromotionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>One deterministic retrieval rationale code.</summary>
public sealed record ConversationRetrievalRationale(
    string Code,
    double Score,
    IReadOnlyList<string> MatchedTerms);

/// <summary>One selected memory item with provenance-preserving rationale.</summary>
public sealed record ConversationRetrievedMemory(
    ConversationMemoryItem Item,
    ConversationRetrievalRationale Rationale);

/// <summary>Host-owned deterministic retrieval input.</summary>
public sealed record ConversationRetrievalRequest
{
    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Current task and acceptance text used for lexical scoring.</summary>
    public required string Query { get; init; }

    /// <summary>Execution phase applying category priority.</summary>
    public ConversationRetrievalPhase Phase { get; init; }

    /// <summary>Maximum selected items.</summary>
    public int MaximumItems { get; init; } = 24;

    /// <summary>Maximum aggregate estimated tokens.</summary>
    public int MaximumTokens { get; init; } = 4_000;
}

/// <summary>Deterministic older-memory selection result.</summary>
public sealed record ConversationRetrievalResult(
    IReadOnlyList<ConversationRetrievedMemory> Selected,
    int CandidateCount,
    int ExcludedStaleOrSupersededCount);

/// <summary>Retrieves eligible older memory with stable host-owned scoring.</summary>
public interface IConversationMemoryRetriever
{
    /// <summary>Selects relevant active memory and returns rationale and provenance.</summary>
    Task<ConversationRetrievalResult> RetrieveAsync(
        ConversationRetrievalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Repository-aware invalidation result.</summary>
public sealed record ConversationMemoryInvalidationResult(
    int InvalidatedCount,
    IReadOnlyList<ConversationMemoryId> MemoryIds);

/// <summary>Connects repository turn-boundary invalidation to governed memory.</summary>
public interface IConversationMemoryInvalidator
{
    /// <summary>Marks affected repository-dependent memory stale without deleting it.</summary>
    Task<ConversationMemoryInvalidationResult> InvalidateAtTurnBoundaryAsync(
        SessionId sessionId,
        IReadOnlyList<string> invalidationKeys,
        string? currentRepositoryRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>Deterministically promotes authoritative typed turn state.</summary>
public sealed class ConversationMemoryGovernor : IConversationMemoryGovernor
{
    private readonly ConversationCompactionPolicy _policy;
    private readonly IConversationStore _store;
    private readonly IOutputSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="ConversationMemoryGovernor"/> class.</summary>
    public ConversationMemoryGovernor(
        IConversationStore store,
        IOutputSanitizer sanitizer,
        ConversationCompactionPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _policy = policy ?? new ConversationCompactionPolicy();
        _policy.Validate();
        _store = store;
        _sanitizer = sanitizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ConversationSummarySnapshot> PromoteAsync(
        ConversationPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceMessage.SessionId != request.SessionId
            || request.SourceMessage.Role != ConversationRole.User)
        {
            throw new ArgumentException("Promotion requires an archived user message from the owning session.", nameof(request));
        }

        var state = await _store.GetSnapshotAsync(
            request.SessionId,
            includeBodies: false,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var items = state.MemoryItems.ToList();
        if (request.SupersedesId is { } supersededId)
        {
            var index = items.FindIndex(item => item.Id == supersededId);
            if (index < 0)
            {
                throw new ArgumentException("The superseded memory item does not exist.", nameof(request));
            }

            items[index] = items[index] with
            {
                Validity = MemoryValidity.Superseded,
                UpdatedAt = now,
            };
        }

        var activeKeys = items
            .Where(item => item.Validity == MemoryValidity.Active)
            .Select(item => CreateDeduplicationKey(item.Kind, item.Content))
            .ToHashSet(StringComparer.Ordinal);
        AddTextCandidates(items, activeKeys, request, request.UserRequirements, ConversationMemoryKind.UserRequirement, now);
        AddTextCandidates(items, activeKeys, request, request.Decisions, ConversationMemoryKind.Decision, now, request.SupersedesId);
        AddTextCandidates(
            items,
            activeKeys,
            request,
            request.Constraints,
            ConversationMemoryKind.Constraint,
            now,
            request.SupersedesId);
        AddTextCandidates(items, activeKeys, request, request.UnresolvedQuestions, ConversationMemoryKind.UnresolvedQuestion, now);
        AddTextCandidates(items, activeKeys, request, request.CompletedWork, ConversationMemoryKind.CompletedWork, now);
        foreach (var evidence in request.RepositoryEvidence
            .Where(item => !item.IsStale
                && item.SessionId == request.SessionId
                && !string.IsNullOrWhiteSpace(item.Provenance.RepositoryRevision)))
        {
            var content = BoundContent(_sanitizer.Sanitize(evidence.Content));
            var key = CreateDeduplicationKey(ConversationMemoryKind.RepositoryFinding, content);
            if (!activeKeys.Add(key))
            {
                continue;
            }

            items.Add(new ConversationMemoryItem
            {
                Id = ConversationMemoryId.New(),
                SessionId = request.SessionId,
                Kind = ConversationMemoryKind.RepositoryFinding,
                Content = content,
                SourceMessageIds = [request.SourceMessage.Id],
                SourceRunIds = [request.SourceMessage.RunId],
                SourceEvidenceIds = [evidence.EvidenceId],
                RepositoryRevision = evidence.Provenance.RepositoryRevision,
                RepositoryDependent = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        EnforceActiveItemBound(items, now);

        var version = (state.Summary?.Version ?? 0) + 1;
        var snapshot = new ConversationSummarySnapshot
        {
            SessionId = request.SessionId,
            Version = version,
            ThroughMessageSequence = request.SourceMessage.Sequence,
            MemoryIdsByKind = items
                .Where(item => item.Validity == MemoryValidity.Active)
                .GroupBy(item => item.Kind)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ConversationMemoryId>)[
                        .. group
                            .OrderBy(item => item.CreatedAt)
                            .ThenBy(item => item.Id.Value)
                            .Select(item => item.Id),
                    ]),
            CreatedAt = now,
        };
        await _store.ReplaceSummaryAsync(request.SessionId, items, snapshot, cancellationToken);
        return snapshot;
    }

    private static string CreateDeduplicationKey(ConversationMemoryKind kind, string content)
    {
        return $"{kind}:{Normalize(content)}";
    }

    private static string Normalize(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }

    private string BoundContent(string content)
    {
        return content.Length <= _policy.MaximumItemCharacters
            ? content
            : content[.._policy.MaximumItemCharacters];
    }

    private void EnforceActiveItemBound(List<ConversationMemoryItem> items, DateTimeOffset now)
    {
        var activeItemCount = items.Count(item => item.Validity == MemoryValidity.Active);
        var overflowCount = Math.Max(0, activeItemCount - _policy.MaximumActiveMemoryItems);
        ConversationMemoryItem[] overflow =
        [
            .. items
                .Where(item => item.Validity == MemoryValidity.Active)
                .OrderByDescending(item => MemoryPreservationOrder(item.Kind))
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id.Value)
                .Take(overflowCount),
        ];
        HashSet<ConversationMemoryId> overflowIds = [.. overflow.Select(item => item.Id)];
        for (var index = 0; index < items.Count; index++)
        {
            if (overflowIds.Contains(items[index].Id))
            {
                items[index] = items[index] with
                {
                    Validity = MemoryValidity.Invalid,
                    UpdatedAt = now,
                };
            }
        }
    }

    private static int MemoryPreservationOrder(ConversationMemoryKind kind)
    {
        return kind switch
        {
            ConversationMemoryKind.UserRequirement => 0,
            ConversationMemoryKind.Decision => 1,
            ConversationMemoryKind.Constraint => 2,
            ConversationMemoryKind.UnresolvedQuestion => 3,
            ConversationMemoryKind.RepositoryFinding => 4,
            ConversationMemoryKind.CompletedWork => 5,
            ConversationMemoryKind.RejectedOrSuperseded => 6,
            _ => 7,
        };
    }

    private void AddTextCandidates(
        List<ConversationMemoryItem> items,
        HashSet<string> activeKeys,
        ConversationPromotionRequest request,
        IReadOnlyList<string> contents,
        ConversationMemoryKind kind,
        DateTimeOffset now,
        ConversationMemoryId? supersedesId = null)
    {
        foreach (var raw in contents)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(raw);
            var content = BoundContent(_sanitizer.Sanitize(raw));
            var key = CreateDeduplicationKey(kind, content);
            if (!activeKeys.Add(key))
            {
                continue;
            }

            items.Add(new ConversationMemoryItem
            {
                Id = ConversationMemoryId.New(),
                SessionId = request.SessionId,
                Kind = kind,
                Content = content,
                SourceMessageIds = [request.SourceMessage.Id],
                SourceRunIds = [request.SourceMessage.RunId],
                SupersedesId = supersedesId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }
}

/// <summary>Creates conservative source-exact candidates without provider-specific state.</summary>
/// <remarks>
/// This compiled fallback promotes only exact sanitized user messages. A configured model-assisted
/// provider may replace it, but receives and returns the same host-owned bounded contracts.
/// </remarks>
public sealed class DeterministicConversationSummaryCandidateProvider : IConversationSummaryCandidateProvider
{
    /// <inheritdoc />
    public Task<ConversationSummaryCandidate> CreateCandidateAsync(
        ConversationCompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<ConversationMessageId> existingSources =
        [
            .. request.ExistingMemory.SelectMany(item => item.SourceMessageIds),
        ];
        ConversationMemoryCandidate[] candidates =
        [
            .. request.Messages
                .Where(message => message.Role == ConversationRole.User
                    && !existingSources.Contains(message.Id)
                    && !string.IsNullOrWhiteSpace(message.Content))
                .Select(message => new ConversationMemoryCandidate
                {
                    Id = ConversationMemoryId.New(),
                    Kind = ConversationMemoryKind.UserRequirement,
                    Content = message.Content ?? string.Empty,
                    TrustClass = ConversationMemoryTrustClass.ExplicitUser,
                    SourceMessageIds = [message.Id],
                    SourceRunIds = [message.RunId],
                }),
        ];
        var through = request.Messages.Count == 0
            ? request.ExistingSummary?.ThroughMessageSequence ?? 0
            : request.Messages.Max(message => message.Sequence);
        return Task.FromResult(new ConversationSummaryCandidate
        {
            ThroughMessageSequence = through,
            Items = candidates,
        });
    }
}

/// <summary>Rejects unsupported, unbounded, secret-like, or cyclic structured candidates.</summary>
public sealed class ConversationSummaryValidator : IConversationSummaryValidator
{
    private readonly ConversationCompactionPolicy _policy;
    private readonly IOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="ConversationSummaryValidator"/> class.</summary>
    public ConversationSummaryValidator(
        ConversationCompactionPolicy policy,
        IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sanitizer);
        policy.Validate();
        _policy = policy;
        _sanitizer = sanitizer;
    }

    /// <inheritdoc />
    public ConversationSummaryValidationResult Validate(
        ConversationCompactionRequest request,
        ConversationSummaryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidate);
        var errors = new List<string>();
        var failureKind = ConversationCompactionOutcomeKind.MalformedOutput;
        if (candidate.SchemaVersion != 1)
        {
            errors.Add($"Unsupported candidate schema {candidate.SchemaVersion}.");
        }

        if (candidate.Items.Count > _policy.MaximumCandidateItems)
        {
            errors.Add("Candidate item count exceeds the hard limit.");
        }

        var maximumSourceSequence = request.Messages.Count == 0
            ? 0
            : request.Messages.Max(message => message.Sequence);
        if (candidate.ThroughMessageSequence != maximumSourceSequence)
        {
            errors.Add("Candidate source range does not match the bounded archive range.");
        }

        HashSet<ConversationMessageId> messageIds = [.. request.Messages.Select(message => message.Id)];
        HashSet<RunId> runIds = [.. request.Messages.Select(message => message.RunId)];
        var currentEvidence = request.Evidence
            .Where(item => !item.IsStale)
            .ToDictionary(item => item.EvidenceId);
        HashSet<EvidenceId> evidenceIds = [.. currentEvidence.Keys];
        HashSet<ConversationMemoryId> existingIds = [.. request.ExistingMemory.Select(item => item.Id)];
        HashSet<ConversationMemoryId> proposedIds = [.. candidate.Items.Select(item => item.Id)];
        if (proposedIds.Count != candidate.Items.Count)
        {
            errors.Add("Candidate contains duplicate memory ids.");
        }

        foreach (var item in candidate.Items)
        {
            if (existingIds.Contains(item.Id))
            {
                errors.Add($"Candidate reuses existing memory id {item.Id.Value:D}.");
            }

            if (!Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.TrustClass))
            {
                errors.Add("Candidate contains an unknown category or trust class.");
            }

            if (string.IsNullOrWhiteSpace(item.Content) || item.Content.Length > _policy.MaximumItemCharacters)
            {
                errors.Add($"Candidate {item.Id.Value:D} has invalid content bounds.");
            }
            else if (!string.Equals(item.Content, _sanitizer.Sanitize(item.Content), StringComparison.Ordinal))
            {
                errors.Add($"Candidate {item.Id.Value:D} contains unsanitized or secret-like content.");
            }

            if (item.SourceMessageIds.Count == 0
                || item.SourceMessageIds.Any(id => !messageIds.Contains(id))
                || item.SourceRunIds.Any(id => !runIds.Contains(id)))
            {
                failureKind = ConversationCompactionOutcomeKind.UnsupportedProvenance;
                errors.Add($"Candidate {item.Id.Value:D} cites missing message or run provenance.");
            }

            if (item.RepositoryDependent
                && (item.SourceEvidenceIds.Count == 0
                    || item.SourceEvidenceIds.Any(id => !evidenceIds.Contains(id))
                    || string.IsNullOrWhiteSpace(item.RepositoryRevision)
                    || item.SourceEvidenceIds.Any(id =>
                        currentEvidence.TryGetValue(id, out var evidence)
                        && !string.Equals(
                            evidence.Provenance.RepositoryRevision,
                            item.RepositoryRevision,
                            StringComparison.Ordinal))))
            {
                failureKind = ConversationCompactionOutcomeKind.UnsupportedProvenance;
                errors.Add($"Repository candidate {item.Id.Value:D} lacks current evidence and matching revision.");
            }

            if (item.Kind == ConversationMemoryKind.CompletedWork
                && item.TrustClass is not ConversationMemoryTrustClass.HostObserved
                and not ConversationMemoryTrustClass.ExplicitUser)
            {
                failureKind = ConversationCompactionOutcomeKind.UnsupportedProvenance;
                errors.Add($"Completed-work candidate {item.Id.Value:D} is not host or user observed.");
            }

            if (item.TrustClass == ConversationMemoryTrustClass.AssistantProposed
                && !HasSourceSupport(request.Messages, item))
            {
                failureKind = ConversationCompactionOutcomeKind.UnsupportedProvenance;
                errors.Add($"Assistant candidate {item.Id.Value:D} is not supported by its cited source content.");
            }

            if (item.SupersedesId == item.Id
                || (item.SupersedesId is { } supersedesId
                    && !existingIds.Contains(supersedesId)
                    && !proposedIds.Contains(supersedesId)))
            {
                errors.Add($"Candidate {item.Id.Value:D} has an invalid supersession link.");
            }

            if (item.SupersedesId is { } protectedId
                && request.ExistingMemory.Any(existing =>
                    existing.Id == protectedId
                    && existing.Kind is ConversationMemoryKind.UserRequirement
                        or ConversationMemoryKind.Decision
                        or ConversationMemoryKind.Constraint)
                && item.TrustClass != ConversationMemoryTrustClass.ExplicitUser)
            {
                failureKind = ConversationCompactionOutcomeKind.UnsupportedProvenance;
                errors.Add($"Candidate {item.Id.Value:D} cannot supersede explicit user memory without a user source.");
            }
        }

        if (HasSupersessionCycle(request.ExistingMemory, candidate.Items))
        {
            errors.Add("Candidate supersession graph contains a cycle.");
        }

        return new ConversationSummaryValidationResult(
            errors.Count == 0,
            errors.Count == 0 ? ConversationCompactionOutcomeKind.Completed : failureKind,
            errors);
    }

    private static bool HasSourceSupport(
        IReadOnlyList<ConversationMessage> messages,
        ConversationMemoryCandidate candidate)
    {
        var sourceTerms = messages
            .Where(message => candidate.SourceMessageIds.Contains(message.Id))
            .SelectMany(message => (message.Content ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(term => term.Trim().ToUpperInvariant())
            .Where(term => term.Length > 2)
            .ToHashSet(StringComparer.Ordinal);
        return candidate.Content
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim().ToUpperInvariant())
            .Where(term => term.Length > 2)
            .Any(sourceTerms.Contains);
    }

    private static bool HasSupersessionCycle(
        IReadOnlyList<ConversationMemoryItem> existing,
        IReadOnlyList<ConversationMemoryCandidate> proposed)
    {
        var edges = existing
            .Where(item => item.SupersedesId is not null)
            .ToDictionary(item => item.Id, item => item.SupersedesId.GetValueOrDefault());
        foreach (var item in proposed.Where(item => item.SupersedesId is not null))
        {
            edges[item.Id] = item.SupersedesId.GetValueOrDefault();
        }

        foreach (var start in edges.Keys)
        {
            var seen = new HashSet<ConversationMemoryId>();
            var current = start;
            while (edges.TryGetValue(current, out var next))
            {
                if (!seen.Add(current))
                {
                    return true;
                }

                current = next;
            }
        }

        return false;
    }
}

/// <summary>Runs one budgeted and cancellable compaction operation per session.</summary>
public sealed class ConversationCompactor : IConversationCompactor
{
    private static readonly ActivitySource ActivitySource = new("Threadsmith.Context.Conversation");

    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _gates = new();
    private readonly IConversationSummaryCandidateProvider _provider;
    private readonly ConversationCompactionPolicy _policy;
    private readonly IConversationStore _store;
    private readonly IConversationSummaryValidator _validator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="ConversationCompactor"/> class.</summary>
    public ConversationCompactor(
        IConversationStore store,
        IConversationSummaryCandidateProvider provider,
        IConversationSummaryValidator validator,
        ConversationCompactionPolicy policy,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        _store = store;
        _provider = provider;
        _validator = validator;
        _policy = policy;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ConversationCompactionResult> CompactAtTurnBoundaryAsync(
        SessionId sessionId,
        IReadOnlyList<Evidence> evidence,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var gate = _gates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var activity = ActivitySource.StartActivity("conversation.compact");
            var state = await _store.GetSnapshotAsync(
                sessionId,
                includeBodies: true,
                cancellationToken);
            var through = state.Summary?.ThroughMessageSequence ?? 0;
            ConversationMessage[] uncompacted =
            [
                .. state.Messages
                    .Where(message => message.Sequence > through)
                    .Take(_policy.MaximumSourceMessages),
            ];
            var uncompactedTokens = uncompacted.Sum(message => message.EstimatedTokens);
            var activeItems = state.MemoryItems.Count(item => item.Validity == MemoryValidity.Active);
            if (uncompacted.Length == 0)
            {
                return new ConversationCompactionResult(
                    ConversationCompactionOutcomeKind.AlreadyCompacted,
                    "No uncompacted source messages remain.");
            }

            if (!force
                && uncompacted.Length < _policy.ArchivedMessageThreshold
                && uncompactedTokens < _policy.ArchivedTokenThreshold
                && activeItems < _policy.MaximumActiveMemoryItems)
            {
                return new ConversationCompactionResult(
                    ConversationCompactionOutcomeKind.NotRequired,
                    "No turn-boundary compaction threshold was met.");
            }

            if (uncompactedTokens > _policy.MaximumInputTokens)
            {
                while (uncompacted.Length > 1
                    && uncompacted.Sum(message => message.EstimatedTokens) > _policy.MaximumInputTokens)
                {
                    uncompacted = [.. uncompacted.Take(uncompacted.Length - 1)];
                }

                if (uncompacted.Sum(message => message.EstimatedTokens) > _policy.MaximumInputTokens)
                {
                    return new ConversationCompactionResult(
                        ConversationCompactionOutcomeKind.MalformedOutput,
                        "The oldest uncompacted message exceeds the compaction input budget; prior state remains active.");
                }
            }

            var request = new ConversationCompactionRequest
            {
                SessionId = sessionId,
                Messages = uncompacted,
                ExistingMemory = state.MemoryItems,
                ExistingSummary = state.Summary,
                Evidence = evidence.Where(item => item.SessionId == sessionId && !item.IsStale).ToArray(),
            };
            var calls = 0;
            ConversationSummaryCandidate candidate;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls++;
                try
                {
                    candidate = await _provider.CreateCandidateAsync(request, cancellationToken);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new ConversationCompactionResult(
                        ConversationCompactionOutcomeKind.Cancelled,
                        "Compaction was cancelled; the prior snapshot remains active.",
                        state.Summary?.Version,
                        calls);
                }
                catch (Exception) when (calls <= _policy.MaximumProviderRetries && calls < _policy.MaximumProviderCalls)
                {
                    continue;
                }
                catch (Exception)
                {
                    return new ConversationCompactionResult(
                        ConversationCompactionOutcomeKind.ProviderFailure,
                        "Candidate provider failed within the bounded retry budget; the prior snapshot remains active.",
                        state.Summary?.Version,
                        calls);
                }
            }

            var validation = _validator.Validate(request, candidate);
            if (!validation.IsValid)
            {
                return new ConversationCompactionResult(
                    validation.FailureKind,
                    string.Join(' ', validation.Errors),
                    state.Summary?.Version,
                    calls);
            }

            var now = _timeProvider.GetUtcNow();
            var retained = state.MemoryItems.ToDictionary(item => item.Id);
            foreach (var proposed in candidate.Items)
            {
                retained[proposed.Id] = new ConversationMemoryItem
                {
                    Id = proposed.Id,
                    SessionId = sessionId,
                    Kind = proposed.Kind,
                    Content = proposed.Content,
                    SourceMessageIds = proposed.SourceMessageIds.ToArray(),
                    SourceRunIds = proposed.SourceRunIds.ToArray(),
                    SourceEvidenceIds = proposed.SourceEvidenceIds.ToArray(),
                    SourceArtifactIds = proposed.SourceArtifactIds.ToArray(),
                    RepositoryRevision = proposed.RepositoryRevision,
                    RepositoryDependent = proposed.RepositoryDependent,
                    SupersedesId = proposed.SupersedesId,
                    CreatedAt = retained.TryGetValue(proposed.Id, out var existing)
                        ? existing.CreatedAt
                        : now,
                    UpdatedAt = now,
                };
                if (proposed.SupersedesId is { } supersededId
                    && retained.TryGetValue(supersededId, out var superseded))
                {
                    retained[supersededId] = superseded with
                    {
                        Validity = MemoryValidity.Superseded,
                        UpdatedAt = now,
                    };
                }
            }

            ConversationMemoryItem[] allItems = [.. retained.Values];
            var version = (state.Summary?.Version ?? 0) + 1;
            var snapshot = new ConversationSummarySnapshot
            {
                SessionId = sessionId,
                Version = version,
                ThroughMessageSequence = candidate.ThroughMessageSequence,
                MemoryIdsByKind = allItems
                    .Where(item => item.Validity == MemoryValidity.Active)
                    .GroupBy(item => item.Kind)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<ConversationMemoryId>)[
                            .. group
                                .OrderBy(item => item.CreatedAt)
                                .ThenBy(item => item.Id.Value)
                                .Select(item => item.Id),
                        ]),
                CreatedAt = now,
            };
            try
            {
                await _store.ReplaceSummaryAsync(sessionId, allItems, snapshot, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ConversationCompactionResult(
                    ConversationCompactionOutcomeKind.Cancelled,
                    "Compaction persistence was cancelled; the prior snapshot remains active.",
                    state.Summary?.Version,
                    calls);
            }
            catch (Exception)
            {
                return new ConversationCompactionResult(
                    ConversationCompactionOutcomeKind.PersistenceFailure,
                    "Snapshot persistence failed; the prior snapshot remains active.",
                    state.Summary?.Version,
                    calls);
            }

            activity?.SetTag("threadsmith.conversation.compaction.items", candidate.Items.Count);
            activity?.SetTag("threadsmith.conversation.compaction.snapshot_version", version);
            return new ConversationCompactionResult(
                ConversationCompactionOutcomeKind.Completed,
                "Validated structured memory replaced the active snapshot atomically.",
                version,
                calls);
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>Performs deterministic lexical and metadata retrieval with stable tie-breaking.</summary>
public sealed partial class ConversationMemoryRetriever : IConversationMemoryRetriever
{
    private readonly IConversationStore _store;

    /// <summary>Initializes a new instance of the <see cref="ConversationMemoryRetriever"/> class.</summary>
    public ConversationMemoryRetriever(IConversationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<ConversationRetrievalResult> RetrieveAsync(
        ConversationRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumTokens);
        var state = await _store.GetSnapshotAsync(
            request.SessionId,
            includeBodies: false,
            cancellationToken);
        var queryTerms = Tokenize(request.Query);
        ConversationMemoryItem[] eligible =
        [
            .. state.MemoryItems.Where(item => item.Validity == MemoryValidity.Active),
        ];
        var ranked = eligible
            .Select(item => Score(item, queryTerms, request.Phase))
            .Where(result => result.Rationale.Score > 0)
            .OrderByDescending(result => result.Rationale.Score)
            .ThenBy(result => CategoryOrder(result.Item.Kind, request.Phase))
            .ThenByDescending(result => result.Item.UpdatedAt)
            .ThenBy(result => result.Item.Id.Value)
            .ToArray();
        var selected = new List<ConversationRetrievedMemory>();
        var tokens = 0;
        foreach (var item in ranked)
        {
            var itemTokens = Math.Max(1, (item.Item.Content.Length + 3) / 4);
            if (selected.Count >= request.MaximumItems || tokens + itemTokens > request.MaximumTokens)
            {
                continue;
            }

            selected.Add(item);
            tokens += itemTokens;
        }

        var excluded = state.MemoryItems.Count(item => item.Validity != MemoryValidity.Active);
        return new ConversationRetrievalResult(selected, eligible.Length, excluded);
    }

    private static int CategoryOrder(
        ConversationMemoryKind kind,
        ConversationRetrievalPhase phase)
    {
        return phase switch
        {
            ConversationRetrievalPhase.Planning => kind switch
            {
                ConversationMemoryKind.UserRequirement => 0,
                ConversationMemoryKind.Constraint => 1,
                ConversationMemoryKind.Decision => 2,
                ConversationMemoryKind.UnresolvedQuestion => 3,
                _ => 4,
            },
            ConversationRetrievalPhase.CodeEdit => kind switch
            {
                ConversationMemoryKind.Constraint => 0,
                ConversationMemoryKind.Decision => 1,
                ConversationMemoryKind.RepositoryFinding => 2,
                ConversationMemoryKind.UserRequirement => 3,
                _ => 4,
            },
            ConversationRetrievalPhase.Validation => kind switch
            {
                ConversationMemoryKind.UserRequirement => 0,
                ConversationMemoryKind.CompletedWork => 1,
                ConversationMemoryKind.RepositoryFinding => 2,
                _ => 3,
            },
            _ => kind switch
            {
                ConversationMemoryKind.UnresolvedQuestion => 0,
                ConversationMemoryKind.UserRequirement => 1,
                ConversationMemoryKind.Decision => 2,
                _ => 3,
            },
        };
    }

    private static ConversationRetrievedMemory Score(
        ConversationMemoryItem item,
        HashSet<string> queryTerms,
        ConversationRetrievalPhase phase)
    {
        var itemTerms = Tokenize(item.Content);
        string[] matched = [.. itemTerms.Intersect(queryTerms).Order(StringComparer.Ordinal)];
        var lexical = matched.Length * 10d / Math.Max(1, queryTerms.Count);
        double category = 6 - CategoryOrder(item.Kind, phase);
        double explicitPriority = item.Kind is ConversationMemoryKind.UserRequirement
            or ConversationMemoryKind.Decision
            or ConversationMemoryKind.Constraint
            ? 3
            : 0;
        double repeatedSupport = Math.Min(3, item.SourceMessageIds.Count + item.SourceEvidenceIds.Count - 1);
        var score = lexical + category + explicitPriority + Math.Max(0, repeatedSupport);
        return new ConversationRetrievedMemory(
            item,
            new ConversationRetrievalRationale(
                matched.Length > 0 ? "LexicalCategoryMatch" : "CategoryPriority",
                score,
                matched));
    }

    private static HashSet<string> Tokenize(string content)
    {
        return WordPattern().Matches(content)
            .Select(match => match.Value.ToUpperInvariant())
            .Where(value => value.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex("[\\p{L}\\p{N}_./-]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}

/// <summary>Conservatively invalidates repository-dependent memory at turn boundaries.</summary>
public sealed class ConversationMemoryInvalidator : IConversationMemoryInvalidator
{
    private readonly IDomainEventStream? _events;
    private readonly IConversationStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="ConversationMemoryInvalidator"/> class.</summary>
    public ConversationMemoryInvalidator(
        IConversationStore store,
        TimeProvider? timeProvider = null,
        IDomainEventStream? events = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _events = events;
    }

    /// <inheritdoc />
    public async Task<ConversationMemoryInvalidationResult> InvalidateAtTurnBoundaryAsync(
        SessionId sessionId,
        IReadOnlyList<string> invalidationKeys,
        string? currentRepositoryRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invalidationKeys);
        var state = await _store.GetSnapshotAsync(
            sessionId,
            includeBodies: false,
            cancellationToken);
        var repositoryChanged = invalidationKeys.Any(key =>
            string.Equals(key, "repository", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("project:", StringComparison.OrdinalIgnoreCase));
        ConversationMemoryItem[] affected =
        [
            .. state.MemoryItems.Where(item =>
                item.Validity == MemoryValidity.Active
                && item.RepositoryDependent
                && (repositoryChanged
                    || (!string.IsNullOrWhiteSpace(currentRepositoryRevision)
                        && !string.Equals(
                            item.RepositoryRevision,
                            currentRepositoryRevision,
                            StringComparison.Ordinal)))),
        ];
        var now = _timeProvider.GetUtcNow();
        HashSet<ConversationMemoryId> affectedIds = [.. affected.Select(item => item.Id)];
        ConversationMemoryItem[] updated =
        [
            .. state.MemoryItems.Select(item => affectedIds.Contains(item.Id)
                ? item with
                {
                    Validity = MemoryValidity.Stale,
                    UpdatedAt = now,
                }
                : item),
        ];
        if (affected.Length > 0)
        {
            var snapshot = new ConversationSummarySnapshot
            {
                SessionId = sessionId,
                Version = (state.Summary?.Version ?? 0) + 1,
                ThroughMessageSequence = state.Summary?.ThroughMessageSequence ?? 0,
                RepositoryRevision = currentRepositoryRevision,
                MemoryIdsByKind = updated
                    .Where(item => item.Validity == MemoryValidity.Active)
                    .GroupBy(item => item.Kind)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<ConversationMemoryId>)[.. group.Select(item => item.Id)]),
                CreatedAt = now,
            };
            await _store.ReplaceSummaryAsync(sessionId, updated, snapshot, cancellationToken);
            if (_events is not null)
            {
                foreach (var item in affected)
                {
                    await _events.PublishAsync(
                        new ConversationMemoryInvalidated(
                            sessionId,
                            now,
                            item.Id,
                            "Repository-dependent memory invalidated at the turn boundary."),
                        cancellationToken);
                }
            }
        }

        return new ConversationMemoryInvalidationResult(
            affected.Length,
            affected.Select(item => item.Id).ToArray());
    }
}
