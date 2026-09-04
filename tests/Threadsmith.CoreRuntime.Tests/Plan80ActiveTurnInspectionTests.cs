namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui;
using Xunit;

/// <summary>Plan 80 bounded interactive inspection projection tests.</summary>
public static class Plan80ActiveTurnInspectionTests
{
    /// <summary>Transient compaction activity and completion expose only bounded token/profile metadata.</summary>
    [Fact]
    public static void Active_turn_activity_formats_before_after_savings_and_profile()
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var profileId = ModelProfileId.New();
        var occurredAt = DateTimeOffset.UtcNow;

        var activity = ConversationalShell.FormatActiveTurnCompactionActivity(
            new ActiveTurnCompactionStarted(
                sessionId,
                occurredAt,
                runId,
                profileId,
                BeforeInputTokens: 87_434,
                PressureTargetTokens: 73_728));
        var completedEvent = new ActiveTurnCompactionCompleted(
                sessionId,
                occurredAt,
                runId,
                profileId,
                ActiveTurnCompactionInspectionStatus.Completed,
                BeforeInputTokens: 87_434,
                AfterInputTokens: 31_699,
                DurationMilliseconds: 15_300);
        var completion = ConversationalShell.FormatActiveTurnCompactionCompletion(completedEvent);
        var completionWithoutDuration = ConversationalShell.FormatActiveTurnCompactionCompletion(
            completedEvent,
            showOperationDuration: false);

        Assert.Contains("COMPACTING CONTEXT: 87,434 tokens", activity, StringComparison.Ordinal);
        Assert.Contains("target 73,728", activity, StringComparison.Ordinal);
        Assert.Contains(profileId.Value.ToString("D"), activity, StringComparison.Ordinal);
        Assert.Contains("CONTEXT COMPACTED: 87,434 → 31,699 tokens", completion, StringComparison.Ordinal);
        Assert.Contains("saved 55,735 (63.7%)", completion, StringComparison.Ordinal);
        Assert.Contains("15.3s", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("15.3s", completionWithoutDuration, StringComparison.Ordinal);
        Assert.DoesNotContain("summary", completion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Completion events render one semantically successful lifecycle segment.</summary>
    [Fact]
    public static void Active_turn_completion_event_renders_one_success_segment()
    {
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(
            segments,
            new ActiveTurnCompactionCompleted(
                SessionId.New(),
                DateTimeOffset.UtcNow,
                RunId.New(),
                ModelProfileId.New(),
                ActiveTurnCompactionInspectionStatus.Completed,
                BeforeInputTokens: 10_000,
                AfterInputTokens: 5_000,
                DurationMilliseconds: 100),
            string.Empty);

        var segment = Assert.Single(segments);
        Assert.Equal(PresentationTextRole.Success, segment.Role);
        Assert.Contains("10,000 → 5,000", segment.Text, StringComparison.Ordinal);
    }

    /// <summary>Interactive inspection exposes bounded active-turn metadata without summary content.</summary>
    [Fact]
    public static void Active_turn_inspection_formats_pressure_cut_and_outcome_metadata()
    {
        var candidateProfileId = Guid.NewGuid();
        var rendered = ConversationalShell.FormatActiveTurnInspection(
            new ActiveTurnCompactionInspectionProjection
            {
                AssessmentSequence = 3,
                Status = ActiveTurnCompactionInspectionStatus.Completed,
                BeforeInputTokens = 12_000,
                AfterInputTokens = 6_000,
                MaximumInputTokens = 15_500,
                PressureTargetTokens = 11_625,
                OutputReserveTokens = 500,
                ConfiguredRetentionTargetTokens = 12_000,
                EffectiveRetentionTargetTokens = 6_000,
                EligibleGroupCount = 1,
                CompactedGroupCount = 1,
                RetainedGroupCount = 1,
                RetainedGroupTokens = 4_000,
                SummaryVersion = 1,
                PrunedPriorItemCount = 2,
                HistoryRewriteGeneration = 1,
                SummaryContentHash = "sha256:must-not-render",
                CandidateProfileId = candidateProfileId,
                CompactedFromGroupSequence = 1,
                CompactedThroughGroupSequence = 1,
                BackoffRoundsRemaining = 0,
                Rationale = "Validated prefix replaced.",
            });

        Assert.Contains("active-turn Completed", rendered, StringComparison.Ordinal);
        Assert.Contains("input 12000 -> 6000", rendered, StringComparison.Ordinal);
        Assert.Contains("target/max 11625/15500", rendered, StringComparison.Ordinal);
        Assert.Contains("retention target effective/configured 6000/12000", rendered, StringComparison.Ordinal);
        Assert.Contains("groups eligible/compacted/retained 1/1/1", rendered, StringComparison.Ordinal);
        Assert.Contains("summary/pruned/generation 1/2/1", rendered, StringComparison.Ordinal);
        Assert.Contains($"candidate profile {candidateProfileId:D}", rendered, StringComparison.Ordinal);
        Assert.Contains("Validated prefix replaced.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-render", rendered, StringComparison.Ordinal);
    }

    /// <summary>Absent active-turn metadata contributes no terminal output.</summary>
    [Fact]
    public static void Missing_active_turn_inspection_formats_empty()
    {
        Assert.Equal(string.Empty, ConversationalShell.FormatActiveTurnInspection(null));
    }
}
