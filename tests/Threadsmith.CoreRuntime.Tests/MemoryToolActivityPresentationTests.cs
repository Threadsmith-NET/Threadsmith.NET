namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Verifies complete, safe console projection of model-managed repository memory outcomes.</summary>
public static class MemoryToolActivityPresentationTests
{
    /// <summary>Reconciliation shows only the proposed note and host notice inside the shared tool block.</summary>
    [Theory]
    [InlineData("situational")]
    [InlineData("standingPreference")]
    public static void Transcript_ReconciliationShowsProposalInsteadOfCandidates(string memoryType)
    {
        const string firstLine = "Record the exact validation command and outcome. ";
        var note = string.Concat(Enumerable.Repeat(firstLine, 8)) + "\r\nsecond line\u001b[31m\n\nlast line";
        var result = JsonSerializer.Serialize(new
        {
            Action = "add",
            Outcome = "reconciliationRequired",
            Entries = new[] { new { Id = "candidate-id", Text = "candidate body", MemoryType = memoryType } },
            Resolution = "model-facing reconciliation instructions",
            QueryTruncated = false,
            OmittedEntries = 0,
            StandingPreferenceWarning = "preference warning",
        });

        var transcript = Render(true, result, activityDetail: "add", transientDetail: note);

        Assert.Contains("TOOLS: memories - completed", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("   │ Not added: host requests reconciliation before adding.", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("   │ Proposed memory:", transcript.Text, StringComparison.Ordinal);
        Assert.Contains(string.Concat(Enumerable.Repeat(firstLine, 8)), transcript.Text, StringComparison.Ordinal);
        Assert.Contains("   │ second line\\u001B[31m", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("   └ last line", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', transcript.Text);
        Assert.All(
            transcript.Text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line) && !line.Contains("TOOLS:", StringComparison.Ordinal)),
            line => Assert.True(line.StartsWith("   │", StringComparison.Ordinal) || line.StartsWith("   └", StringComparison.Ordinal), line));
        foreach (var hidden in new[] { "candidate-id", "candidate body", "Resolution", "model-facing", "QueryTruncated", "OmittedEntries", "preference warning", "Outcome:", "No memories returned" })
        {
            Assert.DoesNotContain(hidden, transcript.Text, StringComparison.Ordinal);
        }
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
        ToolActivitySource? source = null,
        int maximumInspectionCharacters = 98304)
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
        var transcript = new ConversationTranscript(
            string.Empty,
            showOperationDurations: false,
            limits: new TuiResourceLimits { MaximumToolInspectionCharacters = maximumInspectionCharacters });

        transcript.Apply(started);
        transcript.Apply(completed);
        return transcript;
    }

    private static string CreateResult<TEntry>(string action, string outcome, TEntry[] entries, int omittedEntries)
    {
        return JsonSerializer.Serialize(new { Action = action, Outcome = outcome, Entries = entries, OmittedEntries = omittedEntries });
    }
}
