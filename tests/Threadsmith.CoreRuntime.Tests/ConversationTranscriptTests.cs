namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Verifies incremental access to the existing formatted conversation transcript.</summary>
public static class ConversationTranscriptTests
{
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
