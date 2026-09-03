namespace Threadsmith.Context;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Host-owned active-turn continuation compaction policy.</summary>
public sealed record ActiveTurnCompactionPolicy
{
    /// <summary>Whether pressure-triggered active-turn compaction is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Percentage of the selected model input budget that triggers compaction.</summary>
    public int PressureTargetPercent { get; init; } = 75;

    /// <summary>Fallback output reserve used only when no selected-profile reserve is available.</summary>
    public int OutputReserveTokens { get; init; } = 8_192;

    /// <summary>Maximum estimated tokens in one projected active-turn summary.</summary>
    public int SummaryBudgetTokens { get; init; } = 16_384;

    /// <summary>Percentage of the summary budget available to model-written text.</summary>
    public int ModelOutputBudgetPercent { get; init; } = 80;

    /// <summary>Minimum positive estimated reduction required before a candidate is activated.</summary>
    public int MinimumSavingsTokens { get; init; } = 1;

    /// <summary>Newest raw continuation target retained after a cut.</summary>
    public int RetainedRecentTokens { get; init; } = 12_000;

    /// <summary>Maximum groups supplied to one candidate operation.</summary>
    public int MaximumSourceGroups { get; init; } = 48;

    /// <summary>Maximum canonical wire-input estimate for one candidate request.</summary>
    public int MaximumInputTokens { get; init; } = 65_536;

    /// <summary>Maximum aggregate call/result messages across one candidate prefix.</summary>
    public int MaximumCandidateMessages { get; init; } = 512;

    /// <summary>Maximum characters in one host-known source identifier.</summary>
    public int MaximumSourceIdentifierCharacters { get; init; } = 4_096;

    /// <summary>Maximum host-known source identities retained for one complete group.</summary>
    public int MaximumSourcesPerGroup { get; init; } = 512;

    /// <summary>Maximum current-task objective characters supplied to candidate generation.</summary>
    public int MaximumTaskObjectiveCharacters { get; init; } = 4_000;

    /// <summary>Maximum acceptance criteria supplied to candidate generation.</summary>
    public int MaximumAcceptanceIntentItems { get; init; } = 32;

    /// <summary>Maximum characters in one acceptance-intent item.</summary>
    public int MaximumAcceptanceIntentCharacters { get; init; } = 1_000;

    /// <summary>Maximum total characters across candidate acceptance intent.</summary>
    public int MaximumAcceptanceIntentTotalCharacters { get; init; } = 4_000;

    /// <summary>Maximum transient provider retries.</summary>
    public int MaximumProviderRetries { get; init; } = 1;

    /// <summary>Maximum candidate-model calls including retries.</summary>
    public int MaximumProviderCalls { get; init; } = 2;

    /// <summary>Rounds skipped after a failed candidate operation.</summary>
    public int FailureBackoffRounds { get; init; } = 2;

    /// <summary>Uses the selected profile's effective request reserve when one is available.</summary>
    public int ResolveOutputReserve(int? selectedProfileReserveTokens)
    {
        Validate();
        if (selectedProfileReserveTokens is null)
        {
            return OutputReserveTokens;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selectedProfileReserveTokens.Value);
        return selectedProfileReserveTokens.Value;
    }

    /// <summary>Resolves the per-request model-output ceiling within the profile reserve.</summary>
    public int ResolveModelOutputTokens(int profileOutputReserveTokens)
    {
        Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(profileOutputReserveTokens);
        var policyLimit = Math.Max(
            1,
            (int)((long)SummaryBudgetTokens * ModelOutputBudgetPercent / 100));
        return Math.Min(policyLimit, profileOutputReserveTokens);
    }

    /// <summary>Scales newest-raw retention to the current request's activation capacity.</summary>
    public int ResolveEffectiveRetentionTarget(
        int beforeInputTokens,
        int fixedRequestTokens,
        int pressureTargetTokens)
    {
        Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(beforeInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(fixedRequestTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pressureTargetTokens);
        var minimumSavingsTarget = Math.Max(
            1,
            beforeInputTokens - MinimumSavingsTokens);
        var activationTargetTokens = Math.Min(pressureTargetTokens, minimumSavingsTarget);
        var unboundedRecentTokens = activationTargetTokens
            - fixedRequestTokens
            - SummaryBudgetTokens;
        var availableRecentTokens = Math.Max(1, unboundedRecentTokens);
        return Math.Min(RetainedRecentTokens, availableRecentTokens);
    }

    /// <summary>Validates every reliability bound and cross-bound relationship.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PressureTargetPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PressureTargetPercent, 95);
        ArgumentOutOfRangeException.ThrowIfNegative(OutputReserveTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OutputReserveTokens, 262_144);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SummaryBudgetTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SummaryBudgetTokens, 32_768);
        ArgumentOutOfRangeException.ThrowIfLessThan(ModelOutputBudgetPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ModelOutputBudgetPercent, 95);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinimumSavingsTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinimumSavingsTokens, 262_144);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RetainedRecentTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RetainedRecentTokens, 262_144);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSourceGroups);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumSourceGroups, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumInputTokens, 512);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumInputTokens, 131_072);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumCandidateMessages, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumCandidateMessages, 2_048);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumSourceIdentifierCharacters, 64);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumSourceIdentifierCharacters, 16_384);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumSourcesPerGroup, 257);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumSourcesPerGroup, 1_024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumTaskObjectiveCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumTaskObjectiveCharacters, 16_000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAcceptanceIntentItems);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumAcceptanceIntentItems, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumAcceptanceIntentCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumAcceptanceIntentCharacters, 4_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumAcceptanceIntentTotalCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            MaximumAcceptanceIntentTotalCharacters,
            16_000);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumProviderRetries);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumProviderRetries, 3);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProviderCalls);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumProviderCalls, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(FailureBackoffRounds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FailureBackoffRounds, 16);
        if (MaximumProviderRetries + 1 > MaximumProviderCalls)
        {
            throw new ArgumentException("Provider retries exceed the active-turn compaction call budget.");
        }
    }
}

/// <summary>Bounded task context supplied to active-turn candidate generation.</summary>
public sealed record ActiveTurnTaskContext
{
    /// <summary>Sanitized bounded objective.</summary>
    public required string Objective { get; init; }

    /// <summary>Whether host bounding shortened the objective.</summary>
    public bool ObjectiveWasTruncated { get; init; }

    /// <summary>Required-first bounded acceptance intent.</summary>
    public required IReadOnlyList<ActiveTurnAcceptanceIntent> AcceptanceIntent { get; init; }

    /// <summary>Criteria omitted by count or aggregate character bounds.</summary>
    public int OmittedAcceptanceIntentCount { get; init; }
}

/// <summary>Deterministically bounds sanitized task context before candidate projection.</summary>
public static class ActiveTurnTaskContextProjector
{
    /// <summary>Projects required criteria before optional criteria under all host bounds.</summary>
    public static ActiveTurnTaskContext Project(
        TaskSpecification task,
        ActiveTurnCompactionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        var objective = BoundText(task.Intent, policy.MaximumTaskObjectiveCharacters);
        var ordered = task.AcceptanceCriteria
            .Select((criterion, index) => (criterion, index))
            .OrderByDescending(item => item.criterion.IsRequired)
            .ThenBy(item => item.index);
        var acceptance = new List<ActiveTurnAcceptanceIntent>();
        var remainingCharacters = policy.MaximumAcceptanceIntentTotalCharacters;
        foreach (var item in ordered)
        {
            if (acceptance.Count == policy.MaximumAcceptanceIntentItems
                || remainingCharacters < 2)
            {
                break;
            }

            var maximumCharacters = Math.Min(
                policy.MaximumAcceptanceIntentCharacters,
                remainingCharacters);
            var bounded = BoundText(item.criterion.Description, maximumCharacters);
            acceptance.Add(new ActiveTurnAcceptanceIntent
            {
                Description = bounded.Text,
                IsRequired = item.criterion.IsRequired,
                WasTruncated = bounded.WasTruncated,
            });
            remainingCharacters -= bounded.Text.Length;
        }

        return new ActiveTurnTaskContext
        {
            Objective = objective.Text,
            ObjectiveWasTruncated = objective.WasTruncated,
            AcceptanceIntent = acceptance,
            OmittedAcceptanceIntentCount = task.AcceptanceCriteria.Count - acceptance.Count,
        };
    }

    private static BoundedText BoundText(string value, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 2);
        if (value.Length <= maximumCharacters)
        {
            return new BoundedText(value, false);
        }

        var length = maximumCharacters;
        if (char.IsHighSurrogate(value[length - 1])
            && length < value.Length
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return new BoundedText(value[..length], true);
    }

    private sealed record BoundedText(string Text, bool WasTruncated);
}

/// <summary>Closed active-turn source-reference kinds.</summary>
public enum ActiveTurnSourceKind
{
    /// <summary>Complete continuation group.</summary>
    Group,

    /// <summary>Host-generated model tool-call correlation id.</summary>
    ToolCall,

    /// <summary>Host tool invocation identity.</summary>
    ToolInvocation,

    /// <summary>Governed evidence identity.</summary>
    Evidence,

    /// <summary>Exact tool-reported repository or external provenance.</summary>
    ToolProvenance,
}

/// <summary>One bounded reference to host-known active-turn source state.</summary>
public sealed record ActiveTurnSourceReference(
    ActiveTurnSourceKind Kind,
    string Id,
    long GroupSequence);

/// <summary>One complete chronological assistant-call/result group.</summary>
public sealed record ActiveTurnContinuationGroup
{
    /// <summary>Monotonic sequence within the turn.</summary>
    public required long Sequence { get; init; }

    /// <summary>Model round that completed the group.</summary>
    public required int CompletedModelRound { get; init; }

    /// <summary>Complete provider-neutral messages in original order.</summary>
    public required IReadOnlyList<ModelMessage> Messages { get; init; }

    /// <summary>Host-known source identities for candidate validation.</summary>
    public required IReadOnlyList<ActiveTurnSourceReference> Sources { get; init; }

    /// <summary>Files observed through successful read-tool provenance in this group.</summary>
    public required IReadOnlyList<string> FilesRead { get; init; }

    /// <summary>Files observed through an explicit file-change signal in this group.</summary>
    public required IReadOnlyList<string> FilesChanged { get; init; }

    /// <summary>Canonical group estimate excluding frozen context and tool definitions.</summary>
    public required int EstimatedTokens { get; init; }

    /// <summary>Conservative post-sanitization sensitivity of the tool-result group.</summary>
    public ConversationSensitivity Sensitivity { get; init; } = ConversationSensitivity.Sensitive;

    /// <summary>Whether a completed later request received this exact group.</summary>
    public bool WasDeliveredVerbatim { get; init; }
}

/// <summary>Selects only an oldest eligible prefix while retaining a profile-scaled newest raw window.</summary>
public static class ActiveTurnCompactionCutSelector
{
    /// <summary>Returns the bounded complete delivered prefix eligible for replacement.</summary>
    public static IReadOnlyList<ActiveTurnContinuationGroup> SelectEligiblePrefix(
        IReadOnlyList<ActiveTurnContinuationGroup> groups,
        ActiveTurnCompactionPolicy policy,
        int effectiveRetentionTargetTokens)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(effectiveRetentionTargetTokens);
        if (groups.Count < 2)
        {
            return [];
        }

        var retainedStart = groups.Count;
        var retainedTokens = 0;
        for (var index = groups.Count - 1;
            index >= 0
                && (retainedTokens < effectiveRetentionTargetTokens
                    || retainedStart == groups.Count);
            index--)
        {
            retainedTokens = checked(retainedTokens + groups[index].EstimatedTokens);
            retainedStart = index;
        }

        if (retainedStart == 0)
        {
            return [];
        }

        return groups
            .Take(retainedStart)
            .TakeWhile(group => group.WasDeliveredVerbatim)
            .Take(policy.MaximumSourceGroups)
            .ToArray();
    }
}

/// <summary>One model-written replacement summary for a bounded complete group prefix.</summary>
public sealed record ActiveTurnCompactionCandidate
{
    /// <summary>Supported candidate schema version.</summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>Prior activated summary version consumed by this replacement.</summary>
    public int PriorSummaryVersion { get; init; }

    /// <summary>Last raw group represented by the candidate.</summary>
    public required long ThroughGroupSequence { get; init; }

    /// <summary>Exact ordered group sequences represented by the updated summary.</summary>
    public required IReadOnlyList<long> CoveredGroupSequences { get; init; }

    /// <summary>Bounded Markdown returned by the summary model before host file-list projection.</summary>
    public required string SummaryText { get; init; }

    /// <summary>Cumulative host-observed files read during the summarized work.</summary>
    public required IReadOnlyList<string> FilesRead { get; init; }

    /// <summary>Cumulative host-observed files changed during the summarized work.</summary>
    public required IReadOnlyList<string> FilesChanged { get; init; }
}

/// <summary>One validated active-turn summary held for the current turn.</summary>
public sealed record ActiveTurnCompactionSummary
{
    /// <summary>Monotonic summary version.</summary>
    public required int Version { get; init; }

    /// <summary>Last compacted raw group.</summary>
    public required long ThroughGroupSequence { get; init; }

    /// <summary>Exact cumulative source-group range.</summary>
    public required IReadOnlyList<long> CoveredGroupSequences { get; init; }

    /// <summary>Complete bounded summary, including host-appended file lists.</summary>
    public required string Content { get; init; }

    /// <summary>Cumulative host-observed files read during the summarized work.</summary>
    public required IReadOnlyList<string> FilesRead { get; init; }

    /// <summary>Cumulative host-observed files changed during the summarized work.</summary>
    public required IReadOnlyList<string> FilesChanged { get; init; }

    /// <summary>Hash of the model-visible summary projection.</summary>
    public required string ContentHash { get; init; }
}

/// <summary>Cumulative host-observed file lists carried across updated summaries.</summary>
internal sealed record ActiveTurnFileLists(
    IReadOnlyList<string> FilesRead,
    IReadOnlyList<string> FilesChanged);

/// <summary>Combines prior and newly observed file lists in first-seen order.</summary>
internal static class ActiveTurnFileListBuilder
{
    /// <summary>Builds cumulative deduplicated file lists for one selected group prefix.</summary>
    public static ActiveTurnFileLists Build(
        ActiveTurnCompactionSummary? priorSummary,
        IReadOnlyList<ActiveTurnContinuationGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var filesRead = new List<string>();
        var filesChanged = new List<string>();
        AddDistinct(priorSummary?.FilesRead ?? [], filesRead);
        AddDistinct(priorSummary?.FilesChanged ?? [], filesChanged);
        AddDistinct(groups.SelectMany(group => group.FilesRead), filesRead);
        AddDistinct(groups.SelectMany(group => group.FilesChanged), filesChanged);
        return new ActiveTurnFileLists(filesRead, filesChanged);
    }

    private static void AddDistinct(
        IEnumerable<string> source,
        ICollection<string> destination)
    {
        var seen = destination.ToHashSet(StringComparer.Ordinal);
        foreach (var value in source)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                destination.Add(value);
            }
        }
    }
}

/// <summary>Bounded in-memory checkpoint for one activated active-turn history rewrite.</summary>
public sealed record ActiveTurnCompactionCheckpoint
{
    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Activated cumulative summary version.</summary>
    public required int SummaryVersion { get; init; }

    /// <summary>First raw group replaced by this rewrite.</summary>
    public required long CompactedFromGroupSequence { get; init; }

    /// <summary>Last raw group replaced by this rewrite.</summary>
    public required long CompactedThroughGroupSequence { get; init; }

    /// <summary>Bounded host-known source identities represented by the cut.</summary>
    public required IReadOnlyList<ActiveTurnSourceReference> Sources { get; init; }

    /// <summary>Digest of the unchanged frozen context.</summary>
    public required string FrozenContextIdentity { get; init; }

    /// <summary>Canonical complete-request estimate before replacement.</summary>
    public required int BeforeInputTokens { get; init; }

    /// <summary>Canonical complete-request estimate after replacement.</summary>
    public required int AfterInputTokens { get; init; }

    /// <summary>Exact raw groups retained after replacement.</summary>
    public required int RetainedGroupCount { get; init; }

    /// <summary>Estimated tokens in exact raw groups retained after replacement.</summary>
    public required int RetainedGroupTokens { get; init; }

    /// <summary>Candidate model profile, without provider credentials.</summary>
    public required ModelProfileId CandidateProfileId { get; init; }

    /// <summary>Validated summary content hash.</summary>
    public required string SummaryContentHash { get; init; }

    /// <summary>Cumulative prior-summary items pruned under the summary bounds.</summary>
    public required int PrunedPriorItemCount { get; init; }

    /// <summary>New provider-neutral history generation.</summary>
    public required long HistoryRewriteGeneration { get; init; }

    /// <summary>Monotonic generation/validation duration.</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>One bounded acceptance criterion projected to candidate generation.</summary>
public sealed record ActiveTurnAcceptanceIntent
{
    /// <summary>Sanitized bounded acceptance description.</summary>
    public required string Description { get; init; }

    /// <summary>Whether the task marks the criterion as required.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Whether host input bounding shortened the description.</summary>
    public bool WasTruncated { get; init; }
}

/// <summary>Repository-excluding candidate profile facts frozen into one compaction request.</summary>
public sealed record ActiveTurnCompactionCandidateProfile
{
    /// <summary>Stable profile dispatched by the configured model provider.</summary>
    public required ModelProfileId ProfileId { get; init; }

    /// <summary>Candidate profile context window.</summary>
    public required int ContextWindowTokens { get; init; }

    /// <summary>Candidate profile effective request output reserve.</summary>
    public required int OutputReserveTokens { get; init; }

    /// <summary>Candidate profile default reasoning level.</summary>
    public required ReasoningLevel ReasoningLevel { get; init; }

    /// <summary>Whether the candidate profile may receive sensitive input.</summary>
    public required ModelSensitiveDataPolicy SensitiveDataPolicy { get; init; }

    /// <summary>Candidate profile pricing used by request-specific cost constraints.</summary>
    public required ModelCostMetadata Cost { get; init; }

    /// <summary>Exact provider-owned instruction contribution for the candidate profile.</summary>
    public ModelProviderInstructions? ProviderInstructions { get; init; }
}

/// <summary>Bounded provider-neutral active-turn candidate input.</summary>
public sealed record ActiveTurnCompactionRequest
{
    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Selected profile used for the ordinary request and candidate fallback.</summary>
    public required ModelProfileId ProfileId { get; init; }

    /// <summary>Optional repository-excluding candidate profile frozen by composition.</summary>
    public ActiveTurnCompactionCandidateProfile? CandidateProfile { get; init; }

    /// <summary>Exact provider-owned instruction contribution for the ordinary-profile fallback.</summary>
    public ModelProviderInstructions? ProviderInstructions { get; init; }

    /// <summary>Zero-based ordinary tool-continuation round that triggered this candidate assessment.</summary>
    public int ToolContinuationRound { get; init; }

    /// <summary>Digest of the frozen initial context.</summary>
    public required string FrozenContextIdentity { get; init; }

    /// <summary>Bounded sanitized objective that determines task-critical evidence.</summary>
    public required string TaskObjective { get; init; }

    /// <summary>Whether host input bounding shortened the task objective.</summary>
    public bool TaskObjectiveWasTruncated { get; init; }

    /// <summary>Required-first bounded sanitized acceptance intent for evidence preservation.</summary>
    public required IReadOnlyList<ActiveTurnAcceptanceIntent> AcceptanceIntent { get; init; }

    /// <summary>Criteria omitted after required-first count/character bounding.</summary>
    public int OmittedAcceptanceIntentCount { get; init; }

    /// <summary>Previously validated cumulative summary.</summary>
    public ActiveTurnCompactionSummary? PriorSummary { get; init; }

    /// <summary>Oldest complete eligible raw prefix selected for replacement.</summary>
    public required IReadOnlyList<ActiveTurnContinuationGroup> EligiblePrefix { get; init; }

    /// <summary>Selected-model sensitivity constraint, preserved for the candidate call.</summary>
    public required ModelSelectionConstraints SelectionConstraints { get; init; }

    /// <summary>Whether source content is sensitive.</summary>
    public bool ContainsSensitiveData { get; init; }

    /// <summary>Ordinary selected profile's complete context window, used by the candidate fallback.</summary>
    public required int ProfileContextWindowTokens { get; init; }

    /// <summary>Ordinary selected profile's effective output reserve, used by the candidate fallback.</summary>
    public required int ProfileOutputReserveTokens { get; init; }

    /// <summary>Complete request estimate before replacement.</summary>
    public required int BeforeInputTokens { get; init; }

    /// <summary>Pressure target that the rebuilt request should meet.</summary>
    public required int PressureTargetTokens { get; init; }
}

/// <summary>Candidate plus provider usage needed for host budget accounting.</summary>
public sealed record ActiveTurnCandidateGeneration(
    ActiveTurnCompactionCandidate Candidate,
    ModelUsage? Usage);

/// <summary>One normalized candidate request ready for actual provider I/O.</summary>
public interface IActiveTurnCompactionCandidateAttempt
{
    /// <summary>Latest normalized usage observed, including usage received before a later failure.</summary>
    ModelUsage? ObservedUsage { get; }

    /// <summary>Executes the already prepared provider request.</summary>
    Task<ActiveTurnCandidateGeneration> ExecuteAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral active-turn candidate source.</summary>
public interface IActiveTurnCompactionCandidateProvider
{
    /// <summary>Prepares and capacity-checks one candidate request without provider I/O.</summary>
    IActiveTurnCompactionCandidateAttempt PrepareCandidate(
        ActiveTurnCompactionRequest request);
}

/// <summary>Closed candidate rejection reasons.</summary>
public enum ActiveTurnCompactionRejectionReason
{
    /// <summary>No validation error.</summary>
    None,

    /// <summary>Unsupported or inconsistent schema.</summary>
    Schema,

    /// <summary>Candidate exceeded a configured bound.</summary>
    Size,

    /// <summary>Candidate cited an unknown or mismatched source.</summary>
    Source,

    /// <summary>Candidate attempted authority promotion.</summary>
    Authority,

    /// <summary>Candidate content failed sanitization policy.</summary>
    Sensitivity,
}

/// <summary>Strict active-turn candidate validation result.</summary>
public sealed record ActiveTurnCompactionValidationResult(
    bool IsValid,
    ActiveTurnCompactionRejectionReason RejectionReason,
    IReadOnlyList<string> Errors);

/// <summary>Validates an untrusted active-turn candidate against host-known state.</summary>
public interface IActiveTurnCompactionValidator
{
    /// <summary>Validates schema, bounds, source range, authority, and sanitization.</summary>
    ActiveTurnCompactionValidationResult Validate(
        ActiveTurnCompactionRequest request,
        ActiveTurnCompactionCandidate candidate);
}

/// <summary>Closed outcome of one candidate operation.</summary>
public enum ActiveTurnCompactionOutcome
{
    /// <summary>A validated summary is available for atomic request rebuilding.</summary>
    Completed,

    /// <summary>The candidate provider exhausted its bounded call budget.</summary>
    ProviderFailure,

    /// <summary>The candidate was rejected by host validation.</summary>
    ValidationRejected,

    /// <summary>Cancellation retained the prior continuation.</summary>
    Cancelled,
}

/// <summary>Closed outcome of one candidate-provider attempt at the host model-request boundary.</summary>
public enum ActiveTurnCompactionAttemptOutcome
{
    /// <summary>The provider returned a candidate generation.</summary>
    Completed,

    /// <summary>The provider failed before returning a candidate generation.</summary>
    Failed,

    /// <summary>The owning operation cancelled the provider attempt.</summary>
    Cancelled,
}

/// <summary>Host callback around each actual candidate-provider request, including retries.</summary>
public interface IActiveTurnCompactionAttemptObserver
{
    /// <summary>Applies the host pre-request boundary before provider I/O begins.</summary>
    Task BeforeProviderCallAsync(
        ActiveTurnCompactionRequest request,
        int attempt,
        Guid invocationId,
        CancellationToken cancellationToken = default);

    /// <summary>Records the host post-request boundary after provider I/O completes or fails.</summary>
    Task AfterProviderCallAsync(
        ActiveTurnCompactionRequest request,
        int attempt,
        Guid invocationId,
        ActiveTurnCompactionAttemptOutcome outcome,
        ModelUsage? usage,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of candidate generation and validation before activation.</summary>
public sealed record ActiveTurnCompactionResult
{
    /// <summary>Closed outcome.</summary>
    public required ActiveTurnCompactionOutcome Outcome { get; init; }

    /// <summary>Validated summary only when completed.</summary>
    public ActiveTurnCompactionSummary? Summary { get; init; }

    /// <summary>Bounded inspectable rationale without source content.</summary>
    public required string Rationale { get; init; }

    /// <summary>Provider calls consumed by this operation.</summary>
    public int ProviderCalls { get; init; }

    /// <summary>Reported provider usage for budget accounting.</summary>
    public ModelUsage? Usage { get; init; }

    /// <summary>Monotonic operation duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>Generates and validates active-turn summaries without mutating loop state.</summary>
public interface IActiveTurnCompactor
{
    /// <summary>Attempts one bounded atomic candidate operation.</summary>
    Task<ActiveTurnCompactionResult> CompactAsync(
        ActiveTurnCompactionRequest request,
        IActiveTurnCompactionAttemptObserver attemptObserver,
        CancellationToken cancellationToken = default);
}

/// <summary>Validates one model-written replacement summary against its selected source prefix and bounds.</summary>
public sealed class ActiveTurnCompactionValidator : IActiveTurnCompactionValidator
{
    private static readonly string[] DisallowedSummaryMarkers =
    [
        "<host-policy",
        "<phase-policy",
        "<required_output",
        "ignore previous",
        "permission granted",
        "mutation approved",
        "you are now",
    ];

    private readonly ActiveTurnCompactionPolicy _policy;
    private readonly IPromptLoader _prompts;
    private readonly IOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="ActiveTurnCompactionValidator"/> class.</summary>
    public ActiveTurnCompactionValidator(
        ActiveTurnCompactionPolicy policy,
        IOutputSanitizer sanitizer,
        IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(prompts);
        policy.Validate();
        _policy = policy;
        _sanitizer = sanitizer;
        _prompts = prompts;
    }

    /// <inheritdoc />
    public ActiveTurnCompactionValidationResult Validate(
        ActiveTurnCompactionRequest request,
        ActiveTurnCompactionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidate);
        if (request.EligiblePrefix is not { Count: > 0 } eligiblePrefix
            || candidate.CoveredGroupSequences is not { } coveredGroupSequences
            || candidate.SummaryText is not { } summaryText
            || candidate.FilesRead is not { } filesRead
            || candidate.FilesChanged is not { } filesChanged)
        {
            return new ActiveTurnCompactionValidationResult(
                false,
                ActiveTurnCompactionRejectionReason.Schema,
                ["Candidate summary fields or the eligible source range are missing."]);
        }

        var errors = new List<string>();
        var reason = ActiveTurnCompactionRejectionReason.None;
        var priorGroups = request.PriorSummary?.CoveredGroupSequences ?? [];
        var newGroupCount = coveredGroupSequences.Count - priorGroups.Count;
        if (candidate.SchemaVersion != 2
            || candidate.PriorSummaryVersion != (request.PriorSummary?.Version ?? 0)
            || newGroupCount is < 1
            || newGroupCount > eligiblePrefix.Count)
        {
            errors.Add("Candidate schema or replacement boundary does not match the request.");
            reason = ActiveTurnCompactionRejectionReason.Schema;
        }

        var selectedGroups = newGroupCount is > 0 && newGroupCount <= eligiblePrefix.Count
            ? eligiblePrefix.Take(newGroupCount).ToArray()
            : [];
        long[] expectedGroups =
        [
            .. priorGroups,
            .. selectedGroups.Select(group => group.Sequence),
        ];
        if (!coveredGroupSequences.SequenceEqual(expectedGroups)
            || selectedGroups.Length == 0
            || candidate.ThroughGroupSequence != selectedGroups[^1].Sequence)
        {
            errors.Add("Candidate covered groups are not the exact oldest selected prefix.");
            reason = ActiveTurnCompactionRejectionReason.Source;
        }

        var expectedFiles = ActiveTurnFileListBuilder.Build(
            request.PriorSummary,
            selectedGroups);
        if (!filesRead.SequenceEqual(expectedFiles.FilesRead)
            || !filesChanged.SequenceEqual(expectedFiles.FilesChanged))
        {
            errors.Add("Candidate file lists do not match the host-observed summarized work.");
            reason = ActiveTurnCompactionRejectionReason.Source;
        }

        if (string.IsNullOrWhiteSpace(summaryText)
            || summaryText.Length > checked(_policy.SummaryBudgetTokens * 4))
        {
            errors.Add("Candidate summary text is empty or oversized.");
            reason = ActiveTurnCompactionRejectionReason.Size;
        }
        else
        {
            var content = ActiveTurnSummaryFormatter.Render(
                summaryText,
                filesRead,
                filesChanged,
                _prompts);
            if (!string.Equals(_sanitizer.Sanitize(content), content, StringComparison.Ordinal))
            {
                errors.Add("Candidate summary does not satisfy sanitization policy.");
                reason = ActiveTurnCompactionRejectionReason.Sensitivity;
            }
            else if (DisallowedSummaryMarkers.Any(marker =>
                content.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("Candidate summary contains a policy, permission, or instruction marker.");
                reason = ActiveTurnCompactionRejectionReason.Authority;
            }
            else
            {
                var nextVersion = checked((request.PriorSummary?.Version ?? 0) + 1);
                var estimate = ModelWireEstimator.Estimate(
                    [ActiveTurnSummaryFormatter.CreateMessage(nextVersion, content, _prompts)],
                    [],
                    ToolTransportMode.Native,
                    0,
                    0);
                if (estimate.WireInputTokens > _policy.SummaryBudgetTokens)
                {
                    errors.Add("Candidate summary exceeds the configured token budget.");
                    reason = ActiveTurnCompactionRejectionReason.Size;
                }
            }
        }

        return new ActiveTurnCompactionValidationResult(errors.Count == 0, reason, errors);
    }
}

/// <summary>Model-backed provider for one updated active-turn summary.</summary>
public sealed class ModelActiveTurnCompactionCandidateProvider : IActiveTurnCompactionCandidateProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IModelProvider _model;
    private readonly ActiveTurnCompactionPolicy _policy;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="ModelActiveTurnCompactionCandidateProvider"/> class.</summary>
    public ModelActiveTurnCompactionCandidateProvider(
        IModelProvider model,
        ActiveTurnCompactionPolicy policy,
        IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(prompts);
        policy.Validate();
        _model = model;
        _policy = policy;
        _prompts = prompts;
    }

    /// <inheritdoc />
    public IActiveTurnCompactionCandidateAttempt PrepareCandidate(
        ActiveTurnCompactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var containsSensitiveData = request.ContainsSensitiveData
            || (request.CandidateProfile is not null
                && request.EligiblePrefix.Any(group =>
                    group.Sensitivity == ConversationSensitivity.Sensitive));
        var profile = ResolveCandidateProfile(request, containsSensitiveData);
        ValidateProfileCapacity(profile.ContextWindowTokens, profile.OutputReserveTokens);
        if (containsSensitiveData
            && profile.SensitiveDataPolicy == ModelSensitiveDataPolicy.Prohibited)
        {
            throw new ModelProviderException(
                "The configured active-turn compaction profile prohibits sensitive candidate input.");
        }

        if (request.SelectionConstraints.MaximumCombinedCostPerMillionTokens is { } maximumCost
            && profile.Cost is { } cost
            && cost.InputPerMillionTokens + cost.OutputPerMillionTokens > maximumCost)
        {
            throw new ModelProviderException(
                "The configured active-turn compaction profile exceeds the request cost ceiling.");
        }

        ValidateTaskContext(request);
        ValidateRequestShape(request);
        var modelOutputTokens = _policy.ResolveModelOutputTokens(profile.OutputReserveTokens);
        var summaryPrompt = _prompts.Get(request.PriorSummary is null
            ? PromptFileNames.ContextActiveTurnCompactionInitial
            : PromptFileNames.ContextActiveTurnCompactionUpdate);
        var input = CreateInput(request, profile, summaryPrompt, modelOutputTokens);
        var messages = CreateMessages(summaryPrompt, input.Json);
        var wireEstimate = ModelWireEstimator.Estimate(
            messages,
            [],
            ToolTransportMode.Native,
            0,
            modelOutputTokens,
            profile.ProviderInstructions);
        return new ModelCandidateAttempt(
            _model,
            new ModelStreamRequest
            {
                RunId = request.RunId,
                Input = input.Json,
                ToolContinuationRound = request.ToolContinuationRound,
                WorkloadClass = profile.WorkloadClass,
                ContainsSensitiveData = containsSensitiveData,
                RequiredCapabilities = profile.RequiredCapabilities,
                SelectionConstraints = profile.SelectionConstraints,
                ResolvedProfileId = profile.ProfileId,
                ReasoningLevel = profile.ReasoningLevel,
                MaximumOutputTokens = modelOutputTokens,
                AllowMultipleToolCalls = false,
                Messages = messages,
                WireEstimate = wireEstimate,
                ProviderInstructions = profile.ProviderInstructions,
            },
            input.Envelope,
            checked(modelOutputTokens * 4));
    }

    private CandidateInputProjection CreateInput(
        ActiveTurnCompactionRequest request,
        CandidateProfileSelection profile,
        string summaryPrompt,
        int modelOutputTokens)
    {
        var profileInputCapacity = checked(
            profile.ContextWindowTokens - modelOutputTokens);
        var maximumCandidateInputTokens = Math.Min(
            _policy.MaximumInputTokens,
            profileInputCapacity);
        var maximumCandidateCharacters = checked(maximumCandidateInputTokens * 4);
        var fixedCharacters = EstimateFixedInputCharacters(request, summaryPrompt);
        var maximumRawGroupCount = 0;
        var aggregateCharacters = fixedCharacters;
        foreach (var group in request.EligiblePrefix)
        {
            var groupCharacters = EstimateGroupInputCharacters(group);
            if (aggregateCharacters > maximumCandidateCharacters - groupCharacters)
            {
                break;
            }

            aggregateCharacters += groupCharacters;
            maximumRawGroupCount++;
        }

        if (maximumRawGroupCount == 0)
        {
            throw new ModelProviderException(
                "The previous summary and first complete source group cannot fit the bounded candidate request.");
        }

        CandidateInputProjection? selectedInput = null;
        var lowerBound = 1;
        var upperBound = maximumRawGroupCount;
        while (lowerBound <= upperBound)
        {
            var groupCount = lowerBound + ((upperBound - lowerBound) / 2);
            var input = CreateInput(request, groupCount, modelOutputTokens);
            var estimate = ModelWireEstimator.Estimate(
                CreateMessages(summaryPrompt, input.Json),
                [],
                ToolTransportMode.Native,
                0,
                modelOutputTokens,
                profile.ProviderInstructions);
            if (estimate.WireInputTokens <= maximumCandidateInputTokens)
            {
                selectedInput = input;
                lowerBound = groupCount + 1;
            }
            else
            {
                upperBound = groupCount - 1;
            }
        }

        return selectedInput ?? throw new ModelProviderException(
            "The previous summary and first complete source group cannot fit the bounded candidate request.");
    }

    private long EstimateFixedInputCharacters(
        ActiveTurnCompactionRequest request,
        string summaryPrompt)
    {
        var characters = (long)_prompts.Get(PromptFileNames.ContextActiveTurnCompactionSystem).Length
            + _prompts.Get(PromptFileNames.ContextActiveTurnCompactionOutputContract).Length
            + summaryPrompt.Length
            + request.TaskObjective.Length
            + request.AcceptanceIntent.Sum(intent => (long)intent.Description.Length)
            + (request.PriorSummary?.Content.Length ?? 0)
            + (request.PriorSummary?.FilesRead.Sum(path => (long)path.Length) ?? 0)
            + (request.PriorSummary?.FilesChanged.Sum(path => (long)path.Length) ?? 0)
            + 1_024;
        return characters;
    }

    private static long EstimateGroupInputCharacters(ActiveTurnContinuationGroup group)
    {
        return group.Messages.Sum(message =>
                64L
                + (message.ToolCallId?.Length ?? 0)
                + (message.ToolName?.Length ?? 0)
                + message.Content.Where(static part => part.IsModelVisible).Sum(static part => 32L + part.Content.Length))
            + group.FilesRead.Sum(path => 16L + path.Length)
            + group.FilesChanged.Sum(path => 16L + path.Length)
            + 128;
    }

    private IReadOnlyList<ModelMessage> CreateMessages(
        string summaryPrompt,
        string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        return
        [
            new ModelMessage
            {
                Role = ModelMessageRole.System,
                SectionId = "active-turn-compaction-policy",
                Content =
                [
                    new ModelContentPart
                    {
                        Content = _prompts.Get(PromptFileNames.ContextActiveTurnCompactionSystem)
                            + "\n\n"
                            + summaryPrompt
                            + "\n\n"
                            + _prompts.Get(PromptFileNames.ContextActiveTurnCompactionOutputContract),
                    },
                ],
            },
            new ModelMessage
            {
                Role = ModelMessageRole.User,
                SectionId = "active-turn-compaction-input",
                Content = [new ModelContentPart { Kind = ModelContentPartKind.Json, Content = input }],
            },
        ];
    }

    private void ValidateRequestShape(ActiveTurnCompactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.EligiblePrefix);
        if (request.EligiblePrefix.Count == 0
            || request.EligiblePrefix.Count > _policy.MaximumSourceGroups)
        {
            throw new ModelProviderException(
                "The active-turn candidate prefix exceeds the bounded group count.");
        }

        var totalMessages = 0;
        foreach (var group in request.EligiblePrefix)
        {
            if (group is null
                || group.Messages is not { Count: > 0 } messages
                || group.Sources is not { } sources
                || group.FilesRead is not { } filesRead
                || group.FilesChanged is not { } filesChanged
                || sources.Count > _policy.MaximumSourcesPerGroup
                || filesRead.Count > _policy.MaximumSourcesPerGroup
                || filesChanged.Count > _policy.MaximumSourcesPerGroup)
            {
                throw new ModelProviderException(
                    "The active-turn candidate group shape exceeds host bounds.");
            }

            totalMessages = checked(totalMessages + messages.Count);
            if (totalMessages > _policy.MaximumCandidateMessages
                || sources.Any(source => !IsBoundedSource(source))
                || filesRead.Any(path => !IsBoundedPath(path))
                || filesChanged.Any(path => !IsBoundedPath(path)))
            {
                throw new ModelProviderException(
                    "The active-turn candidate message, source, or file-list shape exceeds host bounds.");
            }
        }

        if (request.PriorSummary is { } prior
            && (string.IsNullOrWhiteSpace(prior.Content)
                || prior.CoveredGroupSequences is null
                || prior.FilesRead is null
                || prior.FilesChanged is null
                || prior.FilesRead.Any(path => !IsBoundedPath(path))
                || prior.FilesChanged.Any(path => !IsBoundedPath(path))))
        {
            throw new ModelProviderException(
                "The active-turn prior summary exceeds candidate input bounds.");
        }
    }

    private bool IsBoundedSource(ActiveTurnSourceReference source)
    {
        return source is not null
            && !string.IsNullOrWhiteSpace(source.Id)
            && source.Id.Length <= _policy.MaximumSourceIdentifierCharacters;
    }

    private bool IsBoundedPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.Length <= _policy.MaximumSourceIdentifierCharacters;
    }

    private void ValidateTaskContext(ActiveTurnCompactionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskObjective);
        ArgumentNullException.ThrowIfNull(request.AcceptanceIntent);
        var totalAcceptanceCharacters = 0;
        var optionalCriterionSeen = false;
        foreach (var intent in request.AcceptanceIntent)
        {
            if (intent is null
                || string.IsNullOrWhiteSpace(intent.Description)
                || intent.Description.Length > _policy.MaximumAcceptanceIntentCharacters
                || (intent.IsRequired && optionalCriterionSeen))
            {
                throw new ArgumentException(
                    "Active-turn acceptance intent is invalid, oversized, or not required-first.",
                    nameof(request));
            }

            optionalCriterionSeen |= !intent.IsRequired;
            totalAcceptanceCharacters = checked(
                totalAcceptanceCharacters + intent.Description.Length);
        }

        if (request.TaskObjective.Length > _policy.MaximumTaskObjectiveCharacters
            || request.AcceptanceIntent.Count > _policy.MaximumAcceptanceIntentItems
            || totalAcceptanceCharacters > _policy.MaximumAcceptanceIntentTotalCharacters
            || request.OmittedAcceptanceIntentCount < 0)
        {
            throw new ArgumentException(
                "Active-turn task objective or acceptance intent exceeds candidate-input bounds.",
                nameof(request));
        }
    }

    private static CandidateProfileSelection ResolveCandidateProfile(
        ActiveTurnCompactionRequest request,
        bool containsSensitiveData)
    {
        if (request.CandidateProfile is not { } profile)
        {
            return new CandidateProfileSelection(
                request.ProfileId,
                request.ProfileContextWindowTokens,
                request.ProfileOutputReserveTokens,
                ReasoningLevel.None,
                WorkloadClass.General,
                new ModelCapabilitySet { Streaming = true },
                request.SelectionConstraints with { ContainsSensitiveData = containsSensitiveData },
                null,
                null,
                request.ProviderInstructions);
        }

        return new CandidateProfileSelection(
            profile.ProfileId,
            profile.ContextWindowTokens,
            profile.OutputReserveTokens,
            profile.ReasoningLevel,
            WorkloadClass.Summary,
            new ModelCapabilitySet { Streaming = true },
            new ModelSelectionConstraints
            {
                ContainsSensitiveData = containsSensitiveData,
                MaximumCombinedCostPerMillionTokens =
                    request.SelectionConstraints.MaximumCombinedCostPerMillionTokens,
            },
            profile.SensitiveDataPolicy,
            profile.Cost,
            profile.ProviderInstructions);
    }

    private static void ValidateProfileCapacity(
        int contextWindowTokens,
        int outputReserveTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextWindowTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputReserveTokens);
        if (outputReserveTokens >= contextWindowTokens)
        {
            throw new ArgumentException(
                "The selected candidate profile output reserve must remain below its context window.");
        }
    }

    private static CandidateInputProjection CreateInput(
        ActiveTurnCompactionRequest request,
        int groupCount,
        int modelOutputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(groupCount, request.EligiblePrefix.Count);
        var selectedGroups = request.EligiblePrefix.Take(groupCount).ToArray();
        var files = ActiveTurnFileListBuilder.Build(request.PriorSummary, selectedGroups);
        var source = new
        {
            schemaVersion = 2,
            taskObjective = new
            {
                text = request.TaskObjective,
                wasTruncated = request.TaskObjectiveWasTruncated,
            },
            acceptanceIntent = request.AcceptanceIntent.Select(intent => new
            {
                intent.Description,
                intent.IsRequired,
                intent.WasTruncated,
            }),
            omittedAcceptanceIntentCount = request.OmittedAcceptanceIntentCount,
            requiredOutput = new
            {
                maximumModelOutputTokens = modelOutputTokens,
            },
            previousSummary = request.PriorSummary is null
                ? null
                : new
                {
                    request.PriorSummary.Version,
                    request.PriorSummary.Content,
                },
            newToolActivity = selectedGroups.Select(group => new
            {
                group.Sequence,
                messages = group.Messages.Select(message => new
                {
                    role = message.Role.ToString(),
                    message.ToolCallId,
                    message.ToolName,
                    content = message.Content
                        .Where(static part => part.IsModelVisible)
                        .Select(part => new
                        {
                            kind = part.Kind.ToString(),
                            text = part.Content,
                        }),
                }),
            }),
            filesRead = files.FilesRead,
            filesChanged = files.FilesChanged,
        };
        var envelope = new CandidateEnvelope(
            request.PriorSummary?.Version ?? 0,
            selectedGroups[^1].Sequence,
            (request.PriorSummary?.CoveredGroupSequences ?? [])
                .Concat(selectedGroups.Select(group => group.Sequence))
                .ToArray(),
            files);
        return new CandidateInputProjection(
            JsonSerializer.Serialize(source, JsonOptions),
            envelope);
    }

    private sealed record CandidateProfileSelection(
        ModelProfileId ProfileId,
        int ContextWindowTokens,
        int OutputReserveTokens,
        ReasoningLevel ReasoningLevel,
        WorkloadClass WorkloadClass,
        ModelCapabilitySet RequiredCapabilities,
        ModelSelectionConstraints SelectionConstraints,
        ModelSensitiveDataPolicy? SensitiveDataPolicy,
        ModelCostMetadata? Cost,
        ModelProviderInstructions? ProviderInstructions);

    private sealed record CandidateInputProjection(
        string Json,
        CandidateEnvelope Envelope);

    private sealed record CandidateEnvelope(
        int PriorSummaryVersion,
        long ThroughGroupSequence,
        IReadOnlyList<long> CoveredGroupSequences,
        ActiveTurnFileLists FileLists);

    private sealed class ModelCandidateAttempt : IActiveTurnCompactionCandidateAttempt
    {
        private readonly CandidateEnvelope _envelope;
        private readonly int _maximumResponseCharacters;
        private readonly IModelProvider _model;
        private readonly ModelStreamRequest _request;

        public ModelCandidateAttempt(
            IModelProvider model,
            ModelStreamRequest request,
            CandidateEnvelope envelope,
            int maximumResponseCharacters)
        {
            _model = model;
            _request = request;
            _envelope = envelope;
            _maximumResponseCharacters = maximumResponseCharacters;
        }

        public ModelUsage? ObservedUsage { get; private set; }

        public async Task<ActiveTurnCandidateGeneration> ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            var response = new StringBuilder();
            ModelFinishReason? finishReason = null;
            await foreach (var chunk in _model.StreamAsync(_request, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObservedUsage = chunk.Usage ?? ObservedUsage;
                finishReason = chunk.FinishReason ?? finishReason;
                if (chunk.Output is not null)
                {
                    throw new MalformedModelOutputException(
                        "The active-turn compaction model returned an unsupported structured output kind.");
                }

                if (chunk.Text is not null)
                {
                    if (chunk.Text.Length > _maximumResponseCharacters - response.Length)
                    {
                        throw new MalformedModelOutputException(
                            "The active-turn compaction model exceeded the bounded response size.");
                    }

                    response.Append(chunk.Text);
                }
            }

            if (finishReason is not null and not ModelFinishReason.Stop)
            {
                throw new MalformedModelOutputException(
                    "The active-turn compaction model did not complete the summary normally.");
            }

            var summaryText = RemoveModelFileSections(response.ToString().Trim());
            if (string.IsNullOrWhiteSpace(summaryText))
            {
                throw new MalformedModelOutputException(
                    "The active-turn compaction model returned an empty summary.");
            }

            return new ActiveTurnCandidateGeneration(
                new ActiveTurnCompactionCandidate
                {
                    SchemaVersion = 2,
                    PriorSummaryVersion = _envelope.PriorSummaryVersion,
                    ThroughGroupSequence = _envelope.ThroughGroupSequence,
                    CoveredGroupSequences = _envelope.CoveredGroupSequences.ToArray(),
                    SummaryText = summaryText,
                    FilesRead = _envelope.FileLists.FilesRead.ToArray(),
                    FilesChanged = _envelope.FileLists.FilesChanged.ToArray(),
                },
                ObservedUsage);
        }

        private static string RemoveModelFileSections(string summaryText)
        {
            var content = new StringBuilder(summaryText.Length);
            var copyStart = 0;
            var lineStart = 0;
            var removingFileSection = false;
            while (lineStart < summaryText.Length)
            {
                var lineEnd = summaryText.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                {
                    lineEnd = summaryText.Length;
                }

                var line = summaryText.AsSpan(lineStart, lineEnd - lineStart);
                if (IsMarkdownHeading(line, out var heading))
                {
                    var startsFileSection = heading.Equals(
                            "Files read",
                            StringComparison.OrdinalIgnoreCase)
                        || heading.Equals(
                            "Files changed",
                            StringComparison.OrdinalIgnoreCase);
                    if (startsFileSection && !removingFileSection)
                    {
                        content.Append(summaryText.AsSpan(copyStart, lineStart - copyStart));
                        removingFileSection = true;
                    }
                    else if (!startsFileSection && removingFileSection)
                    {
                        copyStart = lineStart;
                        removingFileSection = false;
                    }
                }

                lineStart = lineEnd + 1;
            }

            if (!removingFileSection)
            {
                content.Append(summaryText.AsSpan(copyStart));
            }

            return content.ToString().TrimEnd();
        }

        private static bool IsMarkdownHeading(
            ReadOnlySpan<char> line,
            out ReadOnlySpan<char> heading)
        {
            line = line.Trim();
            var headingEnd = 0;
            while (headingEnd < line.Length && line[headingEnd] == '#')
            {
                headingEnd++;
            }

            if (headingEnd is < 1 or > 6
                || headingEnd == line.Length
                || !char.IsWhiteSpace(line[headingEnd]))
            {
                heading = [];
                return false;
            }

            heading = line[headingEnd..].Trim().TrimEnd('#').TrimEnd();
            return heading.Length > 0;
        }
    }
}

/// <summary>Bounded candidate orchestration with validation and failure preservation.</summary>
public sealed class ActiveTurnCompactor : IActiveTurnCompactor
{
    private static readonly ActivitySource ActivitySource = new("Threadsmith.Context.ActiveTurnCompaction");

    private readonly IActiveTurnCompactionCandidateProvider _provider;
    private readonly ActiveTurnCompactionPolicy _policy;
    private readonly IPromptLoader _prompts;
    private readonly TimeProvider _timeProvider;
    private readonly IActiveTurnCompactionValidator _validator;

    /// <summary>Initializes a new instance of the <see cref="ActiveTurnCompactor"/> class.</summary>
    public ActiveTurnCompactor(
        IActiveTurnCompactionCandidateProvider provider,
        IActiveTurnCompactionValidator validator,
        ActiveTurnCompactionPolicy policy,
        IPromptLoader prompts,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(prompts);
        policy.Validate();
        _provider = provider;
        _validator = validator;
        _policy = policy;
        _prompts = prompts;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ActiveTurnCompactionResult> CompactAsync(
        ActiveTurnCompactionRequest request,
        IActiveTurnCompactionAttemptObserver attemptObserver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(attemptObserver);
        var startedAt = _timeProvider.GetTimestamp();
        using var activity = ActivitySource.StartActivity("active_turn.compact");
        activity?.SetTag("threadsmith.run.id", request.RunId.Value.ToString("D"));
        var candidateProfileId = request.CandidateProfile?.ProfileId ?? request.ProfileId;
        activity?.SetTag("threadsmith.model.profile_id", candidateProfileId.Value.ToString("D"));
        activity?.SetTag("threadsmith.active_turn.eligible_source_groups", request.EligiblePrefix.Count);
        activity?.SetTag("threadsmith.active_turn.before_input_tokens", request.BeforeInputTokens);
        activity?.SetTag("threadsmith.active_turn.pressure_target_tokens", request.PressureTargetTokens);
        var calls = 0;
        ActiveTurnCandidateGeneration generation;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IActiveTurnCompactionCandidateAttempt candidateAttempt;
            try
            {
                candidateAttempt = _provider.PrepareCandidate(request);
            }
            catch (Exception exception) when (
                exception is TransientModelException
                    or ModelProviderTimeoutException
                    or ModelProviderException
                    or MalformedModelOutputException)
            {
                activity?.SetTag("threadsmith.active_turn.outcome", "preflight_failure");
                activity?.SetStatus(ActivityStatusCode.Error, "preflight_failure");
                return CreateFailure(
                    ActiveTurnCompactionOutcome.ProviderFailure,
                    "The candidate request failed preflight before provider I/O; the original continuation remains active.",
                    calls,
                    startedAt);
            }

            calls++;
            var invocationId = Guid.NewGuid();
            await attemptObserver.BeforeProviderCallAsync(
                request,
                calls,
                invocationId,
                cancellationToken);

            var attemptStartedAt = _timeProvider.GetTimestamp();
            var attemptOutcome = ActiveTurnCompactionAttemptOutcome.Failed;
            ModelUsage? attemptUsage = null;
            try
            {
                generation = await candidateAttempt.ExecuteAsync(cancellationToken);
                attemptUsage = candidateAttempt.ObservedUsage ?? generation.Usage;
                attemptOutcome = ActiveTurnCompactionAttemptOutcome.Completed;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                attemptOutcome = ActiveTurnCompactionAttemptOutcome.Cancelled;
                activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
                throw;
            }
            catch (Exception exception) when (
                (exception is TransientModelException or ModelProviderTimeoutException)
                && calls <= _policy.MaximumProviderRetries
                && calls < _policy.MaximumProviderCalls)
            {
                continue;
            }
            catch (Exception exception) when (
                exception is TransientModelException
                    or ModelProviderTimeoutException
                    or ModelProviderException
                    or MalformedModelOutputException)
            {
                activity?.SetTag("threadsmith.active_turn.outcome", "provider_failure");
                activity?.SetStatus(ActivityStatusCode.Error, "provider_failure");
                return CreateFailure(
                    ActiveTurnCompactionOutcome.ProviderFailure,
                    "The candidate provider failed within the bounded call budget; the original continuation remains active.",
                    calls,
                    startedAt);
            }
            finally
            {
                attemptUsage = candidateAttempt.ObservedUsage ?? attemptUsage;
                await attemptObserver.AfterProviderCallAsync(
                    request,
                    calls,
                    invocationId,
                    attemptOutcome,
                    attemptUsage,
                    _timeProvider.GetElapsedTime(attemptStartedAt),
                    CancellationToken.None);
            }
        }

        var validation = _validator.Validate(request, generation.Candidate);
        if (!validation.IsValid)
        {
            activity?.SetTag("threadsmith.active_turn.outcome", "validation_rejected");
            activity?.SetTag(
                "threadsmith.active_turn.rejection_reason",
                validation.RejectionReason.ToString());
            activity?.SetStatus(ActivityStatusCode.Error, "validation_rejected");
            return new ActiveTurnCompactionResult
            {
                Outcome = ActiveTurnCompactionOutcome.ValidationRejected,
                Rationale = $"Candidate validation rejected the replacement ({validation.RejectionReason}).",
                ProviderCalls = calls,
                Usage = generation.Usage,
                Duration = _timeProvider.GetElapsedTime(startedAt),
            };
        }

        var selectedGroupCount = generation.Candidate.CoveredGroupSequences.Count
            - (request.PriorSummary?.CoveredGroupSequences.Count ?? 0);
        activity?.SetTag("threadsmith.active_turn.source_groups", selectedGroupCount);
        var version = checked((request.PriorSummary?.Version ?? 0) + 1);
        var content = ActiveTurnSummaryFormatter.Render(
            generation.Candidate.SummaryText,
            generation.Candidate.FilesRead,
            generation.Candidate.FilesChanged,
            _prompts);
        var summary = new ActiveTurnCompactionSummary
        {
            Version = version,
            ThroughGroupSequence = generation.Candidate.ThroughGroupSequence,
            CoveredGroupSequences = generation.Candidate.CoveredGroupSequences.ToArray(),
            Content = content,
            FilesRead = generation.Candidate.FilesRead.ToArray(),
            FilesChanged = generation.Candidate.FilesChanged.ToArray(),
            ContentHash = "sha256:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(content))),
        };
        activity?.SetTag("threadsmith.active_turn.summary_version", version);
        activity?.SetTag("threadsmith.active_turn.summary_hash", summary.ContentHash);
        activity?.SetTag("threadsmith.active_turn.provider_calls", calls);
        activity?.SetTag("threadsmith.active_turn.outcome", "completed");
        activity?.SetStatus(ActivityStatusCode.Ok);
        return new ActiveTurnCompactionResult
        {
            Outcome = ActiveTurnCompactionOutcome.Completed,
            Summary = summary,
            Rationale = "A validated cumulative active-turn summary is ready for atomic request rebuilding.",
            ProviderCalls = calls,
            Usage = generation.Usage,
            Duration = _timeProvider.GetElapsedTime(startedAt),
        };
    }

    private ActiveTurnCompactionResult CreateFailure(
        ActiveTurnCompactionOutcome outcome,
        string rationale,
        int calls,
        long startedAt)
    {
        return new ActiveTurnCompactionResult
        {
            Outcome = outcome,
            Rationale = rationale,
            ProviderCalls = calls,
            Duration = _timeProvider.GetElapsedTime(startedAt),
        };
    }
}

/// <summary>Projects one validated active-turn summary as earlier assistant notes.</summary>
public static class ActiveTurnSummaryFormatter
{
    /// <summary>Creates one historical assistant message without changing system or current-user messages.</summary>
    public static ModelMessage CreateMessage(int version, string content, IPromptLoader prompts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentNullException.ThrowIfNull(prompts);
        return new ModelMessage
        {
            Role = ModelMessageRole.Assistant,
            SectionId = "active-turn-summary",
            Content =
            [
                new ModelContentPart
                {
                    Content = prompts.Render(
                        PromptFileNames.ContextActiveTurnSummaryUntrustedWrapper,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Version"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["SummaryContent"] = content,
                        }),
                },
            ],
        };
    }

    /// <summary>Appends cumulative host-observed file lists to one model-written summary.</summary>
    public static string Render(
        string summaryText,
        IReadOnlyList<string> filesRead,
        IReadOnlyList<string> filesChanged,
        IPromptLoader prompts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryText);
        ArgumentNullException.ThrowIfNull(filesRead);
        ArgumentNullException.ThrowIfNull(filesChanged);
        ArgumentNullException.ThrowIfNull(prompts);
        var fileLists = PromptAssetRenderer.RenderWithPlatformLineEndings(
            prompts,
            PromptFileNames.ContextActiveTurnSummaryHostFileLists,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FilesRead"] = RenderFileList(filesRead),
                ["FilesChanged"] = RenderFileList(filesChanged),
            });
        return summaryText.Trim() + Environment.NewLine + Environment.NewLine + fileLists;
    }

    private static string RenderFileList(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return "- None.";
        }

        return string.Join(Environment.NewLine, paths.Select(path => "- " + JsonSerializer.Serialize(path)));
    }
}
