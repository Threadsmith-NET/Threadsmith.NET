namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Verifies full web details survive both the completed block and transcript formatting.</summary>
public static class WebToolActivityPresentationTests
{
    /// <summary>Oversized web detail retains the existing visible truncation marker.</summary>
    [Theory]
    [InlineData("web_search")]
    [InlineData("web_fetch")]
    public static void Transcript_TruncatesLongWebDetail(string tool)
    {
        var detail = "https://example.com/" + new string('x', 400) + "?q=tail";
        var started = new ToolInvocationStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ToolInvocationId.New(),
            tool,
            TransientActivityDetail: detail);
        var completed = new ToolInvocationCompleted(started.SessionId, DateTimeOffset.UtcNow, started.ToolInvocationId, true);
        var transcript = new ConversationTranscript(string.Empty, showOperationDurations: false);

        transcript.Apply(started);
        transcript.Apply(completed);

        Assert.Contains("\u2514 " + detail[..240] + "...", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("?q=tail", transcript.Text, StringComparison.Ordinal);
    }
}
