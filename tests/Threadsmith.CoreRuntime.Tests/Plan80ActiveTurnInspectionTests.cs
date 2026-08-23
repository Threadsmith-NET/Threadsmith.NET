namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Tui;
using Xunit;

/// <summary>Plan 80 bounded interactive inspection projection tests.</summary>
public static class Plan80ActiveTurnInspectionTests
{
    /// <summary>Interactive inspection exposes bounded active-turn metadata without summary content.</summary>
    [Fact]
    public static void Active_turn_inspection_formats_pressure_cut_and_outcome_metadata()
    {
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
                CandidateProfileId = Guid.NewGuid(),
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
