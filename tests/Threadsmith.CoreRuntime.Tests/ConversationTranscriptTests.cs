namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Verifies incremental access to the existing formatted conversation transcript.</summary>
public static class ConversationTranscriptTests
{
    /// <summary>Only successes for the same exact file and completed approval batch are combined.</summary>
    [Fact]
    public static void AppliedMutationsAreGroupedByExactFileAndApprovalBatch()
    {
        var transcript = new ConversationTranscript(string.Empty);
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        var first = MutationSetId.New();
        var second = MutationSetId.New();
        var firstApproval = ApprovalId.New();
        var secondApproval = ApprovalId.New();
        transcript.Apply(new MutationSetProposed(sessionId, now, first, RequiredApproval: MutationApprovalLevel.PolicyAutoApproved, ApprovalId: firstApproval));
        transcript.Apply(new MutationSetProposed(sessionId, now, second, ApprovalId: secondApproval));
        foreach (var path in new[] { "src/File.cs", "src/Other.cs", "src/File.cs", "src/file.cs", "src/File.cs" })
        {
            Assert.False(transcript.Apply(new MutationApplied(sessionId, now, MutationId.New(), first, path)));
        }

        Assert.False(transcript.Apply(new MutationApplied(sessionId, now, MutationId.New(), second, "src/File.cs")));
        Assert.Empty(transcript.Text);
        Assert.False(transcript.Apply(new ApprovalGranted(sessionId, now, ApprovalId.New())));
        var granted = new ApprovalGranted(sessionId, now, firstApproval);
        Assert.True(transcript.Apply(granted));
        Assert.Contains("3 mutations successfully applied to src/File.cs", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Mutation applied: src/Other.cs", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Mutation applied: src/file.cs", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Mutation applied: src/File.cs", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Applied under the active approval policy", transcript.Text, StringComparison.Ordinal);
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(segments, granted, transcript.Text);
        Assert.Equal(3, segments.Count(segment => segment.Role == PresentationTextRole.Success));
        Assert.Equal(transcript.Text, string.Concat(segments.Select(segment => segment.Text)));

        var length = transcript.Length;
        Assert.True(transcript.Apply(new ApprovalGranted(sessionId, now, secondApproval)));
        Assert.Contains("Mutation applied: src/File.cs", transcript.GetTextSince(length), StringComparison.Ordinal);
        Assert.DoesNotContain("3 mutations", transcript.GetTextSince(length), StringComparison.Ordinal);
        Assert.False(transcript.Apply(granted));
    }

    /// <summary>Failure diagnostics and rollback reporting retain their individual details.</summary>
    [Fact]
    public static void MutationFailureDiagnosticsRemainIndividualAndRollbackRemainsVisible()
    {
        var transcript = new ConversationTranscript(string.Empty);
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        var setId = MutationSetId.New();
        transcript.Apply(new MutationSetProposed(sessionId, now, setId, ApprovalId: ApprovalId.New()));
        transcript.Apply(new MutationApplied(sessionId, now, MutationId.New(), setId, "src/File.cs"));
        var segments = new List<PresentationTextSegment>();
        foreach (var reason in new[] { "First replacement did not match.", "Second replacement was ambiguous." })
        {
            var failure = new DiagnosticObserved(sessionId, now, "MutationFailed", reason);
            var length = transcript.Length;
            transcript.Apply(failure);
            InteractionEventSegments.Append(segments, failure, transcript.GetTextSince(length));
        }

        var text = string.Concat(segments.Select(segment => segment.Text));
        Assert.Contains("First replacement did not match.", text, StringComparison.Ordinal);
        Assert.Contains("Second replacement was ambiguous.", text, StringComparison.Ordinal);
        Assert.True(transcript.Apply(new MutationSetRolledBack(sessionId, now, setId, ["src/File.cs"])));
        Assert.Contains("Mutation applied: src/File.cs", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Mutation set rolled back; restored 1 files.", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Successful events remain visible if cancellation interrupts the final approval event.</summary>
    [Fact]
    public static void RunCompletionFlushesAppliedMutationsWhenApprovalPublicationWasInterrupted()
    {
        var transcript = new ConversationTranscript(string.Empty);
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        var setId = MutationSetId.New();
        transcript.Apply(new MutationSetProposed(sessionId, now, setId, ApprovalId: ApprovalId.New()));
        transcript.Apply(new MutationApplied(sessionId, now, MutationId.New(), setId, "src/File.cs"));
        var length = transcript.Length;
        Assert.True(transcript.Apply(new RunCompleted(sessionId, now, RunId.New(), false)));
        Assert.Contains("Mutation applied: src/File.cs", transcript.GetTextSince(length), StringComparison.Ordinal);
    }

    /// <summary>Successive deltas reproduce the full transcript without losing formatting or turn boundaries.</summary>
    [Fact]
    public static void DeltasPreserveFormattedTextAcrossVisibleAndInvisibleEvents()
    {
        const string initial = "Earlier conversation\n";
        var transcript = new ConversationTranscript(initial, showOperationDurations: false);
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        var toolId = ToolInvocationId.New();
        IDomainEvent[] events =
        [
            new TaskIntentRecorded(sessionId, now, "first turn"),
            new ModelOutputObserved(sessionId, now, "   "),
            new ModelReasoningObserved(sessionId, now, "hidden reasoning"),
            new ModelOutputObserved(sessionId, now, "Hello 😀"),
            new ModelOutputObserved(sessionId, now, "世界\n"),
            new ToolInvocationStarted(sessionId, now, toolId, "read_file"),
            new ToolInvocationCompleted(sessionId, now, toolId, true),
            new RunCompleted(sessionId, now, RunId.New(), true),
            new TaskIntentRecorded(sessionId, now, "second turn"),
            new ModelOutputObserved(sessionId, now, "Next answer"),
            new RunCompleted(sessionId, now, RunId.New(), true),
        ];
        var reconstructed = new StringBuilder(initial);

        foreach (var domainEvent in events)
        {
            var previousLength = transcript.Length;
            var previousText = transcript.Text;
            var changed = transcript.Apply(domainEvent);
            var delta = transcript.GetTextSince(previousLength);
            Assert.Equal(transcript.Text[previousLength..], delta);
            Assert.Equal(transcript.Text.Length, transcript.Length);
            if (!changed)
            {
                Assert.Empty(delta);
                Assert.Equal(previousText, transcript.Text);
            }

            reconstructed.Append(delta);
        }

        Assert.Equal(transcript.Text, reconstructed.ToString());
        Assert.Empty(transcript.GetTextSince(transcript.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => transcript.GetTextSince(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => transcript.GetTextSince(transcript.Length + 1));
    }

    /// <summary>Small deltas allocate independently of a large existing conversation.</summary>
    [Fact]
    public static void StreamingDeltasDoNotAllocateCopiesOfConversationHistory()
    {
        var transcript = new ConversationTranscript(new string('x', 100_000));
        var output = new ModelOutputObserved(SessionId.New(), DateTimeOffset.UtcNow, "chunk");
        transcript.Apply(output);
        _ = transcript.GetTextSince(transcript.Length - output.Text.Length);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var appendedCharacters = 0;
        for (var index = 0; index < 4096; index++)
        {
            var previousLength = transcript.Length;
            transcript.Apply(output);
            appendedCharacters += transcript.GetTextSince(previousLength).Length;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper?.WriteLine($"4,096 transcript deltas: {allocated:N0} bytes.");
        Assert.Equal(4096 * output.Text.Length, appendedCharacters);
        Assert.True(allocated < 1_000_000, $"Transcript history copying allocated {allocated:N0} bytes.");
    }
}
