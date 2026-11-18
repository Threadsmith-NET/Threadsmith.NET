namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
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
        const string note = firstLine + "\nsecond line\u001b[31m";
        var transcript = Render(
            succeeded: true,
            CreateResult("add", "added", [new { Id = "memory-1", Text = note }], 0));

        Assert.Contains("Outcome: added", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Memory memory-1 [situational]:", transcript.Text, StringComparison.Ordinal);
        Assert.Contains(firstLine, transcript.Text, StringComparison.Ordinal);
        Assert.Contains("second line\\u001B[31m", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(note[..240] + "...", transcript.Text, StringComparison.Ordinal);
    }

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

    /// <summary>Unavailable historical proposal text never falls back to an existing candidate's body.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public static void Transcript_ReconciliationWithoutProposalDoesNotShowCandidates(string? proposal)
    {
        var result = CreateResult("add", "reconciliationRequired", [new { Id = "candidate", Text = "existing candidate body" }], 0);

        var transcript = Render(true, result, activityDetail: "add", transientDetail: proposal);

        Assert.Contains("Not added: host requests reconciliation before adding.", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Proposed memory text unavailable.", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("existing candidate body", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>The concise reconciliation output still observes the configured display bound.</summary>
    [Fact]
    public static void Transcript_ReconciliationBoundsProposalText()
    {
        var note = new string('x', 1000);
        var result = CreateResult<object>("add", "reconciliationRequired", [], 0);

        var transcript = Render(true, result, activityDetail: "add", transientDetail: note, maximumInspectionCharacters: 256);

        Assert.Contains("Not added: host requests reconciliation before adding.", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Proposed memory:", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("[memory output truncated by console display bound]", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(note, transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Legacy entries remain visible while standing preference bodies are suppressed and outcomes remain visible.</summary>
    [Fact]
    public static void Transcript_ShowsLegacyTypeAndStandingPreferenceWarning()
    {
        var legacy = Render(
            succeeded: true,
            CreateResult("list", "listed", [new { Id = "legacy", Text = "legacy note" }], 0));
        var typed = Render(
            succeeded: true,
            JsonSerializer.Serialize(new
            {
                Action = "add",
                Outcome = "added",
                Entries = new[] { new { Id = "preference", Text = "always run tests", MemoryType = "standingPreference" } },
                OmittedEntries = 0,
                StandingPreferenceWarning = "You now have 4 preference memories. You may want to consider adding some of these to AGENTS.md for the repo.",
            }));

        Assert.Contains("Memory legacy [situational]:", legacy.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory preference [standingPreference]:", typed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("always run tests", typed.Text, StringComparison.Ordinal);
        Assert.Contains("Outcome: added", typed.Text, StringComparison.Ordinal);
        Assert.Contains("You now have 4 preference memories.", typed.Text, StringComparison.Ordinal);
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

    /// <summary>Small accepted inspection limits still render a bounded failure without indexing before the text.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(51)]
    public static void FailedMemoryInspectionHonorsSmallCharacterLimits(int maximum)
    {
        var transcript = Render(false, null, transientDetail: new string('x', 100), maximumInspectionCharacters: maximum);
        Assert.Contains("write failed", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 100), transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Checklist operations report investigation progress with the shared terminal encoding.</summary>
    [Theory]
    [InlineData("updateCoverage", false)]
    [InlineData("coverage", false)]
    [InlineData("coverage", true)]
    public static void Transcript_ShowsChecklistProgressInsteadOfEmptyMemories(string action, bool empty)
    {
        var result = JsonSerializer.Serialize(new
        {
            Action = action,
            Outcome = "prepared",
            Entries = Array.Empty<object>(),
            Coverage = new
            {
                Items = empty ? [] : new[] { new { Area = "Shared output\u009b31m", Status = "investigated", Priority = "optional" } },
                NextAfterKey = empty ? null : "output",
            },
        });
        var transcript = Render(true, result, activityDetail: action);

        Assert.DoesNotContain("No memories returned.", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Outcome: prepared", transcript.Text, StringComparison.Ordinal);
        Assert.Contains(empty ? "No investigation checklist items on this page." : "Investigation: Shared output\\u009B31m — investigated (optional)", transcript.Text, StringComparison.Ordinal);
        Assert.Equal(!empty, transcript.Text.Contains("More investigation checklist items available.", StringComparison.Ordinal));
        Assert.DoesNotContain('\u009b', transcript.Text);
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
