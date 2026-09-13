namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Checks shared event ownership independently of slash commands and terminal implementations.</summary>
public sealed class SkillOperationActivityTests
{
    /// <summary>Verifies the shared tool and skill activity contract.</summary>
    [Fact]
    public void ModelSkillProgressBelongsToItsToolAndCompletesOnce()
    {
        var skill = Start() with { InvokingToolInvocationId = ToolInvocationId.New() };
        var tool = new ToolInvocationStarted(skill.SessionId, skill.OccurredAt, skill.InvokingToolInvocationId.Value, "invoke_skill", skill.RunId!.Value);
        var activities = new InteractionOperationActivities(TimeProvider.System, true);
        var transcript = new ConversationTranscript(string.Empty);
        activities.Observe(tool);
        transcript.Apply(tool);
        activities.Observe(skill);
        transcript.Apply(skill);
        var started = Assert.Single(activities.Activities).StartedTimestamp;
        var progress = new SkillInvocationProgressObserved(skill.SessionId, skill.OccurredAt, skill.InvocationId, 0, "Fetching repository (Git)");
        activities.Observe(progress);
        activities.SetToolProgress(tool.ToolInvocationId, [new("Reviewer started", PresentationTextRole.Status)]);
        var activity = Assert.Single(activities.Activities);
        Assert.Equal(started, activity.StartedTimestamp);
        Assert.Equal(new[] { "Fetching repository (Git)", "Reviewer started" }, activity.ToolProgress.Select(item => item.Text));
        var completed = skill with { Status = SkillInvocationStatus.Completed };
        activities.Observe(completed);
        Assert.False(transcript.Apply(completed));
        Assert.Single(activities.Activities);
        var toolCompleted = new ToolInvocationCompleted(skill.SessionId, skill.OccurredAt, tool.ToolInvocationId, true);
        activities.Observe(toolCompleted);
        Assert.True(transcript.Apply(toolCompleted));
        Assert.Empty(activities.Activities);
        Assert.DoesNotContain("SKILLS:", transcript.Text, StringComparison.Ordinal);
        Assert.False(transcript.Apply(toolCompleted));
    }

    /// <summary>Verifies generation-aware lifecycle boundaries.</summary>
    [Theory]
    [InlineData(SkillInvocationStatus.Completed)]
    [InlineData(SkillInvocationStatus.Failed)]
    [InlineData(SkillInvocationStatus.Cancelled)]
    [InlineData(SkillInvocationStatus.AwaitingHost)]
    public void TerminalBoundaryRemovesOnlyItsSkillAndResumeRejectsOldProgress(SkillInvocationStatus status)
    {
        var skill = Start();
        var other = Start();
        var activities = new InteractionOperationActivities(TimeProvider.System, true);
        var transcript = new ConversationTranscript(string.Empty);
        activities.Observe(skill);
        activities.Observe(other);
        transcript.Apply(skill);
        var terminal = skill with { Status = status };
        activities.Observe(terminal);
        Assert.True(transcript.Apply(terminal));
        Assert.False(transcript.Apply(terminal));
        Assert.Single(activities.Activities);
        Assert.False(activities.Observe(skill));
        var resumed = skill with { Generation = 1 };
        activities.Observe(resumed);
        transcript.Apply(resumed);
        Assert.False(activities.Observe(terminal));
        Assert.False(transcript.Apply(terminal));
        Assert.False(activities.Observe(new SkillInvocationProgressObserved(skill.SessionId, skill.OccurredAt, skill.InvocationId, 0, "Stale")));
        Assert.Equal(2, activities.Activities.Count);
        Assert.True(activities.Observe(new SkillInvocationProgressObserved(skill.SessionId, skill.OccurredAt, skill.InvocationId, 1, "Current")));
        Assert.Equal("Current", activities.ActivityFor(resumed)!.ToolProgress.Single().Text);
    }

    /// <summary>Verifies the shared tool and skill activity contract.</summary>
    [Fact]
    public void HostProgressAndCorrelationRoundTripThroughEventPersistence()
    {
        var skill = Start() with { InvokingToolInvocationId = ToolInvocationId.New() };
        IDomainEvent progress = new SkillInvocationProgressObserved(skill.SessionId, skill.OccurredAt, skill.InvocationId, 2, "Reading source snapshots: 64/100 blobs");
        foreach (var item in new IDomainEvent[] { skill, progress })
        {
            Assert.Equal(item, DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(item), 1, DomainEventJson.Serialize(item)));
        }
    }

    private static SkillWorkflowCheckpointWritten Start() => new(
        SessionId.New(), DateTimeOffset.UtcNow, SkillInvocationId.New(), SkillWorkflowId.New(), new SkillId("review"), "1.0.0", new string('a', 64), SkillInvocationStatus.Running, 0, "Execute") { RunId = RunId.New() };
}
