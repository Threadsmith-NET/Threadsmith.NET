namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Verifies full web details survive both the completed block and transcript formatting.</summary>
public static class WebToolActivityPresentationTests
{
    /// <summary>Queries and ordinary full URLs appear in the branch detail line on success and failure.</summary>
    [Theory]
    [InlineData("web_search", false)]
    [InlineData("web_search", true)]
    [InlineData("web_fetch", false)]
    [InlineData("web_fetch", true)]
    public static void Transcript_ShowsFullLiveWebDetail(string tool, bool failed)
    {
        var detail = tool == "web_search"
            ? "Qwen reasoning behaviour"
            : "https://example.com/docs?q=complete&view=full";
        var started = new ToolInvocationStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ToolInvocationId.New(),
            tool,
            TransientActivityDetail: detail);
        var completed = new ToolInvocationCompleted(
            started.SessionId,
            DateTimeOffset.UtcNow,
            started.ToolInvocationId,
            !failed,
            Error: failed ? "Provider failed." : null);
        var transcript = new ConversationTranscript(string.Empty, showOperationDurations: false);

        transcript.Apply(started);
        transcript.Apply(completed);

        Assert.Contains("\u2514 " + detail, transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("no additional detail", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("...", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Successful retrieval replaces the initial URL with the complete destination after redirects.</summary>
    [Fact]
    public static void Transcript_PrefersFinalFetchedUrl()
    {
        const string finalUrl = "https://example.com/final?q=destination&view=full";
        var started = new ToolInvocationStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ToolInvocationId.New(),
            "web_fetch",
            ActivityDetail: "authorized web reference",
            TransientActivityDetail: "https://example.com/initial?q=original");
        var completed = new ToolInvocationCompleted(
            started.SessionId,
            DateTimeOffset.UtcNow,
            started.ToolInvocationId,
            true,
            TransientActivityDetail: finalUrl);
        var transcript = new ConversationTranscript(string.Empty, showOperationDurations: false);

        transcript.Apply(started);
        transcript.Apply(completed);

        Assert.Contains("\u2514 " + finalUrl, transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("initial", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("authorized web reference", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(finalUrl, DomainEventJson.Serialize(completed), StringComparison.Ordinal);
    }

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
