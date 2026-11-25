namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
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

        var activity = InteractionCoordinator.FormatActiveTurnCompactionActivity(
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
        var completion = InteractionCoordinator.FormatActiveTurnCompactionCompletion(completedEvent);
        var completionWithoutDuration = InteractionCoordinator.FormatActiveTurnCompactionCompletion(
            completedEvent,
            showOperationDuration: false);

        Assert.Equal("COMPACTING CONTEXT: 87,434 tokens; pressure threshold 73,728", activity);
        Assert.Contains("CONTEXT COMPACTED: 87,434 → 31,699 tokens", completion, StringComparison.Ordinal);
        Assert.Contains("saved 55,735 (63.7%)", completion, StringComparison.Ordinal);
        Assert.Contains("15.3s", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("15.3s", completionWithoutDuration, StringComparison.Ordinal);
        Assert.DoesNotContain("summary", completion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Absent active-turn metadata contributes no terminal output.</summary>
    [Fact]
    public static void Missing_active_turn_inspection_formats_empty()
    {
        Assert.Equal(string.Empty, InteractionCoordinator.FormatActiveTurnInspection(null));
    }
}
