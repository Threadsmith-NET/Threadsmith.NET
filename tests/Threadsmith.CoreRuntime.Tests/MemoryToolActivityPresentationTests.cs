namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Verifies complete, safe console projection of model-managed repository memory outcomes.</summary>
public static class MemoryToolActivityPresentationTests
{
    /// <summary>Successful memory output retains a complete multiline note beyond the compact tool-detail limit.</summary>
    [Fact]
    public static void Transcript_ShowsCompleteMultilineMemoryTextBeyondCompactDetailLimit()
    {
        const string firstLine = "For release reviews in this repository, record the exact validation command and its outcome beside the change description. Include the relevant target framework and mention any skipped integration checks, so the next maintainer can distinguish what was exercised locally from what still needs a deployment environment.";
        var note = firstLine + "\nsecond line\u001b[31m";
        var transcript = Render(
            succeeded: true,
            CreateResult("add", "added", [new { Id = "memory-1", Text = note }], 0));

        Assert.Contains("Outcome: added", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Memory memory-1:", transcript.Text, StringComparison.Ordinal);
        Assert.Contains(firstLine, transcript.Text, StringComparison.Ordinal);
        Assert.Contains("second line\\u001B[31m", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(note[..240] + "...", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Empty and bounded list outcomes retain their status and inspectability guidance.</summary>
    [Fact]
    public static void Transcript_ShowsEmptyListAndOmissionGuidance()
    {
        var transcript = Render(succeeded: true, CreateResult<object>("list", "listed", [], 2));

        Assert.Contains("Outcome: listed", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("No memories returned.", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Omitted memories: 2. Use /memory inspect <id> for an individual note.", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Remove and no-op outcomes remain visible even when they carry no note body.</summary>
    [Theory]
    [InlineData("remove", "removed")]
    [InlineData("remove", "absent")]
    [InlineData("update", "notFound")]
    [InlineData("add", "duplicate")]
    public static void Transcript_ShowsMemoryOperationOutcome(string action, string outcome)
    {
        var result = outcome == "removed"
            ? CreateResult(action, outcome, [new { Id = "memory-2", Text = "removed note" }], 0)
            : CreateResult<object>(action, outcome, [], 0);
        var transcript = Render(succeeded: true, result, activityDetail: action + " memory-2");

        Assert.Contains(action + " memory-2", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Outcome: " + outcome, transcript.Text, StringComparison.Ordinal);
        Assert.Equal(outcome == "removed", transcript.Text.Contains("removed note", StringComparison.Ordinal));
    }

    /// <summary>Failed writes retain the requested memory text without requiring a result payload.</summary>
    [Fact]
    public static void Transcript_ShowsRequestedMemoryTextOnFailure()
    {
        var transcript = Render(succeeded: false, resultJson: null, transientDetail: "requested note");

        Assert.Contains("Requested memory:", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("requested note", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Malformed and non-built-in memory-shaped results never expose parsed note text.</summary>
    [Fact]
    public static void Transcript_DoesNotExposeMemoryBodyForMalformedOrNonBuiltInEvents()
    {
        var malformed = Render(succeeded: true, "{\"Outcome\":\"listed\",\"Entries\":[{\"Text\":\"external-memory-canary\"}]");
        var external = Render(
            succeeded: true,
            CreateResult("list", "listed", [new { Id = "memory-3", Text = "external-memory-canary" }], 0),
            source: new ToolActivitySource(ToolActivitySourceKind.Mcp, "external"));

        Assert.DoesNotContain("external-memory-canary", malformed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("external-memory-canary", external.Text, StringComparison.Ordinal);
    }

    private static ConversationTranscript Render(
        bool succeeded,
        string? resultJson,
        string activityDetail = "list",
        string? transientDetail = null,
        ToolActivitySource? source = null)
    {
        var started = new ToolInvocationStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ToolInvocationId.New(),
            "memories",
            ActivityDetail: activityDetail,
            Source: source,
            TransientActivityDetail: transientDetail);
        var completed = new ToolInvocationCompleted(
            started.SessionId,
            DateTimeOffset.UtcNow,
            started.ToolInvocationId,
            succeeded,
            ResultJson: resultJson,
            Source: source,
            Error: succeeded ? null : "write failed");
        var transcript = new ConversationTranscript(string.Empty, showOperationDurations: false);

        transcript.Apply(started);
        transcript.Apply(completed);
        return transcript;
    }

    private static string CreateResult<TEntry>(string action, string outcome, TEntry[] entries, int omittedEntries)
    {
        return JsonSerializer.Serialize(new { Action = action, Outcome = outcome, Entries = entries, OmittedEntries = omittedEntries });
    }
}
