namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Exercises advisory completion projection, incomplete evidence and durable replay.</summary>
public static class SemanticCheckPresentationTests
{
    /// <summary>Replayed compiler and analyzer completions distinguish candidate and committed-source phases.</summary>
    [Theory]
    [InlineData("advisory edit analysis", "Compiler Checks", SemanticCheckPhase.PreMutation, "pre-mutation candidate analysis")]
    [InlineData("advisory edit analysis", "Compiler Checks", SemanticCheckPhase.PostMutation, "post-mutation committed-source analysis")]
    [InlineData("advisory edit analyzers", "Analyzer Checks", SemanticCheckPhase.PreMutation, "pre-mutation candidate analysis")]
    [InlineData("advisory edit analyzers", "Analyzer Checks", SemanticCheckPhase.PostMutation, "post-mutation committed-source analysis")]
    public static void ReplayedCompletionTitlesDistinguishPhases(string checkName, string caption, SemanticCheckPhase phase, string title)
    {
        var completed = new SemanticCheckCompleted(SessionId.New(), DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), phase, checkName, SemanticCheckOutcome.Completed);
        var restored = DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(completed), 1, DomainEventJson.Serialize(completed));
        var transcript = new ConversationTranscript(string.Empty);

        Assert.True(transcript.Apply(restored));

        Assert.Contains($"{caption}: {title} - completed", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Compiler and analyzer locations survive serialization and use the shared column display.</summary>
    [Theory]
    [InlineData("compiler")]
    [InlineData("analyzer")]
    public static void FindingColumnsSurviveReplayAndDistinguishSameLineLocations(string producer)
    {
        var feedback = new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Feedback")
        {
            EditAnalysis = new()
            {
                EffectId = Guid.NewGuid(),
                ProjectsAnalyzed = 1,
                ProjectsInScope = 1,
                Diagnostics = [
                    new("CS8604", "Nullable item", "Example.cs", 108, "Example", "net10.0", "unknown") { Column = 46, Producer = producer },
                    new("CS8604", "Nullable item", "Example.cs", 108, "Example", "net10.0", "unknown") { Column = 76, Producer = producer },
                    new("CS8604", "Legacy finding", "Example.cs", 191, "Example", "net10.0", "unknown"),
                ],
            },
        };
        var restored = DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(feedback), 1, DomainEventJson.Serialize(feedback));
        var transcript = new ConversationTranscript(string.Empty);
        transcript.Apply(restored);

        Assert.Contains("Example.cs:108:46 [", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Example.cs:108:76 [", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Example.cs:191 [", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>A completion rendered before a correlated wait suppresses that stale notice in live and replay projections.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void CorrelatedWaitRespectsSemanticCompletionOrder(bool completedBeforeWait)
    {
        var sessionId = SessionId.New();
        var effectId = Guid.NewGuid();
        var start = new SemanticCheckStarted(sessionId, DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), SemanticCheckPhase.PreMutation, "advisory edit analysis") { EffectId = effectId };
        var completed = new SemanticCheckCompleted(sessionId, DateTimeOffset.UtcNow, start.RunId, start.SemanticCheckId, start.Phase, start.CheckName, SemanticCheckOutcome.Completed)
        {
            Analysis = new() { EffectId = effectId, ProjectsAnalyzed = 1, ProjectsInScope = 1 },
        };
        var wait = new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Waiting for semantic feedback to complete.") { PendingSemanticEffectId = effectId };
        var transcript = new ConversationTranscript(string.Empty);
        transcript.Apply(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(start), 1, DomainEventJson.Serialize(start)));
        transcript.Apply(start with { SemanticCheckId = SemanticCheckId.New(), EffectId = Guid.NewGuid() });
        if (completedBeforeWait)
        {
            transcript.Apply(completed);
        }

        var restoredWait = DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(wait), 1, DomainEventJson.Serialize(wait));
        var before = transcript.Length;
        Assert.Equal(!completedBeforeWait, transcript.Apply(restoredWait));
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(segments, restoredWait, transcript.GetTextSince(before));
        Assert.Equal(!completedBeforeWait, string.Concat(segments.Select(segment => segment.Text)).Contains(wait.Message, StringComparison.Ordinal));
        Assert.All(segments, segment => Assert.Equal(PresentationTextRole.Warning, segment.Role));
    }

    private static string Render(SourceEditAnalysis analysis, SemanticCheckPhase phase = SemanticCheckPhase.PreMutation, SemanticCheckOutcome outcome = SemanticCheckOutcome.Completed)
    {
        var transcript = new ConversationTranscript(string.Empty);
        Assert.True(transcript.Apply(Completion(analysis, phase, outcome)));
        return transcript.Text;
    }

    private static SemanticCheckCompleted Completion(SourceEditAnalysis analysis, SemanticCheckPhase phase = SemanticCheckPhase.PreMutation, SemanticCheckOutcome outcome = SemanticCheckOutcome.Completed)
    {
        return new(SessionId.New(), DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), phase, "advisory edit analysis", outcome, Detail: "Legacy detail") { Analysis = analysis };
    }
}
